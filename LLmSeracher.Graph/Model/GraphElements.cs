namespace LLmSeracher.Graph.Model;

/// <summary>
/// Узел графа перед записью. <see cref="SourceFile"/> — файл, при разборе которого узел
/// порождён; именно по нему работает инкрементальное удаление.
/// </summary>
public sealed class GraphNode
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string SourceFile { get; init; }

    /// <summary>
    /// Заглушка — символ, о котором мы узнали из чужого файла (внешняя сборка либо ещё
    /// не разобранный проект). Пишется через <c>ON CREATE SET</c>, чтобы не затирать
    /// настоящие свойства, когда до объявления символа дойдёт очередь.
    /// </summary>
    public bool IsStub { get; init; }

    public Dictionary<string, object> Props { get; } = [];

    public GraphNode Set(string key, object? value)
    {
        if (value is not null and not "") Props[key] = value;
        return this;
    }

    /// <summary>Плоская карта для UNWIND: свойства + служебные поля одним словарём.</summary>
    public Dictionary<string, object> ToParameters()
    {
        var props = new Dictionary<string, object>(Props)
        {
            ["id"] = Id,
            ["kind"] = Kind
        };

        // У заглушки sourceFile не выставляется: файл-владелец узнается при разборе объявления.
        if (!IsStub) props["sourceFile"] = SourceFile;

        return new Dictionary<string, object> { ["id"] = Id, ["props"] = props };
    }
}

/// <summary>Ребро графа. Владелец — файл, при разборе которого ребро найдено.</summary>
public sealed class GraphEdge
{
    public required string From { get; init; }
    public required string To { get; init; }
    public required string Kind { get; init; }
    public required string SourceFile { get; init; }

    public Dictionary<string, object> Props { get; } = [];

    public GraphEdge Set(string key, object? value)
    {
        if (value is not null and not "") Props[key] = value;
        return this;
    }

    public Dictionary<string, object> ToParameters() => new()
    {
        ["from"] = From,
        ["to"] = To,
        ["sourceFile"] = SourceFile,
        ["props"] = new Dictionary<string, object>(Props)
    };
}

/// <summary>Результат разбора: узлы и рёбра, сгруппированные по файлам-владельцам.</summary>
public sealed class GraphBatch
{
    public List<GraphNode> Nodes { get; } = [];
    public List<GraphEdge> Edges { get; } = [];

    /// <summary>Файлы, разобранные в этом проходе, — их прежние рёбра подлежат удалению.</summary>
    public HashSet<string> TouchedFiles { get; } = new(StringComparer.Ordinal);
}

public sealed record GraphStats(long Nodes, long Edges, IReadOnlyDictionary<string, long> NodesByKind,
    IReadOnlyDictionary<string, long> EdgesByKind);
