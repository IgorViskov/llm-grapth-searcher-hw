using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LLmSeracher.Core.A2A;

/// <summary>
/// A2A-транспорт поверх HTTP: задача уходит POST-ом, ответ читается как Server-Sent Events.
/// Ключевой момент — ответ не буферизуется: <see cref="HttpCompletionOption.ResponseHeadersRead"/>
/// плюс потоковый разбор SSE дают сквозной streaming от удалённого агента до консоли.
/// </summary>
public sealed class HttpAgentClient : IAgentClient
{
    public const string HttpClientName = "a2a";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpAgentClient> _logger;

    public string Endpoint { get; }

    public HttpAgentClient(
        IHttpClientFactory httpClientFactory,
        IOptions<A2AOptions> options,
        ILogger<HttpAgentClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        Endpoint = options.Value.HostUrl;
    }

    public async Task<AgentCard?> GetCardAsync(string agentId, CancellationToken ct)
    {
        var http = _httpClientFactory.CreateClient(HttpClientName);
        try
        {
            return await http.GetFromJsonAsync<AgentCard>(
                $"/a2a/agents/{agentId}/card", A2AJson.Options, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "Карточка агента {AgentId} недоступна на {Endpoint}", agentId, Endpoint);
            return null;
        }
    }

    public async IAsyncEnumerable<AgentEvent> SendAsync(
        string agentId, AgentTask task, [EnumeratorCancellation] CancellationToken ct)
    {
        var http = _httpClientFactory.CreateClient(HttpClientName);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/a2a/agents/{agentId}/tasks")
        {
            Content = JsonContent.Create(task, options: A2AJson.Options)
        };
        request.Headers.Accept.ParseAdd("text/event-stream");

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            yield return new FailedEvent(agentId, $"HTTP {(int)response.StatusCode}: {Trim(body)}");
            yield break;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var parser = SseParser.Create(stream, static (_, data) =>
            JsonSerializer.Deserialize<AgentEvent>(data, A2AJson.Options));

        await foreach (var item in parser.EnumerateAsync(ct))
        {
            if (item.Data is null) continue;
            yield return item.Data;
        }
    }

    private static string Trim(string value) =>
        value.Length <= 200 ? value : value[..200] + "…";
}
