using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using LLmSeracher.Core.A2A;
using LLmSeracher.Core.Llm;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LLmSeracher.Core.Agents;

/// <summary>
/// Агент-суммаризатор. Контекст в источники не ходит — он приходит вместе с задачей,
/// это и есть передача контекста между агентами. Результат отдаётся потоком: инициатор
/// видит сжатие по мере генерации, а не после.
/// </summary>
public sealed class SummarizerAgent : IAgent
{
    private readonly IChatClient _chat;
    private readonly DelegationService _delegation;
    private readonly A2AOptions _a2a;
    private readonly AgentOptions _agentOptions;
    private readonly LlmOptions _llm;
    private readonly ILogger<SummarizerAgent> _logger;

    public AgentCard Card { get; } = new(
        Id: AgentIds.Summarizer,
        Name: "Summarizer",
        Description: "Сжимает переданный блок контекста, сохраняя нумерацию источников.",
        Skills:
        [
            new AgentSkill(Skills.ContextSummarize, "Сжать блок контекста до заданного размера",
                [Scopes.LlmInvoke])
        ]);

    public SummarizerAgent(
        IChatClient chat,
        DelegationService delegation,
        IOptions<A2AOptions> a2a,
        IOptions<AgentOptions> agentOptions,
        IOptions<LlmOptions> llm,
        ILogger<SummarizerAgent> logger)
    {
        _chat = chat;
        _delegation = delegation;
        _a2a = a2a.Value;
        _agentOptions = agentOptions.Value;
        _llm = llm.Value;
        _logger = logger;
    }

    public async IAsyncEnumerable<AgentEvent> ExecuteAsync(
        AgentTask task, [EnumeratorCancellation] CancellationToken ct)
    {
        var check = _delegation.Validate(task.Delegation, Card.Id, Scopes.LlmInvoke);
        if (_a2a.RequireDelegation && !check.IsValid)
        {
            _logger.LogWarning("Задача {TaskId} отклонена: {Error}", task.TaskId, check.Error);
            yield return new FailedEvent(Card.Id, $"вызов LLM запрещён: {check.Error}");
            yield break;
        }

        var chunks = task.Context ?? [];
        if (chunks.Count == 0)
        {
            yield return new FailedEvent(Card.Id, "в задаче не передан контекст для сжатия");
            yield break;
        }

        var started = Stopwatch.GetTimestamp();
        var contextBlock = PromptBuilder.RenderContextBlock(chunks);

        yield return new StatusEvent(Card.Id,
            $"сжимаю {chunks.Count} фрагментов ({contextBlock.Length} симв.) до ~{_agentOptions.SummaryBudgetChars}");

        List<ChatMessage> messages =
        [
            new(ChatRole.System, PromptBuilder.BuildSummarySystemPrompt(_agentOptions.SummaryBudgetChars)),
            new(ChatRole.User, contextBlock)
        ];

        var options = new ChatOptions { ModelId = _llm.UtilityModel, Temperature = 0f };
        var text = new StringBuilder();

        await foreach (var update in _chat.GetStreamingResponseAsync(messages, options, ct))
        {
            if (string.IsNullOrEmpty(update.Text)) continue;

            text.Append(update.Text);
            yield return new TokenEvent(Card.Id, update.Text);
        }

        yield return new CompletedEvent(
            Card.Id,
            text.ToString(),
            Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            PromptBuilder.BuildSources(chunks));
    }
}
