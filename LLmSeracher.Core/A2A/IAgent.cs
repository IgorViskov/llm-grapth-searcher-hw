namespace LLmSeracher.Core.A2A;

/// <summary>
/// Агент. Контракт намеренно потоковый: результат — это последовательность событий,
/// а не одно значение в конце. Транспорт (в процессе / по HTTP+SSE) агент не знает.
/// </summary>
public interface IAgent
{
    AgentCard Card { get; }

    IAsyncEnumerable<AgentEvent> ExecuteAsync(AgentTask task, CancellationToken ct);
}

/// <summary>
/// Клиент вызова агента. Реализации: <see cref="InProcessAgentClient"/> и
/// <see cref="HttpAgentClient"/>. Агент-инициатор пишется один раз и работает
/// с локальным и с удалённым исполнителем одинаково.
/// </summary>
public interface IAgentClient
{
    /// <summary>Куда/кому уходит вызов — для логов и вывода в консоль.</summary>
    string Endpoint { get; }

    Task<AgentCard?> GetCardAsync(string agentId, CancellationToken ct);

    IAsyncEnumerable<AgentEvent> SendAsync(string agentId, AgentTask task, CancellationToken ct);
}
