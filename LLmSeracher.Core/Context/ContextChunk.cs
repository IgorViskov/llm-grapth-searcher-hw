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

    // ── Кодовые атрибуты ─────────────────────────────────────────────────────────────
    // Все свойства опциональны: markdown-источники их не заполняют, JSON-контракт A2A
    // остаётся совместимым со старыми узлами сети.

    /// <summary>Язык фрагмента — задаёт подсветку в промпте: "csharp", "typescript", "python".</summary>
    public string? Language { get; init; }

    /// <summary>Путь файла относительно корня репозитория.</summary>
    public string? FilePath { get; init; }

    public int? StartLine { get; init; }
    public int? EndLine { get; init; }

    /// <summary>Идентификатор символа в графе — стабилен между сборками.</summary>
    public string? SymbolId { get; init; }

    /// <summary>Вид фрагмента: Method, Type, Property, Field, GraphEdges, Doc.</summary>
    public string? Kind { get; init; }

    /// <summary>
    /// Почему фрагмент подключён: путь в графе от точки входа. Попадает в промпт и в
    /// консоль — без него графовая выдача необъяснима, а с ним видно ход рассуждения.
    /// </summary>
    public string? Rationale { get; init; }

    /// <summary>Ссылка на место в коде для цитирования: <c>path/File.cs:24-62</c>.</summary>
    public string? Location => FilePath is null
        ? null
        : StartLine is null ? FilePath : $"{FilePath}:{StartLine}-{EndLine ?? StartLine}";

    /// <summary>Фрагмент пришёл из кода, а не из markdown-справки.</summary>
    public bool IsCode => Language is not null || FilePath is not null;
}
