using Batonor.Abstractions;
using Batonor.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Batonor.Tests;

public class JsonSerializationTests
{
    [Fact]
    public void Instance_Round_Trips_With_Variables_Position_And_Definition()
    {
        var ser = new SystemTextJsonWorkflowSerializer();

        var instance = new WorkflowInstance
        {
            InstanceId = "i1",
            DefinitionId = "wf",
            DefinitionVersion = 2,
            Status = WorkflowStatus.Suspended,
            Variables = new JsonObject { ["order"] = new JsonObject { ["amount"] = 150 } },
            Position = new ExecutionPosition
            {
                NodeId = "seq",
                State = ExecutionPositionState.Running,
                SequenceIndex = 1,
                Child = new ExecutionPosition { NodeId = "decide", State = ExecutionPositionState.Running, SuspendedDecisionId = "abc" },
            },
            Definition = new WorkflowDefinition
            {
                Id = "wf",
                Version = 2,
                Steps = new[]
                {
                    new WorkflowNode { Id = "seq", Type = "sequence", Config = new JsonObject { ["steps"] = new JsonArray(JsonValue.Create("pre"), JsonValue.Create("decide")) } },
                    new WorkflowNode { Id = "decide", Type = "decision", Config = new JsonObject { ["prompt"] = "ok", ["options"] = new JsonArray() } },
                },
            },
        };

        var json = ser.SerializeInstance(instance);
        var back = ser.DeserializeInstance(json);

        Assert.Equal(WorkflowStatus.Suspended, back.Status);
        Assert.Equal(150, back.Variables!["order"]!["amount"]!.GetValue<int>());
        Assert.Equal("seq", back.Position!.NodeId);
        Assert.Equal("decide", back.Position!.Child!.NodeId);
        Assert.Equal(2, back.Definition!.Version);
        Assert.Equal("decision", back.Definition!.Steps[1].Type);
    }

    [Fact]
    public void PendingDecision_Round_Trips_When_Written_Via_Context()
    {
        var decision = new PendingDecision
        {
            DecisionId = "d1",
            InstanceId = "i1",
            NodeId = "approve",
            Prompt = "Approve?",
            Options = new[] { new DecisionOption { Label = "Yes", Value = "yes", IsDefault = true } },
        };

        var json = System.Text.Json.JsonSerializer.Serialize(decision, BatonorJsonContext.Default.PendingDecision);
        var back = System.Text.Json.JsonSerializer.Deserialize(json, BatonorJsonContext.Default.PendingDecision);

        Assert.NotNull(back);
        Assert.Equal("d1", back!.DecisionId);
        Assert.Equal("yes", back.Options[0].Value);
    }
}
