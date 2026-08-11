using LLmSeracher.Cli;
using LLmSeracher.Core;
using LLmSeracher.Core.A2A;
using LLmSeracher.Core.Agents;
using LLmSeracher.Core.Context;
using LLmSeracher.Core.Llm;
using LLmSeracher.Graph;
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
{
    builder.Services.AddInProcessAgentTransport();
    // В локальном режиме ретривер живёт здесь же — значит, и граф нужен здесь.
    builder.Services.AddCodeGraph(builder.Configuration);
}
else
{
    builder.Services.AddHttpAgentTransport();
}

builder.Services.AddSingleton<SearchAgent>();

using var host = builder.Build();

var searchAgent = host.Services.GetRequiredService<SearchAgent>();
var agentClient = host.Services.GetRequiredService<IAgentClient>();
var delegation = host.Services.GetRequiredService<DelegationService>();
var llm = host.Services.GetRequiredService<IOptions<LlmOptions>>().Value;
var renderer = new ConsoleRenderer();
// Эндпоинт печатаем рядом с моделью: при работе с OpenAI-совместимым API это единственный
// способ увидеть, что запросы уходят туда, куда задумано.
var modelLabel = llm.UseOpenAi ? $"{llm.Model} @ {llm.EndpointLabel}" : "offline-stub";

CancellationTokenSource? currentOperation = null;
Console.CancelKeyPress += (_, e) =>
{
    if (currentOperation is not { IsCancellationRequested: false }) return;

    // Прерываем только текущую генерацию, приложение продолжает работать.
    e.Cancel = true;
    currentOperation.Cancel();
};

AnsiConsole.Write(new FigletText("LLM Searcher").Color(Color.Blue));

// Откуда взялся ключ, видно сразу: иначе опечатка в имени переменной окружения выглядит
// как беспричинный уход в оффлайн-режим.
AnsiConsole.MarkupLine(llm.UseOpenAi
    ? $"[grey]llm: {Markup.Escape(llm.Model)} @ {Markup.Escape(llm.EndpointLabel)}; ключ — {Markup.Escape(llm.ApiKeySource)}[/]"
    : $"[grey]llm: оффлайн-заглушка; ключ — {Markup.Escape(llm.ApiKeySource)}, адрес API не задан[/]");

if (!cli.Local && !await PingHostAsync()) return 1;
if (cli.Local) AnsiConsole.MarkupLine("[grey]сеть агентов: in-process (retriever, summarizer в этом же процессе)[/]");

if (cli.AclDemo)
{
    await AclDemoAsync();
    return 0;
}

if (cli.Demo)
{
    // Набор вопросов зависит от того, какой источник включён: спрашивать про сроки
    // возврата у графа кода бессмысленно ровно так же, как про CALLS у markdown-справки.
    var sources = host.Services.GetRequiredService<IOptions<ContextOptions>>().Value.Sources;
    var questions = sources.Contains(ContextSources.CodeGraph, StringComparer.OrdinalIgnoreCase)
        ? CliOptions.CodeDemoQuestions
        : CliOptions.DemoQuestions;

    foreach (var question in questions)
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
        AnsiConsole.Markup("[blue]?[/] ");

        // Spectre.Console TextPrompt читает клавиши, а не строку, и падает
        // InvalidOperationException("Failed to read input in non-interactive mode"),
        // если поток ввода или вывода перенаправлен — конвейер, запуск из IDE, CI.
        // Console.ReadLine работает в обоих случаях; null — это конец ввода,
        // штатный выход, а не ошибка.
        var question = Console.ReadLine();
        if (question is null)
        {
            AnsiConsole.WriteLine();
            return;
        }

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
    var task = AgentTask.Create(Skills.ContextSearch, "проверка scope в DelegationService");

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
