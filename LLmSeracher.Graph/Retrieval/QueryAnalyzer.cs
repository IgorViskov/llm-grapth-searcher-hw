using System.Text;
using System.Text.RegularExpressions;

namespace LLmSeracher.Graph.Retrieval;

/// <summary>
/// Разбор запроса на естественном языке в то, по чему можно искать в графе:
/// явные идентификаторы (структурный канал) и набор термов (полнотекстовый канал).
/// </summary>
public static partial class QueryAnalyzer
{
    /// <summary>
    /// Служебные слова, которые в вопросе есть всегда, а в коде и комментариях встречаются
    /// в каждом втором символе. Без их отсева полнотекстовый канал выдаёт случайные попадания:
    /// анализатор Lucene не знает русского и не отфильтрует их сам.
    /// </summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "как", "что", "где", "кто", "чем", "это", "этот", "эта", "для", "или", "если",
        "при", "над", "под", "про", "без", "все", "весь", "вся", "она", "они", "оно",
        "его", "ему", "нее", "неё", "них", "там", "тут", "так", "уже", "ещё", "еще",
        "быть", "есть", "был", "была", "было", "были", "будет", "надо", "нужно",
        "может", "можно", "чтобы", "когда", "потом", "затем", "того", "тому", "тем",
        "the", "and", "for", "with", "that", "this", "from", "how", "what", "where",
        "who", "does", "did", "are", "was", "were", "will", "can", "should"
    };

    /// <summary>
    /// Намерение запроса. Определяет, в какую сторону расширять обход: вопрос
    /// «кто вызывает X» требует входящих рёбер CALLS, а не самого X, — но текстовый
    /// поиск всегда находит именно X и заполняет им всю выдачу.
    /// </summary>
    public enum SearchIntent
    {
        General,
        Callers,
        Implementations
    }

    public static SearchIntent DetectIntent(string query)
    {
        var q = query.ToLowerInvariant();

        if (CallersRegex().IsMatch(q)) return SearchIntent.Callers;
        if (ImplementationsRegex().IsMatch(q)) return SearchIntent.Implementations;

        return SearchIntent.General;
    }

    // Между вопросительным словом и глаголом почти всегда что-то стоит («кто ЕГО вызывает»),
    // поэтому подстрока не годится — нужен зазор.
    [GeneratedRegex(@"(кто|кем|откуда|где|что)\W+(\w+\W+){0,3}(вызыв|использ|дёрга|дерга|созда|обращ)"
                    + @"|что сломается|на что влия|кто зависит|caller|usages?|who calls")]
    private static partial Regex CallersRegex();

    [GeneratedRegex(@"реализац|реализует|реализуют|наследник|потомок|зарегистрирован|implementation")]
    private static partial Regex ImplementationsRegex();

    /// <summary>
    /// Идентификаторы, названные в запросе явно: <c>SearchAsync</c>, <c>Composite.SearchAsync</c>.
    /// Такие попадания заведомо точнее полнотекстовых, поэтому идут отдельным каналом.
    /// </summary>
    public static IReadOnlyList<string> ExtractIdentifiers(string query)
    {
        var found = new List<string>();

        foreach (Match m in QualifiedNameRegex().Matches(query))
            found.Add(m.Value);

        foreach (Match m in BareIdentifierRegex().Matches(query))
        {
            // Часть составного имени уже учтена — второй раз как самостоятельный терм не берём.
            if (found.Any(f => f.Contains(m.Value, StringComparison.Ordinal))) continue;
            found.Add(m.Value);
        }

        return found.Distinct(StringComparer.Ordinal).Take(8).ToList();
    }

    /// <summary>Пути файлов в запросе: <c>Context/FileContextProvider.cs</c>.</summary>
    public static IReadOnlyList<string> ExtractPaths(string query) =>
        PathRegex().Matches(query).Select(m => m.Value.Replace('\\', '/')).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Строка запроса для Lucene. Спецсимволы экранируются, термы соединяются через OR,
    /// а идентификаторы дополнительно раскладываются по camelCase — так описательный вопрос
    /// («потоковая выдача контекста») попадает и в имена, и в русские XML-комментарии.
    /// </summary>
    public static string BuildFullTextQuery(string query)
    {
        var terms = new List<string>();

        foreach (var raw in WordRegex().Matches(query).Select(m => m.Value))
        {
            if (raw.Length < 3 || StopWords.Contains(raw)) continue;

            // Идентификатор кода — сигнал куда более сильный, чем обычное слово вопроса,
            // и только он заслуживает бустинга.
            var parts = IdentifierTokenizer.Split(raw);
            var looksLikeIdentifier = parts.Length > 1;

            terms.Add(looksLikeIdentifier ? $"{Escape(raw)}^4" : Escape(raw));

            if (looksLikeIdentifier)
                terms.AddRange(parts.Where(p => p.Length >= 3 && !StopWords.Contains(p)).Select(Escape));
        }

        return string.Join(" OR ", terms.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string Escape(string term)
    {
        var builder = new StringBuilder(term.Length + 8);
        foreach (var c in term)
        {
            if ("+-&|!(){}[]^\"~*?:\\/".Contains(c)) builder.Append('\\');
            builder.Append(c);
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"\b[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+\b")]
    private static partial Regex QualifiedNameRegex();

    // Идентификатор кода отличаем от обычного слова по внутренней заглавной букве
    // (SearchAsync, IContextProvider) — иначе в канал точных совпадений полезет любое слово.
    [GeneratedRegex(@"\b[A-Za-z_][a-z0-9_]*[A-Z][A-Za-z0-9_]*\b")]
    private static partial Regex BareIdentifierRegex();

    [GeneratedRegex(@"[\w./\\-]+\.(?:cs|ts|tsx|js|py|csproj|json|md)\b")]
    private static partial Regex PathRegex();

    [GeneratedRegex(@"[\p{L}\p{N}_]{2,}")]
    private static partial Regex WordRegex();
}

/// <summary>Разбор идентификатора на слова: <c>SearchAsync</c> → <c>Search Async</c>.</summary>
public static class IdentifierTokenizer
{
    public static string[] Split(string identifier)
    {
        if (string.IsNullOrEmpty(identifier)) return [];

        var words = new List<string>();
        var current = new StringBuilder();

        foreach (var c in identifier)
        {
            if (c is '_' or '-' or '.')
            {
                Flush();
                continue;
            }

            // Граница слова — переход из нижнего регистра (или цифры) в верхний.
            if (char.IsUpper(c) && current.Length > 0 && !char.IsUpper(current[^1]))
                Flush();

            current.Append(c);
        }

        Flush();
        return words.ToArray();

        void Flush()
        {
            if (current.Length == 0) return;
            words.Add(current.ToString());
            current.Clear();
        }
    }

    /// <summary>Значение для индексируемого свойства <c>nameTokens</c>.</summary>
    public static string ToSearchableText(string identifier) => string.Join(' ', Split(identifier));
}
