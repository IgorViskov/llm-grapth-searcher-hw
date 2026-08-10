using System.Runtime.CompilerServices;

namespace LLmSeracher.Core.A2A;

public static class AgentStream
{
    /// <summary>
    /// Оборачивает поток событий агента так, чтобы исключение в середине стрима превращалось
    /// в <see cref="FailedEvent"/>, а не рвало соединение: получатель уже мог показать часть
    /// ответа, и ему важно узнать причину обрыва в том же канале.
    /// Отмену пропускаем наружу — это не ошибка, а штатное завершение.
    /// </summary>
    public static async IAsyncEnumerable<AgentEvent> Guarded(
        string agentId,
        IAsyncEnumerable<AgentEvent> source,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var enumerator = source.GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                AgentEvent? current = null;
                string? error = null;

                try
                {
                    if (await enumerator.MoveNextAsync()) current = enumerator.Current;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }

                if (error is not null)
                {
                    yield return new FailedEvent(agentId, error);
                    break;
                }

                if (current is null) break;
                yield return current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }
}
