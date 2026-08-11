using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LLmSeracher.Core.Context;

/// <summary>
/// Объединяет несколько источников: опрашивает их параллельно, дедуплицирует по
/// <see cref="ContextChunk.Key"/> и отдаёт top-N.
///
/// Слияние идёт по Reciprocal Rank Fusion, а не по сырому <see cref="ContextChunk.Score"/>:
/// у файлового источника это доля совпавших токенов, у графа — результат ранжирования обхода,
/// у внешнего API — вообще что угодно. Величины несопоставимы по шкале, и сортировка по ним
/// систематически отдавала бы предпочтение источнику с самой щедрой метрикой. RRF смотрит
/// только на позицию в выдаче своего источника и от шкалы не зависит.
/// </summary>
public sealed class CompositeContextProvider : IContextProvider
{
    /// <summary>Сглаживающая константа RRF; 60 — общепринятое значение из исходной работы.</summary>
    private const double RrfK = 60.0;

    private readonly IReadOnlyList<IContextProvider> _providers;
    private readonly ContextOptions _options;
    private readonly ILogger<CompositeContextProvider> _logger;

    public string Name => "composite";

    public CompositeContextProvider(
        IEnumerable<IContextProvider> providers,
        IOptions<ContextOptions> options,
        ILogger<CompositeContextProvider> logger)
    {
        _providers = providers.ToArray();
        _options = options.Value;
        _logger = logger;
    }

    public async IAsyncEnumerable<ContextChunk> SearchAsync(
        string query, int limit, [EnumeratorCancellation] CancellationToken ct)
    {
        var harvested = new ConcurrentBag<IReadOnlyList<ContextChunk>>();

        await Parallel.ForEachAsync(_providers, ct, async (provider, token) =>
        {
            var collected = new List<ContextChunk>();
            try
            {
                await foreach (var chunk in provider.SearchAsync(query, limit, token))
                {
                    if (chunk.Score < _options.MinScore) continue;
                    collected.Add(chunk);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Отказ одного источника не должен ронять запрос: остальные уже собрали своё.
                _logger.LogWarning(ex, "Источник '{Source}' отказал — продолжаем без него", provider.Name);
            }

            harvested.Add(collected);
        });

        var fused = new Dictionary<string, double>(StringComparer.Ordinal);
        var chunks = new Dictionary<string, ContextChunk>(StringComparer.Ordinal);

        foreach (var fromOneSource in harvested)
        {
            var ranked = fromOneSource.OrderByDescending(c => c.Score).ToList();
            for (var rank = 0; rank < ranked.Count; rank++)
            {
                var chunk = ranked[rank];
                fused[chunk.Key] = fused.GetValueOrDefault(chunk.Key) + 1.0 / (RrfK + rank + 1);

                // Дедупликация: при совпадении ключа оставляем фрагмент от источника,
                // считающего его более релевантным.
                if (!chunks.TryGetValue(chunk.Key, out var kept) || kept.Score < chunk.Score)
                    chunks[chunk.Key] = chunk;
            }
        }

        if (fused.Count == 0) yield break;

        // Нормировка на максимум возвращает скор в привычный диапазон 0..1 — его печатает
        // консоль и по нему же отсекает вызывающая сторона.
        var max = fused.Values.Max();

        // Ранжирование возможно только после того, как отработали все источники:
        // ради корректного top-N здесь сознательно жертвуем ранней выдачей.
        foreach (var (key, score) in fused.OrderByDescending(p => p.Value).Take(limit))
            yield return chunks[key] with { Score = Math.Round(score / max, 3) };
    }
}
