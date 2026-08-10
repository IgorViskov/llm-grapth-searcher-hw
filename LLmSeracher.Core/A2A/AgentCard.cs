namespace LLmSeracher.Core.A2A;

/// <summary>
/// «Визитка» агента — то, что он публикует по <c>/.well-known/agent-card.json</c>.
/// Вызывающая сторона по карточке понимает, что агент умеет и какие полномочия
/// требуется делегировать, не читая его код.
/// </summary>
public sealed record AgentCard(
    string Id,
    string Name,
    string Description,
    IReadOnlyList<AgentSkill> Skills,
    string Protocol = "a2a/1.0",
    bool Streaming = true);

/// <param name="RequiredScopes">Полномочия, которые должен нести делегирующий токен (ACP/AP2).</param>
public sealed record AgentSkill(
    string Id,
    string Description,
    IReadOnlyList<string> RequiredScopes);
