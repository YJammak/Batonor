using Batonor.Abstractions;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.RepresentationModel;

namespace Batonor.Yaml;

/// <summary>
/// <see cref="IWorkflowSerializer"/> backed by YAML. YAML is parsed into a <see cref="YamlNode"/> tree
/// (via <c>YamlStream</c>, reflection-free), converted to a <see cref="JsonNode"/>, and bound to the
/// Batonor types through the source-generated <see cref="Batonor.Json.BatonorJsonContext"/> — AOT-safe.
/// </summary>
public sealed class YamlWorkflowSerializer : IWorkflowSerializer
{
    public string SerializeDefinition(WorkflowDefinition definition)
        => Serialize(JsonSerializer.SerializeToNode(definition, Batonor.Json.BatonorJsonContext.Default.WorkflowDefinition));

    public WorkflowDefinition DeserializeDefinition(string yaml)
        => Deserialize(yaml).Deserialize(Batonor.Json.BatonorJsonContext.Default.WorkflowDefinition)
           ?? throw new BatonorException("Failed to deserialize workflow definition from YAML.");

    public string SerializeInstance(WorkflowInstance instance)
        => Serialize(JsonSerializer.SerializeToNode(instance, Batonor.Json.BatonorJsonContext.Default.WorkflowInstance));

    public WorkflowInstance DeserializeInstance(string yaml)
        => Deserialize(yaml).Deserialize(Batonor.Json.BatonorJsonContext.Default.WorkflowInstance)
           ?? throw new BatonorException("Failed to deserialize workflow instance from YAML.");

    private static JsonNode? Deserialize(string yaml)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(yaml);
        stream.Load(reader);
        return stream.Documents.Count == 0 ? null : YamlNodeConverter.ToJsonNode(stream.Documents[0].RootNode);
    }

    private static string Serialize(JsonNode? node)
    {
        var doc = new YamlDocument(YamlNodeConverter.ToYamlNode(node ?? new JsonObject()));
        var sb = new StringBuilder();
        var writer = new StringWriter(sb);
        new YamlStream(doc).Save(writer, assignAnchors: false);
        return sb.ToString();
    }
}
