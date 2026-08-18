namespace Batonor.Abstractions;

/// <summary>
/// Serializes workflow definitions and instance state. Default implementation uses
/// System.Text.Json source generation (AOT-safe); alternatives: MessagePack, protobuf.
/// </summary>
public interface IWorkflowSerializer
{
    string SerializeDefinition(WorkflowDefinition definition);

    WorkflowDefinition DeserializeDefinition(string json);

    string SerializeInstance(WorkflowInstance instance);

    WorkflowInstance DeserializeInstance(string json);
}
