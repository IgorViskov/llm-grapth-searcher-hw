# LLM Searcher — контекст и streaming в сети агентов

Домашнее задание: подключение внешнего источника контекста к агенту и потоковая выдача
ответа. Сверх минимума реализована сеть из трёх агентов, связанных по A2A, с делегированием
полномочий в стиле ACP/AP2.

Описание архитектуры — [ARCHITECTURE.md](ARCHITECTURE.md).

## Что здесь происходит

```
вопрос → SearchAgent ──A2A──> RetrieverAgent  → knowledge/*.md + GET /api/docs
                    ──A2A──> SummarizerAgent → сжатие контекста (потоком)
                    → LLM (streaming) → токены в консоль по мере генерации
```

- **источник контекста** — два: файлы `knowledge/*.md` и HTTP API `GET /api/docs?q=`;
- **streaming** — сквозной: LLM → агент → SSE по сети → консоль, без промежуточной буферизации;
- **A2A** — задачи и контекст передаются между агентами как `AgentTask` / `AgentEvent`;
- **делегирование** — каждый межагентный вызов несёт подписанный токен с ограниченным scope.

## Требования

.NET SDK 10.0. Ключ OpenAI не обязателен: без него включается оффлайн-заглушка LLM,
и сценарий воспроизводится полностью.

## Запуск

Терминал 1 — хост агентов (`retriever`, `summarizer`, API документов):

```bash
dotnet run --project LLmSeracher.AgentHost
```

Терминал 2 — консольный клиент с агентом-оркестратором:

```bash
dotnet run --project LLmSeracher -- "Сколько дней на возврат товара и что с праздничными заказами?"
```

Всё в одном процессе, без хоста и сети:

```bash
dotnet run --project LLmSeracher -- --local "Действует ли гарантия при неоригинальной зарядке?"
```

## Сценарии для демонстрации

| Команда | Что показывает |
|---|---|
| `dotnet run --project LLmSeracher -- --demo` | три вопроса подряд: контекст, делегирование, streaming |
| `dotnet run --project LLmSeracher -- --acl-demo` | отказ агента без токена, успех с токеном, отказ при чужом scope |
| `dotnet run --project LLmSeracher` | интерактивный режим; **Ctrl+C** прерывает генерацию, не убивая приложение |
| `dotnet run --project LLmSeracher -- --local` | та же логика агентов без сети — доказательство, что транспорт подменяем |

Ключи: `--host <url>` — адрес хоста агентов, `--help` — справка.

## Ключ OpenAI

```bash
dotnet user-secrets --project LLmSeracher set "Llm:ApiKey" "sk-..."
```

Либо переменная окружения `OPENAI_API_KEY`. Модель задаётся в `appsettings.json`
(`Llm:Model`, по умолчанию `gpt-4o-mini`). Без ключа `Llm:Provider=auto` переключается
на `FakeChatClient`: он не ходит в сеть, но проходит ровно тот же потоковый конвейер.

## Структура

```
knowledge/                    база знаний (*.md) — источник контекста №1
LLmSeracher.Core/             контракты и реализации
  Context/                    IContextProvider: файлы, HTTP API, композит
  Llm/                        IChatClient: OpenAI и оффлайн-заглушка
  A2A/                        AgentCard, AgentTask, AgentEvent, транспорты, делегирование
  Agents/                     SearchAgent, RetrieverAgent, SummarizerAgent, PromptBuilder
LLmSeracher.AgentHost/        ASP.NET Core: карточки агентов, приём задач (SSE), /api/docs
LLmSeracher/                  консольный клиент и сценарии
```

## Соответствие критериям задания

| Критерий | Где смотреть |
|---|---|
| реализована передача контекста | `Context/*`, событие `ContextAttachedEvent` — таблица источников печатается до ответа |
| работает streaming ответа | `IAsyncEnumerable<AgentEvent>` от `IChatClient` до консоли; по сети — SSE |
| сценарий воспроизводим | `--demo` и `--local` работают без ключа и без внешних сервисов |
| зафиксирована архитектура | [ARCHITECTURE.md](ARCHITECTURE.md) |
