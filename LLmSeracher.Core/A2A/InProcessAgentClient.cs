using System.Runtime.CompilerServices;

namespace LLmSeracher.Core.A2A;

/// <summary>
/// A2A-транспорт «в одном процессе»: тот же контракт, но без сети. Нужен, чтобы сценарий
/// можно было воспроизвести одной командой, когда хост агентов не поднят (ключ <c>--local</c>).
/// </summary>
public sealed class InProcessAgentClient : IAgentClient
{
    private readonly IReadOnlyDictionary<string, IAgent> _agents;

    public string Endpoint => "in-process";

    public InProcessAgentClient(IEnumerable<IAgent> agents) =>
        _agents = agents.ToDictionary(a => a.Card.Id, StringComparer.OrdinalIgnoreCase);

    public Task<AgentCard?> GetCardAsync(string agentId, CancellationToken ct) =>
        Task.FromResult(_agents.TryGetValue(agentId, out var agent) ? agent.Card : null);

    public async IAsyncEnumerable<AgentEvent> SendAsync(
        string agentId, AgentTask task, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!_agents.TryGetValue(agentId, out var agent))
        {
            yield return new FailedEvent(agentId, $"агент '{agentId}' не зарегистрирован в процессе");
            yield break;
        }

        await foreach (var evt in agent.ExecuteAsync(task, ct))
            yield return evt;
    }
}
