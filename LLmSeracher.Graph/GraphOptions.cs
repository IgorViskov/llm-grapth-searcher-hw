namespace LLmSeracher.Graph;

public sealed class GraphOptions
{
    public string Uri { get; set; } = "bolt://localhost:7687";
    public string User { get; set; } = "neo4j";
    public string Password { get; set; } = "llmsearcher-local";

    /// <summary>
    /// Community Edition обслуживает единственную пользовательскую базу с именем <c>neo4j</c>;
    /// менять имеет смысл только на Enterprise.
    /// </summary>
    public string Database { get; set; } = "neo4j";

    /// <summary>Имя полнотекстового индекса по символам.</summary>
    public const string FullTextIndex = "symbol_text";
}
