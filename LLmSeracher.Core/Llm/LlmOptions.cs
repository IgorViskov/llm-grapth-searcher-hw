namespace LLmSeracher.Core.Llm;

public sealed class LlmOptions
{
    /// <summary>auto — OpenAI, если задан ключ, иначе оффлайн-заглушка; openai; fake.</summary>
    public string Provider { get; set; } = "auto";

    /// <summary>Ключ API. Если пуст — читается из переменной окружения, см. <see cref="ApiKeyEnvironmentVariable"/>.</summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Имя переменной окружения с ключом. Нужно, когда ключ уже лежит в переменной со своим
    /// именем — корпоративной, общей для нескольких сервисов или заданной оркестратором,
    /// и переименовывать её в <c>OPENAI_API_KEY</c> нельзя.
    /// Заданное здесь имя используется как единственное: тихого отката на имя по умолчанию
    /// нет, иначе опечатка в названии молча уводила бы приложение на чужой ключ.
    /// </summary>
    public string ApiKeyEnvironmentVariable { get; set; } = DefaultApiKeyVariable;

    public const string DefaultApiKeyVariable = "OPENAI_API_KEY";

    /// <summary>
    /// Откуда фактически взялся ключ. Заполняется при конфигурировании и печатается в консоль:
    /// без этого опечатка в имени переменной выглядит просто как молчаливый уход
    /// в оффлайн-заглушку, и причину не видно.
    /// </summary>
    public string ApiKeySource { get; set; } = "не задан";

    /// <summary>
    /// Адрес OpenAI-совместимого API. Пусто — официальный api.openai.com.
    /// Путь указывается целиком, вместе с версией: SDK дописывает к нему только
    /// <c>/chat/completions</c>. Примеры: <c>http://localhost:1234/v1</c> (LM Studio),
    /// <c>http://localhost:11434/v1</c> (Ollama), <c>https://openrouter.ai/api/v1</c>.
    /// Переменная окружения — <c>OPENAI_BASE_URL</c>.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Обращаться к модели в обход системного прокси (<c>HTTP_PROXY</c> / <c>HTTPS_PROXY</c>).
    /// По умолчанию выключено: обычная работа через корпоративный прокси не должна ломаться
    /// из-за настройки в коде. Включать, когда прокси рвёт TLS-рукопожатие до хоста модели —
    /// симптом: «The SSL connection could not be established» на каждом запросе.
    /// Альтернатива без перекомпиляции — добавить хост модели в переменную <c>NO_PROXY</c>.
    /// </summary>
    public bool BypassProxy { get; set; }

    /// <summary>Основная модель — генерация ответа.</summary>
    public string Model { get; set; } = "gpt-4o-mini";

    /// <summary>Модель для вспомогательных агентов (сжатие контекста) — дешевле и быстрее.</summary>
    public string UtilityModel { get; set; } = "gpt-4o-mini";

    /// <summary>Задержка между словами в оффлайн-заглушке, мс. Делает streaming видимым глазом.</summary>
    public int FakeDelayMs { get; set; } = 35;

    public bool UseOpenAi => Provider.ToLowerInvariant() switch
    {
        "openai" => true,
        "fake" => false,
        // Локальный OpenAI-совместимый сервер обычно не требует ключа: заданного адреса
        // достаточно, чтобы считать, что модель есть и заглушка не нужна.
        _ => !string.IsNullOrWhiteSpace(ApiKey) || !string.IsNullOrWhiteSpace(BaseUrl)
    };

    /// <summary>Короткая метка эндпоинта для консоли — видно, куда реально уходят запросы.</summary>
    public string EndpointLabel =>
        string.IsNullOrWhiteSpace(BaseUrl) ? "api.openai.com" : new Uri(BaseUrl).Authority;
}
