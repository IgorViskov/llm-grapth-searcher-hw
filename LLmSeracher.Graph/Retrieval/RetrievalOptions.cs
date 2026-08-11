using LLmSeracher.Graph.Model;

namespace LLmSeracher.Graph.Retrieval;

public sealed class RetrievalOptions
{
    /// <summary>Сколько узлов-точек входа брать из каждого канала до слияния.</summary>
    public int SeedLimit { get; set; } = 12;

    /// <summary>Сколько фрагментов максимум отдавать наружу.</summary>
    public int MaxChunks { get; set; } = 12;

    /// <summary>Бюджет на весь блок контекста; набор режется по нему, а не по числу фрагментов.</summary>
    public int MaxContextChars { get; set; } = 16000;

    /// <summary>Ограничение на тело одного фрагмента.</summary>
    public int MaxSnippetChars { get; set; } = 1800;

    /// <summary>Затухание веса за каждый шаг обхода.</summary>
    public double HopDecay { get; set; } = 0.55;

    /// <summary>Порог отсечки после ранжирования.</summary>
    public double MinScore { get; set; } = 0.02;

    /// <summary>
    /// Сколько точек входа брать из одного файла. Полное имя каждого члена содержит имя
    /// своего типа, поэтому запрос «что делает FooProvider» текстовым каналом вытягивает
    /// весь FooProvider целиком — конструктор, поля, свойства — и вытесняет из выдачи всё
    /// остальное, включая тех, кто этот тип вызывает.
    /// </summary>
    public int SeedsPerFile { get; set; } = 3;

    /// <summary>Сколько итоговых фрагментов максимум отдавать из одного файла.</summary>
    public int ChunksPerFile { get; set; } = 4;

    /// <summary>
    /// Вес члена типа, попавшего в точки входа. Выше единицы намеренно: у узла-типа в снимке
    /// только сигнатуры, содержательный код — в методах, и без них тип бесполезен как ответ.
    /// </summary>
    public double TypeMemberWeight { get; set; } = 1.6;

    /// <summary>Насколько придавить непубличные члены при подтягивании их из типа.</summary>
    public double NonPublicMemberFactor { get; set; } = 0.55;

    /// <summary>
    /// Вклад типа ребра в вес соседа. Нули отключают тип связи, не трогая индексацию:
    /// HAS_PARAM и RETURNS дают много шумных соседей и по умолчанию придавлены.
    /// </summary>
    public Dictionary<string, double> EdgeWeights { get; set; } = new(StringComparer.Ordinal)
    {
        [EdgeKinds.Calls] = 1.00,
        [EdgeKinds.ImplementsMember] = 0.95,
        [EdgeKinds.RegisteredAs] = 0.90,
        [EdgeKinds.Overrides] = 0.85,
        [EdgeKinds.HasMember] = 0.75,
        [EdgeKinds.Implements] = 0.70,
        [EdgeKinds.Inherits] = 0.70,
        [EdgeKinds.Instantiates] = 0.60,
        [EdgeKinds.Declares] = 0.35,
        [EdgeKinds.Returns] = 0.25,
        [EdgeKinds.HasParam] = 0.20,
        [EdgeKinds.References] = 0.15,
        [EdgeKinds.Contains] = 0.10
    };

    public IReadOnlyList<string> ActiveEdgeKinds =>
        EdgeWeights.Where(p => p.Value > 0).Select(p => p.Key).ToArray();

    public double WeightOf(string edgeKind) =>
        EdgeWeights.TryGetValue(edgeKind, out var w) ? w : 0.1;
}
