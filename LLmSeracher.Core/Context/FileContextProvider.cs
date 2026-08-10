using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LLmSeracher.Core.Context;

/// <summary>
/// Источник контекста №1 — файлы. Читает *.md из каталога базы знаний и режет каждый
/// файл на фрагменты по заголовкам второго уровня ("## "). Индекс строится один раз,
/// лениво, при первом запросе.
/// </summary>
public sealed class FileContextProvider : IContextProvider
{
    private readonly ContextOptions _options;
    private readonly ILogger<FileContextProvider> _logger;
    private readonly Lazy<IReadOnlyList<Document>> _index;

    public string Name => "files";

    public FileContextProvider(IOptions<ContextOptions> options, ILogger<FileContextProvider> logger)
    {
        _options = options.Value;
        _logger = logger;
        _index = new Lazy<IReadOnlyList<Document>>(BuildIndex, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public async IAsyncEnumerable<ContextChunk> SearchAsync(
        string query, int limit, [EnumeratorCancellation] CancellationToken ct)
    {
        var queryTokens = Tokenizer.Tokenize(query);

        var ranked = _index.Value
            .Select(doc => (doc, score: Tokenizer.Score(queryTokens, doc.Tokens)))
            .Where(x => x.score >= _options.MinScore)
            .OrderByDescending(x => x.score)
            .Take(limit);

        foreach (var (doc, score) in ranked)
        {
            ct.ThrowIfCancellationRequested();

            // Чтение с диска здесь уже произошло, но yield в асинхронном потоке оставляет
            // возможность подменить реализацию на действительно асинхронный индекс.
            await Task.Yield();
            yield return new ContextChunk(Name, doc.Id, doc.Title, doc.Text, Math.Round(score, 3));
        }
    }

    private IReadOnlyList<Document> BuildIndex()
    {
        var root = ResolveRoot();
        if (root is null)
        {
            _logger.LogWarning("Каталог базы знаний '{Root}' не найден — файловый источник пуст",
                _options.KnowledgeRoot);
            return [];
        }

        var documents = new List<Document>();
        foreach (var file in Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories))
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            foreach (var (title, text, index) in SplitBySections(File.ReadAllText(file), fileName))
            {
                documents.Add(new Document(
                    Id: $"{fileName}#{index}",
                    Title: title,
                    Text: text,
                    Tokens: Tokenizer.Tokenize($"{title}\n{text}")));
            }
        }

        _logger.LogInformation("Файловый источник: {Count} фрагментов из {Root}", documents.Count, root);
        return documents;
    }

    /// <summary>
    /// Ищет каталог знаний рядом с бинарником, а если не нашёл — поднимается вверх по дереву.
    /// Нужно, чтобы <c>dotnet run</c> и запуск из bin вели себя одинаково.
    /// </summary>
    private string? ResolveRoot()
    {
        if (Path.IsPathRooted(_options.KnowledgeRoot))
            return Directory.Exists(_options.KnowledgeRoot) ? _options.KnowledgeRoot : null;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, _options.KnowledgeRoot);
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        return null;
    }

    private static IEnumerable<(string Title, string Text, int Index)> SplitBySections(
        string markdown, string fallbackTitle)
    {
        var lines = markdown.Split('\n');
        var title = fallbackTitle;
        var buffer = new List<string>();
        var index = 0;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (buffer.Count > 0)
                {
                    var text = string.Join('\n', buffer).Trim();
                    if (text.Length > 0) yield return (title, text, index++);
                    buffer.Clear();
                }

                title = line[3..].Trim();
                continue;
            }

            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                title = line[2..].Trim();
                continue;
            }

            buffer.Add(line);
        }

        if (buffer.Count > 0)
        {
            var text = string.Join('\n', buffer).Trim();
            if (text.Length > 0) yield return (title, text, index);
        }
    }

    private sealed record Document(string Id, string Title, string Text, IReadOnlyList<string> Tokens);
}
