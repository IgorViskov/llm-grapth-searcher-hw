namespace LLmSeracher.Core.Llm;

public sealed class LlmOptions
{
    /// <summary>auto — OpenAI, если задан ключ, иначе оффлайн-заглушка; openai; fake.</summary>
    public string Provider { get; set; } = "auto";

    /// <summary>Ключ OpenAI. Берётся из user-secrets или переменной окружения OPENAI_API_KEY.</summary>
    public string? ApiKey { get; set; }

    public string Model { get; set; } = "gpt-4o-mini";

    /// <summary>Модель для вспомогательных агентов (сжатие контекста) — дешевле и быстрее.</summary>
    public string UtilityModel { get; set; } = "gpt-4o-mini";

    /// <summary>Задержка между словами в оффлайн-заглушке, мс. Делает streaming видимым глазом.</summary>
    public int FakeDelayMs { get; set; } = 35;

    public bool UseOpenAi => Provider.ToLowerInvariant() switch
    {
        "openai" => true,
        "fake" => false,
        _ => !string.IsNullOrWhiteSpace(ApiKey)
    };
}
