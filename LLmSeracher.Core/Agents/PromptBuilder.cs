using System.Text;
using LLmSeracher.Core.A2A;
using LLmSeracher.Core.Context;

namespace LLmSeracher.Core.Agents;

/// <summary>
/// Собирает промпт из подключённого контекста. Формат блока контекста — единственное место,
/// где зафиксирована нумерация источников: её же использует и агент-суммаризатор,
/// и оффлайн-заглушка LLM, и итоговый список ссылок в консоли.
/// </summary>
public static class PromptBuilder
{
    /// <summary>
    /// Пронумерованный блок контекста:
    /// <code>
    /// [1] Заголовок — files
    /// текст фрагмента
    /// </code>
    /// </summary>
    public static string RenderContextBlock(IReadOnlyList<ContextChunk> chunks)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < chunks.Count; i++)
        {
            builder.Append('[').Append(i + 1).Append("] ")
                   .Append(chunks[i].Title).Append(" — ").Append(chunks[i].SourceId).Append('\n')
                   .Append(chunks[i].Text.Trim()).Append("\n\n");
        }

        return builder.ToString().TrimEnd();
    }

    public static IReadOnlyList<SourceRef> BuildSources(IReadOnlyList<ContextChunk> chunks) =>
        chunks.Select((c, i) => new SourceRef(i + 1, c.SourceId, c.Title)).ToArray();

    public static string BuildAnswerSystemPrompt(string contextBlock) =>
        $"""
         Ты — ассистент службы поддержки интернет-магазина «Орбита».

         Правила:
         1. Отвечай ТОЛЬКО на основе блока КОНТЕКСТ ниже. Внешние знания не используй.
         2. Если ответа в контексте нет — прямо скажи об этом и предложи, что уточнить.
         3. После каждого фактического утверждения ставь ссылку на источник: [1], [2].
         4. Пиши по-русски, кратко, без воды. Списки — маркированные.

         ### КОНТЕКСТ
         {contextBlock}
         """;

    public static string BuildSummarySystemPrompt(int budgetChars) =>
        $"""
         Ты — агент-суммаризатор в цепочке агентов. На вход приходит блок контекста
         с пронумерованными фрагментами.

         Верни тот же блок в ТОЧНО ТОМ ЖЕ формате:
         [n] Заголовок — источник
         сжатый текст

         Требования:
         - сохрани нумерацию, заголовки и названия источников без изменений;
         - сожми тела фрагментов, оставив только факты, числа, сроки и условия;
         - уложись примерно в {budgetChars} символов суммарно;
         - ничего не добавляй от себя и не пиши комментариев вне блока.
         """;
}
