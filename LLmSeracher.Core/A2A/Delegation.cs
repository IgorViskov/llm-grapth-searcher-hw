using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace LLmSeracher.Core.A2A;

public sealed class A2AOptions
{
    /// <summary>Идентификатор текущего узла — попадает в поле issuer выдаваемых токенов.</summary>
    public string SelfId { get; set; } = "unknown";

    /// <summary>Адрес хоста агентов, к которому ходит <see cref="HttpAgentClient"/>.</summary>
    public string HostUrl { get; set; } = "http://localhost:5080";

    /// <summary>
    /// Общий секрет для подписи делегирующих токенов. В учебном стенде — из конфига;
    /// в продакшене здесь были бы асимметричные ключи и публикация JWKS.
    /// </summary>
    public string SigningSecret { get; set; } = "dev-only-shared-secret-change-me";

    /// <summary>Время жизни делегирующего токена.</summary>
    public TimeSpan DelegationLifetime { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Проверять ли полномочия у входящих задач. Выключается только для отладки —
    /// с включённой проверкой прямой curl-запрос к агенту получит отказ, и это часть демо.
    /// </summary>
    public bool RequireDelegation { get; set; } = true;
}

/// <summary>Полезная нагрузка делегирующего токена — урезанный до сути аналог ACP/AP2-мандата.</summary>
/// <param name="Issuer">Кто делегирует.</param>
/// <param name="Audience">Кому делегируют: токен, выданный одному агенту, не примет другой.</param>
/// <param name="TaskId">Токен привязан к конкретной задаче, а не выдан «вообще».</param>
/// <param name="Scopes">Что именно разрешено сделать.</param>
/// <param name="ExpiresAtUnix">Когда полномочия истекают.</param>
public sealed record DelegationPayload(
    string Issuer,
    string Audience,
    string TaskId,
    string ConversationId,
    IReadOnlyList<string> Scopes,
    long ExpiresAtUnix);

public sealed record DelegationResult(bool IsValid, string? Error, DelegationPayload? Payload)
{
    public static DelegationResult Ok(DelegationPayload payload) => new(true, null, payload);
    public static DelegationResult Fail(string error) => new(false, error, null);
}

/// <summary>
/// Выдача и проверка делегирующих токенов. Формат — base64url(payload).base64url(HMAC-SHA256),
/// то есть по сути подписанный мандат: получатель проверяет подпись, адресата,
/// срок и наличие требуемого scope, и только потом выполняет задачу.
/// </summary>
public sealed class DelegationService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly A2AOptions _options;

    public DelegationService(IOptions<A2AOptions> options) => _options = options.Value;

    public string Issue(string audience, AgentTask task, params string[] scopes)
    {
        var payload = new DelegationPayload(
            Issuer: _options.SelfId,
            Audience: audience,
            TaskId: task.TaskId,
            ConversationId: task.ConversationId,
            Scopes: scopes,
            ExpiresAtUnix: DateTimeOffset.UtcNow.Add(_options.DelegationLifetime).ToUnixTimeSeconds());

        var body = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        return $"{Base64Url(body)}.{Base64Url(Sign(body))}";
    }

    public DelegationResult Validate(string? token, string expectedAudience, string requiredScope)
    {
        if (string.IsNullOrWhiteSpace(token))
            return DelegationResult.Fail("делегирующий токен отсутствует");

        var parts = token.Split('.');
        if (parts.Length != 2)
            return DelegationResult.Fail("некорректный формат токена");

        byte[] body, signature;
        try
        {
            body = FromBase64Url(parts[0]);
            signature = FromBase64Url(parts[1]);
        }
        catch (FormatException)
        {
            return DelegationResult.Fail("некорректная кодировка токена");
        }

        if (!CryptographicOperations.FixedTimeEquals(signature, Sign(body)))
            return DelegationResult.Fail("подпись не совпадает");

        var payload = JsonSerializer.Deserialize<DelegationPayload>(body, Json);
        if (payload is null)
            return DelegationResult.Fail("пустая полезная нагрузка");

        if (!string.Equals(payload.Audience, expectedAudience, StringComparison.Ordinal))
            return DelegationResult.Fail($"токен выдан другому агенту ({payload.Audience})");

        if (DateTimeOffset.FromUnixTimeSeconds(payload.ExpiresAtUnix) < DateTimeOffset.UtcNow)
            return DelegationResult.Fail("срок действия полномочий истёк");

        if (!payload.Scopes.Contains(requiredScope, StringComparer.Ordinal))
            return DelegationResult.Fail($"нет полномочия '{requiredScope}'");

        return DelegationResult.Ok(payload);
    }

    private byte[] Sign(byte[] body) =>
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(_options.SigningSecret), body);

    private static string Base64Url(byte[] data) => Convert.ToBase64String(data)
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(normalized.PadRight((normalized.Length + 3) / 4 * 4, '='));
    }
}
