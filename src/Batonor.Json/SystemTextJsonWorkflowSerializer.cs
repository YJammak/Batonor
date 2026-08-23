using Batonor.Abstractions;
using System.Text.Json;

namespace Batonor.Json;

/// <summary>
/// Implements <see cref="IWorkflowSerializer"/> over the source-generated
/// <see cref="BatonorJsonContext"/> (AOT-safe, reflection-free).
/// </summary>
public sealed class SystemTextJsonWorkflowSerializer : IWorkflowSerializer
{
    public string SerializeDefinition(WorkflowDefinition definition)
        => JsonSerializer.Serialize(definition, BatonorJsonContext.Default.WorkflowDefinition);

    public WorkflowDefinition DeserializeDefinition(string json)
        => JsonSerializer.Deserialize(json, BatonorJsonContext.Default.WorkflowDefinition)
           ?? throw new BatonorException("Failed to deserialize workflow definition.");

    public string SerializeInstance(WorkflowInstance instance)
        => JsonSerializer.Serialize(instance, BatonorJsonContext.Default.WorkflowInstance);

    public WorkflowInstance DeserializeInstance(string json)
        => JsonSerializer.Deserialize(json, BatonorJsonContext.Default.WorkflowInstance)
           ?? throw new BatonorException("Failed to deserialize workflow instance.");
}
