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
и сценарий воспроизводится полностью. Для графа кода — Docker.

## Граф кода

Основной источник контекста — граф кода в Neo4j: поиск идёт по нему, а не по файлам.
Анализ выбора БД и модели графа — [GRAPH-CONTEXT-ANALYSIS.md](GRAPH-CONTEXT-ANALYSIS.md).

Поднять БД:

```bash
docker compose up -d
```

Проиндексировать решение (первый запуск — с очисткой графа):

```bash
dotnet run --project LLmSeracher.Indexer -- index --reset
```

Дальше достаточно `index` без ключа: повторный прогон снимает рёбра изменившихся файлов
и записывает их заново, счётчики графа не растут. Проверить поиск без участия LLM:

```bash
dotnet run --project LLmSeracher.Indexer -- search "кто вызывает SearchAsync"
```

Браузер графа — <http://localhost:7474> (`neo4j` / `llmsearcher-local`).

Какие источники контекста включены, задаёт `Context:Sources` в `appsettings.json`:
`["code-graph"]` — только граф (по умолчанию), `["files", "docs-api"]` — markdown-справка
из `knowledge/`, под которую написаны `--demo`-вопросы про интернет-магазин.

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
| `dotnet run --project LLmSeracher -- --demo` | три вопроса к графу кода: обратные вызовы, реализации интерфейса через DI, «почему так сделано» |
| `dotnet run --project LLmSeracher -- --acl-demo` | отказ агента без токена, успех с токеном, отказ при чужом scope |
| `dotnet run --project LLmSeracher` | интерактивный режим; **Ctrl+C** прерывает генерацию, не убивая приложение |
| `dotnet run --project LLmSeracher -- --local` | та же логика агентов без сети — доказательство, что транспорт подменяем |

Ключи: `--host <url>` — адрес хоста агентов, `--help` — справка.

## Внешний потребитель сети

Агент `retriever` обслуживает не только консольный клиент. Соседний проект
[LLMAgent](../LLMAgent) — ревьюер, проверяющий изменения перед `git push`, — ставит ему
ту же задачу `context.search`, чтобы узнать, кто вызывает изменённые символы:

```
reviewer ──POST /a2a/agents/retriever/tasks (SSE)──> retriever ──> граф кода
```

Со стороны этого проекта ничего настраивать не нужно: достаточно поднятого `AgentHost`
и совпадающего `A2A:SigningSecret`. Ревьюер предъявляет такой же подписанный токен с
полномочием `context:read`, что и `SearchAgent`, — тем и проверяется, что A2A здесь
протокол, а не внутренний вызов между классами одного решения.

## Подключение модели

Настройки разнесены по трём местам — по тому, что можно коммитить:

| Ключ | Где задаётся | Что задаёт |
|---|---|---|
| `Llm:ApiKey` | user-secrets | ключ API |
| `Llm:ApiKeyEnvironmentVariable` | `Properties/launchSettings.json` | имя переменной окружения с ключом; по умолчанию `OPENAI_API_KEY` |
| `Llm:BaseUrl` | `Properties/launchSettings.json` | адрес OpenAI-совместимого API; пусто — официальный `api.openai.com` |
| `Llm:BypassProxy` | `appsettings.json` = `false`, в `launchSettings.json` = `true` | обращаться к модели мимо `HTTP_PROXY` / `HTTPS_PROXY` |
| `Llm:Model` | `appsettings.json` | основная модель — генерация ответа |
| `Llm:UtilityModel` | `appsettings.json` | модель агента-суммаризатора (сжатие контекста) |
| `Llm:Provider` | `appsettings.json` | `auto` (по умолчанию), `openai`, `fake` |

`launchSettings.json` обоих проектов исключён из гита: адрес модели и имя переменной с ключом
у каждого свои. В свежем клоне этих файлов нет — приложение поднимается на оффлайн-заглушке
и остаётся работоспособным. Чтобы подключить модель, создайте
`LLmSeracher/Properties/launchSettings.json` и `LLmSeracher.AgentHost/Properties/launchSettings.json`:

```json
{
  "profiles": {
    "LLmSeracher": {
      "commandName": "Project",
      "environmentVariables": {
        "Llm__ApiKeyEnvironmentVariable": "ACME_LLM_TOKEN",
        "Llm__BaseUrl": "http://localhost:1234/v1"
      }
    }
  }
}
```

Двойное подчёркивание — разделитель уровней конфигурации в переменных окружения:
`Llm__BaseUrl` читается как `Llm:BaseUrl`. Применяет эти переменные `dotnet run`;
при запуске собранного `.exe` напрямую `launchSettings.json` не участвует — тогда задавайте
`Llm__BaseUrl` в окружении сами.

**Ключ не хранить в `appsettings.json`** — он в репозитории:

```bash
dotnet user-secrets --project LLmSeracher set "Llm:ApiKey" "sk-..."
```

Переменная окружения с ключом читается, только если `Llm:ApiKey` пуст. Заданное имя
используется как единственное — отката на `OPENAI_API_KEY` нет, иначе опечатка молча уводила
бы приложение на чужой ключ. Что именно сработало, видно в первой строке вывода:

```
llm: gpt-4o-mini @ api.openai.com; ключ — переменная ACME_LLM_TOKEN
llm: оффлайн-заглушка; ключ — переменная ACME_LLM_TOKN пуста, адрес API не задан
```

Адрес API берётся и из переменной `OPENAI_BASE_URL` — это имя фиксированное.

**Настраивать нужно оба процесса.** Консольный клиент выполняет финальную генерацию,
хост агентов — сжатие контекста суммаризатором, поэтому `launchSettings.json` нужен обоим.
Ключ общий: у проектов один `UserSecretsId`, команда выше покрывает оба.

### OpenAI-совместимый API

`Llm:BaseUrl` указывается **вместе с версией пути**: SDK дописывает к нему только
`/chat/completions`.

| Сервис | `Llm:BaseUrl` | `Llm:Model` (пример) |
|---|---|---|
| LM Studio | `http://localhost:1234/v1` | `qwen2.5-coder-14b-instruct` |
| Ollama | `http://localhost:11434/v1` | `qwen2.5-coder:14b` |
| vLLM | `http://localhost:8000/v1` | путь к весам, как его отдаёт сервер |
| OpenRouter | `https://openrouter.ai/api/v1` | `anthropic/claude-sonnet-4.5` |

Локальные серверы ключ обычно не проверяют: достаточно указать `BaseUrl`, и `Provider=auto`
сам переключится с оффлайн-заглушки на реальную модель. Куда уходят запросы, видно в шапке
вывода: `модель: qwen2.5-coder-14b-instruct @ localhost:1234`.

Если не задано ни ключа, ни адреса, `Llm:Provider=auto` включает `FakeChatClient`: он не ходит
в сеть, но проходит ровно тот же потоковый конвейер.

Если в системе поднят локальный прокси (переменные `HTTP_PROXY` / `HTTPS_PROXY`), .NET пойдёт
через него и на адресе модели может получить обрыв TLS — на каждом запросе будет
`The SSL connection could not be established`. Лечится ключом `Llm:BypassProxy`: он
отключает прокси только для клиента модели, не трогая A2A и источники контекста.
По умолчанию `false`, включается в `launchSettings.json`:

```json
"Llm__BypassProxy": "true"
```

Вариант без правки конфигурации — добавить хост модели в переменную `NO_PROXY`.

## Структура

```
knowledge/                    база знаний (*.md) — markdown-источник контекста
docker-compose.yml            Neo4j для графа кода
LLmSeracher.Core/             контракты и реализации
  Context/                    IContextProvider: файлы, HTTP API, композит (слияние RRF)
  Llm/                        IChatClient: OpenAI и оффлайн-заглушка
  A2A/                        AgentCard, AgentTask, AgentEvent, транспорты, делегирование
  Agents/                     SearchAgent, RetrieverAgent, SummarizerAgent, PromptBuilder
LLmSeracher.Graph/            граф кода
  Model/                      виды узлов и рёбер, элементы батча
  Neo4jGraphStore.cs          схема, инкрементальный upsert, чтение
  Retrieval/                  каналы поиска, обход, ранжирование
  GraphContextProvider.cs     граф за тем же IContextProvider
LLmSeracher.Indexer/          разбор решения Roslyn'ом: index / stats / search
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
