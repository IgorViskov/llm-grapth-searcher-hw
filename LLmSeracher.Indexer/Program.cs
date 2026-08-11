using System.Diagnostics;
using System.Runtime.CompilerServices;
using LLmSeracher.Graph;
using LLmSeracher.Graph.Retrieval;
using LLmSeracher.Indexer;
using LLmSeracher.Indexer.Extraction;
using Microsoft.Build.Locator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Spectre.Console;

// Locator обязан отработать до того, как загрузится хоть один тип MSBuild. Поэтому вся
// работа с Roslyn спрятана за отдельным методом с запретом инлайна: иначе JIT подтянет
// сборки MSBuild при компиляции этого файла и регистрация опоздает.
if (!MSBuildLocator.IsRegistered) MSBuildLocator.RegisterDefaults();

var options = CliOptions.Parse(args);
if (options.ShowHelp)
{
    CliOptions.PrintHelp();
    return 0;
}

var builder = Host.CreateApplicationBuilder();
builder.Configuration
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables();

builder.Services.AddCodeGraph(builder.Configuration);
builder.Services.AddSingleton<CSharpExtractor>();

using var host = builder.Build();
var store = host.Services.GetRequiredService<IGraphStore>();

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

try
{
    return options.Command switch
    {
        "index" => await RunIndexAsync(options, cancellation.Token),
        "stats" => await RunStatsAsync(cancellation.Token),
        "search" => await RunSearchAsync(options, cancellation.Token),
        _ => Fail($"неизвестная команда '{options.Command}'")
    };
}
catch (OperationCanceledException)
{
    AnsiConsole.MarkupLine("[yellow]прервано[/]");
    return 130;
}
catch (Exception ex)
{
    AnsiConsole.MarkupLine($"[red]ошибка:[/] {Markup.Escape(ex.Message)}");
    return 1;
}

// ── Команды ──────────────────────────────────────────────────────────────────────────

[MethodImpl(MethodImplOptions.NoInlining)]
async Task<int> RunIndexAsync(CliOptions cli, CancellationToken ct)
{
    var solution = cli.SolutionPath ?? CliOptions.FindSolution();
    if (solution is null) return Fail("решение не найдено, укажите --solution <путь к .sln>");

    var repoRoot = cli.RepoRoot ?? Path.GetDirectoryName(Path.GetFullPath(solution))!;

    AnsiConsole.MarkupLine($"[grey]решение:[/] {Markup.Escape(solution)}");
    AnsiConsole.MarkupLine($"[grey]корень: [/] {Markup.Escape(repoRoot)}");

    var started = Stopwatch.GetTimestamp();
    await store.EnsureSchemaAsync(ct);

    if (cli.Reset)
    {
        AnsiConsole.MarkupLine("[yellow]--reset:[/] очищаю граф");
        await store.ResetAsync(ct);
    }

    var extractor = host.Services.GetRequiredService<CSharpExtractor>();
    var written = 0;

    var report = await extractor.ExtractAsync(solution, repoRoot, async (batch, token) =>
    {
        // Инкрементальность: сперва снимаем всё, что порождали эти файлы раньше, затем
        // пишем заново. Полная переиндексация и обновление одного файла идут одним путём.
        await store.DeleteBySourceFilesAsync(batch.TouchedFiles, token);
        await store.UpsertAsync(batch, token);
        written += batch.Nodes.Count;
    }, ct);

    // Полнотекстовый индекс наполняется асинхронно — без ожидания первый поиск пуст.
    await store.EnsureSchemaAsync(ct);

    var elapsed = Stopwatch.GetElapsedTime(started);
    AnsiConsole.MarkupLine(
        $"[green]готово[/] за {elapsed.TotalSeconds:0.0} с: {report.Projects} проектов, " +
        $"{report.Documents} файлов, {report.Nodes} узлов, {report.Edges} рёбер");

    foreach (var error in report.Errors)
        AnsiConsole.MarkupLine($"[yellow]  ! {Markup.Escape(error)}[/]");

    await RunStatsAsync(ct);
    return report.Errors.Count == 0 ? 0 : 2;
}

async Task<int> RunStatsAsync(CancellationToken ct)
{
    var stats = await store.GetStatsAsync(ct);

    var table = new Table().Border(TableBorder.Rounded)
        .Title("[blue]граф кода[/]")
        .AddColumn("узлы").AddColumn("[grey]шт[/]").AddColumn("рёбра").AddColumn("[grey]шт[/]");

    var nodes = stats.NodesByKind.OrderByDescending(p => p.Value).ToList();
    var edges = stats.EdgesByKind.OrderByDescending(p => p.Value).ToList();

    for (var i = 0; i < Math.Max(nodes.Count, edges.Count); i++)
    {
        table.AddRow(
            i < nodes.Count ? nodes[i].Key : "",
            i < nodes.Count ? nodes[i].Value.ToString() : "",
            i < edges.Count ? edges[i].Key : "",
            i < edges.Count ? edges[i].Value.ToString() : "");
    }

    table.AddRow("[bold]всего[/]", $"[bold]{stats.Nodes}[/]", "[bold]всего[/]", $"[bold]{stats.Edges}[/]");
    AnsiConsole.Write(table);
    return 0;
}

async Task<int> RunSearchAsync(CliOptions cli, CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(cli.Query)) return Fail("не задан запрос: search \"текст\"");

    var retriever = host.Services.GetRequiredService<GraphRetriever>();
    var started = Stopwatch.GetTimestamp();
    var results = await retriever.SearchAsync(cli.Query, cli.Limit, ct);
    var elapsed = Stopwatch.GetElapsedTime(started);

    AnsiConsole.Write(new Rule($"[bold]{Markup.Escape(cli.Query)}[/]").LeftJustified());

    if (results.Count == 0)
    {
        AnsiConsole.MarkupLine("[red]ничего не найдено[/]");
        return 0;
    }

    var table = new Table().Border(TableBorder.Rounded)
        .AddColumn("[grey]#[/]").AddColumn("[grey]score[/]").AddColumn("[grey]вид[/]")
        .AddColumn("[grey]символ[/]").AddColumn("[grey]место[/]").AddColumn("[grey]почему подключён[/]");

    for (var i = 0; i < results.Count; i++)
    {
        var r = results[i];
        table.AddRow(
            (i + 1).ToString(),
            r.Score.ToString("0.000"),
            Markup.Escape(r.Kind),
            Markup.Escape(Shorten(r.Fqn, 46)),
            Markup.Escape($"{Path.GetFileName(r.FilePath ?? "?")}:{r.StartLine}"),
            Markup.Escape(r.Rationale));
    }

    AnsiConsole.Write(table);
    AnsiConsole.MarkupLine($"[grey]{results.Count} фрагментов за {elapsed.TotalMilliseconds:0} мс[/]");

    if (!cli.Verbose) return 0;

    foreach (var r in results.Take(3))
    {
        AnsiConsole.Write(new Rule($"[green]{Markup.Escape(r.Fqn)}[/]").LeftJustified());
        // Console, а не AnsiConsole: тело фрагмента — исходный код, и фигурные скобки
        // в нём AnsiConsole принимает за шаблон форматирования и падает.
        Console.WriteLine(r.Snippet.Length > 700 ? r.Snippet[..700] + "…" : r.Snippet);
    }

    return 0;
}

static int Fail(string message)
{
    AnsiConsole.MarkupLine($"[red]{Markup.Escape(message)}[/]");
    return 1;
}

static string Shorten(string value, int max) =>
    value.Length <= max ? value : "…" + value[^(max - 1)..];
