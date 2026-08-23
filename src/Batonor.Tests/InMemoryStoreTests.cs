using Batonor.Abstractions;
using Batonor.Persistence.InMemory;
using Xunit;

namespace Batonor.Tests;

public class InMemoryStoreTests
{
    [Fact]
    public async Task Save_And_Load_Instance_Round_Trips()
    {
        var store = new InMemoryWorkflowStore();
        var instance = new WorkflowInstance
        {
            InstanceId = "i1",
            DefinitionId = "wf",
            DefinitionVersion = 1,
            Variables = new System.Text.Json.Nodes.JsonObject { ["x"] = 5 },
        };

        await store.SaveInstanceAsync(instance);
        var loaded = await store.LoadInstanceAsync("i1");

        Assert.NotNull(loaded);
        Assert.Equal("wf", loaded!.DefinitionId);
        Assert.Equal(5, loaded.Variables!["x"]!.GetValue<int>());
    }

    [Fact]
    public async Task Pending_Decision_Can_Be_Saved_Listed_And_Completed()
    {
        var store = new InMemoryWorkflowStore();
        var decision = new PendingDecision
        {
            DecisionId = "d1",
            InstanceId = "i1",
            NodeId = "approve",
            Prompt = "Approve?",
            Options = new[]
            {
                new DecisionOption { Label = "Yes", Value = "yes", IsDefault = true },
                new DecisionOption { Label = "No", Value = "no" },
            },
        };

        await store.SavePendingDecisionAsync(decision);

        var byId = await store.LoadPendingDecisionAsync("d1");
        Assert.NotNull(byId);
        Assert.Equal("approve", byId!.NodeId);

        var all = await store.ListPendingDecisionsAsync();
        Assert.Single(all);

        await store.CompleteDecisionAsync("d1", "yes");
        Assert.Null(await store.LoadPendingDecisionAsync("d1"));
        Assert.Empty(await store.ListPendingDecisionsAsync());
    }

    [Fact]
    public async Task Definition_Save_And_Load_Round_Trips()
    {
        var store = new InMemoryWorkflowStore();
        var def = new WorkflowDefinition { Id = "wf", Version = 3 };

        await store.SaveDefinitionAsync(def);
        var loaded = await store.LoadDefinitionAsync("wf", 3);

        Assert.NotNull(loaded);
        Assert.Equal(3, loaded!.Version);
    }
}
