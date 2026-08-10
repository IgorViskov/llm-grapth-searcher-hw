using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace LLmSeracher.Core.Llm;

/// <summary>
/// Оффлайн-реализация <see cref="IChatClient"/>. Никуда не ходит: собирает ответ по шаблону
/// из контекста, который агент положил в промпт, и отдаёт его словами с задержкой.
/// Смысл — сценарий остаётся полностью воспроизводимым без ключа OpenAI, а весь потоковый
/// конвейер (агент → SSE → консоль) работает ровно тот же самый.
/// </summary>
public sealed partial class FakeChatClient : IChatClient
{
    private readonly LlmOptions _options;

    public FakeChatClient(IOptions<LlmOptions> options) => _options = options.Value;

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var text = new StringBuilder();
        await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken))
            text.Append(update.Text);

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text.ToString()));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        var system = LastText(list, ChatRole.System);
        var user = LastText(list, ChatRole.User);

        // Роль заглушки определяется по системному промпту: тот же клиент обслуживает
        // и агента-ответчика, и агента-суммаризатора.
        var answer = system.Contains("агент-суммаризатор", StringComparison.Ordinal)
            ? ComposeSummary(user)
            : ComposeAnswer(user, system);

        foreach (var word in SplitKeepingWhitespace(answer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_options.FakeDelayMs > 0)
                await Task.Delay(_options.FakeDelayMs, cancellationToken);

            yield return new ChatResponseUpdate(ChatRole.Assistant, word);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }

    private string ComposeAnswer(string question, string system)
    {
        var fragments = ParseFragments(system);
        var builder = new StringBuilder();
        builder.Append("[оффлайн-режим: ключ OpenAI не задан, ответ собран из подключённого контекста]\n\n");

        if (fragments.Count == 0)
        {
            builder.Append("В подключённом контексте нет фрагментов, релевантных запросу «")
                   .Append(question.Trim())
                   .Append("». Уточните формулировку или пополните базу знаний.");
            return builder.ToString();
        }

        builder.Append("По запросу «").Append(question.Trim()).Append("» в базе знаний найдено следующее:\n\n");

        foreach (var fragment in fragments)
        {
            builder.Append("- ").Append(fragment.Title).Append(": ")
                   .Append(Shorten(fragment.Body, 2))
                   .Append(" [").Append(fragment.Number).Append("]\n");
        }

        builder.Append("\nОтвет опирается только на перечисленные источники.");
        return builder.ToString();
    }

    /// <summary>Возвращает тот же блок контекста, но с укороченными телами фрагментов.</summary>
    private static string ComposeSummary(string contextBlock)
    {
        var fragments = ParseFragments(contextBlock);
        if (fragments.Count == 0) return contextBlock;

        var builder = new StringBuilder();
        foreach (var fragment in fragments)
        {
            builder.Append('[').Append(fragment.Number).Append("] ").Append(fragment.Header).Append('\n')
                   .Append(Shorten(fragment.Body, 1)).Append("\n\n");
        }

        return builder.ToString().TrimEnd();
    }

    private static List<Fragment> ParseFragments(string text)
    {
        var matches = SourceHeaderRegex().Matches(text);
        var result = new List<Fragment>(matches.Count);

        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index + matches[i].Length;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;

            var header = matches[i].Groups[2].Value.Trim();
            var title = header.Split(" — ", StringSplitOptions.TrimEntries)[0];

            result.Add(new Fragment(matches[i].Groups[1].Value, header, title, text[start..end].Trim()));
        }

        return result;
    }

    private static string Shorten(string text, int sentences)
    {
        var flat = string.Join(' ', text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                        .Select(line => line.TrimStart('-', '*', ' ')));
        var taken = flat.Split('.', StringSplitOptions.RemoveEmptyEntries)
                        .Take(sentences)
                        .Select(s => s.Trim())
                        .Where(s => s.Length > 0);

        var joined = string.Join(". ", taken);
        if (joined.Length == 0) return text.Trim();
        return joined.EndsWith('.') ? joined : joined + ".";
    }

    private static string LastText(List<ChatMessage> messages, ChatRole role) =>
        messages.LastOrDefault(m => m.Role == role)?.Text ?? string.Empty;

    /// <summary>Режет текст на «слово вместе с идущими за ним пробелами» — так поток выглядит естественно.</summary>
    private static IEnumerable<string> SplitKeepingWhitespace(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsWhiteSpace(text[i])) continue;

            while (i + 1 < text.Length && char.IsWhiteSpace(text[i + 1])) i++;
            yield return text[start..(i + 1)];
            start = i + 1;
        }

        if (start < text.Length) yield return text[start..];
    }

    private sealed record Fragment(string Number, string Header, string Title, string Body);

    [GeneratedRegex(@"^\[(\d+)\]\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex SourceHeaderRegex();
}
