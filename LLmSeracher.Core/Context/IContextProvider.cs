namespace LLmSeracher.Core.Context;

/// <summary>
/// Источник контекста. Реализации: файлы на диске, HTTP API, что угодно ещё.
/// Возвращает поток — источник может отдавать фрагменты по мере готовности,
/// не дожидаясь полного обхода.
/// </summary>
public interface IContextProvider
{
    /// <summary>Идентификатор источника, попадает в <see cref="ContextChunk.SourceId"/>.</summary>
    string Name { get; }

    IAsyncEnumerable<ContextChunk> SearchAsync(string query, int limit, CancellationToken ct);
}
