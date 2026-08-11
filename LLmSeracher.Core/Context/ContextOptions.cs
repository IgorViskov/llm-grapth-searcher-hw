namespace LLmSeracher.Core.Context;

public sealed class ContextOptions
{
    /// <summary>Каталог с *.md базы знаний. Относительный путь резолвится от каталога приложения.</summary>
    public string KnowledgeRoot { get; set; } = "knowledge";

    /// <summary>Сколько фрагментов максимум подключать к одному запросу.</summary>
    public int MaxChunks { get; set; } = 4;

    /// <summary>Фрагменты с релевантностью ниже порога в промпт не попадают.</summary>
    public double MinScore { get; set; } = 0.05;

    /// <summary>Базовый адрес «внешнего» API-источника документов.</summary>
    public string DocsApiBaseUrl { get; set; } = "http://localhost:5080";

    /// <summary>
    /// Какие источники включены. Пустой список — все зарегистрированные.
    /// Для кодовых репозиториев осмысленно оставить один <c>code-graph</c>: markdown-справка
    /// в такой выдаче только шумит.
    /// </summary>
    public string[] Sources { get; set; } = [];
}

/// <summary>Имена источников контекста и ключ, под которым они регистрируются в DI.</summary>
public static class ContextSources
{
    /// <summary>
    /// Ключ листовых источников. Композит собирается из них и сам регистрируется как
    /// обычный <see cref="IContextProvider"/> — так он не попадает в собственную коллекцию.
    /// </summary>
    public const string LeafKey = "context-leaf";

    public const string Files = "files";
    public const string DocsApi = "docs-api";
    public const string CodeGraph = "code-graph";
}
