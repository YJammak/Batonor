using Batonor.Abstractions;
using Batonor.Core;
using Batonor.Expressions;
using Batonor.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using System.Text.Json.Nodes;
using Xunit;

namespace Batonor.Tests;

public class SqliteWorkflowStoreTests
{
    private static SqliteWorkflowStore CreateStore() => new("Data Source=:memory:");

    [Fact]
    public async Task Instance_Save_And_Load_Round_Trips()
    {
        var store = CreateStore();
        var instance = new WorkflowInstance
        {
            InstanceId = "i1",
            DefinitionId = "wf",
            DefinitionVersion = 1,
            Status = WorkflowStatus.Suspended,
            Variables = new JsonObject { ["x"] = 5 },
            Position = new ExecutionPosition { NodeId = "d", State = ExecutionPositionState.Running, SuspendedDecisionId = "pd1" },
        };

        await store.SaveInstanceAsync(instance);
        var loaded = await store.LoadInstanceAsync("i1");

        Assert.NotNull(loaded);
        Assert.Equal(WorkflowStatus.Suspended, loaded!.Status);
        Assert.Equal(5, loaded.Variables!["x"]!.GetValue<int>());
    }

    [Fact]
    public async Task Definition_Save_And_Load_Round_Trips()
    {
        var store = CreateStore();
        var def = new WorkflowDefinition { Id = "wf", Version = 3, Description = "d", Steps = new[] { new WorkflowNode { Id = "a", Type = "echo" } } };

        await store.SaveDefinitionAsync(def);
        var loaded = await store.LoadDefinitionAsync("wf", 3);

        Assert.NotNull(loaded);
        Assert.Equal(3, loaded!.Version);
        Assert.Equal("echo", loaded.Steps[0].Type);
    }

    [Fact]
    public async Task Pending_Decision_Can_Be_Saved_Listed_And_Completed()
    {
        var store = CreateStore();
        var decision = new PendingDecision
        {
            DecisionId = "d1",
            InstanceId = "i1",
            NodeId = "approve",
            Prompt = "Approve?",
            Options = new[] { new DecisionOption { Label = "Yes", Value = "yes", IsDefault = true } },
        };

        await store.SavePendingDecisionAsync(decision);
        var byId = await store.LoadPendingDecisionAsync("d1");
        var all = await store.ListPendingDecisionsAsync();

        Assert.Equal("approve", byId!.NodeId);
        Assert.Single(all);

        await store.CompleteDecisionAsync("d1", "yes");
        Assert.Null(await store.LoadPendingDecisionAsync("d1"));
    }

    [Fact]
    public async Task Load_After_Dispose_Throws_And_Dispose_Is_Idempotent()
    {
        var store = CreateStore();

        store.Dispose();
        store.Dispose(); // idempotent: a second Dispose must not throw

        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.LoadInstanceAsync("x"));
    }

    [Fact]
    public async Task Suspended_Instance_Resumes_Across_A_Fresh_Engine_And_Store()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"batonor-{Guid.NewGuid():N}.db");
        SqliteWorkflowStore? store1 = null, store2 = null;
        try
        {
            store1 = new SqliteWorkflowStore($"Data Source={dbPath}");
            var engine1 = CreateEngine(store1);
            RecordActivity.Calls.Clear();

            var suspended = await engine1.StartAsync(BuildDefinition(), null);
            Assert.Equal(WorkflowStatus.Suspended, suspended.Status);
            Assert.Equal(new[] { "pre" }, RecordActivity.Calls.ToArray());

            // "Process restart": a brand-new store + engine against the same DB file.
            store2 = new SqliteWorkflowStore($"Data Source={dbPath}");
            var engine2 = CreateEngine(store2);

            var pending = (await store2.ListPendingDecisionsAsync()).Single();
            var completed = await engine2.CompleteDecisionAsync(pending.DecisionId, "yes");

            Assert.Equal(WorkflowStatus.Completed, completed.Status);
            Assert.Equal(new[] { "pre", "ship" }, RecordActivity.Calls.ToArray());
        }
        finally
        {
            // Microsoft.Data.Sqlite pools connections by default, and the stores hold their
            // connections checked out. Dispose the stores so their connections return to the pool,
            // then clear the pool so the file handle is released and Windows lets us delete it.
            store2?.Dispose();
            store1?.Dispose();
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    private static WorkflowEngine CreateEngine(IWorkflowStore store)
    {
        var activities = new Dictionary<string, IActivity> { ["record"] = new RecordActivity() };
        return new WorkflowEngine(
            new DictionaryActivityResolver(activities),
            new ConditionEvaluator(),
            new TemplateEngine(),
            store);
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
                    ["options"] = new JsonArray(new JsonObject { ["label"] = "Yes", ["value"] = "yes", ["isDefault"] = true }),
                    ["branches"] = new JsonObject { ["yes"] = "ship", ["default"] = "ship" },
                }},
                new WorkflowNode { Id = "ship", Type = "record", Config = new JsonObject { ["value"] = "ship" } },
            },
        };
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
}
