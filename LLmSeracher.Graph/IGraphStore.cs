using LLmSeracher.Graph.Model;
using Neo4j.Driver;

namespace LLmSeracher.Graph;

/// <summary>
/// Доступ к графу. Cypher за этим интерфейсом остаётся, но точка подключения одна —
/// переезд на Memgraph (тот же Bolt) или FalkorDB меняет только реализацию.
/// </summary>
public interface IGraphStore : IAsyncDisposable
{
    Task EnsureSchemaAsync(CancellationToken ct);

    /// <summary>Полная очистка графа — для <c>--reset</c> перед холодной индексацией.</summary>
    Task ResetAsync(CancellationToken ct);

    /// <summary>
    /// Снимает всё, что было порождено перечисленными файлами: рёбра — целиком,
    /// узлы — только оставшиеся без связей. Первый шаг инкрементального обновления.
    /// </summary>
    Task<int> DeleteBySourceFilesAsync(IReadOnlyCollection<string> files, CancellationToken ct);

    Task UpsertAsync(GraphBatch batch, CancellationToken ct);

    Task<IReadOnlyList<T>> ReadAsync<T>(
        string cypher, object? parameters, Func<IRecord, T> map, CancellationToken ct);

    Task<GraphStats> GetStatsAsync(CancellationToken ct);
}
