using System.Globalization;
using System.Text.Json.Nodes;
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
        JsonValue v => v.TryGetValue<string>(out var s) ? new YamlScalarNode(s)
            : new YamlScalarNode(v.ToString()),
        JsonArray arr => new YamlSequenceNode(arr.Select(ToYamlNode)),
        JsonObject obj => new YamlMappingNode(obj.Select(kv => new KeyValuePair<YamlNode, YamlNode>(
            new YamlScalarNode(kv.Key), ToYamlNode(kv.Value)))),
        _ => new YamlScalarNode("null"),
    };

    private static JsonNode? ScalarToJson(YamlScalarNode scalar)
    {
        var text = scalar.Value ?? "";
        if (text.Length == 0) return null;
        if (text == "null") return null;
        if (text == "true") return JsonValue.Create(true);
        if (text == "false") return JsonValue.Create(false);
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return JsonValue.Create(d);
        if (long.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var l))
            return JsonValue.Create(l);
        return JsonValue.Create(text);
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
