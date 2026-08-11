using System.Runtime.CompilerServices;
using LLmSeracher.Core.Context;
using LLmSeracher.Graph.Retrieval;
using Microsoft.Extensions.Logging;
using Neo4j.Driver;

namespace LLmSeracher.Graph;

/// <summary>
/// Источник контекста поверх графа кода. Реализует тот же <see cref="IContextProvider"/>,
/// что и файловый источник, — поэтому агенты, транспорт A2A и конвейер streaming
/// не меняются вовсе.
///
/// Тела символов лежат в самом графе (свойство <c>snippet</c>), к диску на этапе поиска
/// обращений нет: приложение действительно ищет в БД, а не по файлам.
/// </summary>
public sealed class GraphContextProvider : IContextProvider
{
    private readonly GraphRetriever _retriever;
    private readonly ILogger<GraphContextProvider> _logger;

    public string Name => "code-graph";

    public GraphContextProvider(GraphRetriever retriever, ILogger<GraphContextProvider> logger)
    {
        _retriever = retriever;
        _logger = logger;
    }

    public async IAsyncEnumerable<ContextChunk> SearchAsync(
        string query, int limit, [EnumeratorCancellation] CancellationToken ct)
    {
        IReadOnlyList<CodeSearchResult> results;
        try
        {
            // Один фрагмент резервируем под блок структуры связей.
            results = await _retriever.SearchAsync(query, Math.Max(1, limit - 1), ct);
        }
        catch (Exception ex) when (ex is Neo4jException or ServiceUnavailableException)
        {
            // Недоступный граф не должен ронять запрос — ровно так же ведёт себя HTTP-источник.
            _logger.LogWarning(ex, "Граф недоступен ({Message}) — продолжаем без него", ex.Message);
            yield break;
        }

        if (results.Count == 0) yield break;

        foreach (var result in results)
            yield return result.ToChunk(Name);

        var edges = await _retriever.DescribeEdgesAsync(
            results.Select(r => r.SymbolId).ToList(), ct);

        if (edges.Count == 0) yield break;

        // Отдельным фрагментом отдаём связи между уже подключёнными символами: модель видит
        // структуру, а не мешок независимых кусков кода.
        yield return new ContextChunk(
            SourceId: Name,
            DocumentId: "graph:structure",
            Title: "связи между подключёнными символами",
            Text: string.Join('\n', edges),
            Score: results[0].Score)
        {
            Kind = "GraphEdges",
            Rationale = "рёбра графа между фрагментами выше"
        };
    }
}
