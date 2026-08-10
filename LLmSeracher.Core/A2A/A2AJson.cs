using System.Text.Json;
using System.Text.Json.Serialization;

namespace LLmSeracher.Core.A2A;

/// <summary>
/// Единые настройки сериализации A2A-сообщений. Хост и клиент обязаны использовать одни и те же,
/// иначе полиморфные события перестанут разбираться на другой стороне.
/// </summary>
public static class A2AJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };
}
