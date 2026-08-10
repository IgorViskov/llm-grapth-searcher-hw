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
}
