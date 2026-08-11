using System.ClientModel;
using System.ClientModel.Primitives;
using LLmSeracher.Core.A2A;
using LLmSeracher.Core.Agents;
using LLmSeracher.Core.Context;
using LLmSeracher.Core.Llm;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;

namespace LLmSeracher.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Общая часть для всех узлов сети: опции, подпись делегирующих токенов, LLM-клиент.
    /// </summary>
    public static IServiceCollection AddSearcherCore(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<ContextOptions>(config.GetSection("Context"));
        services.Configure<A2AOptions>(config.GetSection("A2A"));
        services.Configure<AgentOptions>(config.GetSection("Agents"));
        services.Configure<LlmOptions>(config.GetSection("Llm"));

        // Переменные окружения подхватываются, только если в конфиге пусто, — чтобы не хранить
        // секрет в репозитории и при этом не ломать явную настройку в appsettings.
        services.PostConfigure<LlmOptions>(options =>
        {
            if (!string.IsNullOrWhiteSpace(options.ApiKey))
            {
                options.ApiKeySource = "конфигурация";
            }
            else
            {
                var variable = string.IsNullOrWhiteSpace(options.ApiKeyEnvironmentVariable)
                    ? LlmOptions.DefaultApiKeyVariable
                    : options.ApiKeyEnvironmentVariable.Trim();

                options.ApiKey = Environment.GetEnvironmentVariable(variable);
                options.ApiKeySource = string.IsNullOrWhiteSpace(options.ApiKey)
                    ? $"переменная {variable} пуста"
                    : $"переменная {variable}";
            }

            if (string.IsNullOrWhiteSpace(options.BaseUrl))
                options.BaseUrl = Environment.GetEnvironmentVariable("OPENAI_BASE_URL");
        });

        services.AddSingleton<DelegationService>();
        services.AddSingleton(CreateChatClient);

        return services;
    }

    /// <summary>
    /// Источники контекста: файлы + внешний API, объединённые композитом. Граф кода
    /// подключается отдельно, из проекта LLmSeracher.Graph — Core о нём не знает.
    /// </summary>
    public static IServiceCollection AddContextSources(this IServiceCollection services)
    {
        services.AddHttpClient(HttpDocsContextProvider.HttpClientName, (sp, http) =>
        {
            var options = sp.GetRequiredService<IOptions<ContextOptions>>().Value;
            http.BaseAddress = new Uri(options.DocsApiBaseUrl);
            http.Timeout = TimeSpan.FromSeconds(15);
        });

        // Листовые источники регистрируются под ключом: композит собирает именно их,
        // а сам публикуется как обычный IContextProvider — цикла не возникает.
        services.AddKeyedSingleton<IContextProvider, FileContextProvider>(ContextSources.LeafKey);
        services.AddKeyedSingleton<IContextProvider, HttpDocsContextProvider>(ContextSources.LeafKey);

        services.AddSingleton<IContextProvider>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ContextOptions>>();
            var enabled = options.Value.Sources;

            var leaves = sp.GetKeyedServices<IContextProvider>(ContextSources.LeafKey)
                .Where(p => enabled.Length == 0 || enabled.Contains(p.Name, StringComparer.OrdinalIgnoreCase));

            return new CompositeContextProvider(
                leaves, options, sp.GetRequiredService<ILogger<CompositeContextProvider>>());
        });

        return services;
    }

    /// <summary>Агенты, которые публикует хост.</summary>
    public static IServiceCollection AddHostedAgents(this IServiceCollection services)
    {
        services.AddContextSources();
        services.AddSingleton<IAgent, RetrieverAgent>();
        services.AddSingleton<IAgent, SummarizerAgent>();
        return services;
    }

    /// <summary>A2A-транспорт поверх HTTP — вызовы уходят на хост агентов.</summary>
    public static IServiceCollection AddHttpAgentTransport(this IServiceCollection services)
    {
        services.AddHttpClient(HttpAgentClient.HttpClientName, (sp, http) =>
        {
            var options = sp.GetRequiredService<IOptions<A2AOptions>>().Value;
            http.BaseAddress = new Uri(options.HostUrl);
            // Стрим живёт столько, сколько генерируется ответ, — обычный таймаут здесь мешает.
            http.Timeout = Timeout.InfiniteTimeSpan;
        });

        services.AddSingleton<IAgentClient, HttpAgentClient>();
        return services;
    }

    /// <summary>A2A-транспорт «в одном процессе»: те же агенты, но без сети.</summary>
    public static IServiceCollection AddInProcessAgentTransport(this IServiceCollection services)
    {
        services.AddHostedAgents();
        services.AddSingleton<IAgentClient>(sp => new InProcessAgentClient(sp.GetServices<IAgent>()));
        return services;
    }

    private static IChatClient CreateChatClient(IServiceProvider sp)
    {
        var accessor = sp.GetRequiredService<IOptions<LlmOptions>>();
        var options = accessor.Value;

        if (!options.UseOpenAi)
            return new FakeChatClient(accessor);

        // Локальные OpenAI-совместимые серверы ключ не проверяют, но SDK требует непустое
        // значение — подставляем заглушку, иначе клиент не создастся.
        var credential = new ApiKeyCredential(
            string.IsNullOrWhiteSpace(options.ApiKey) ? "no-key-required" : options.ApiKey);

        var clientOptions = new OpenAIClientOptions();
        if (!string.IsNullOrWhiteSpace(options.BaseUrl))
            clientOptions.Endpoint = new Uri(options.BaseUrl);

        // Подменяем транспорт целиком: у HttpClient прокси задаётся только на обработчике,
        // и другого способа отключить его для одного клиента нет. Затрагивает исключительно
        // запросы к модели — A2A и источники контекста ходят своими HttpClient'ами.
        if (options.BypassProxy)
            clientOptions.Transport = new HttpClientPipelineTransport(
                new HttpClient(new HttpClientHandler { UseProxy = false }));

        var openAi = new OpenAIClient(credential, clientOptions);

        // Модель здесь задаёт значение по умолчанию; агенты перекрывают её через
        // ChatOptions.ModelId — отвечающий берёт Model, суммаризатор UtilityModel.
        return openAi.GetChatClient(options.Model).AsIChatClient();
    }
}
