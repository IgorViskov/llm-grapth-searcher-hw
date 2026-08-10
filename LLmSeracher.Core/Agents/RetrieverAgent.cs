using System.Diagnostics;
using System.Runtime.CompilerServices;
using LLmSeracher.Core.A2A;
using LLmSeracher.Core.Context;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LLmSeracher.Core.Agents;

/// <summary>
/// Агент-владелец базы знаний. Только он ходит в источники контекста; остальные агенты
/// получают контекст от него по A2A и обязаны предъявить полномочие <c>context:read</c>.
/// </summary>
public sealed class RetrieverAgent : IAgent
{
    private readonly IContextProvider _context;
    private readonly DelegationService _delegation;
    private readonly A2AOptions _a2a;
    private readonly ContextOptions _contextOptions;
    private readonly ILogger<RetrieverAgent> _logger;

    public AgentCard Card { get; } = new(
        Id: AgentIds.Retriever,
        Name: "Retriever",
        Description: "Ищет фрагменты в базе знаний: файлы *.md и внешний API документов.",
        Skills:
        [
            new AgentSkill(Skills.ContextSearch, "Вернуть релевантные фрагменты контекста",
                [Scopes.ContextRead])
        ]);

    public RetrieverAgent(
        IContextProvider context,
        DelegationService delegation,
        IOptions<A2AOptions> a2a,
        IOptions<ContextOptions> contextOptions,
        ILogger<RetrieverAgent> logger)
    {
        _context = context;
        _delegation = delegation;
        _a2a = a2a.Value;
        _contextOptions = contextOptions.Value;
        _logger = logger;
    }

    public async IAsyncEnumerable<AgentEvent> ExecuteAsync(
        AgentTask task, [EnumeratorCancellation] CancellationToken ct)
    {
        var check = _delegation.Validate(task.Delegation, Card.Id, Scopes.ContextRead);
        if (_a2a.RequireDelegation && !check.IsValid)
        {
            _logger.LogWarning("Задача {TaskId} отклонена: {Error}", task.TaskId, check.Error);
            yield return new FailedEvent(Card.Id, $"доступ к контексту запрещён: {check.Error}");
            yield break;
        }

        var started = Stopwatch.GetTimestamp();
        yield return new StatusEvent(Card.Id,
            $"полномочия приняты от '{check.Payload?.Issuer ?? "—"}', ищу контекст");

        var chunks = new List<ContextChunk>();
        await foreach (var chunk in _context.SearchAsync(task.Query, _contextOptions.MaxChunks, ct))
            chunks.Add(chunk);

        _logger.LogInformation("Задача {TaskId}: найдено {Count} фрагментов", task.TaskId, chunks.Count);

        yield return new ContextAttachedEvent(Card.Id, chunks);
        yield return new CompletedEvent(
            Card.Id,
            Text: string.Empty,
            ElapsedMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            Sources: PromptBuilder.BuildSources(chunks));
    }
}
