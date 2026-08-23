using Batonor.Abstractions;
using Batonor.Persistence.Sqlite;
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
}
