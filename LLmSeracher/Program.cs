using LLmSeracher.Cli;
using LLmSeracher.Core;
using LLmSeracher.Core.A2A;
using LLmSeracher.Core.Agents;
using LLmSeracher.Core.Llm;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Spectre.Console;

// Без этого кириллица в Windows-консоли выводится знаками вопроса.
Console.OutputEncoding = System.Text.Encoding.UTF8;

if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine(CliOptions.Usage);
    return 0;
}

var cli = CliOptions.Parse(args);

// ContentRoot указываем явно: иначе appsettings.json ищется в текущем каталоге оболочки
// и настройки молча теряются при запуске через dotnet run из корня решения.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    ContentRootPath = AppContext.BaseDirectory
});

builder.Configuration.AddUserSecrets(typeof(Program).Assembly, optional: true);
if (cli.HostUrl is not null)
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["A2A:HostUrl"] = cli.HostUrl });

builder.Services.AddSearcherCore(builder.Configuration);

// Единственное отличие двух режимов — реализация IAgentClient.
// Код агента-оркестратора при этом не меняется вообще.
if (cli.Local)
    builder.Services.AddInProcessAgentTransport();
else
    builder.Services.AddHttpAgentTransport();

builder.Services.AddSingleton<SearchAgent>();

using var host = builder.Build();

var searchAgent = host.Services.GetRequiredService<SearchAgent>();
var agentClient = host.Services.GetRequiredService<IAgentClient>();
var delegation = host.Services.GetRequiredService<DelegationService>();
var llm = host.Services.GetRequiredService<IOptions<LlmOptions>>().Value;
var renderer = new ConsoleRenderer();
var modelLabel = llm.UseOpenAi ? llm.Model : "offline-stub";

CancellationTokenSource? currentOperation = null;
Console.CancelKeyPress += (_, e) =>
{
    if (currentOperation is not { IsCancellationRequested: false }) return;

    // Прерываем только текущую генерацию, приложение продолжает работать.
    e.Cancel = true;
    currentOperation.Cancel();
};

AnsiConsole.Write(new FigletText("LLM Searcher").Color(Color.Blue));

if (!cli.Local && !await PingHostAsync()) return 1;
if (cli.Local) AnsiConsole.MarkupLine("[grey]сеть агентов: in-process (retriever, summarizer в этом же процессе)[/]");

if (cli.AclDemo)
{
    await AclDemoAsync();
    return 0;
}

if (cli.Demo)
{
    foreach (var question in CliOptions.DemoQuestions)
        await AskAsync(question);

    return 0;
}

if (cli.Question is not null)
{
    await AskAsync(cli.Question);
    return 0;
}

await InteractiveAsync();
return 0;

// ── Сценарии ──────────────────────────────────────────────────────────────────────

async Task<bool> PingHostAsync()
{
    var card = await agentClient.GetCardAsync(AgentIds.Retriever, CancellationToken.None);
    if (card is null)
    {
        AnsiConsole.MarkupLine($"[red]Хост агентов недоступен: {Markup.Escape(agentClient.Endpoint)}[/]");
        AnsiConsole.MarkupLine("[grey]Запустите его командой[/] dotnet run --project LLmSeracher.AgentHost");
        AnsiConsole.MarkupLine("[grey]или используйте режим[/] --local");
        return false;
    }

    AnsiConsole.MarkupLine(
        $"[grey]сеть агентов: {Markup.Escape(agentClient.Endpoint)} → {Markup.Escape(card.Id)} ({Markup.Escape(card.Protocol)})[/]");
    return true;
}

async Task AskAsync(string question)
{
    using var cts = new CancellationTokenSource();
    currentOperation = cts;
    renderer.Reset();
    renderer.Question(question, agentClient.Endpoint, modelLabel);

    var task = AgentTask.Create(Skills.Answer, question);

    try
    {
        await foreach (var evt in AgentStream.Guarded(searchAgent.Card.Id, searchAgent.ExecuteAsync(task, cts.Token), cts.Token))
            renderer.Handle(evt);
    }
    catch (OperationCanceledException)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]  ⟂ поток прерван пользователем (Ctrl+C)[/]");
        AnsiConsole.WriteLine();
    }
    finally
    {
        currentOperation = null;
    }
}

async Task InteractiveAsync()
{
    AnsiConsole.MarkupLine("[grey]Введите вопрос. Пустая строка или 'exit' — выход, Ctrl+C — прервать генерацию.[/]");
    AnsiConsole.WriteLine();

    while (true)
    {
        var question = AnsiConsole.Prompt(
            new TextPrompt<string>("[blue]?[/]").AllowEmpty());

        if (string.IsNullOrWhiteSpace(question) ||
            question.Equals("exit", StringComparison.OrdinalIgnoreCase))
            return;

        await AskAsync(question);
    }
}

/// <summary>
/// Демонстрация делегирования полномочий: одна и та же задача без токена и с токеном.
/// </summary>
async Task AclDemoAsync()
{
    var task = AgentTask.Create(Skills.ContextSearch, "возврат товара надлежащего качества");

    AnsiConsole.Write(new Rule("[bold]1. Задача агенту retriever без делегирующего токена[/]").LeftJustified());
    await foreach (var evt in agentClient.SendAsync(AgentIds.Retriever, task, CancellationToken.None))
        renderer.Handle(evt);

    AnsiConsole.WriteLine();

    var signed = task with { Delegation = delegation.Issue(AgentIds.Retriever, task, Scopes.ContextRead) };
    AnsiConsole.Write(new Rule("[bold]2. Та же задача с токеном на context:read[/]").LeftJustified());
    AnsiConsole.MarkupLine($"[grey]  токен: {Markup.Escape(Preview(signed.Delegation!))}[/]");
    await foreach (var evt in agentClient.SendAsync(AgentIds.Retriever, signed, CancellationToken.None))
        renderer.Handle(evt);

    AnsiConsole.WriteLine();

    var wrongScope = task with { Delegation = delegation.Issue(AgentIds.Retriever, task, Scopes.LlmInvoke) };
    AnsiConsole.Write(new Rule("[bold]3. Токен есть, но полномочие другое (llm:invoke)[/]").LeftJustified());
    await foreach (var evt in agentClient.SendAsync(AgentIds.Retriever, wrongScope, CancellationToken.None))
        renderer.Handle(evt);
}

static string Preview(string token) =>
    token.Length <= 48 ? token : $"{token[..24]}…{token[^12..]}";
