using System.ClientModel;
using LLmSeracher.Core.A2A;
using LLmSeracher.Core.Agents;
using LLmSeracher.Core.Context;
using LLmSeracher.Core.Llm;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

        // Ключ из переменной окружения имеет приоритет над пустым значением в конфиге,
        // чтобы не хранить секрет в репозитории.
        services.PostConfigure<LlmOptions>(options =>
            options.ApiKey = string.IsNullOrWhiteSpace(options.ApiKey)
                ? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                : options.ApiKey);

        services.AddSingleton<DelegationService>();
        services.AddSingleton(CreateChatClient);

        return services;
    }

    /// <summary>Источники контекста: файлы + внешний API, объединённые композитом.</summary>
    public static IServiceCollection AddContextSources(this IServiceCollection services)
    {
        services.AddHttpClient(HttpDocsContextProvider.HttpClientName, (sp, http) =>
        {
            var options = sp.GetRequiredService<IOptions<ContextOptions>>().Value;
            http.BaseAddress = new Uri(options.DocsApiBaseUrl);
            http.Timeout = TimeSpan.FromSeconds(15);
        });

        services.AddSingleton<FileContextProvider>();
        services.AddSingleton<HttpDocsContextProvider>();

        // Композит регистрируется явно, а не через IEnumerable<IContextProvider>:
        // иначе он попал бы в собственную коллекцию источников и получился бы цикл.
        services.AddSingleton<IContextProvider>(sp => new CompositeContextProvider(
        [
            sp.GetRequiredService<FileContextProvider>(),
            sp.GetRequiredService<HttpDocsContextProvider>()
        ], sp.GetRequiredService<IOptions<ContextOptions>>()));

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

        var openAi = new OpenAIClient(new ApiKeyCredential(options.ApiKey!));
        return openAi.GetChatClient(options.Model).AsIChatClient();
    }
}
