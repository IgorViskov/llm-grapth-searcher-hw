using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using LLmSeracher.Graph.Model;
using LLmSeracher.Graph.Retrieval;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;

namespace LLmSeracher.Indexer.Extraction;

/// <summary>
/// Разбор решения C# в узлы и рёбра графа средствами Roslyn.
///
/// Работа идёт по семантической модели, а не по синтаксису: только так вызов
/// <c>_context.SearchAsync(...)</c> превращается в ребро к конкретному методу конкретного
/// типа. Отдельно извлекаются регистрации DI — без них граф вызовов рвётся на каждом
/// интерфейсе, потому что связь «интерфейс → реализация» существует только в
/// <c>AddSingleton&lt;TService, TImpl&gt;()</c> и синтаксически не выводится ниоткуда.
/// </summary>
public sealed class CSharpExtractor
{
    private const int MaxSnippetChars = 4000;
    private const int MaxTypeMembersInSnippet = 40;

    private static readonly HashSet<string> DiMethods = new(StringComparer.Ordinal)
    {
        "AddSingleton", "AddScoped", "AddTransient",
        "AddKeyedSingleton", "AddKeyedScoped", "AddKeyedTransient"
    };

    private readonly ILogger<CSharpExtractor> _logger;
    private readonly HashSet<string> _realIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _stubIds = new(StringComparer.Ordinal);

    public CSharpExtractor(ILogger<CSharpExtractor> logger) => _logger = logger;

    public async Task<ExtractionReport> ExtractAsync(
        string solutionPath,
        string repoRoot,
        Func<GraphBatch, CancellationToken, Task> sink,
        CancellationToken ct)
    {
        using var workspace = MSBuildWorkspace.Create();
        using var failures = workspace.RegisterWorkspaceFailedHandler(e =>
        {
            if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
                _logger.LogWarning("MSBuild: {Message}", e.Diagnostic.Message);
        });

        _logger.LogInformation("Открываю решение {Path}", solutionPath);
        var solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: ct);

        var assemblyByProject = solution.Projects.ToDictionary(
            p => p.Id, p => p.AssemblyName, EqualityComparer<ProjectId>.Default);

        var report = new Report();

        foreach (var project in solution.Projects)
        {
            ct.ThrowIfCancellationRequested();
            if (project.Language != LanguageNames.CSharp) continue;

            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null)
            {
                report.Errors.Add($"{project.Name}: компиляция недоступна");
                continue;
            }

            // Непроведённый restore Roslyn не сообщает никак: семантическая модель просто
            // молча перестаёт разрешать символы, и половина рёбер CALLS не появляется.
            // Поэтому диагностику проверяем явно и громко.
            var errors = compilation.GetDiagnostics(ct)
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.Id)
                .Distinct()
                .Take(5)
                .ToList();

            if (errors.Count > 0)
            {
                var message = $"{project.Name}: ошибки компиляции ({string.Join(", ", errors)}) — " +
                              "часть связей будет потеряна, проверьте dotnet restore";
                _logger.LogWarning("{Message}", message);
                report.Errors.Add(message);
            }

            var batch = new GraphBatch();
            ProcessProject(project, compilation, assemblyByProject, repoRoot, batch);

            foreach (var document in project.Documents)
            {
                ct.ThrowIfCancellationRequested();
                if (!IsIndexable(document)) continue;

                var model = await document.GetSemanticModelAsync(ct);
                var root = await document.GetSyntaxRootAsync(ct);
                if (model is null || root is null) continue;

                ProcessDocument(project, model, root, document.FilePath!, repoRoot, batch);
                report.Documents++;
            }

            report.Projects++;
            report.Nodes += batch.Nodes.Count;
            report.Edges += batch.Edges.Count;

            _logger.LogInformation("{Project}: {Nodes} узлов, {Edges} рёбер",
                project.Name, batch.Nodes.Count, batch.Edges.Count);

            await sink(batch, ct);
        }

        return new ExtractionReport(
            report.Projects, report.Documents, report.Nodes, report.Edges, report.Errors);
    }

    // ── Проект ───────────────────────────────────────────────────────────────────────

    private void ProcessProject(
        Project project, Compilation compilation,
        Dictionary<ProjectId, string> assemblyByProject, string repoRoot, GraphBatch batch)
    {
        var projectFile = Relative(repoRoot, project.FilePath);
        batch.TouchedFiles.Add(projectFile);

        var projectId = ProjectNodeId(project.AssemblyName);
        batch.Nodes.Add(new GraphNode { Id = projectId, Kind = NodeKinds.Project, SourceFile = projectFile }
            .Set("name", project.Name)
            .Set("fqn", project.AssemblyName)
            .Set("nameTokens", IdentifierTokenizer.ToSearchableText(project.Name))
            .Set("language", "csharp")
            .Set("filePath", projectFile)
            .Set("snippet", $"проект {project.Name} ({project.AssemblyName})"));

        foreach (var reference in project.ProjectReferences)
        {
            if (!assemblyByProject.TryGetValue(reference.ProjectId, out var assembly)) continue;

            batch.Edges.Add(new GraphEdge
            {
                From = projectId,
                To = ProjectNodeId(assembly),
                Kind = EdgeKinds.References,
                SourceFile = projectFile
            });
        }

        foreach (var package in ReadPackageReferences(project.FilePath))
        {
            var packageId = $"pkg::{package}";
            batch.Nodes.Add(new GraphNode
                {
                    Id = packageId, Kind = NodeKinds.External, SourceFile = projectFile, IsStub = true
                }
                .Set("name", package)
                .Set("fqn", package)
                .Set("nameTokens", IdentifierTokenizer.ToSearchableText(package)));

            batch.Edges.Add(new GraphEdge
            {
                From = projectId, To = packageId, Kind = EdgeKinds.References, SourceFile = projectFile
            }.Set("kind", "package"));
        }
    }

    private static IEnumerable<string> ReadPackageReferences(string? csprojPath)
    {
        if (csprojPath is null || !File.Exists(csprojPath)) yield break;

        XDocument document;
        try
        {
            document = XDocument.Load(csprojPath);
        }
        catch (System.Xml.XmlException)
        {
            yield break;
        }

        foreach (var element in document.Descendants("PackageReference"))
        {
            var include = element.Attribute("Include")?.Value;
            if (!string.IsNullOrWhiteSpace(include)) yield return include;
        }
    }

    // ── Документ ─────────────────────────────────────────────────────────────────────

    private void ProcessDocument(
        Project project, SemanticModel model, SyntaxNode root,
        string absolutePath, string repoRoot, GraphBatch batch)
    {
        var relPath = Relative(repoRoot, absolutePath);
        batch.TouchedFiles.Add(relPath);

        var fileId = $"file::{relPath}";
        var fileName = Path.GetFileNameWithoutExtension(absolutePath);

        batch.Nodes.Add(new GraphNode { Id = fileId, Kind = NodeKinds.File, SourceFile = relPath }
            .Set("name", fileName)
            .Set("fqn", relPath)
            .Set("nameTokens", IdentifierTokenizer.ToSearchableText(fileName))
            .Set("language", "csharp")
            .Set("filePath", relPath));

        batch.Edges.Add(new GraphEdge
        {
            From = ProjectNodeId(project.AssemblyName), To = fileId,
            Kind = EdgeKinds.Contains, SourceFile = relPath
        });

        foreach (var typeDecl in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            ProcessType(typeDecl, model, fileId, relPath, batch);

        // Файлы с операторами верхнего уровня (Program.cs хоста и клиента) не имеют
        // объявленного типа — без синтетического узла точки входа из графа выпали бы
        // и регистрация сервисов, и объявление HTTP-эндпоинтов.
        ProcessTopLevelStatements(project, root, model, fileId, relPath, batch);
    }

    private void ProcessType(
        BaseTypeDeclarationSyntax declaration, SemanticModel model,
        string fileId, string relPath, GraphBatch batch)
    {
        if (model.GetDeclaredSymbol(declaration) is not INamedTypeSymbol symbol) return;

        var typeId = EmitReal(symbol, NodeKinds.Type, relPath, batch,
            BuildTypeSnippet(declaration, symbol), declaration);
        if (typeId is null) return;

        batch.Edges.Add(Edge(fileId, typeId, EdgeKinds.Declares, relPath));

        if (symbol.BaseType is { SpecialType: SpecialType.None } baseType &&
            EnsureSymbol(baseType, relPath, batch) is { } baseId)
            batch.Edges.Add(Edge(typeId, baseId, EdgeKinds.Inherits, relPath));

        foreach (var iface in symbol.Interfaces)
        {
            if (EnsureSymbol(iface, relPath, batch) is { } ifaceId)
                batch.Edges.Add(Edge(typeId, ifaceId, EdgeKinds.Implements, relPath));
        }

        if (declaration is not TypeDeclarationSyntax typeDeclaration) return;

        var interfaceMap = BuildInterfaceMap(symbol);

        foreach (var member in typeDeclaration.Members)
        {
            switch (member)
            {
                case BaseTypeDeclarationSyntax:
                    continue; // вложенные типы обходятся внешним циклом по документу

                case MethodDeclarationSyntax or ConstructorDeclarationSyntax:
                    ProcessMember(member, model.GetDeclaredSymbol(member), NodeKinds.Method,
                        typeId, fileId, relPath, model, interfaceMap, batch);
                    break;

                case PropertyDeclarationSyntax property:
                    ProcessMember(member, model.GetDeclaredSymbol(property), NodeKinds.Property,
                        typeId, fileId, relPath, model, interfaceMap, batch);
                    break;

                case FieldDeclarationSyntax field:
                    foreach (var variable in field.Declaration.Variables)
                        ProcessMember(member, model.GetDeclaredSymbol(variable), NodeKinds.Field,
                            typeId, fileId, relPath, model, interfaceMap, batch);
                    break;
            }
        }
    }

    private void ProcessMember(
        SyntaxNode declaration, ISymbol? symbol, string kind,
        string typeId, string fileId, string relPath, SemanticModel model,
        Dictionary<ISymbol, List<ISymbol>> interfaceMap, GraphBatch batch)
    {
        if (symbol is null) return;

        var memberId = EmitReal(symbol, kind, relPath, batch, Snippet(declaration), declaration);
        if (memberId is null) return;

        batch.Edges.Add(Edge(typeId, memberId, EdgeKinds.HasMember, relPath));
        batch.Edges.Add(Edge(fileId, memberId, EdgeKinds.Declares, relPath));

        if (symbol is IMethodSymbol method)
        {
            if (method.OverriddenMethod is { } overridden &&
                EnsureSymbol(overridden, relPath, batch) is { } overriddenId)
                batch.Edges.Add(Edge(memberId, overriddenId, EdgeKinds.Overrides, relPath));

            if (!method.ReturnsVoid && Named(method.ReturnType) is { } returnType &&
                EnsureSymbol(returnType, relPath, batch) is { } returnId)
                batch.Edges.Add(Edge(memberId, returnId, EdgeKinds.Returns, relPath));

            foreach (var parameter in method.Parameters)
            {
                if (Named(parameter.Type) is { } parameterType &&
                    EnsureSymbol(parameterType, relPath, batch) is { } parameterId)
                    batch.Edges.Add(Edge(memberId, parameterId, EdgeKinds.HasParam, relPath)
                        .Set("name", parameter.Name));
            }
        }

        if (interfaceMap.TryGetValue(symbol, out var implemented))
        {
            foreach (var ifaceMember in implemented)
            {
                if (EnsureSymbol(ifaceMember, relPath, batch) is { } ifaceMemberId)
                    batch.Edges.Add(Edge(memberId, ifaceMemberId, EdgeKinds.ImplementsMember, relPath));
            }
        }

        ProcessBody(declaration, memberId, model, relPath, batch);
    }

    private void ProcessTopLevelStatements(
        Project project, SyntaxNode root, SemanticModel model,
        string fileId, string relPath, GraphBatch batch)
    {
        var statements = root.ChildNodes().OfType<GlobalStatementSyntax>().ToList();
        if (statements.Count == 0) return;

        var entryId = $"{SymbolNaming.Prefix}TOPLEVEL:{relPath}";
        var name = $"{project.AssemblyName} (точка входа)";

        batch.Nodes.Add(new GraphNode { Id = entryId, Kind = NodeKinds.Method, SourceFile = relPath }
            .Set("name", name)
            .Set("fqn", $"{project.AssemblyName}.Program")
            .Set("nameTokens", IdentifierTokenizer.ToSearchableText(project.AssemblyName) + " Program main")
            .Set("language", "csharp")
            .Set("filePath", relPath)
            .Set("startLine", 1)
            .Set("signature", $"// {relPath} — операторы верхнего уровня")
            .Set("snippet", Truncate(string.Join('\n', statements.Select(s => s.ToString())))));

        _realIds.Add(entryId);
        batch.Edges.Add(Edge(fileId, entryId, EdgeKinds.Declares, relPath));

        foreach (var statement in statements)
            ProcessBody(statement, entryId, model, relPath, batch);
    }

    // ── Тело метода: вызовы, создание объектов, регистрации DI ───────────────────────

    private void ProcessBody(
        SyntaxNode body, string ownerId, SemanticModel model, string relPath, GraphBatch batch)
    {
        foreach (var node in body.DescendantNodes())
        {
            switch (node)
            {
                case InvocationExpressionSyntax invocation:
                {
                    if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol target) break;

                    // Расширяющий метод приходит в «редуцированном» виде — для идентичности
                    // нужен исходный, иначе ключ разъедется с объявлением.
                    var callee = target.ReducedFrom ?? target;

                    if (EnsureSymbol(callee, relPath, batch) is { } calleeId && calleeId != ownerId)
                    {
                        batch.Edges.Add(Edge(ownerId, calleeId, EdgeKinds.Calls, relPath)
                            .Set("line", LineOf(invocation))
                            .Set("viaInterface", callee.ContainingType?.TypeKind == TypeKind.Interface));
                    }

                    DetectDiRegistration(invocation, target, model, relPath, batch);
                    break;
                }

                case BaseObjectCreationExpressionSyntax creation:
                {
                    var created = model.GetSymbolInfo(creation).Symbol is IMethodSymbol constructor
                        ? constructor.ContainingType
                        : Named(model.GetTypeInfo(creation).Type);

                    if (created is not null && EnsureSymbol(created, relPath, batch) is { } createdId)
                        batch.Edges.Add(Edge(ownerId, createdId, EdgeKinds.Instantiates, relPath)
                            .Set("line", LineOf(creation)));
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Регистрация в DI-контейнере. Покрывает обе формы: обобщённую
    /// <c>AddSingleton&lt;IAgent, RetrieverAgent&gt;()</c> и фабричную
    /// <c>AddSingleton&lt;IContextProvider&gt;(sp =&gt; new CompositeContextProvider(...))</c> —
    /// вторая в реальном коде встречается не реже и теряется, если её не разбирать.
    /// </summary>
    private void DetectDiRegistration(
        InvocationExpressionSyntax invocation, IMethodSymbol target,
        SemanticModel model, string relPath, GraphBatch batch)
    {
        if (!DiMethods.Contains(target.Name) || target.TypeArguments.Length == 0) return;
        if (Named(target.TypeArguments[0]) is not { } service) return;

        var implementation = target.TypeArguments.Length >= 2
            ? Named(target.TypeArguments[1])
            : FindFactoryType(invocation, model);

        if (implementation is null ||
            SymbolEqualityComparer.Default.Equals(service, implementation)) return;

        if (EnsureSymbol(service, relPath, batch) is not { } serviceId) return;
        if (EnsureSymbol(implementation, relPath, batch) is not { } implementationId) return;

        batch.Edges.Add(Edge(serviceId, implementationId, EdgeKinds.RegisteredAs, relPath)
            .Set("lifetime", target.Name.Replace("AddKeyed", "").Replace("Add", "").ToLowerInvariant())
            .Set("line", LineOf(invocation)));
    }

    private static INamedTypeSymbol? FindFactoryType(InvocationExpressionSyntax invocation, SemanticModel model)
    {
        var nodes = invocation.ArgumentList.Arguments.SelectMany(a => a.DescendantNodes()).ToList();

        // Порядок проверок важен. Тело фабрики почти всегда сначала разрешает зависимости
        // через GetRequiredService, и только потом конструирует результат. Если брать первое
        // совпадение подряд, реализацией окажется IOptions или ILogger вместо искомого типа —
        // поэтому создание объекта ищется первым проходом, а резолв контейнера только вторым,
        // когда явного new в фабрике нет.
        foreach (var node in nodes)
        {
            if (node is BaseObjectCreationExpressionSyntax creation &&
                model.GetSymbolInfo(creation).Symbol is IMethodSymbol constructor)
                return constructor.ContainingType;
        }

        // Фабрика вида sp => sp.GetRequiredService<GraphContextProvider>()
        foreach (var node in nodes)
        {
            if (node is InvocationExpressionSyntax inner &&
                model.GetSymbolInfo(inner).Symbol is IMethodSymbol
                    { Name: "GetRequiredService" or "GetService" } resolver &&
                resolver.TypeArguments.Length == 1)
                return Named(resolver.TypeArguments[0]);
        }

        return null;
    }

    // ── Узлы ─────────────────────────────────────────────────────────────────────────

    private string? EmitReal(
        ISymbol symbol, string kind, string relPath, GraphBatch batch,
        string snippet, SyntaxNode declaration)
    {
        var id = SymbolNaming.IdOf(symbol);
        if (id is null) return null;

        var span = declaration.GetLocation().GetLineSpan();
        var doc = SymbolNaming.DocText(symbol);

        batch.Nodes.Add(new GraphNode { Id = id, Kind = kind, SourceFile = relPath }
            .Set("name", symbol.Name)
            .Set("fqn", SymbolNaming.Fqn(symbol))
            .Set("nameTokens", IdentifierTokenizer.ToSearchableText(symbol.Name))
            .Set("signature", SymbolNaming.Signature(symbol))
            .Set("language", "csharp")
            .Set("filePath", relPath)
            .Set("startLine", span.StartLinePosition.Line + 1)
            .Set("endLine", span.EndLinePosition.Line + 1)
            .Set("docComment", doc)
            .Set("snippet", snippet)
            .Set("bodyHash", Hash(snippet))
            .Set("accessibility", symbol.DeclaredAccessibility.ToString().ToLowerInvariant())
            .Set("isStatic", symbol.IsStatic)
            .Set("isAbstract", symbol.IsAbstract)
            .Set("isTest", IsTest(symbol)));

        _realIds.Add(id);
        return id;
    }

    /// <summary>
    /// Узел-заглушка для символа, о котором мы узнали из чужого файла. Пишется через
    /// ON CREATE SET, поэтому не затирает объявление, когда до него дойдёт очередь —
    /// и порядок обхода проектов перестаёт влиять на результат.
    /// </summary>
    private string? EnsureSymbol(ISymbol symbol, string relPath, GraphBatch batch)
    {
        var id = SymbolNaming.IdOf(symbol);
        if (id is null) return null;
        if (_realIds.Contains(id) || !_stubIds.Add(id)) return id;

        var definition = symbol.OriginalDefinition;
        var fromSource = SymbolNaming.IsFromSource(definition);

        batch.Nodes.Add(new GraphNode
            {
                Id = id,
                Kind = fromSource ? KindOf(definition) : NodeKinds.External,
                SourceFile = relPath,
                IsStub = true
            }
            .Set("name", definition.Name)
            .Set("fqn", SymbolNaming.Fqn(definition))
            .Set("nameTokens", IdentifierTokenizer.ToSearchableText(definition.Name))
            .Set("language", "csharp")
            .Set("assembly", definition.ContainingAssembly?.Name));

        return id;
    }

    // ── Вспомогательное ──────────────────────────────────────────────────────────────

    private static Dictionary<ISymbol, List<ISymbol>> BuildInterfaceMap(INamedTypeSymbol type)
    {
        var map = new Dictionary<ISymbol, List<ISymbol>>(SymbolEqualityComparer.Default);

        foreach (var iface in type.AllInterfaces)
        {
            foreach (var member in iface.GetMembers())
            {
                var implementation = type.FindImplementationForInterfaceMember(member);
                if (implementation is null) continue;
                if (!SymbolEqualityComparer.Default.Equals(implementation.ContainingType, type)) continue;

                if (!map.TryGetValue(implementation, out var list))
                    map[implementation] = list = [];

                list.Add(member);
            }
        }

        return map;
    }

    private static string BuildTypeSnippet(BaseTypeDeclarationSyntax declaration, INamedTypeSymbol symbol)
    {
        var header = declaration switch
        {
            TypeDeclarationSyntax t =>
                $"{t.Modifiers} {t.Keyword} {t.Identifier}{t.TypeParameterList} {t.BaseList}".Trim(),
            EnumDeclarationSyntax e => $"{e.Modifiers} enum {e.Identifier} {e.BaseList}".Trim(),
            _ => symbol.Name
        };

        var builder = new StringBuilder(Collapse(header)).Append("\n{\n");

        // Тело типа целиком дублировало бы фрагменты его же методов; в контекст полезнее
        // отдать «оглавление» — список сигнатур членов.
        var members = symbol.GetMembers()
            .Where(m => !m.IsImplicitlyDeclared && m.CanBeReferencedByName
                        || m is IMethodSymbol { MethodKind: MethodKind.Constructor })
            .Take(MaxTypeMembersInSnippet);

        foreach (var member in members)
            builder.Append("    ").Append(SymbolNaming.Signature(member)).Append(";\n");

        return Truncate(builder.Append('}').ToString());
    }

    private static bool IsIndexable(Document document)
    {
        if (document.FilePath is not { } path) return false;
        if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
            path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) return false;

        var name = Path.GetFileName(path);
        return !name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
               && !name.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)
               && !name.Equals("AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTest(ISymbol symbol) =>
        symbol.GetAttributes().Any(a => a.AttributeClass?.Name is
            "FactAttribute" or "TheoryAttribute" or "TestAttribute" or "TestMethodAttribute");

    private static INamedTypeSymbol? Named(ITypeSymbol? type) => type switch
    {
        INamedTypeSymbol named => (INamedTypeSymbol)named.OriginalDefinition,
        IArrayTypeSymbol array => Named(array.ElementType),
        _ => null
    };

    private static string KindOf(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol => NodeKinds.Type,
        IMethodSymbol => NodeKinds.Method,
        IPropertySymbol => NodeKinds.Property,
        IFieldSymbol => NodeKinds.Field,
        _ => NodeKinds.External
    };

    private static GraphEdge Edge(string from, string to, string kind, string sourceFile) =>
        new() { From = from, To = to, Kind = kind, SourceFile = sourceFile };

    private static string ProjectNodeId(string assemblyName) => $"{SymbolNaming.Prefix}PROJECT:{assemblyName}";

    private static string Relative(string repoRoot, string? path) =>
        path is null ? "?" : Path.GetRelativePath(repoRoot, path).Replace('\\', '/');

    private static int LineOf(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static string Snippet(SyntaxNode node) => Truncate(node.ToString());

    private static string Truncate(string text) =>
        text.Length <= MaxSnippetChars ? text : text[..MaxSnippetChars] + "\n// … фрагмент усечён";

    private static string Collapse(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();

    private sealed class Report
    {
        public int Projects;
        public int Documents;
        public int Nodes;
        public int Edges;
        public List<string> Errors { get; } = [];
    }
}

public sealed record ExtractionReport(
    int Projects, int Documents, int Nodes, int Edges, IReadOnlyList<string> Errors);
