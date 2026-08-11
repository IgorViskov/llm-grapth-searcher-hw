using LLmSeracher.Core.Context;

namespace LLmSeracher.Graph.Retrieval;

/// <summary>
/// Найденный фрагмент кода вместе с объяснением, почему он попал в выдачу.
/// <see cref="Rationale"/> — не украшение: путь в графе объясняет подключение фрагмента
/// и там, где векторный поиск не смог бы дать никакого обоснования.
/// </summary>
public sealed record CodeSearchResult(
    string SymbolId,
    string Kind,
    string Name,
    string Fqn,
    string? Signature,
    string? FilePath,
    int? StartLine,
    int? EndLine,
    string? DocComment,
    string Snippet,
    double Score,
    string Rationale,
    int Hops)
{
    public string Title => string.IsNullOrEmpty(Fqn) ? Name : Fqn;

    public ContextChunk ToChunk(string sourceId) => new(
        SourceId: sourceId,
        DocumentId: SymbolId,
        Title: Title,
        Text: BuildText(),
        Score: Math.Round(Score, 3))
    {
        Language = "csharp",
        FilePath = FilePath,
        StartLine = StartLine,
        EndLine = EndLine,
        SymbolId = SymbolId,
        Kind = Kind,
        Rationale = Rationale
    };

    private string BuildText()
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(DocComment))
            parts.Add(DocComment.Trim());

        parts.Add(Snippet.Trim());
        return string.Join("\n\n", parts);
    }
}
