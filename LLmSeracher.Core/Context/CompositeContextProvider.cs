using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace LLmSeracher.Core.Context;

/// <summary>
/// Объединяет несколько источников: опрашивает их параллельно, сливает выдачу в один поток,
/// дедуплицирует по <see cref="ContextChunk.Key"/> и отдаёт top-N по релевантности.
/// </summary>
public sealed class CompositeContextProvider : IContextProvider
{
    private readonly IReadOnlyList<IContextProvider> _providers;
    private readonly ContextOptions _options;

    public string Name => "composite";

    public CompositeContextProvider(IEnumerable<IContextProvider> providers, IOptions<ContextOptions> options)
    {
        _providers = providers.ToArray();
        _options = options.Value;
    }

    public async IAsyncEnumerable<ContextChunk> SearchAsync(
        string query, int limit, [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<ContextChunk>();

        var pump = Task.Run(async () =>
        {
            try
            {
                await Parallel.ForEachAsync(_providers, ct, async (provider, token) =>
                {
                    await foreach (var chunk in provider.SearchAsync(query, limit, token))
                        await channel.Writer.WriteAsync(chunk, token);
                });
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        }, ct);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var collected = new List<ContextChunk>();

        await foreach (var chunk in channel.Reader.ReadAllAsync(ct))
        {
            if (chunk.Score < _options.MinScore) continue;
            if (!seen.Add(chunk.Key)) continue;
            collected.Add(chunk);
        }

        await pump;

        // Ранжирование возможно только после того, как отработали все источники:
        // ради корректного top-N здесь сознательно жертвуем ранней выдачей.
        foreach (var chunk in collected.OrderByDescending(c => c.Score).Take(limit))
            yield return chunk;
    }
}
