namespace LLmSeracher.Graph.Model;

/// <summary>
/// Вид узла. Хранится свойством <c>kind</c>, а не меткой: метка у всех узлов кода одна
/// (<c>:Symbol</c>), поэтому один полнотекстовый индекс и один constraint покрывают весь граф.
/// </summary>
public static class NodeKinds
{
    public const string Project = "Project";
    public const string File = "File";
    public const string Type = "Type";
    public const string Method = "Method";
    public const string Property = "Property";
    public const string Field = "Field";

    /// <summary>Символ из внешней сборки (NuGet, BCL): без тела, но участвует в рёбрах.</summary>
    public const string External = "External";

    /// <summary>Виды, которые имеет смысл отдавать в контекст как самостоятельный фрагмент.</summary>
    public static readonly string[] Retrievable = [Type, Method, Property, Field];
}

/// <summary>
/// Типы рёбер. Список закрытый: тип связи в Cypher нельзя передать параметром, поэтому
/// запросы собираются подстановкой имени — и подставлять можно только значение отсюда.
/// </summary>
public static class EdgeKinds
{
    public const string Contains = "CONTAINS";                  // Project → File
    public const string Declares = "DECLARES";                  // File → Type|Method|...
    public const string HasMember = "HAS_MEMBER";               // Type → Method|Property|Field
    public const string References = "REFERENCES";              // Project → Project|Package
    public const string Inherits = "INHERITS";                  // Type → Type
    public const string Implements = "IMPLEMENTS";              // Type → Interface
    public const string Overrides = "OVERRIDES";                // Method → Method
    public const string ImplementsMember = "IMPLEMENTS_MEMBER"; // Method → метод интерфейса
    public const string Calls = "CALLS";                        // Method → Method
    public const string Instantiates = "INSTANTIATES";          // Method → Type
    public const string Returns = "RETURNS";                    // Method → Type
    public const string HasParam = "HAS_PARAM";                 // Method → Type
    public const string RegisteredAs = "REGISTERED_AS";         // интерфейс → реализация (DI)

    public static readonly string[] All =
    [
        Contains, Declares, HasMember, References, Inherits, Implements,
        Overrides, ImplementsMember, Calls, Instantiates, Returns, HasParam, RegisteredAs
    ];

    private static readonly HashSet<string> Known = new(All, StringComparer.Ordinal);

    /// <summary>Защита от подстановки произвольной строки в текст запроса.</summary>
    public static string Validated(string kind) =>
        Known.Contains(kind) ? kind : throw new ArgumentOutOfRangeException(nameof(kind), kind, "неизвестный тип ребра");
}
