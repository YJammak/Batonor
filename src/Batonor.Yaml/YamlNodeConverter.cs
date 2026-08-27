using System.Globalization;
using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Batonor.Yaml;

/// <summary>Converts a <see cref="YamlNode"/> tree to a <see cref="JsonNode"/> tree and back, without reflection.</summary>
internal static class YamlNodeConverter
{
    public static JsonNode? ToJsonNode(YamlNode node) => node switch
    {
        YamlScalarNode scalar => ScalarToJson(scalar),
        YamlSequenceNode sequence => SequenceToJson(sequence),
        YamlMappingNode mapping => MappingToJson(mapping),
        _ => null,
    };

    public static YamlNode ToYamlNode(JsonNode? node) => node switch
    {
        null => new YamlScalarNode("null"),
        JsonValue v => v.TryGetValue<string>(out var s)
            ? new YamlScalarNode(s) { Style = NeedsQuoting(s) ? ScalarStyle.SingleQuoted : ScalarStyle.Any }
            : new YamlScalarNode(v.ToString()),
        JsonArray arr => new YamlSequenceNode(arr.Select(ToYamlNode)),
        JsonObject obj => new YamlMappingNode(obj.Select(kv => new KeyValuePair<YamlNode, YamlNode>(
            new YamlScalarNode(kv.Key), ToYamlNode(kv.Value)))),
        _ => new YamlScalarNode("null"),
    };

    private static JsonNode? ScalarToJson(YamlScalarNode scalar)
    {
        var text = scalar.Value ?? "";

        // Quoted scalars are always strings in YAML, regardless of their content.
        if (scalar.Style is ScalarStyle.SingleQuoted or ScalarStyle.DoubleQuoted)
        {
            return JsonValue.Create(text);
        }

        if (text.Length == 0) return null;
        if (text is "~" or "null" or "Null" or "NULL") return null;
        if (text is "true" or "True" or "TRUE") return JsonValue.Create(true);
        if (text is "false" or "False" or "FALSE") return JsonValue.Create(false);
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
            return JsonValue.Create(l);
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return JsonValue.Create(d);
        return JsonValue.Create(text);
    }

    /// <summary>True when a string scalar must be quoted to avoid being re-parsed as null/bool/number.</summary>
    private static bool NeedsQuoting(string s)
    {
        if (s.Length == 0) return true;
        if (s is "~" or "null" or "Null" or "NULL" or "true" or "True" or "TRUE" or "false" or "False" or "FALSE")
        {
            return true;
        }

        return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            || double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    }

    private static JsonArray SequenceToJson(YamlSequenceNode seq) =>
        new(seq.Children.Select(ToJsonNode).ToArray());

    private static JsonObject MappingToJson(YamlMappingNode map)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in map.Children)
        {
            var keyText = (key as YamlScalarNode)?.Value ?? key.ToString();
            obj[keyText] = ToJsonNode(value);
        }

        return obj;
    }
}
