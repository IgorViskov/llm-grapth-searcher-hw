using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using LLmSeracher.AgentHost;
using LLmSeracher.Core;
using LLmSeracher.Core.A2A;
using LLmSeracher.Core.Context;
using LLmSeracher.Graph;
using Microsoft.Extensions.Options;

// Как и в консольном клиенте: без этого кириллица в логах хоста на Windows выводится
// знаками вопроса, а при перенаправлении вывода в файл — мусором.
Console.OutputEncoding = System.Text.Encoding.UTF8;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

// Явно, а не через автоматику ASP.NET Core: та подключает user-secrets только в окружении
// Development, и запуск собранного exe остался бы без ключа.
builder.Configuration.AddUserSecrets<Program>(optional: true);

builder.Services.AddSearcherCore(builder.Configuration);
builder.Services.AddHostedAgents();

// Граф кода — ещё один источник контекста. Регистрация листовая: композит в Core подхватит
// его наравне с файлами, а какие источники реально включены, решает Context:Sources.
builder.Services.AddCodeGraph(builder.Configuration);

// Настройки сериализации должны совпадать с A2AJson на стороне клиента,
// иначе полиморфные события не разберутся.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

var app = builder.Build();

// Стартовая проверка графа. Недоступная или пустая БД проявляется только в самом конце
// цепочки — сообщением «релевантный контекст не найден», по которому причину не установить:
// источник контекста по контракту логирует отказ и молча пропускается.
await ReportGraphStateAsync(app);

app.MapGet("/", (IEnumerable<IAgent> agents) =>
    Results.Text("LLmSeracher Agent Host\n" +
                 "Агенты: " + string.Join(", ", agents.Select(a => a.Card.Id)) + "\n\n" +
                 """
                 GET  /a2a/agents               — каталог агентов
                 GET  /a2a/agents/{id}/card     — карточка агента
                 POST /a2a/agents/{id}/tasks    — задача агенту, ответ Server-Sent Events
                 GET  /api/docs?q=...&limit=3   — внешний источник контекста
                 """, "text/plain; charset=utf-8"));

// ── Каталог агентов: по нему вызывающая сторона узнаёт, кто что умеет ──────────────
app.MapGet("/a2a/agents", (IEnumerable<IAgent> agents) =>
    Results.Ok(agents.Select(a => a.Card)));

app.MapGet("/a2a/agents/{agentId}/card", (string agentId, IEnumerable<IAgent> agents) =>
    Find(agents, agentId) is { } agent
        ? Results.Ok(agent.Card)
        : Results.NotFound(new { error = $"агент '{agentId}' не найден" }));

// ── Приём задачи: ответ уходит потоком Server-Sent Events ──────────────────────────
app.MapPost("/a2a/agents/{agentId}/tasks",
    (string agentId, AgentTask task, IEnumerable<IAgent> agents, CancellationToken ct) =>
    {
        var agent = Find(agents, agentId);
        if (agent is null)
            return Results.NotFound(new { error = $"агент '{agentId}' не найден" });

        return TypedResults.ServerSentEvents(StreamAgent(agent, task, ct));
    });

// ── «Внешний» API документов — второй источник контекста ───────────────────────────
app.MapGet("/api/docs", (string q, int? limit, CancellationToken ct) =>
    DocsCatalog.SearchAsync(q, limit ?? 3, ct));

app.Run();

static IAgent? Find(IEnumerable<IAgent> agents, string agentId) =>
    agents.FirstOrDefault(a => string.Equals(a.Card.Id, agentId, StringComparison.OrdinalIgnoreCase));

static async Task ReportGraphStateAsync(WebApplication app)
{
    var sources = app.Services.GetRequiredService<IOptions<ContextOptions>>().Value.Sources;
    var graphEnabled = sources.Length == 0 ||
                       sources.Contains(ContextSources.CodeGraph, StringComparer.OrdinalIgnoreCase);

    if (!graphEnabled) return;

    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("CodeGraph");
    var store = app.Services.GetRequiredService<IGraphStore>();

    try
    {
        var stats = await store.GetStatsAsync(CancellationToken.None);
        if (stats.Nodes == 0)
            logger.LogWarning(
                "Граф кода пуст. Наполнить: dotnet run --project LLmSeracher.Indexer -- index --reset");
        else
            logger.LogInformation("Граф кода: {Nodes} узлов, {Edges} рёбер", stats.Nodes, stats.Edges);
    }
    catch (Exception ex)
    {
        logger.LogError("Граф кода недоступен ({Message}). Поднять БД: docker compose up -d", ex.Message);
    }
}

/// <summary>
/// Каждое событие агента уходит отдельным SSE-сообщением с собственным типом:
/// клиент может реагировать на "token" и "context" по-разному, не разбирая тело.
/// </summary>
static async IAsyncEnumerable<SseItem<AgentEvent>> StreamAgent(
    IAgent agent, AgentTask task, [EnumeratorCancellation] CancellationToken ct)
{
    await foreach (var evt in AgentStream.Guarded(agent.Card.Id, agent.ExecuteAsync(task, ct), ct))
        yield return new SseItem<AgentEvent>(evt, evt.SseEventName());
}
