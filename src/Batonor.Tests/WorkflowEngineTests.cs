using System.Text.Json.Nodes;
using Batonor.Abstractions;
using Batonor.Core;
using Batonor.Expressions;
using Xunit;

namespace Batonor.Tests;

public class WorkflowEngineTests
{
    private static WorkflowEngine CreateEngine()
    {
        var activities = new Dictionary<string, IActivity>
        {
            ["double"] = new DoubleActivity(),
            ["echo"] = new EchoActivity(),
        };

        return new WorkflowEngine(
            new DictionaryActivityResolver(activities),
            new ConditionEvaluator(),
            new TemplateEngine());
    }

    [Fact]
    public async Task Engine_Runs_Sequence_And_Choice()
    {
        var engine = CreateEngine();

        var definition = new WorkflowDefinition
        {
            Id = "order-process",
            Version = 1,
            Steps = new[]
            {
                new WorkflowNode
                {
                    Id = "d",
                    Type = "double",
                    Config = new JsonObject { ["value"] = "${input.x}" },
                    Output = "result",
                },
                new WorkflowNode
                {
                    Id = "c",
                    Type = "choice",
                    Config = new JsonObject
                    {
                        ["branches"] = new JsonArray(
                            new JsonObject { ["when"] = "${result} > 10", ["then"] = "big" },
                            new JsonObject { ["default"] = "small" }),
                    },
                },
                new WorkflowNode { Id = "big", Type = "echo", Config = new JsonObject { ["value"] = "big" }, Output = "label" },
                new WorkflowNode { Id = "small", Type = "echo", Config = new JsonObject { ["value"] = "small" }, Output = "label" },
            },
        };

        var instance = await engine.StartAsync(definition, new JsonObject { ["x"] = 20.0 });

        Assert.True(instance.Status == WorkflowStatus.Completed, $"Status={instance.Status}, Error={instance.Error}");
        Assert.Equal(40.0, instance.Variables!["result"]!.GetValue<double>());
        Assert.Equal("big", instance.Variables!["label"]!.GetValue<string>());
    }

    [Fact]
    public async Task Engine_Takes_Default_Branch()
    {
        var engine = CreateEngine();

        var definition = new WorkflowDefinition
        {
            Id = "order-process",
            Version = 1,
            Steps = new[]
            {
                new WorkflowNode
                {
                    Id = "c",
                    Type = "choice",
                    Config = new JsonObject
                    {
                        ["branches"] = new JsonArray(
                            new JsonObject { ["when"] = "${input.x} > 10", ["then"] = "big" },
                            new JsonObject { ["default"] = "small" }),
                    },
                },
                new WorkflowNode { Id = "big", Type = "echo", Config = new JsonObject { ["value"] = "big" }, Output = "label" },
                new WorkflowNode { Id = "small", Type = "echo", Config = new JsonObject { ["value"] = "small" }, Output = "label" },
            },
        };

        var instance = await engine.StartAsync(definition, new JsonObject { ["x"] = 3.0 });

        Assert.Equal(WorkflowStatus.Completed, instance.Status);
        Assert.Equal("small", instance.Variables!["label"]!.GetValue<string>());
    }

    [Fact]
    public async Task Engine_Runs_Parallel_Branches()
    {
        var engine = CreateEngine();

        var definition = new WorkflowDefinition
        {
            Id = "parallel-test",
            Steps = new[]
            {
                new WorkflowNode
                {
                    Id = "p",
                    Type = "parallel",
                    Config = new JsonObject
                    {
                        ["branches"] = new JsonArray(
                            new JsonArray(JsonValue.Create("b1"), JsonValue.Create("b2")),
                            new JsonArray(JsonValue.Create("b3"))),
                    },
                },
                new WorkflowNode { Id = "b1", Type = "echo", Config = new JsonObject { ["value"] = "a" } },
                new WorkflowNode { Id = "b2", Type = "echo", Config = new JsonObject { ["value"] = "b" } },
                new WorkflowNode { Id = "b3", Type = "echo", Config = new JsonObject { ["value"] = "c" } },
            },
        };

        var instance = await engine.StartAsync(definition, null);

        Assert.Equal(WorkflowStatus.Completed, instance.Status);
    }

    private sealed class DoubleActivity : IActivity
    {
        public ValueTask<object?> ExecuteAsync(IActivityContext context, CancellationToken cancellationToken)
        {
            var value = context.Input?["value"]?.GetValue<double>() ?? 0;
            return ValueTask.FromResult<object?>(value * 2);
        }
    }

    private sealed class EchoActivity : IActivity
    {
        public ValueTask<object?> ExecuteAsync(IActivityContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult<object?>(context.Input?["value"]);
    }
}
