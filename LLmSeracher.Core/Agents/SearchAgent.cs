using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using LLmSeracher.Core.A2A;
using LLmSeracher.Core.Context;
using LLmSeracher.Core.Llm;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LLmSeracher.Core.Agents;

/// <summary>
/// Агент-оркестратор. Сам в базу знаний не ходит и сам ничего не сжимает: контекст
/// запрашивает у <see cref="RetrieverAgent"/>, сжатие при необходимости делегирует
/// <see cref="SummarizerAgent"/> — и только финальную генерацию делает сам, отдавая
/// её потоком наружу.
/// </summary>
public sealed class SearchAgent : IAgent
{
    private readonly IAgentClient _agents;
    private readonly IChatClient _chat;
    private readonly DelegationService _delegation;
    private readonly AgentOptions _agentOptions;
    private readonly LlmOptions _llm;
    private readonly ILogger<SearchAgent> _logger;

    public AgentCard Card { get; } = new(
        Id: AgentIds.Search,
        Name: "Search",
        Description: "Отвечает на вопрос по базе знаний, привлекая другие агенты сети.",
        Skills:
        [
            new AgentSkill(Skills.Answer, "Ответ на вопрос со ссылками на источники", [])
        ]);

    public SearchAgent(
        IAgentClient agents,
        IChatClient chat,
        DelegationService delegation,
        IOptions<AgentOptions> agentOptions,
        IOptions<LlmOptions> llm,
        ILogger<SearchAgent> logger)
    {
        _agents = agents;
        _chat = chat;
        _delegation = delegation;
        _agentOptions = agentOptions.Value;
        _llm = llm.Value;
        _logger = logger;
    }

    public async IAsyncEnumerable<AgentEvent> ExecuteAsync(
        AgentTask task, [EnumeratorCancellation] CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();

        // ── Шаг 1. Контекст запрашивается у агента-ретривера по A2A ───────────────────
        var retrieveTask = NewSubTask(Skills.ContextSearch, task.Query, task.ConversationId);
        retrieveTask = retrieveTask with
        {
            Delegation = _delegation.Issue(AgentIds.Retriever, retrieveTask, Scopes.ContextRead)
        };

        yield return new DelegatedEvent(Card.Id, AgentIds.Retriever, Skills.ContextSearch,
            $"владелец базы знаний ({_agents.Endpoint})", [Scopes.ContextRead]);

        var chunks = new List<ContextChunk>();
        var retrieverFailed = false;

        await foreach (var evt in _agents.SendAsync(AgentIds.Retriever, retrieveTask, ct))
        {
            switch (evt)
            {
                case ContextAttachedEvent attached:
                    chunks.AddRange(attached.Chunks);
                    break;
                case StatusEvent status:
                    yield return status;
                    break;
                case FailedEvent failed:
                    retrieverFailed = true;
                    yield return failed;
                    break;
            }

            if (retrieverFailed) break;
        }

        if (retrieverFailed) yield break;

        if (chunks.Count == 0)
        {
            yield return new FailedEvent(Card.Id,
                "релевантный контекст не найден — отвечать без источников агент не будет");
            yield break;
        }

        yield return new ContextAttachedEvent(Card.Id, chunks);

        // ── Шаг 2. Слишком большой контекст сжимается другим агентом ──────────────────
        var contextBlock = PromptBuilder.RenderContextBlock(chunks);

        if (contextBlock.Length > _agentOptions.SummarizeThresholdChars)
        {
            var summarizeTask = NewSubTask(Skills.ContextSummarize, task.Query, task.ConversationId) with
            {
                Context = chunks
            };
            summarizeTask = summarizeTask with
            {
                Delegation = _delegation.Issue(AgentIds.Summarizer, summarizeTask, Scopes.LlmInvoke)
            };

            yield return new DelegatedEvent(Card.Id, AgentIds.Summarizer, Skills.ContextSummarize,
                $"контекст {contextBlock.Length} симв. > порога {_agentOptions.SummarizeThresholdChars}",
                [Scopes.LlmInvoke]);

            string? compressed = null;
            await foreach (var evt in _agents.SendAsync(AgentIds.Summarizer, summarizeTask, ct))
            {
                switch (evt)
                {
                    case CompletedEvent completed:
                        compressed = completed.Text;
                        break;
                    case StatusEvent status:
                        yield return status;
                        break;
                    case FailedEvent failed:
                        // Деградация вместо падения: работаем с полным контекстом.
                        yield return new StatusEvent(Card.Id,
                            $"суммаризатор отказал ({failed.Message}) — беру контекст целиком");
                        break;
                }
            }

            if (!string.IsNullOrWhiteSpace(compressed))
            {
                // Сжатие принимается, только если номера по-прежнему указывают на те же
                // фрагменты: список источников строится по исходному порядку, и разъехавшаяся
                // нумерация даёт ответ с правильными фактами и неправильными ссылками.
                if (PromptBuilder.PreservesNumbering(compressed, chunks))
                {
                    contextBlock = compressed.Trim();
                    yield return new StatusEvent(Card.Id, $"контекст сжат до {contextBlock.Length} симв.");
                }
                else
                {
                    yield return new StatusEvent(Card.Id,
                        "сжатый контекст потерял привязку номеров к фрагментам — беру контекст целиком");
                }
            }
        }

        // ── Шаг 3. Генерация ответа потоком ──────────────────────────────────────────
        yield return new StatusEvent(Card.Id,
            $"генерирую ответ, модель {(_llm.UseOpenAi ? _llm.Model : "offline-stub")}");

        List<ChatMessage> messages =
        [
            // Профиль промпта выбирается по самому контексту: кодовые фрагменты требуют
            // других правил цитирования, чем markdown-справка.
            new(ChatRole.System, PromptBuilder.BuildAnswerSystemPrompt(contextBlock, chunks)),
            new(ChatRole.User, task.Query)
        ];

        var options = new ChatOptions { ModelId = _llm.Model, Temperature = 0.2f };
        var answer = new StringBuilder();

        await foreach (var update in _chat.GetStreamingResponseAsync(messages, options, ct))
        {
            if (string.IsNullOrEmpty(update.Text)) continue;

            answer.Append(update.Text);
            yield return new TokenEvent(Card.Id, update.Text);
        }

        _logger.LogInformation("Задача {TaskId} завершена, {Chars} симв.", task.TaskId, answer.Length);

        yield return new CompletedEvent(
            Card.Id,
            answer.ToString(),
            Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            PromptBuilder.BuildSources(chunks));
    }

    private static AgentTask NewSubTask(string skill, string query, string conversationId) =>
        new(Guid.NewGuid().ToString("n")[..12], skill, query, conversationId);
}
