using LLmSeracher.Core.A2A;
using Spectre.Console;

namespace LLmSeracher.Cli;

/// <summary>
/// Печатает поток событий агента. Все ветки, кроме <see cref="TokenEvent"/>, — служебные:
/// они показывают, откуда взялся контекст и кто кому передал задачу. Токены пишутся
/// в консоль по мере поступления, без накопления в буфере — это и есть видимый streaming.
/// </summary>
internal sealed class ConsoleRenderer
{
    private bool _answerStarted;
    private int _tokenCount;

    public void Reset()
    {
        _answerStarted = false;
        _tokenCount = 0;
    }

    public void Question(string question, string transport, string model)
    {
        AnsiConsole.Write(new Rule($"[bold]{Markup.Escape(question)}[/]").LeftJustified());
        AnsiConsole.MarkupLine($"[grey]транспорт:[/] {Markup.Escape(transport)}   [grey]модель:[/] {Markup.Escape(model)}");
        AnsiConsole.WriteLine();
    }

    public void Handle(AgentEvent evt)
    {
        switch (evt)
        {
            case StatusEvent status:
                AnsiConsole.MarkupLine($"[grey]  · [[{Markup.Escape(status.AgentId)}]] {Markup.Escape(status.Message)}[/]");
                break;

            case DelegatedEvent delegated:
                AnsiConsole.MarkupLine(
                    $"[yellow]  → {Markup.Escape(delegated.FromAgentId)} делегирует «{Markup.Escape(delegated.Skill)}» агенту {Markup.Escape(delegated.ToAgentId)}[/]");
                AnsiConsole.MarkupLine(
                    $"[grey]    причина: {Markup.Escape(delegated.Reason)}; полномочия: {Markup.Escape(string.Join(", ", delegated.Scopes))}[/]");
                break;

            case ContextAttachedEvent attached:
                RenderContext(attached);
                break;

            case TokenEvent token:
                // Модели часто начинают ответ с пустых строк — заголовок «ответ» и первый
                // видимый текст не должны разъезжаться на пол-экрана.
                if (!_answerStarted && string.IsNullOrWhiteSpace(token.Text)) break;

                if (!_answerStarted)
                {
                    _answerStarted = true;
                    AnsiConsole.WriteLine();
                    AnsiConsole.Write(new Rule("[green]ответ (streaming)[/]").LeftJustified());
                }

                _tokenCount++;

                // Именно Console.Write, а не AnsiConsole.Write: последний трактует строку
                // как шаблон форматирования, и первая же фигурная скобка в потоке кода
                // (например `$"нет полномочия '{scope}'"`) роняет приложение
                // с FormatException прямо посреди ответа.
                Console.Write(token.Text);
                break;

            case CompletedEvent completed:
                RenderCompleted(completed);
                break;

            case FailedEvent failed:
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine($"[red]  ✖ [[{Markup.Escape(failed.AgentId)}]] {Markup.Escape(failed.Message)}[/]");
                break;
        }
    }

    private static void RenderContext(ContextAttachedEvent attached)
    {
        if (attached.Chunks.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]  контекст пуст[/]");
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title($"[blue]контекст, подключённый агентом {Markup.Escape(attached.AgentId)}[/]")
            .AddColumn("[grey]#[/]")
            .AddColumn("[grey]источник[/]")
            .AddColumn("[grey]фрагмент[/]")
            .AddColumn("[grey]score[/]");

        for (var i = 0; i < attached.Chunks.Count; i++)
        {
            var chunk = attached.Chunks[i];
            table.AddRow(
                (i + 1).ToString(),
                Markup.Escape(chunk.SourceId),
                Markup.Escape(Ellipsis(chunk.Title, 44)),
                chunk.Score.ToString("0.00"));
        }

        AnsiConsole.Write(table);
    }

    private void RenderCompleted(CompletedEvent completed)
    {
        if (!_answerStarted) return;

        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[grey]источники[/]").LeftJustified());

        foreach (var source in completed.Sources)
            AnsiConsole.MarkupLine($"[grey]  [[{source.Number}]] {Markup.Escape(source.Title)} — {Markup.Escape(source.SourceId)}[/]");

        AnsiConsole.MarkupLine(
            $"[grey]  {completed.ElapsedMs:0} мс, {_tokenCount} чанков потока, {completed.Text.Length} символов[/]");
        AnsiConsole.WriteLine();
    }

    private static string Ellipsis(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
