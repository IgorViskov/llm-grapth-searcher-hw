namespace LLmSeracher.Core.Agents;

/// <summary>Идентификаторы агентов и навыков — общий словарь для всех узлов сети.</summary>
public static class AgentIds
{
    public const string Search = "search";
    public const string Retriever = "retriever";
    public const string Summarizer = "summarizer";
}

public static class Skills
{
    public const string ContextSearch = "context.search";
    public const string ContextSummarize = "context.summarize";
    public const string Answer = "answer";
}

/// <summary>Полномочия, которые переносит делегирующий токен.</summary>
public static class Scopes
{
    public const string ContextRead = "context:read";
    public const string LlmInvoke = "llm:invoke";
}

public sealed class AgentOptions
{
    /// <summary>Если собранный контекст длиннее — задача сжатия делегируется агенту-суммаризатору.</summary>
    public int SummarizeThresholdChars { get; set; } = 1200;

    /// <summary>Целевой размер сжатого контекста.</summary>
    public int SummaryBudgetChars { get; set; } = 700;

    /// <summary>Сколько фрагментов запрашивать у агента-ретривера.</summary>
    public int ContextLimit { get; set; } = 4;
}
