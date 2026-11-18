using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>Parsing of a stored JSON blob with an error that names it: an empty body (a Nexis ghost that could not be hydrated, or a truncated upload) is not a bare JsonReaderException.</summary>
public static class StoredJson
{
    public sealed class EmptyBlobException(string name) : InvalidOperationException($"stored blob {name} is empty (0 bytes): a Nexis ghost that was not hydrated, or an interrupted write; expected JSON");

    public static JsonNode? Parse(byte[]? data, string name)
    {
        if (data == null) throw new InvalidOperationException($"stored blob {name} does not exist");
        if (data.Length == 0 || IsBlank(data)) throw new EmptyBlobException(name);
        try { return JsonNode.Parse(data); }
        catch (JsonException e) { throw new InvalidOperationException($"stored blob {name} is not valid JSON: {e.Message}", e); }
    }

    /// <summary>The same for a System.Text.Json document (the callers that read properties off RootElement).</summary>
    public static JsonDocument Doc(byte[]? data, string name)
    {
        if (data == null) throw new InvalidOperationException($"stored blob {name} does not exist");
        if (data.Length == 0 || IsBlank(data)) throw new EmptyBlobException(name);
        try { return JsonDocument.Parse(data); }
        catch (JsonException e) { throw new InvalidOperationException($"stored blob {name} is not valid JSON: {e.Message}", e); }
    }

    public static JsonObject Object(byte[]? data, string name) => Parse(data, name) as JsonObject ?? throw new InvalidOperationException($"stored blob {name} is not a JSON object");

    static bool IsBlank(byte[] d) { foreach (var b in d) if (b is not (0x20 or 0x09 or 0x0A or 0x0D or 0xEF or 0xBB or 0xBF)) return false; return true; }
}
