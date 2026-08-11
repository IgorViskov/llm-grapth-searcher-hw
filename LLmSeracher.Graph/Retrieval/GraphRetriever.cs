using LLmSeracher.Graph.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.Driver;

namespace LLmSeracher.Graph.Retrieval;

/// <summary>
/// Поиск по графу: точки входа → обход по типам связей → ранжирование → упаковка.
///
/// Точки входа ищутся двумя каналами — структурным (явно названный в вопросе идентификатор
/// или путь) и полнотекстовым. Результаты сливаются Reciprocal Rank Fusion: скор Lucene
/// и «единица за точное совпадение» несопоставимы по шкале, складывать их напрямую нельзя.
///
/// Векторный канал (ANN по эмбеддингам карточек символов) в этой реализации не подключён —
/// см. раздел «Что не сделано» в GRAPH-CONTEXT-ANALYSIS.md. Слияние каналов написано так,
/// что добавление третьего источника точек входа его не меняет.
/// </summary>
public sealed class GraphRetriever
{
    private const double RrfK = 60.0;
    private const double StructuralChannelWeight = 1.6;
    private const double LexicalChannelWeight = 1.0;

    /// <summary>Сколько соседей брать у одной точки входа — защита от «звёзд» вроде файла с сотней членов.</summary>
    private const int NeighboursPerSeed = 40;

    private readonly IGraphStore _store;
    private readonly RetrievalOptions _options;
    private readonly ILogger<GraphRetriever> _logger;

    public GraphRetriever(IGraphStore store, IOptions<RetrievalOptions> options, ILogger<GraphRetriever> logger)
    {
        _store = store;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<CodeSearchResult>> SearchAsync(
        string query, int limit, CancellationToken ct)
    {
        var seeds = await FindSeedsAsync(query, ct);
        if (seeds.Count == 0)
        {
            _logger.LogInformation("Точки входа для запроса «{Query}» не найдены", query);
            return [];
        }

        var intent = QueryAnalyzer.DetectIntent(query);
        var candidates = await ExpandAsync(seeds, intent, ct);
        var loaded = await LoadAsync(candidates.Keys, ct);

        var ranked = candidates.Values
            .Where(c => loaded.ContainsKey(c.Id))
            .Where(c => c.Score >= _options.MinScore)
            .OrderByDescending(c => c.Score)
            .ToList();

        var results = new List<CodeSearchResult>();
        var budget = _options.MaxContextChars;
        var cap = Math.Min(limit <= 0 ? _options.MaxChunks : limit, _options.MaxChunks);
        var perFile = new Dictionary<string, int>(StringComparer.Ordinal);
        var crowdedOut = new List<Candidate>();

        // Ограничение на файл — предпочтение, а не запрет. Без него ответ на «что делает X»
        // состоит из конструктора, полей и свойств одного типа. Но бывает и обратное:
        // всё относящееся к вопросу лежит в одном файле, и жёсткий лимит тогда просто
        // выкидывает нужный метод. Поэтому вытесненные кандидаты добираются вторым проходом.
        foreach (var candidate in ranked)
        {
            if (results.Count >= cap || budget <= 0) break;

            var file = loaded[candidate.Id].FilePath ?? string.Empty;
            if (perFile.GetValueOrDefault(file) >= _options.ChunksPerFile)
            {
                crowdedOut.Add(candidate);
                continue;
            }

            TryAdd(candidate, file);
        }

        foreach (var candidate in crowdedOut)
        {
            if (results.Count >= cap || budget <= 0) break;
            TryAdd(candidate, loaded[candidate.Id].FilePath ?? string.Empty);
        }

        void TryAdd(Candidate candidate, string file)
        {
            var node = loaded[candidate.Id];
            var snippet = Trim(node.Snippet, Math.Min(_options.MaxSnippetChars, budget));
            if (snippet.Length == 0) return;

            perFile[file] = perFile.GetValueOrDefault(file) + 1;
            budget -= snippet.Length;
            results.Add(new CodeSearchResult(
                node.Id, node.Kind, node.Name, node.Fqn, node.Signature,
                node.FilePath, node.StartLine, node.EndLine, node.DocComment,
                snippet, candidate.Score, candidate.Rationale, candidate.Hops));
        }

        _logger.LogInformation(
            "Запрос «{Query}»: {Seeds} точек входа, {Candidates} кандидатов, {Results} фрагментов",
            query, seeds.Count, candidates.Count, results.Count);

        return results;
    }

    /// <summary>Связи между отобранными символами — блок структуры для промпта.</summary>
    public async Task<IReadOnlyList<string>> DescribeEdgesAsync(
        IReadOnlyCollection<string> symbolIds, CancellationToken ct)
    {
        if (symbolIds.Count < 2) return [];

        // Место, породившее ребро, отдаём вместе с ним: у REGISTERED_AS это строка, где тип
        // зарегистрирован в контейнере, у CALLS — строка вызова. Без него на вопрос
        // «где они регистрируются» ответить нечем, хотя граф это знает.
        return await _store.ReadAsync("""
            MATCH (a:Symbol)-[r]->(b:Symbol)
            WHERE a.id IN $ids AND b.id IN $ids AND type(r) IN $edgeTypes
            RETURN coalesce(a.fqn, a.name) AS src, type(r) AS edge, coalesce(b.fqn, b.name) AS dst,
                   r.sourceFile AS file, r.line AS line
            LIMIT 120
            """,
            new { ids = symbolIds.ToList(), edgeTypes = _options.ActiveEdgeKinds.ToList() },
            r =>
            {
                var edge = $"{r["src"].As<string>()} --{r["edge"].As<string>()}--> {r["dst"].As<string>()}";
                var file = r["file"].As<string?>();
                var line = r["line"].As<int?>();

                return file is null ? edge
                    : line is null ? $"{edge}   [{file}]"
                    : $"{edge}   [{file}:{line}]";
            },
            ct);
    }

    // ── Точки входа ──────────────────────────────────────────────────────────────────

    private async Task<Dictionary<string, Seed>> FindSeedsAsync(string query, CancellationToken ct)
    {
        var structural = await FindExactAsync(query, ct);
        var lexical = await FindFullTextAsync(query, ct);

        var fused = new Dictionary<string, double>(StringComparer.Ordinal);
        Fuse(fused, structural, StructuralChannelWeight);
        Fuse(fused, lexical, LexicalChannelWeight);

        if (fused.Count == 0) return [];

        var max = fused.Values.Max();
        var origins = structural.ToDictionary(x => x.Id, _ => "точное совпадение имени", StringComparer.Ordinal);

        var fileById = structural.Concat(lexical)
            .Where(h => h.File is not null)
            .GroupBy(h => h.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().File!, StringComparer.Ordinal);

        return Diversify(fused, fileById, _options.SeedsPerFile, _options.SeedLimit)
            .ToDictionary(
                p => p.Key,
                p => new Seed(p.Key, p.Value / max,
                    origins.GetValueOrDefault(p.Key, "полнотекстовое совпадение")),
                StringComparer.Ordinal);

        static void Fuse(Dictionary<string, double> target, IReadOnlyList<Hit> hits, double weight)
        {
            for (var rank = 0; rank < hits.Count; rank++)
            {
                var contribution = weight / (RrfK + rank + 1);
                target[hits[rank].Id] = target.GetValueOrDefault(hits[rank].Id) + contribution;
            }
        }
    }

    /// <summary>
    /// Отбор с ограничением на файл. Вытесненные кандидаты не выбрасываются, а дополняют
    /// хвост выдачи: если релевантен действительно один файл, ограничение не должно
    /// оставить запрос без контекста.
    /// </summary>
    private static IEnumerable<KeyValuePair<string, double>> Diversify(
        Dictionary<string, double> ranked,
        IReadOnlyDictionary<string, string> fileById,
        int perFile,
        int total)
    {
        var used = new Dictionary<string, int>(StringComparer.Ordinal);
        var deferred = new List<KeyValuePair<string, double>>();
        var taken = 0;

        foreach (var item in ranked.OrderByDescending(p => p.Value))
        {
            var file = fileById.GetValueOrDefault(item.Key, string.Empty);
            if (used.GetValueOrDefault(file) >= perFile)
            {
                deferred.Add(item);
                continue;
            }

            used[file] = used.GetValueOrDefault(file) + 1;
            yield return item;
            if (++taken >= total) yield break;
        }

        foreach (var item in deferred)
        {
            yield return item;
            if (++taken >= total) yield break;
        }
    }

    private async Task<IReadOnlyList<Hit>> FindExactAsync(string query, CancellationToken ct)
    {
        var identifiers = QueryAnalyzer.ExtractIdentifiers(query);
        var paths = QueryAnalyzer.ExtractPaths(query);
        if (identifiers.Count == 0 && paths.Count == 0) return [];

        return await _store.ReadAsync("""
            CALL () {
                UNWIND $names AS term
                MATCH (s:Symbol)
                WHERE s.kind IN $kinds AND (s.name = term OR s.fqn = term OR s.fqn ENDS WITH ('.' + term))
                RETURN s, 1.0 AS score
              UNION
                UNWIND $paths AS term
                MATCH (s:Symbol)
                WHERE s.kind IN $kinds AND s.filePath ENDS WITH term
                RETURN s, 0.8 AS score
            }
            RETURN s.id AS id, s.filePath AS file, score
            ORDER BY score DESC
            LIMIT $limit
            """,
            new
            {
                names = identifiers.ToList(),
                paths = paths.ToList(),
                kinds = NodeKinds.Retrievable.ToList(),
                limit = _options.SeedLimit
            },
            r => new Hit(r["id"].As<string>(), r["score"].As<double>(), r["file"].As<string?>()),
            ct);
    }

    private async Task<IReadOnlyList<Hit>> FindFullTextAsync(string query, CancellationToken ct)
    {
        var lucene = QueryAnalyzer.BuildFullTextQuery(query);
        if (string.IsNullOrWhiteSpace(lucene)) return [];

        try
        {
            // Структурная поправка к текстовому скору: при близких совпадениях выигрывает
            // символ, у которого больше связей. Иначе описательный запрос упирается
            // в случайную константу с удачным словом в комментарии, а не в узел,
            // вокруг которого действительно крутится обсуждаемый механизм.
            return await _store.ReadAsync($$"""
                CALL db.index.fulltext.queryNodes('{{GraphOptions.FullTextIndex}}', $q, {limit: $limit})
                YIELD node, score
                WHERE node.kind IN $kinds
                RETURN node.id AS id, node.filePath AS file,
                       score * (1 + 0.35 * log(1 + COUNT { (node)--() })) AS score
                ORDER BY score DESC
                """,
                new
                {
                    q = lucene,
                    kinds = NodeKinds.Retrievable.ToList(),
                    // Берём с запасом: фильтр по kind применяется уже после выдачи индекса.
                    limit = _options.SeedLimit * 4
                },
                r => new Hit(r["id"].As<string>(), r["score"].As<double>(), r["file"].As<string?>()),
                ct);
        }
        catch (Neo4jException ex)
        {
            _logger.LogWarning(ex, "Полнотекстовый канал недоступен, работаем только по точным совпадениям");
            return [];
        }
    }

    // ── Обход ────────────────────────────────────────────────────────────────────────

    private async Task<Dictionary<string, Candidate>> ExpandAsync(
        Dictionary<string, Seed> seeds, QueryAnalyzer.SearchIntent intent, CancellationToken ct)
    {
        var candidates = seeds.Values.ToDictionary(
            s => s.Id,
            s => new Candidate(s.Id, s.Score, s.Rationale, Hops: 0),
            StringComparer.Ordinal);

        var seedIds = seeds.Keys.ToList();
        var edgeTypes = _options.ActiveEdgeKinds.ToList();

        var neighbours = await _store.ReadAsync("""
            UNWIND $seedIds AS sid
            MATCH (s:Symbol {id: sid})
            CALL (s) {
                MATCH (s)-[r]-(n:Symbol)
                WHERE type(r) IN $edgeTypes AND n.kind IN $kinds
                RETURN r, n
                LIMIT $perSeed
            }
            RETURN s.id AS seedId,
                   coalesce(s.fqn, s.name) AS seedFqn,
                   s.kind AS seedKind,
                   n.id AS id,
                   n.accessibility AS access,
                   type(r) AS edge,
                   CASE WHEN startNode(r).id = s.id THEN 'out' ELSE 'in' END AS dir
            """,
            new
            {
                seedIds,
                edgeTypes,
                kinds = NodeKinds.Retrievable.ToList(),
                perSeed = NeighboursPerSeed
            },
            r => new Neighbour(
                r["seedId"].As<string>(), r["seedFqn"].As<string>(),
                r["id"].As<string>(), r["edge"].As<string>(), r["dir"].As<string>(),
                r["seedKind"].As<string?>(), r["access"].As<string?>()),
            ct);

        foreach (var n in neighbours)
        {
            // Все члены одного типа приходят с одинаковым весом, и без второго признака
            // порядок между ними случаен — в выдачу попадал приватный хелпер вместо
            // публичного метода. Вопрос о поведении типа почти всегда про его открытую часть.
            var weight = MemberOfTypeSeed(n)
                ? _options.TypeMemberWeight * (n.IsPublic ? 1.0 : _options.NonPublicMemberFactor)
                : _options.WeightOf(n.Edge) * IntentBoost(intent, n.Edge, n.Dir);

            var score = seeds[n.SeedId].Score * _options.HopDecay * weight;
            Offer(candidates, n.Id, score, Rationales.Describe(n.Edge, n.Dir, Short(n.SeedFqn)), 1);
        }

        // Второй шаг только по вызовам вверх: «кто вызывает того, кто вызывает X» —
        // это и есть анализ влияния, ради которого граф в первую очередь и нужен.
        var callers = await _store.ReadAsync("""
            UNWIND $seedIds AS sid
            MATCH (s:Symbol {id: sid})
            CALL (s) {
                MATCH (s)<-[:CALLS]-(:Symbol)<-[:CALLS]-(n:Symbol)
                RETURN DISTINCT n
                LIMIT $perSeed
            }
            RETURN s.id AS seedId, coalesce(s.fqn, s.name) AS seedFqn, n.id AS id
            """,
            new { seedIds, perSeed = NeighboursPerSeed },
            r => new Neighbour(
                r["seedId"].As<string>(), r["seedFqn"].As<string>(),
                r["id"].As<string>(), EdgeKinds.Calls, "in", SeedKind: null),
            ct);

        var decay2 = _options.HopDecay * _options.HopDecay;
        var callerBoost = IntentBoost(intent, EdgeKinds.Calls, "in");

        foreach (var n in callers)
        {
            var score = seeds[n.SeedId].Score * decay2 * _options.WeightOf(EdgeKinds.Calls) * callerBoost;
            Offer(candidates, n.Id, score, $"вызывает {Short(n.SeedFqn)} через один уровень", 2);
        }

        return candidates;

        static void Offer(Dictionary<string, Candidate> map, string id, double score, string why, int hops)
        {
            if (map.TryGetValue(id, out var existing) && existing.Score >= score) return;
            map[id] = new Candidate(id, score, why, hops);
        }
    }

    /// <summary>
    /// Поправка на намерение запроса. Текстовый поиск на «кто вызывает X» всегда находит
    /// сам X и его однофамильцев, заполняя ими всю выдачу, — множитель поднимает соседей
    /// по нужному направлению выше точек входа.
    /// </summary>
    private static double IntentBoost(QueryAnalyzer.SearchIntent intent, string edge, string dir) =>
        (intent, edge, dir) switch
        {
            (QueryAnalyzer.SearchIntent.Callers, EdgeKinds.Calls, "in") => 2.4,

            // Для типа «кто его вызывает» означает «кто его создаёт»: конструктор в графе
            // вызовов не участвует, место создания даёт ребро INSTANTIATES.
            (QueryAnalyzer.SearchIntent.Callers, EdgeKinds.Instantiates, "in") => 2.2,
            (QueryAnalyzer.SearchIntent.Implementations, EdgeKinds.ImplementsMember, "in") => 2.2,
            (QueryAnalyzer.SearchIntent.Implementations, EdgeKinds.Implements, "in") => 2.2,
            (QueryAnalyzer.SearchIntent.Implementations, EdgeKinds.RegisteredAs, "out") => 2.2,
            (QueryAnalyzer.SearchIntent.Implementations, EdgeKinds.Inherits, "in") => 1.8,
            _ => 1.0
        };

    private async Task<Dictionary<string, NodeRow>> LoadAsync(
        IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];

        var rows = await _store.ReadAsync("""
            UNWIND $ids AS id
            MATCH (s:Symbol {id: id})
            RETURN s.id AS id, s.kind AS kind, coalesce(s.name, '') AS name,
                   coalesce(s.fqn, '') AS fqn, s.signature AS signature,
                   s.filePath AS filePath, s.startLine AS startLine, s.endLine AS endLine,
                   s.docComment AS docComment, coalesce(s.snippet, '') AS snippet
            """,
            new { ids = ids.ToList() },
            r => new NodeRow(
                r["id"].As<string>(), r["kind"].As<string>(), r["name"].As<string>(),
                r["fqn"].As<string>(), r["signature"].As<string?>(), r["filePath"].As<string?>(),
                r["startLine"].As<int?>(), r["endLine"].As<int?>(),
                r["docComment"].As<string?>(), r["snippet"].As<string>()),
            ct);

        return rows.ToDictionary(r => r.Id, StringComparer.Ordinal);
    }

    private static string Short(string fqn)
    {
        var parts = fqn.Split('.');
        return parts.Length <= 2 ? fqn : string.Join('.', parts[^2..]);
    }

    private static string Trim(string text, int max)
    {
        if (max <= 0) return string.Empty;
        if (text.Length <= max) return text;

        // Режем по границе строки — обрывок посреди выражения читается хуже, чем на строку короче.
        var cut = text.LastIndexOf('\n', Math.Min(max, text.Length - 1));
        return (cut > max / 2 ? text[..cut] : text[..max]) + "\n// … фрагмент усечён";
    }

    private sealed record Hit(string Id, double Score, string? File);
    private sealed record Seed(string Id, double Score, string Rationale);
    private sealed record Candidate(string Id, double Score, string Rationale, int Hops);
    /// <summary>
    /// Член типа, найденного как точка входа. Узел типа по построению несёт только заголовок
    /// и список сигнатур — тела лежат в его членах. Поэтому тип в выдаче без своих методов
    /// это оглавление без содержания, и член релевантного типа надо оценивать почти наравне
    /// с ним самим, а не как рядового соседа через ребро.
    /// </summary>
    private static bool MemberOfTypeSeed(Neighbour n) =>
        n is { Edge: EdgeKinds.HasMember, Dir: "out", SeedKind: NodeKinds.Type };

    private sealed record Neighbour(
        string SeedId, string SeedFqn, string Id, string Edge, string Dir,
        string? SeedKind, string? Access = null)
    {
        public bool IsPublic => string.Equals(Access, "public", StringComparison.Ordinal);
    }

    private sealed record NodeRow(
        string Id, string Kind, string Name, string Fqn, string? Signature,
        string? FilePath, int? StartLine, int? EndLine, string? DocComment, string Snippet);
}

/// <summary>Человекочитаемое объяснение, почему сосед попал в контекст.</summary>
internal static class Rationales
{
    public static string Describe(string edge, string dir, string seed)
    {
        var outgoing = dir == "out";

        return edge switch
        {
            EdgeKinds.Calls => outgoing ? $"вызывается из {seed}" : $"вызывает {seed}",
            EdgeKinds.ImplementsMember => outgoing ? $"метод интерфейса, реализуемый {seed}" : $"реализация метода {seed}",
            EdgeKinds.Overrides => outgoing ? $"переопределяемый метод {seed}" : $"переопределяет {seed}",
            EdgeKinds.RegisteredAs => outgoing ? $"реализация {seed}, зарегистрированная в DI" : $"интерфейс, под которым {seed} зарегистрирован в DI",
            EdgeKinds.HasMember => outgoing ? $"член типа {seed}" : $"тип, объявляющий {seed}",
            EdgeKinds.Implements => outgoing ? $"интерфейс, реализуемый {seed}" : $"реализация {seed}",
            EdgeKinds.Inherits => outgoing ? $"базовый тип {seed}" : $"наследник {seed}",
            EdgeKinds.Instantiates => outgoing ? $"создаётся в {seed}" : $"создаёт {seed}",
            EdgeKinds.Returns => outgoing ? $"возвращается из {seed}" : $"метод, возвращающий {seed}",
            EdgeKinds.HasParam => outgoing ? $"тип параметра {seed}" : $"метод, принимающий {seed}",
            EdgeKinds.Declares => outgoing ? $"объявлен в {seed}" : $"файл, объявляющий {seed}",
            _ => $"связан с {seed} ({edge})"
        };
    }
}
