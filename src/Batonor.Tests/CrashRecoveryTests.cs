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
    private static WorkflowEngine CreateEngine(IWorkflowStore store)
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

    [Fact]
    public async Task Recover_Skips_Only_Interrupted_AtMostOnce_Activity()
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
                new WorkflowNode { Id = "b", Type = "record", Recovery = RecoveryPolicy.AtMostOnce, Config = new JsonObject { ["value"] = "b" } },
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
        // Both nodes are AtMostOnce, but only the interrupted node "a" is skipped —
        // the sibling "b" must still execute (regression for the whole-subtree skip).
        Assert.Equal(new[] { "b" }, RecordActivity.Calls.ToArray());
    }

    private sealed class RecordingStore : IWorkflowStore
    {
        private readonly InMemoryWorkflowStore _inner = new();
        public bool SawRunningSave { get; private set; }
        public List<ExecutionPosition?> RunningPositions { get; } = new();

        public Task SaveDefinitionAsync(WorkflowDefinition definition, CancellationToken ct = default) =>
            _inner.SaveDefinitionAsync(definition, ct);

        public Task<WorkflowDefinition?> LoadDefinitionAsync(string id, int version, CancellationToken ct = default) =>
            _inner.LoadDefinitionAsync(id, version, ct);

        public async Task SaveInstanceAsync(WorkflowInstance instance, CancellationToken ct = default)
        {
            if (instance.Status == WorkflowStatus.Running)
            {
                SawRunningSave = true;
                RunningPositions.Add(instance.Position);
            }

            await _inner.SaveInstanceAsync(instance, ct);
        }

        public Task<WorkflowInstance?> LoadInstanceAsync(string instanceId, CancellationToken ct = default) =>
            _inner.LoadInstanceAsync(instanceId, ct);

        public Task<IReadOnlyList<PendingDecision>> ListPendingDecisionsAsync(CancellationToken ct = default) =>
            _inner.ListPendingDecisionsAsync(ct);

        public Task CompleteDecisionAsync(string decisionId, string choice, CancellationToken ct = default) =>
            _inner.CompleteDecisionAsync(decisionId, choice, ct);

        public Task SavePendingDecisionAsync(PendingDecision decision, CancellationToken ct = default) =>
            _inner.SavePendingDecisionAsync(decision, ct);

        public Task<PendingDecision?> LoadPendingDecisionAsync(string decisionId, CancellationToken ct = default) =>
            _inner.LoadPendingDecisionAsync(decisionId, ct);
    }

    private static string Terminal(ExecutionPosition? position)
    {
        while (position?.Child is not null)
        {
            position = position.Child;
        }

        return position?.NodeId ?? "";
    }

    [Fact]
    public async Task CompleteDecision_Checkpoints_Position_At_Routed_Branch()
    {
        var store = new RecordingStore();
        var engine = CreateEngine(store);
        RecordActivity.Calls.Clear();

        var def = new WorkflowDefinition
        {
            Id = "decision-crash",
            Version = 1,
            Steps = new[]
            {
                new WorkflowNode { Id = "seq", Type = "sequence", Config = new JsonObject
                { ["steps"] = new JsonArray(JsonValue.Create("pre"), JsonValue.Create("approve")) }},
                new WorkflowNode { Id = "pre", Type = "record", Config = new JsonObject { ["value"] = "pre" } },
                new WorkflowNode { Id = "approve", Type = "decision", Config = new JsonObject
                {
                    ["prompt"] = "Approve?",
                    ["options"] = new JsonArray(new JsonObject { ["label"] = "Yes", ["value"] = "yes", ["isDefault"] = true }),
                    ["branches"] = new JsonObject { ["yes"] = "ship", ["no"] = "cancel", ["default"] = "cancel" },
                }},
                new WorkflowNode { Id = "ship", Type = "record", Config = new JsonObject { ["value"] = "ship" } },
                new WorkflowNode { Id = "cancel", Type = "record", Config = new JsonObject { ["value"] = "cancel" } },
            },
        };

        var suspended = await engine.StartAsync(def, null);
        Assert.Equal(WorkflowStatus.Suspended, suspended.Status);

        var pending = (await store.ListPendingDecisionsAsync()).Single();
        await engine.CompleteDecisionAsync(pending.DecisionId, "yes");

        // The crash checkpoint written while the routed "ship" branch executes must point at the
        // routed activity ("ship"), not at the "approve" decision node. If it pointed at the decision,
        // a crash during "ship" would re-consult the decision on recovery and fall back to the default
        // branch ("cancel") — turning an approval into a cancellation.
        Assert.Contains(store.RunningPositions, p => Terminal(p) == "ship");
        Assert.DoesNotContain(store.RunningPositions, p => Terminal(p) == "approve");
    }

    [Fact]
    public async Task Recover_Routes_A_Recorded_Decision_To_Its_Chosen_Branch()
    {
        var store = new InMemoryWorkflowStore();
        var engine = CreateEngine(store);
        RecordActivity.Calls.Clear();

        var def = new WorkflowDefinition
        {
            Id = "decision-crash",
            Version = 1,
            Steps = new[]
            {
                new WorkflowNode { Id = "seq", Type = "sequence", Config = new JsonObject
                { ["steps"] = new JsonArray(JsonValue.Create("pre"), JsonValue.Create("approve")) }},
                new WorkflowNode { Id = "pre", Type = "record", Config = new JsonObject { ["value"] = "pre" } },
                new WorkflowNode { Id = "approve", Type = "decision", Config = new JsonObject
                {
                    ["prompt"] = "Approve?",
                    ["options"] = new JsonArray(new JsonObject { ["label"] = "Yes", ["value"] = "yes", ["isDefault"] = true }),
                    ["branches"] = new JsonObject { ["yes"] = "ship", ["no"] = "cancel", ["default"] = "cancel" },
                }},
                new WorkflowNode { Id = "ship", Type = "record", Config = new JsonObject { ["value"] = "ship" } },
                new WorkflowNode { Id = "cancel", Type = "record", Config = new JsonObject { ["value"] = "cancel" } },
            },
        };

        // A crash checkpoint written while the routed "ship" branch was executing: the decision frame
        // records ChosenBranch = ship, so recovery routes to ship — not the default branch (cancel).
        var instance = new WorkflowInstance
        {
            InstanceId = "crash-d1",
            DefinitionId = def.Id,
            DefinitionVersion = 1,
            Definition = def,
            Variables = new JsonObject(),
            Status = WorkflowStatus.Running,
            Position = new ExecutionPosition
            {
                NodeId = "seq",
                State = ExecutionPositionState.Running,
                SequenceIndex = 1,
                Child = new ExecutionPosition
                {
                    NodeId = "approve",
                    State = ExecutionPositionState.Running,
                    ChosenBranch = "ship",
                    Child = new ExecutionPosition { NodeId = "ship", State = ExecutionPositionState.Running },
                },
            },
        };
        await store.SaveInstanceAsync(instance);

        var recovered = await engine.RecoverAsync("crash-d1");

        Assert.Equal(WorkflowStatus.Completed, recovered.Status);
        Assert.Equal(new[] { "ship" }, RecordActivity.Calls.ToArray());
    }

    [Fact]
    public async Task StartAsync_Checkpoints_Running_State_Before_Activities()
    {
        var store = new RecordingStore();
        var engine = CreateEngine(store);
        RecordActivity.Calls.Clear();

        var def = new WorkflowDefinition
        {
            Id = "cp",
            Version = 1,
            Steps = new[]
            {
                new WorkflowNode { Id = "s", Type = "sequence", Config = new JsonObject
                { ["steps"] = new JsonArray(JsonValue.Create("a"), JsonValue.Create("b")) }},
                new WorkflowNode { Id = "a", Type = "record", Config = new JsonObject { ["value"] = "a" } },
                new WorkflowNode { Id = "b", Type = "record", Config = new JsonObject { ["value"] = "b" } },
            },
        };

        await engine.StartAsync(def, null);

        Assert.True(store.SawRunningSave, "Expected at least one Running checkpoint before an activity.");
    }

    [Fact]
    public async Task CompleteDecision_Surfaces_Domain_Errors_To_Caller()
    {
        var store = new InMemoryWorkflowStore();
        var engine = CreateEngine(store);
        RecordActivity.Calls.Clear();

        // 构造一个 Running 实例，其 Position 指向一个不存在的 node id => RecoverAsync 应抛领域错误而非吞成 Failed。
        var def = new WorkflowDefinition
        {
            Id = "x",
            Version = 1,
            Steps = new[] { new WorkflowNode { Id = "a", Type = "record", Config = new JsonObject { ["value"] = "a" } } },
        };
        var instance = new WorkflowInstance
        {
            InstanceId = "c1",
            DefinitionId = def.Id,
            DefinitionVersion = 1,
            Definition = def,
            Variables = new JsonObject(),
            Status = WorkflowStatus.Running,
            Position = new ExecutionPosition { NodeId = "does-not-exist", State = ExecutionPositionState.Running },
        };
        await store.SaveInstanceAsync(instance);

        // Position 指向不存在的 node：当前是 KeyNotFoundException（被 catch(Exception) 吞成 Failed）。
        // 修复后应抛 BatonorException（清晰领域错误）。
        await Assert.ThrowsAsync<BatonorException>(() => engine.RecoverAsync("c1"));
    }
}
