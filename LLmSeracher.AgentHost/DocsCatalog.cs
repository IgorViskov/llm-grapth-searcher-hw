using System.Runtime.CompilerServices;
using LLmSeracher.Core.Context;

namespace LLmSeracher.AgentHost;

/// <summary>
/// Источник контекста «внешний API»: внутренние регламенты, которых нет в файловой базе знаний.
/// В реальном стенде здесь была бы чужая система; для демо достаточно каталога в памяти.
/// </summary>
public static class DocsCatalog
{
    private static readonly Document[] Documents =
    [
        new("reg-sc-14", "Регламент СЦ-14: ускоренная диагностика",
            """
            Для заказов дороже 100 000 рублей срок диагностики в сервисном центре сокращён
            с 10 до 3 рабочих дней. Клиенту отправляется SMS с номером ремонта в день приёма.
            Если диагностика превысила 3 дня, оператор обязан предложить подменный аппарат
            без заявления.
            """),

        new("ord-2026-11", "Приказ 2026-11: продление сроков возврата в праздники",
            """
            Для заказов, оформленных с 15 декабря по 10 января, срок возврата товара
            надлежащего качества продлевается с 14 до 30 календарных дней.
            Продление действует автоматически и не требует обращения в поддержку.
            На технику, купленную в рассрочку, продление также распространяется.
            """),

        new("ins-comp-07", "Инструкция КОМП-07: компенсации за просрочку доставки",
            """
            Помимо автоматических 500 бонусных рублей оператор вправе назначить денежную
            компенсацию 5% от стоимости заказа, если задержка превысила 3 суток.
            Компенсация оформляется заявкой в биллинг и выплачивается в течение 7 рабочих дней.
            Повторная задержка по тому же заказу удваивает компенсацию.
            """),

        new("faq-int-22", "Внутренний FAQ-22: бонусы и рассрочка",
            """
            Бонусные рубли, начисленные как компенсация, не сгорают и не ограничены
            лимитом 30% от суммы заказа — в отличие от обычных бонусов.
            При оплате рассрочки бонусами закрывается только первый платёж.
            """)
    ];

    /// <summary>
    /// Отдаёт документы по одному с небольшой паузой: клиент видит, что источник контекста
    /// тоже потоковый, а не отвечает одним куском.
    /// </summary>
    public static async IAsyncEnumerable<DocDto> SearchAsync(
        string query, int limit, [EnumeratorCancellation] CancellationToken ct)
    {
        var queryTokens = Tokenizer.Tokenize(query);

        var ranked = Documents
            .Select(doc => (doc, score: Tokenizer.Score(queryTokens, doc.Tokens)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .Take(limit);

        foreach (var (doc, score) in ranked)
        {
            await Task.Delay(40, ct);
            yield return new DocDto(doc.Id, doc.Title, doc.Text, Math.Round(score, 3));
        }
    }

    private sealed record Document
    {
        public Document(string id, string title, string text)
        {
            Id = id;
            Title = title;
            Text = text;
            Tokens = Tokenizer.Tokenize($"{title}\n{text}");
        }

        public string Id { get; }
        public string Title { get; }
        public string Text { get; }
        public IReadOnlyList<string> Tokens { get; }
    }
}

public sealed record DocDto(string Id, string Title, string Text, double Score);
