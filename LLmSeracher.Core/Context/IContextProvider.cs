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

    /// <summary>
    /// Из чего источник состоит на самом деле. Обычный источник — это он сам; композит
    /// перечисляет включённые листья. Нужно, чтобы агент-ретривер публиковал в карточке
    /// фактический состав источников, а не зашитую в код строку.
    /// </summary>
    IReadOnlyList<string> SourceNames => [Name];

    IAsyncEnumerable<ContextChunk> SearchAsync(string query, int limit, CancellationToken ct);
}
