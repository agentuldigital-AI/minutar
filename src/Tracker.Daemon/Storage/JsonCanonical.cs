using System.IO;
using System.Text;
using System.Text.Json;

namespace Tracker.Daemon.Storage;

/// <summary>
/// THE single definition of event-data equality (plan D2/#5): canonical JSON with
/// object keys sorted ordinally, no whitespace, numbers kept as their raw text.
/// Used by every write path AND the peewee importer, so a heartbeat sent right after
/// import merges seamlessly with the last imported event.
/// </summary>
public static class JsonCanonical
{
    public static string Serialize(IReadOnlyDictionary<string, object?> data)
    {
        // round-trip through JsonElement so runtime types (bool/int/string/nested) and
        // JsonElement values passed by callers all canonicalize identically
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(data));
        return Canonicalize(doc.RootElement);
    }

    public static string Canonicalize(JsonElement element)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            Write(element, writer);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void Write(JsonElement el, Utf8JsonWriter w)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                w.WriteStartObject();
                foreach (var p in el.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    w.WritePropertyName(p.Name);
                    Write(p.Value, w);
                }
                w.WriteEndObject();
                break;
            case JsonValueKind.Array:
                w.WriteStartArray();
                foreach (var item in el.EnumerateArray()) Write(item, w);
                w.WriteEndArray();
                break;
            case JsonValueKind.Number:
                w.WriteRawValue(el.GetRawText()); // preserve source representation (no 1 vs 1.0 drift)
                break;
            default:
                el.WriteTo(w);
                break;
        }
    }
}
