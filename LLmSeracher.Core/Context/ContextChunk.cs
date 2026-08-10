namespace LLmSeracher.Core.Context;

/// <summary>
/// Единица контекста, которую агент может подключить к промпту.
/// Один и тот же тип едет и внутри процесса, и по A2A-каналу в JSON.
/// </summary>
/// <param name="SourceId">Идентификатор источника: "files", "docs-api", ...</param>
/// <param name="DocumentId">Идентификатор документа внутри источника.</param>
/// <param name="Title">Человекочитаемый заголовок — попадает в ссылку [1].</param>
/// <param name="Text">Собственно текст фрагмента.</param>
/// <param name="Score">Релевантность запросу, 0..1. Используется для сортировки и отсечки.</param>
public sealed record ContextChunk(
    string SourceId,
    string DocumentId,
    string Title,
    string Text,
    double Score)
{
    /// <summary>Ключ для дедупликации фрагментов, пришедших из нескольких источников.</summary>
    public string Key => $"{SourceId}/{DocumentId}";
}
