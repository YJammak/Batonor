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

    [Fact]
    public async Task CompleteDecision_Resumes_And_Continues_Sequence()
    {
        var store = new InMemoryWorkflowStore();
        var engine = CreateEngine(store);
        RecordActivity.Calls.Clear();

        var instance = await engine.StartAsync(BuildDefinition(), null);
        Assert.Equal(WorkflowStatus.Suspended, instance.Status);

        var pending = (await store.ListPendingDecisionsAsync()).Single();
        var resumed = await engine.CompleteDecisionAsync(pending.DecisionId, "yes");

        Assert.Equal(WorkflowStatus.Completed, resumed.Status);
        Assert.Null(resumed.Position);
        Assert.Equal(new[] { "pre", "ship" }, RecordActivity.Calls.ToArray());
    }

    [Fact]
    public async Task CompleteDecision_Reject_Goes_To_Default_Branch()
    {
        var store = new InMemoryWorkflowStore();
        var engine = CreateEngine(store);
        RecordActivity.Calls.Clear();

        var instance = await engine.StartAsync(BuildDefinition(), null);
        var pending = (await store.ListPendingDecisionsAsync()).Single();

        var resumed = await engine.CompleteDecisionAsync(pending.DecisionId, "no");

        Assert.Equal(WorkflowStatus.Completed, resumed.Status);
        Assert.Equal(new[] { "pre", "cancel" }, RecordActivity.Calls.ToArray());
    }

    [Fact]
    public async Task Suspend_Without_Store_Throws()
    {
        // 'record' must be registered so the workflow reaches the decision node; only then does
        // the absence of a store surface as a throw (the test's intent).
        var engine = new WorkflowEngine(
            new DictionaryActivityResolver(new Dictionary<string, IActivity> { ["record"] = new RecordActivity() }),
            new ConditionEvaluator(),
            new TemplateEngine());

        await Assert.ThrowsAsync<BatonorException>(() => engine.StartAsync(BuildDefinition(), null));
    }

    [Fact]
    public async Task CompleteDecision_Illegal_Choice_Throws()
    {
        var store = new InMemoryWorkflowStore();
        var engine = CreateEngine(store);

        var instance = await engine.StartAsync(BuildDefinition(), null);
        var pending = (await store.ListPendingDecisionsAsync()).Single();

        await Assert.ThrowsAsync<BatonorException>(
            () => engine.CompleteDecisionAsync(pending.DecisionId, "not-an-option"));

        // The invalid choice must not fail the instance; it stays suspended in the store.
        var again = await store.LoadInstanceAsync(instance.InstanceId);
        Assert.Equal(WorkflowStatus.Suspended, again!.Status);
    }

    [Fact]
    public async Task Decision_Nested_In_Choice_Resumes_And_Can_Resuspend()
    {
        var store = new InMemoryWorkflowStore();
        var engine = CreateEngine(store);
        RecordActivity.Calls.Clear();

        var def = new WorkflowDefinition
        {
            Id = "nested",
            Version = 1,
            Steps = new[]
            {
                new WorkflowNode { Id = "gate", Type = "choice", Config = new JsonObject
                {
                    ["branches"] = new JsonArray(
                        new JsonObject { ["when"] = "${input.go} == 'true'", ["then"] = "ask" }),
                }},
                new WorkflowNode { Id = "ask", Type = "decision", Config = new JsonObject
                {
                    ["prompt"] = "Proceed?",
                    ["options"] = new JsonArray(
                        new JsonObject { ["label"] = "Yes", ["value"] = "go", ["isDefault"] = true },
                        new JsonObject { ["label"] = "Stop", ["value"] = "stop" }),
                    ["branches"] = new JsonObject { ["go"] = "done", ["stop"] = "halt" },
                }},
                new WorkflowNode { Id = "done", Type = "record", Config = new JsonObject { ["value"] = "done" } },
                new WorkflowNode { Id = "halt", Type = "record", Config = new JsonObject { ["value"] = "halt" } },
            },
        };

        var s1 = await engine.StartAsync(def, new JsonObject { ["go"] = "true" });
        Assert.Equal(WorkflowStatus.Suspended, s1.Status);
        Assert.Equal("gate", s1.Position!.NodeId);
        Assert.Equal("ask", s1.Position!.Child!.NodeId);

        var p1 = (await store.ListPendingDecisionsAsync()).Single();
        var s2 = await engine.CompleteDecisionAsync(p1.DecisionId, "go");
        Assert.Equal(WorkflowStatus.Completed, s2.Status);
        Assert.Equal(new[] { "done" }, RecordActivity.Calls.ToArray());
    }

    [Fact]
    public async Task Two_Decisions_Sequentially_Suspend_Twice()
    {
        var store = new InMemoryWorkflowStore();
        var engine = CreateEngine(store);
        RecordActivity.Calls.Clear();

        var def = new WorkflowDefinition
        {
            Id = "two",
            Version = 1,
            Steps = new[]
            {
                new WorkflowNode { Id = "s", Type = "sequence", Config = new JsonObject
                {
                    ["steps"] = new JsonArray(JsonValue.Create("ask1"), JsonValue.Create("ask2")),
                }},
                new WorkflowNode { Id = "ask1", Type = "decision", Config = new JsonObject
                {
                    ["prompt"] = "First?",
                    ["options"] = new JsonArray(new JsonObject { ["label"] = "Y", ["value"] = "y", ["isDefault"] = true }),
                    ["branches"] = new JsonObject { ["y"] = "a1", ["default"] = "a1" },
                }},
                new WorkflowNode { Id = "a1", Type = "record", Config = new JsonObject { ["value"] = "a1" } },
                new WorkflowNode { Id = "ask2", Type = "decision", Config = new JsonObject
                {
                    ["prompt"] = "Second?",
                    ["options"] = new JsonArray(new JsonObject { ["label"] = "Y", ["value"] = "y", ["isDefault"] = true }),
                    ["branches"] = new JsonObject { ["y"] = "a2", ["default"] = "a2" },
                }},
                new WorkflowNode { Id = "a2", Type = "record", Config = new JsonObject { ["value"] = "a2" } },
            },
        };

        var s1 = await engine.StartAsync(def, null);
        Assert.Equal(WorkflowStatus.Suspended, s1.Status);

        var p1 = (await store.ListPendingDecisionsAsync()).Single();
        var s2 = await engine.CompleteDecisionAsync(p1.DecisionId, "y");
        Assert.Equal(WorkflowStatus.Suspended, s2.Status);
        Assert.Equal(new[] { "a1" }, RecordActivity.Calls.ToArray());

        var p2 = (await store.ListPendingDecisionsAsync()).Single(p => p.NodeId == "ask2");
        var s3 = await engine.CompleteDecisionAsync(p2.DecisionId, "y");
        Assert.Equal(WorkflowStatus.Completed, s3.Status);
        Assert.Equal(new[] { "a1", "a2" }, RecordActivity.Calls.ToArray());
    }
}
