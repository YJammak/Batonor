using System.Text.Json.Nodes;
using Batonor.Abstractions;
using Batonor.Core;
using Batonor.Expressions;
using Xunit;

namespace Batonor.Tests;

// Top-level activity fixture. Batonor.SourceGen is referenced as an analyzer on this project, so at
// build time it scans the test sources for [Activity]-annotated classes and generates an
// ActivityRegistry (in this namespace) that registers this class under the name "double".
[Activity("double")]
public sealed class DoubleActivity : IActivity
{
    public ValueTask<object?> ExecuteAsync(IActivityContext context, CancellationToken ct)
        => ValueTask.FromResult<object?>((context.Input?["value"]?.GetValue<double>() ?? 0) * 2);
}

public class GeneratedRegistryE2E
{
    [Fact]
    public async Task E2E_Uses_SourceGenerated_ActivityRegistry()
    {
        // ActivityRegistry is generated at build time (Batonor.SourceGen is an analyzer on this project).
        var resolver = new ActivityRegistry();
        var engine = new WorkflowEngine(resolver, new ConditionEvaluator(), new TemplateEngine());

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
}
