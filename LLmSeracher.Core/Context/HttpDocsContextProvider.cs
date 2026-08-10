using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace LLmSeracher.Core.Context;

/// <summary>
/// Источник контекста №2 — внешний HTTP API (<c>GET /api/docs?q=...</c>).
/// Демонстрирует, что за интерфейсом <see cref="IContextProvider"/> может стоять
/// что угодно: своя выдача, чужая CMS, векторная база.
/// </summary>
public sealed class HttpDocsContextProvider : IContextProvider
{
    public const string HttpClientName = "docs-api";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpDocsContextProvider> _logger;

    public string Name => "docs-api";

    public HttpDocsContextProvider(IHttpClientFactory httpClientFactory, ILogger<HttpDocsContextProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async IAsyncEnumerable<ContextChunk> SearchAsync(
        string query, int limit, [EnumeratorCancellation] CancellationToken ct)
    {
        var http = _httpClientFactory.CreateClient(HttpClientName);
        var url = $"/api/docs?q={Uri.EscapeDataString(query)}&limit={limit}";

        IAsyncEnumerable<DocDto?>? stream = null;
        try
        {
            // Ответ читается как поток JSON-элементов: первые документы обрабатываются,
            // пока сервер ещё дописывает остальные.
            stream = http.GetFromJsonAsAsyncEnumerable<DocDto>(url, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "API-источник недоступен ({Url}) — продолжаем без него", url);
        }

        if (stream is null) yield break;

        var enumerator = stream.GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                DocDto? dto;
                try
                {
                    if (!await enumerator.MoveNextAsync()) break;
                    dto = enumerator.Current;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException
                                          || (ex is TaskCanceledException && !ct.IsCancellationRequested))
                {
                    // Недоступный внешний источник не должен ронять запрос: файловая база
                    // знаний остаётся, агент отвечает по ней.
                    _logger.LogWarning("API-источник недоступен ({Message}) — работаем без него", ex.Message);
                    break;
                }

                if (dto is null) continue;
                yield return new ContextChunk(Name, dto.Id, dto.Title, dto.Text, dto.Score);
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    private sealed record DocDto(string Id, string Title, string Text, double Score);
}
