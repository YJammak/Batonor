using Batonor.Abstractions;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Batonor.Json;

/// <summary>
/// AOT-safe System.Text.Json source-generated serialization context for Batonor's persisted types.
/// </summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(WorkflowDefinition))]
[JsonSerializable(typeof(WorkflowInstance))]
[JsonSerializable(typeof(WorkflowNode))]
[JsonSerializable(typeof(NodeErrorHandling))]
[JsonSerializable(typeof(ExecutionPosition))]
[JsonSerializable(typeof(ExecutionPositionState))]
[JsonSerializable(typeof(PendingDecision))]
[JsonSerializable(typeof(DecisionOption))]
[JsonSerializable(typeof(JsonNode))]
public partial class BatonorJsonContext : JsonSerializerContext
{
}
