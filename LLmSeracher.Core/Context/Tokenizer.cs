using System.Globalization;

namespace LLmSeracher.Core.Context;

/// <summary>
/// Примитивная нормализация текста для поиска: режем на слова, приводим к нижнему регистру,
/// отбрасываем стоп-слова и короткие токены. Полноценного стемминга здесь нет — для базы
/// знаний в несколько документов хватает совпадения по основе слова.
/// </summary>
public static class Tokenizer
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "и", "или", "но", "а", "в", "во", "на", "не", "что", "как", "для", "при", "по", "из",
        "за", "то", "же", "ли", "бы", "это", "быть", "есть", "если", "так", "уже", "ещё", "еще",
        "the", "a", "an", "of", "to", "in", "is", "are", "and", "or", "for", "on", "with"
    };

    public static IReadOnlyList<string> Tokenize(string text)
    {
        var result = new List<string>();
        var start = -1;

        for (var i = 0; i <= text.Length; i++)
        {
            var isWordChar = i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '-');
            if (isWordChar)
            {
                if (start < 0) start = i;
                continue;
            }

            if (start < 0) continue;

            var token = text[start..i].ToLower(CultureInfo.InvariantCulture);
            start = -1;

            if (token.Length < 3 || StopWords.Contains(token)) continue;
            result.Add(token);
        }

        return result;
    }

    /// <summary>
    /// Доля токенов запроса, нашедшихся в документе. Совпадением считается и вхождение по
    /// префиксу длиной 4+ символа — это дёшево компенсирует русскую морфологию
    /// ("возврата" ↔ "возврат").
    /// </summary>
    public static double Score(IReadOnlyList<string> queryTokens, IReadOnlyList<string> documentTokens)
    {
        if (queryTokens.Count == 0 || documentTokens.Count == 0) return 0;

        var matched = 0;
        foreach (var q in queryTokens)
        {
            foreach (var d in documentTokens)
            {
                if (d == q || (q.Length >= 4 && d.StartsWith(q[..4], StringComparison.Ordinal)))
                {
                    matched++;
                    break;
                }
            }
        }

        return (double)matched / queryTokens.Count;
    }
}
