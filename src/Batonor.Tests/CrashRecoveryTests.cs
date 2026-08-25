using Batonor.Abstractions;
using Batonor.Core;
using Batonor.Expressions;
using Batonor.Persistence.InMemory;
using System.Text.Json.Nodes;
using Xunit;

namespace Batonor.Tests;

/// <summary>
/// Regression guard for the durable-state write ordering (see the Round 3 plan).
/// </summary>
public class CrashRecoveryTests
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
    public async Task CompleteDecision_Persists_Instance_And_Retires_Answer()
    {
        var store = new InMemoryWorkflowStore();
        var engine = CreateEngine(store);
        RecordActivity.Calls.Clear();

        var suspended = await engine.StartAsync(BuildDefinition(), null);
        Assert.Equal(WorkflowStatus.Suspended, suspended.Status);

        var pending = (await store.ListPendingDecisionsAsync()).Single();
        var completed = await engine.CompleteDecisionAsync(pending.DecisionId, "yes");

        Assert.Equal(WorkflowStatus.Completed, completed.Status);

        // The answered decision must be retired, and the instance must be persisted as Completed.
        Assert.Empty(await store.ListPendingDecisionsAsync());
        var persisted = await store.LoadInstanceAsync(completed.InstanceId);
        Assert.Equal(WorkflowStatus.Completed, persisted!.Status);
        Assert.Equal(new[] { "pre", "ship" }, RecordActivity.Calls.ToArray());
    }

    [Fact]
    public async Task Recover_Reruns_AtLeastOnce_Activity_After_Crash()
    {
        var store = new InMemoryWorkflowStore();
        var engine = CreateEngine(store);
        RecordActivity.Calls.Clear();

        var def = new WorkflowDefinition
        {
            Id = "crash",
            Version = 1,
            Steps = new[]
            {
                new WorkflowNode { Id = "seq", Type = "sequence", Config = new JsonObject
                { ["steps"] = new JsonArray(JsonValue.Create("a"), JsonValue.Create("b")) }},
                new WorkflowNode { Id = "a", Type = "record", Recovery = RecoveryPolicy.AtLeastOnce, Config = new JsonObject { ["value"] = "a" } },
                new WorkflowNode { Id = "b", Type = "record", Config = new JsonObject { ["value"] = "b" } },
            },
        };

        // Simulate a crash checkpoint: a Running instance whose position points at activity "a".
        var instance = new WorkflowInstance
        {
            InstanceId = "crash-1",
            DefinitionId = def.Id,
            DefinitionVersion = 1,
            Definition = def,
            Variables = new JsonObject(),
            Status = WorkflowStatus.Running,
            Position = new ExecutionPosition
            {
                NodeId = "seq",
                State = ExecutionPositionState.Running,
                SequenceIndex = 0,
                Child = new ExecutionPosition { NodeId = "a", State = ExecutionPositionState.Running },
            },
        };
        await store.SaveInstanceAsync(instance);

        var recovered = await engine.RecoverAsync("crash-1");

        Assert.Equal(WorkflowStatus.Completed, recovered.Status);
        // AtLeastOnce → a is re-run, then b executes
        Assert.Equal(new[] { "a", "b" }, RecordActivity.Calls.ToArray());
    }

    [Fact]
    public async Task Recover_Skips_AtMostOnce_Activity_And_Continues()
    {
        var store = new InMemoryWorkflowStore();
        var engine = CreateEngine(store);
        RecordActivity.Calls.Clear();

        var def = new WorkflowDefinition
        {
            Id = "crash",
            Version = 1,
            Steps = new[]
            {
                new WorkflowNode { Id = "seq", Type = "sequence", Config = new JsonObject
                { ["steps"] = new JsonArray(JsonValue.Create("a"), JsonValue.Create("b")) }},
                new WorkflowNode { Id = "a", Type = "record", Recovery = RecoveryPolicy.AtMostOnce, Config = new JsonObject { ["value"] = "a" } },
                new WorkflowNode { Id = "b", Type = "record", Config = new JsonObject { ["value"] = "b" } },
            },
        };

        var instance = new WorkflowInstance
        {
            InstanceId = "crash-1",
            DefinitionId = def.Id,
            DefinitionVersion = 1,
            Definition = def,
            Variables = new JsonObject(),
            Status = WorkflowStatus.Running,
            Position = new ExecutionPosition
            {
                NodeId = "seq",
                State = ExecutionPositionState.Running,
                SequenceIndex = 0,
                Child = new ExecutionPosition { NodeId = "a", State = ExecutionPositionState.Running },
            },
        };
        await store.SaveInstanceAsync(instance);

        var recovered = await engine.RecoverAsync("crash-1");

        Assert.Equal(WorkflowStatus.Completed, recovered.Status);
        // AtMostOnce → a is skipped, only b executes
        Assert.Equal(new[] { "b" }, RecordActivity.Calls.ToArray());
    }
}
