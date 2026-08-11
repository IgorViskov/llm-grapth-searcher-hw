using Spectre.Console;

namespace LLmSeracher.Indexer;

internal sealed class CliOptions
{
    public string Command { get; private init; } = "index";
    public string? SolutionPath { get; private init; }
    public string? RepoRoot { get; private init; }
    public string? Query { get; private init; }
    public int Limit { get; private init; } = 10;
    public bool Reset { get; private init; }
    public bool Verbose { get; private init; }
    public bool ShowHelp { get; private init; }

    public static CliOptions Parse(string[] args)
    {
        if (args.Length == 0) return new CliOptions { ShowHelp = true };

        var command = args[0].StartsWith('-') ? "index" : args[0];
        var rest = args[0].StartsWith('-') ? args : args[1..];

        string? solution = null, repoRoot = null, query = null;
        var limit = 10;
        bool reset = false, verbose = false, help = false;

        for (var i = 0; i < rest.Length; i++)
        {
            switch (rest[i])
            {
                case "--solution" when i + 1 < rest.Length: solution = rest[++i]; break;
                case "--repo-root" when i + 1 < rest.Length: repoRoot = rest[++i]; break;
                case "--limit" when i + 1 < rest.Length: limit = int.Parse(rest[++i]); break;
                case "--reset": reset = true; break;
                case "--verbose" or "-v": verbose = true; break;
                case "--help" or "-h": help = true; break;
                default:
                    if (!rest[i].StartsWith('-')) query = query is null ? rest[i] : $"{query} {rest[i]}";
                    break;
            }
        }

        return new CliOptions
        {
            Command = command,
            SolutionPath = solution,
            RepoRoot = repoRoot,
            Query = query,
            Limit = limit,
            Reset = reset,
            Verbose = verbose,
            ShowHelp = help
        };
    }

    /// <summary>Ищет .sln вверх по дереву — чтобы утилита работала из любого каталога решения.</summary>
    public static string? FindSolution()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            var solution = directory.GetFiles("*.sln").FirstOrDefault();
            if (solution is not null) return solution.FullName;
            directory = directory.Parent;
        }

        return null;
    }

    public static void PrintHelp() => AnsiConsole.Write(new Markup("""
        [bold]LLmSeracher.Indexer[/] — наполнение графа кода и проверка поиска по нему

        [grey]Команды:[/]
          index  [[--solution <path>]] [[--repo-root <path>]] [[--reset]]
                 разобрать решение Roslyn'ом и записать граф; --reset очищает граф перед разбором
          stats  показать состав графа
          search "<запрос>" [[--limit N]] [[-v]]
                 поиск по графу без участия LLM; -v печатает тела найденных фрагментов

        [grey]Примеры:[/]
          dotnet run --project LLmSeracher.Indexer -- index --reset
          dotnet run --project LLmSeracher.Indexer -- search "кто вызывает SearchAsync"
          dotnet run --project LLmSeracher.Indexer -- stats

        [grey]Адрес БД берётся из appsettings.json, секция Graph.[/]

        """));
}
