using Batonor.Abstractions;
using Batonor.Core;
using Batonor.Expressions;
using Batonor.Persistence.InMemory;
using System.Text.Json.Nodes;
using Xunit;

namespace Batonor.Tests;

public class DecisionSuspendResumeTests
{
    private static WorkflowEngine CreateEngine(InMemoryWorkflowStore store)
    {
        var activities = new Dictionary<string, IActivity>
        {
            ["record"] = new RecordActivity(),
        };
        return new WorkflowEngine(
            new DictionaryActivityResolver(activities),
            new ConditionEvaluator(),
            new TemplateEngine(),
            store);
    }

    private sealed class RecordActivity : IActivity
    {
        public static readonly List<string> Calls = new();
        public ValueTask<object?> ExecuteAsync(IActivityContext context, CancellationToken ct)
        {
            Calls.Add(context.Input?["value"]?.GetValue<string>() ?? "");
            return ValueTask.FromResult<object?>(null);
        }
    }

    private static WorkflowDefinition BuildDefinition()
    {
        return new WorkflowDefinition
        {
            Id = "order",
            Version = 1,
            Steps = new[]
            {
                new WorkflowNode { Id = "seq", Type = "sequence", Config = new JsonObject
                {
                    ["steps"] = new JsonArray(JsonValue.Create("pre"), JsonValue.Create("approve")),
                }},
                new WorkflowNode { Id = "pre", Type = "record", Config = new JsonObject { ["value"] = "pre" } },
                new WorkflowNode { Id = "approve", Type = "decision", Config = new JsonObject
                {
                    ["prompt"] = "Approve?",
                    ["options"] = new JsonArray(
                        new JsonObject { ["label"] = "Yes", ["value"] = "yes", ["isDefault"] = true },
                        new JsonObject { ["label"] = "No", ["value"] = "no" }),
                    ["branches"] = new JsonObject { ["yes"] = "ship", ["no"] = "cancel", ["default"] = "cancel" },
                }},
                new WorkflowNode { Id = "ship", Type = "record", Config = new JsonObject { ["value"] = "ship" } },
                new WorkflowNode { Id = "cancel", Type = "record", Config = new JsonObject { ["value"] = "cancel" } },
            },
        };
    }

    [Fact]
    public async Task Decision_Suspends_The_Instance_And_Persists_Pending_Decision()
    {
        var store = new InMemoryWorkflowStore();
        var engine = CreateEngine(store);
        RecordActivity.Calls.Clear();

        var instance = await engine.StartAsync(BuildDefinition(), null);

        Assert.Equal(WorkflowStatus.Suspended, instance.Status);
        Assert.NotNull(instance.Position);
        Assert.Equal("seq", instance.Position!.NodeId);
        Assert.Equal(1, instance.Position!.SequenceIndex); // suspended at step index 1 (approve)
        Assert.Equal("approve", instance.Position!.Child!.NodeId);

        var pending = await store.ListPendingDecisionsAsync();
        Assert.Single(pending);
        Assert.Equal("approve", pending[0].NodeId);
        Assert.Equal(instance.InstanceId, pending[0].InstanceId);

        // 'pre' ran and wrote its output; the decision never ran a branch.
        Assert.Equal(new[] { "pre" }, RecordActivity.Calls.ToArray());
    }
}
