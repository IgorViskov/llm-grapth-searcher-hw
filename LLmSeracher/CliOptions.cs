namespace LLmSeracher.Cli;

/// <param name="Local">Запустить агентов в одном процессе, без хоста и сети.</param>
/// <param name="HostUrl">Адрес хоста агентов (перекрывает конфиг).</param>
/// <param name="Demo">Прогнать заранее заданный сценарий из нескольких вопросов.</param>
/// <param name="AclDemo">Показать отказ агента при вызове без делегирующего токена.</param>
/// <param name="Question">Вопрос, если он передан в командной строке.</param>
internal sealed record CliOptions(
    bool Local,
    string? HostUrl,
    bool Demo,
    bool AclDemo,
    string? Question)
{
    /// <summary>Вопросы к markdown-базе знаний — источники "files" и "docs-api".</summary>
    public static readonly string[] DemoQuestions =
    [
        "Сколько дней даётся на возврат товара и как быстро вернут деньги на карту?",
        "Курьер задержал доставку на трое суток — какая положена компенсация?",
        "Действует ли гарантия, если я заряжал телефон неоригинальной зарядкой?"
    ];

    /// <summary>
    /// Вопросы к графу кода. Каждый задействует свой механизм, а не просто «поиск по коду»:
    /// <list type="number">
    /// <item>точное имя символа плюс обход по обратным рёбрам CALLS — то, чего текстовый
    /// и векторный поиск не умеют в принципе;</item>
    /// <item>рёбра REGISTERED_AS из регистраций DI — связь «интерфейс → реализация»,
    /// которой в синтаксисе не существует;</item>
    /// <item>вопрос «почему так сделано»: имя типа даёт точку входа, содержательный ответ
    /// собирается из его членов и русских XML-комментариев.</item>
    /// </list>
    /// Все три называют символ явно — и это не случайность: чисто описательный запрос,
    /// где понятие сформулировано по-русски, а в коде оно по-английски, лексический канал
    /// не вытягивает. Закрывается это векторным каналом, см. GRAPH-CONTEXT-ANALYSIS.md.
    /// </summary>
    public static readonly string[] CodeDemoQuestions =
    [
        "Кто вызывает RenderContextBlock и зачем?",
        "Какие есть реализации IContextProvider и где они регистрируются?",
        "Почему CompositeContextProvider сливает источники по RRF, а не по сырому score?"
    ];

    public static CliOptions Parse(string[] args)
    {
        var local = false;
        var demo = false;
        var aclDemo = false;
        string? hostUrl = null;
        var rest = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--local":
                    local = true;
                    break;
                case "--demo":
                    demo = true;
                    break;
                case "--acl-demo":
                    aclDemo = true;
                    break;
                case "--host" when i + 1 < args.Length:
                    hostUrl = args[++i];
                    break;
                default:
                    rest.Add(args[i]);
                    break;
            }
        }

        var question = rest.Count > 0 ? string.Join(' ', rest) : null;
        return new CliOptions(local, hostUrl, demo, aclDemo, question);
    }

    public static string Usage =>
        """
        Использование:
          dotnet run --project LLmSeracher -- [ключи] [вопрос]

          --local          агенты работают в одном процессе (хост не нужен)
          --host <url>     адрес хоста агентов, по умолчанию http://localhost:5080
          --demo           прогнать сценарий из нескольких вопросов подряд
          --acl-demo       показать отказ агента при вызове без делегирующего токена

        Без вопроса запускается интерактивный режим. Ctrl+C прерывает генерацию.

        Источник контекста задаётся ключом Context:Sources в appsettings.json.
        По умолчанию включён граф кода (code-graph) — вопросы --demo про эту кодовую базу;
        при ["files", "docs-api"] тот же ключ прогоняет вопросы к markdown-справке.
        Граф наполняется отдельно: dotnet run --project LLmSeracher.Indexer -- index --reset
        """;
}
