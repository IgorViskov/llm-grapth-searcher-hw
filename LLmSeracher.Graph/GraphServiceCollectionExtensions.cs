using LLmSeracher.Core.Context;
using LLmSeracher.Graph.Retrieval;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LLmSeracher.Graph;

public static class GraphServiceCollectionExtensions
{
    /// <summary>Граф как источник контекста: драйвер, ретривер, провайдер.</summary>
    public static IServiceCollection AddCodeGraph(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<GraphOptions>(config.GetSection("Graph"));
        services.Configure<RetrievalOptions>(config.GetSection("Retrieval"));

        services.AddSingleton<IGraphStore, Neo4jGraphStore>();
        services.AddSingleton<GraphRetriever>();
        services.AddSingleton<GraphContextProvider>();

        // Регистрация листовым источником — композит в Core подхватит его наравне
        // с файлами и HTTP-API, ничего о графе не зная.
        services.AddKeyedSingleton<IContextProvider>(
            ContextSources.LeafKey, (sp, _) => sp.GetRequiredService<GraphContextProvider>());

        return services;
    }

    /// <summary>Только доступ к графу, без источника контекста — для индексатора.</summary>
    public static IServiceCollection AddCodeGraphStore(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<GraphOptions>(config.GetSection("Graph"));
        services.AddSingleton<IGraphStore, Neo4jGraphStore>();
        return services;
    }
}
