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
    public static readonly string[] DemoQuestions =
    [
        "Сколько дней даётся на возврат товара и как быстро вернут деньги на карту?",
        "Курьер задержал доставку на трое суток — какая положена компенсация?",
        "Действует ли гарантия, если я заряжал телефон неоригинальной зарядкой?"
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
        """;
}
