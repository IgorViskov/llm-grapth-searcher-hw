using System.Text;
using LLmSeracher.Core.A2A;
using LLmSeracher.Core.Context;

namespace LLmSeracher.Core.Agents;

/// <summary>
/// Собирает промпт из подключённого контекста. Формат блока контекста — единственное место,
/// где зафиксирована нумерация источников: её же использует и агент-суммаризатор,
/// и оффлайн-заглушка LLM, и итоговый список ссылок в консоли.
///
/// Профиля два. «Справочный» обслуживает markdown-базу знаний, «кодовый» — граф кода:
/// у них разный системный промпт и разный рендер фрагмента. Выбор идёт по самому контексту,
/// отдельного переключателя в конфигурации нет.
/// </summary>
public static class PromptBuilder
{
    /// <summary>
    /// Пронумерованный блок контекста. Заголовок вида <c>[1] Название — источник</c>
    /// одинаков для обоих профилей: на него опираются и суммаризатор, и оффлайн-заглушка.
    /// Для кода под заголовком добавляется строка с местом в репозитории и обоснованием,
    /// а тело оборачивается в блок с указанием языка.
    /// </summary>
    public static string RenderContextBlock(IReadOnlyList<ContextChunk> chunks)
    {
        var builder = new StringBuilder();

        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];

            builder.Append('[').Append(i + 1).Append("] ")
                   .Append(chunk.Title).Append(" — ").Append(chunk.SourceId).Append('\n');

            if (chunk.IsCode)
            {
                var meta = new List<string>();
                if (chunk.Location is { } location) meta.Add(location);
                if (!string.IsNullOrWhiteSpace(chunk.Rationale)) meta.Add(chunk.Rationale);
                if (meta.Count > 0) builder.Append("// ").Append(string.Join(" · ", meta)).Append('\n');

                builder.Append("```").Append(chunk.Language ?? "text").Append('\n')
                       .Append(chunk.Text.Trim()).Append("\n```\n\n");
            }
            else
            {
                builder.Append(chunk.Text.Trim()).Append("\n\n");
            }
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Список источников для консоли. Для кода в заголовок подставляется место в репозитории —
    /// ссылка «[3]» без файла и строки в разговоре о коде бесполезна.
    /// </summary>
    public static IReadOnlyList<SourceRef> BuildSources(IReadOnlyList<ContextChunk> chunks) =>
        chunks.Select((c, i) => new SourceRef(
            i + 1,
            c.SourceId,
            c.Location is { } location ? $"{c.Title}  ({location})" : c.Title)).ToArray();

    /// <summary>
    /// Сохранил ли сжатый блок привязку «номер → фрагмент». Суммаризатору это предписано
    /// промптом, но живая модель предписание нарушает: перенумеровывает, склеивает или
    /// выбрасывает фрагменты. Номера при этом остаются в ответе — и указывают не туда,
    /// а список источников строится по исходному порядку. Молча получить неверные ссылки
    /// хуже, чем не сжать контекст, поэтому результат проверяется, а не принимается на веру.
    ///
    /// Проверяется именно связка номера с заголовком: одного лишь набора номеров мало —
    /// последовательность [1]..[N] сохранится и при перестановке фрагментов местами.
    /// </summary>
    public static bool PreservesNumbering(string summarized, IReadOnlyList<ContextChunk> chunks)
    {
        for (var i = 0; i < chunks.Count; i++)
        {
            if (!summarized.Contains($"[{i + 1}] {chunks[i].Title}", StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    /// <summary>Системный промпт под контекст, который реально подключился.</summary>
    public static string BuildAnswerSystemPrompt(string contextBlock, IReadOnlyList<ContextChunk> chunks) =>
        chunks.Count(c => c.IsCode) * 2 >= chunks.Count
            ? BuildCodeAnswerSystemPrompt(contextBlock)
            : BuildAnswerSystemPrompt(contextBlock);

    /// <summary>Справочный профиль: ответ по markdown-базе знаний.</summary>
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

    /// <summary>
    /// Кодовый профиль. Отличия от справочного существенны: контекст собран обходом графа,
    /// поэтому фрагменты связаны между собой, и об этих связях модели нужно сказать явно —
    /// иначе она читает выдачу как несвязанный набор кусков.
    /// </summary>
    public static string BuildCodeAnswerSystemPrompt(string contextBlock) =>
        $"""
         Ты — инженер, отвечающий на вопросы по конкретной кодовой базе.

         Контекст ниже собран из графа кода: фрагменты найдены по имени и описанию, а затем
         дополнены соседями по связям — вызовами, реализациями интерфейсов, регистрациями в DI.
         Строка после заголовка фрагмента говорит, где он лежит и почему подключён.
         Фрагмент «связи между подключёнными символами», если он есть, перечисляет рёбра графа
         между уже показанными символами — используй его, чтобы описывать поток управления.

         Правила:
         1. Отвечай ТОЛЬКО по блоку КОНТЕКСТ. Не додумывай код, которого в нём нет.
         2. Если для ответа не хватает фрагмента — прямо скажи, какого символа не видно.
         3. Ссылайся номером И местом в коде: «[2] Context/CompositeContextProvider.cs:44».
         4. Имена типов, методов и файлов пиши точно так, как они в контексте.
         5. Отвечай по-русски, кратко. Код цитируй только там, где он подтверждает утверждение.

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
         - если фрагмент — код, сохрани сигнатуры и ключевые строки, выбрось тела и обвязку;
         - уложись примерно в {budgetChars} символов суммарно;
         - ничего не добавляй от себя и не пиши комментариев вне блока.
         """;
}
