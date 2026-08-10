using System.Text.Json.Serialization;
using LLmSeracher.Core.Context;

namespace LLmSeracher.Core.A2A;

/// <summary>
/// Единица потокового ответа агента. Дискриминатор "type" позволяет одному и тому же
/// типу событий ехать и внутри процесса, и по SSE в JSON.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(StatusEvent), "status")]
[JsonDerivedType(typeof(ContextAttachedEvent), "context")]
[JsonDerivedType(typeof(DelegatedEvent), "delegated")]
[JsonDerivedType(typeof(TokenEvent), "token")]
[JsonDerivedType(typeof(CompletedEvent), "completed")]
[JsonDerivedType(typeof(FailedEvent), "failed")]
public abstract record AgentEvent
{
    /// <summary>Имя SSE-события: клиент может фильтровать поток, не разбирая тело.</summary>
    [JsonIgnore]
    public abstract string EventType { get; }
}

/// <summary>Служебная телеметрия хода выполнения — на неё удобно смотреть в демо.</summary>
public sealed record StatusEvent(string AgentId, string Message) : AgentEvent
{
    public override string EventType => "status";
}

/// <summary>Контекст, который агент подключил к промпту. Приходит до первого токена.</summary>
public sealed record ContextAttachedEvent(string AgentId, IReadOnlyList<ContextChunk> Chunks) : AgentEvent
{
    public override string EventType => "context";
}

/// <summary>Факт передачи задачи другому агенту.</summary>
public sealed record DelegatedEvent(
    string FromAgentId,
    string ToAgentId,
    string Skill,
    string Reason,
    IReadOnlyList<string> Scopes) : AgentEvent
{
    public override string EventType => "delegated";
}

/// <summary>Очередной кусок ответа. Именно эти события создают эффект печати.</summary>
public sealed record TokenEvent(string AgentId, string Text) : AgentEvent
{
    public override string EventType => "token";
}

public sealed record CompletedEvent(
    string AgentId,
    string Text,
    double ElapsedMs,
    IReadOnlyList<SourceRef> Sources) : AgentEvent
{
    public override string EventType => "completed";
}

public sealed record FailedEvent(string AgentId, string Message) : AgentEvent
{
    public override string EventType => "failed";
}

public sealed record SourceRef(int Number, string SourceId, string Title);
