using LLmSeracher.Core.Context;

namespace LLmSeracher.Core.A2A;

/// <summary>
/// Задача, передаваемая агенту. Один и тот же тип используется и при вызове в процессе,
/// и как тело POST /a2a/tasks/{agentId}.
/// </summary>
/// <param name="TaskId">Идентификатор задачи, уникален для одного вызова.</param>
/// <param name="Skill">Какой навык из карточки агента вызывается.</param>
/// <param name="Query">Запрос пользователя или инструкция от агента-инициатора.</param>
/// <param name="ConversationId">Сквозной идентификатор диалога — связывает цепочку делегирований.</param>
/// <param name="Context">Контекст, передаваемый вместе с задачей: агент-получатель не ищет его заново.</param>
/// <param name="Delegation">Подписанный токен полномочий (ACP/AP2-lite).</param>
public sealed record AgentTask(
    string TaskId,
    string Skill,
    string Query,
    string ConversationId,
    IReadOnlyList<ContextChunk>? Context = null,
    string? Delegation = null)
{
    public static AgentTask Create(string skill, string query, string? conversationId = null) => new(
        TaskId: Guid.NewGuid().ToString("n")[..12],
        Skill: skill,
        Query: query,
        ConversationId: conversationId ?? Guid.NewGuid().ToString("n")[..12]);
}
