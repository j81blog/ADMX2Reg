using System.Text.Json;
using System.Text.Json.Serialization;
using ADMX2Reg.Core.Gpo;
using ADMX2Reg.Core.Registry;

namespace ADMX2Reg.Core;

public sealed partial class GpoStore {
    private static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions() {
        var options = new JsonSerializerOptions {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new RegistryOperationConverter());
        return options;
    }

    public partial List<GpoDocument> LoadAll(out List<string> errors) {
        errors = [];
        var result = new List<GpoDocument>();
        Directory.CreateDirectory(Folder);
        foreach (var file in Directory.EnumerateFiles(Folder, "*.json").Order(StringComparer.OrdinalIgnoreCase)) {
            try {
                var gpo = Deserialize(File.ReadAllText(file));
                gpo.FilePath = file;
                result.Add(gpo);
            } catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException) {
                errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
        return result;
    }

    public partial void Save(GpoDocument gpo) {
        var path = gpo.FilePath ?? Path.Combine(Folder, gpo.Id + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        gpo.Modified = DateTimeOffset.Now;
        var temp = path + ".tmp";
        File.WriteAllText(temp, Serialize(gpo));
        File.Move(temp, path, overwrite: true);
        gpo.FilePath = path;
    }

    public partial void Delete(GpoDocument gpo) {
        if (gpo.FilePath != null && File.Exists(gpo.FilePath)) {
            File.Delete(gpo.FilePath);
        }
    }

    public static partial GpoDocument Duplicate(GpoDocument gpo) {
        var copy = Deserialize(Serialize(gpo));
        copy.Id = Guid.NewGuid();
        copy.Name = gpo.Name + " (Copy)";
        copy.Created = DateTimeOffset.Now;
        copy.Modified = copy.Created;
        copy.FilePath = null;
        return copy;
    }

    public static partial string Serialize(GpoDocument gpo) => JsonSerializer.Serialize(gpo, Options);

    public static partial GpoDocument Deserialize(string json) {
        var gpo = JsonSerializer.Deserialize<GpoDocument>(json, Options) ?? throw new JsonException("File is empty.");
        // The deserializer replaces the dictionaries, so restore the case-insensitive element lookup.
        foreach (var setting in gpo.Computer.Concat(gpo.User)) {
            setting.Values = new Dictionary<string, PolicyElementValue>(setting.Values, StringComparer.OrdinalIgnoreCase);
        }
        return gpo;
    }

    /// <summary>Writes RegistryOperation.Data as the JSON type that matches Type and restores the exact CLR type on read.</summary>
    private sealed class RegistryOperationConverter : JsonConverter<RegistryOperation> {
        public override RegistryOperation Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            var type = ParseEnum(Find(root, "type"), RegValueType.None);
            object? data = null;
            if (Find(root, "data") is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } d) {
                data = type switch {
                    RegValueType.String or RegValueType.ExpandString => d.GetString(),
                    RegValueType.MultiString => d.EnumerateArray().Select(x => x.GetString() ?? "").ToArray(),
                    RegValueType.DWord => d.GetUInt32(),
                    RegValueType.QWord => d.GetUInt64(),
                    RegValueType.Binary => Convert.FromBase64String(d.GetString() ?? ""),
                    _ => null
                };
            }
            var hive = Find(root, "hive");
            var kind = Find(root, "kind");
            var key = Find(root, "key");
            if (hive == null || kind == null || key == null) {
                throw new JsonException("Registry operation needs hive, kind and key.");
            }
            return new RegistryOperation {
                Hive = ParseEnum(hive, RegistryHive.LocalMachine),
                Kind = ParseEnum(kind, RegistryOperationKind.SetValue),
                Key = key.Value.GetString() ?? "",
                ValueName = Find(root, "valueName") is { ValueKind: JsonValueKind.String } vn ? vn.GetString() : null,
                Type = type,
                Data = data
            };
        }

        public override void Write(Utf8JsonWriter writer, RegistryOperation value, JsonSerializerOptions options) {
            writer.WriteStartObject();
            writer.WriteString("hive", value.Hive.ToString());
            writer.WriteString("kind", value.Kind.ToString());
            writer.WriteString("key", value.Key);
            if (value.ValueName != null) {
                writer.WriteString("valueName", value.ValueName);
            }
            writer.WriteString("type", value.Type.ToString());
            switch (value.Data) {
                case null:
                    break;
                case string s:
                    writer.WriteString("data", s);
                    break;
                case string[] lines:
                    writer.WriteStartArray("data");
                    foreach (var line in lines) {
                        writer.WriteStringValue(line);
                    }
                    writer.WriteEndArray();
                    break;
                case uint u:
                    writer.WriteNumber("data", u);
                    break;
                case ulong ul:
                    writer.WriteNumber("data", ul);
                    break;
                case byte[] bytes:
                    writer.WriteString("data", Convert.ToBase64String(bytes));
                    break;
                default:
                    throw new JsonException($"Unsupported registry data type {value.Data.GetType().Name}.");
            }
            writer.WriteEndObject();
        }

        private static JsonElement? Find(JsonElement obj, string name) {
            foreach (var p in obj.EnumerateObject()) {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) {
                    return p.Value;
                }
            }
            return null;
        }

        private static T ParseEnum<T>(JsonElement? element, T fallback) where T : struct, Enum =>
            element is { ValueKind: JsonValueKind.String } e && Enum.TryParse<T>(e.GetString(), true, out var v) ? v : fallback;
    }
}
