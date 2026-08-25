using Batonor;
using Batonor.Abstractions;
using System.Text.Json.Nodes;
using Xunit;

namespace Batonor.Tests;

public class BuilderTests
{
    private sealed class DoubleActivity : IActivity
    {
        public ValueTask<object?> ExecuteAsync(IActivityContext context, CancellationToken ct)
            => ValueTask.FromResult<object?>((context.Input?["value"]?.GetValue<double>() ?? 0) * 2);
    }

    [Fact]
    public async Task Builder_Assembles_An_Engine_And_Runs_A_Workflow()
    {
        var engine = BatonorEngine.CreateBuilder()
            .AddActivity<DoubleActivity>("double")
            .UseInMemory()
            .Build();

        var definition = new WorkflowDefinition
        {
            Id = "wf",
            Version = 1,
            Steps = new[]
            {
                new WorkflowNode { Id = "d", Type = "double", Config = new JsonObject { ["value"] = "${input.x}" }, Output = "result" },
            },
        };

        var instance = await engine.StartAsync(definition, new JsonObject { ["x"] = 21.0 });

        Assert.Equal(WorkflowStatus.Completed, instance.Status);
        Assert.Equal(42.0, instance.Variables!["result"]!.GetValue<double>());
    }

    [Fact]
    public async Task EndToEnd_InMemory_Engine_Runs_A_CommandLine_Step()
    {
        var engine = BatonorEngine.CreateBuilder()
            .UseBuiltInActivities()
            .UseInMemory()
            .Build();

        var definition = new WorkflowDefinition
        {
            Id = "e2e",
            Version = 1,
            Steps = new[]
            {
                new WorkflowNode { Id = "say", Type = "commandline", Config = new JsonObject
                {
                    ["executable"] = "cmd.exe",
                    ["args"] = new JsonArray(JsonValue.Create("/c"), JsonValue.Create("echo"), JsonValue.Create("hello")),
                    ["captureStdout"] = true,
                }, Output = "greeting" },
            },
        };

        var instance = await engine.StartAsync(definition, null);

        Assert.Equal(WorkflowStatus.Completed, instance.Status);
        Assert.Contains("hello", instance.Variables!["greeting"]!.GetValue<string>());
    }
}
