using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;

namespace LLmSeracher.Indexer.Extraction;

/// <summary>
/// Идентификация и человекочитаемое представление символов Roslyn.
///
/// Ключ узла — <see cref="ISymbol.GetDocumentationCommentId"/> вида
/// <c>M:Ns.Type.Method(System.String)</c>. Он не зависит от форматирования, переживает
/// перемещение файла и переименование проекта, поэтому MERGE по нему устойчив между сборками.
/// </summary>
internal static class SymbolNaming
{
    public const string Prefix = "csharp::";

    private static readonly SymbolDisplayFormat FqnFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private static readonly SymbolDisplayFormat SignatureFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters
                       | SymbolDisplayMemberOptions.IncludeType
                       | SymbolDisplayMemberOptions.IncludeModifiers
                       | SymbolDisplayMemberOptions.IncludeAccessibility,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType
                          | SymbolDisplayParameterOptions.IncludeName
                          | SymbolDisplayParameterOptions.IncludeParamsRefOut,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
                              | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    /// <summary>
    /// Идентификатор узла или <c>null</c>, если у символа нет стабильного имени —
    /// локальные функции, лямбды, анонимные типы. Такие в граф не попадают осознанно.
    /// </summary>
    public static string? IdOf(ISymbol symbol)
    {
        // OriginalDefinition сводит List<ContextChunk> к List<T>: конкретизация дженерика —
        // не отдельная сущность кодовой базы, и разводить их узлами вредно.
        var docId = symbol.OriginalDefinition.GetDocumentationCommentId();
        return string.IsNullOrEmpty(docId) || docId.StartsWith('!') ? null : Prefix + docId;
    }

    public static string Fqn(ISymbol symbol) => symbol.OriginalDefinition.ToDisplayString(FqnFormat);

    public static string Signature(ISymbol symbol) => symbol.OriginalDefinition.ToDisplayString(SignatureFormat);

    public static bool IsFromSource(ISymbol symbol) =>
        symbol.OriginalDefinition.Locations.Any(l => l.IsInSource);

    /// <summary>
    /// Плоский текст из XML-документации: summary, param, returns. Русские комментарии
    /// этой кодовой базы попадают в полнотекстовый индекс именно отсюда — и описательные
    /// вопросы по-русски работают благодаря им.
    /// </summary>
    public static string DocText(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml)) return string.Empty;

        XElement root;
        try
        {
            root = XElement.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var element in root.Elements())
        {
            var text = Flatten(element);
            if (text.Length == 0) continue;

            var name = element.Attribute("name")?.Value;
            builder.Append(name is null ? text : $"{name}: {text}").Append(' ');
        }

        return builder.ToString().Trim();
    }

    /// <summary>Текст элемента вместе с содержимым <c>see cref</c> — там лежат имена символов.</summary>
    private static string Flatten(XElement element)
    {
        var builder = new StringBuilder();

        foreach (var node in element.DescendantNodes())
        {
            switch (node)
            {
                case XText text:
                    builder.Append(text.Value);
                    break;
                case XElement { Name.LocalName: "see" or "seealso" } reference:
                    var cref = reference.Attribute("cref")?.Value;
                    if (cref is not null) builder.Append(' ').Append(LastSegment(cref)).Append(' ');
                    break;
            }
        }

        return string.Join(' ', builder.ToString()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string LastSegment(string cref)
    {
        var withoutPrefix = cref.Length > 2 && cref[1] == ':' ? cref[2..] : cref;
        var parenthesis = withoutPrefix.IndexOf('(');
        if (parenthesis > 0) withoutPrefix = withoutPrefix[..parenthesis];

        var dot = withoutPrefix.LastIndexOf('.');
        return dot >= 0 ? withoutPrefix[(dot + 1)..] : withoutPrefix;
    }
}
