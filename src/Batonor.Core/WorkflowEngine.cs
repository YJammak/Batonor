using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Batonor.Abstractions;

namespace Batonor.Core;

/// <summary>
/// Executes a workflow definition in memory. The first slice supports the <c>sequence</c>,
/// <c>choice</c> and <c>parallel</c> control flows plus activity leaf nodes.
/// Decision (suspend/resume), loops, retry and persistence are added in later steps.
/// </summary>
public sealed class WorkflowEngine
{
    private readonly IActivityResolver _activities;
    private readonly IExpressionEvaluator _expressions;
    private readonly ITemplateEngine _templates;
    private readonly IServiceProvider? _services;

    public WorkflowEngine(
        IActivityResolver activities,
        IExpressionEvaluator expressions,
        ITemplateEngine templates,
        IServiceProvider? services = null)
    {
        _activities = activities;
        _expressions = expressions;
        _templates = templates;
        _services = services;
    }

    public async Task<WorkflowInstance> StartAsync(
        WorkflowDefinition definition,
        JsonObject? input,
        ClaimsPrincipal? principal = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var instance = new WorkflowInstance
        {
            InstanceId = Guid.NewGuid().ToString("N"),
            DefinitionId = definition.Id,
            DefinitionVersion = definition.Version,
            Definition = definition,
            Variables = new JsonObject(),
            Status = WorkflowStatus.Running,
        };

        var index = BuildIndex(definition);
        var context = new ExecutionContext(instance.InstanceId, _activities, _expressions, _templates, _services);
        context.SetRoot("input", input ?? new JsonObject());

        if (definition.Variables is not null)
        {
            foreach (var (name, value) in definition.Variables)
            {
                context.SetRoot(name, value);
            }
        }

        try
        {
            foreach (var root in GetRootNodes(definition))
            {
                await RunNodeAsync(root, index, context, cancellationToken).ConfigureAwait(false);
            }

            instance.Status = WorkflowStatus.Completed;
        }
        catch (OperationCanceledException)
        {
            instance.Status = WorkflowStatus.Cancelled;
        }
        catch (Exception ex)
        {
            instance.Status = WorkflowStatus.Failed;
            instance.Error = ex.Message;
        }
        finally
        {
            instance.CompletedAt = DateTimeOffset.UtcNow;
            instance.Variables = context.Snapshot();
        }

        return instance;
    }

    private async Task RunNodeAsync(
        WorkflowNode node,
        IReadOnlyDictionary<string, WorkflowNode> index,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        switch (node.Type)
        {
            case "sequence":
                await RunSequenceAsync(node, index, context, cancellationToken).ConfigureAwait(false);
                break;
            case "choice":
                await RunChoiceAsync(node, index, context, cancellationToken).ConfigureAwait(false);
                break;
            case "parallel":
                await RunParallelAsync(node, index, context, cancellationToken).ConfigureAwait(false);
                break;
            default:
                await RunActivityAsync(node, context, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task RunNodeByIdAsync(
        string id,
        IReadOnlyDictionary<string, WorkflowNode> index,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (!index.TryGetValue(id, out var node))
        {
            throw new WorkflowDefinitionException($"Referenced node '{id}' was not found.");
        }

        await RunNodeAsync(node, index, context, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunSequenceAsync(
        WorkflowNode node,
        IReadOnlyDictionary<string, WorkflowNode> index,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (node.Config?["steps"] is not JsonArray steps)
        {
            return;
        }

        foreach (var step in steps)
        {
            if (step is JsonValue v && v.TryGetValue<string>(out var id))
            {
                await RunNodeByIdAsync(id, index, context, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task RunChoiceAsync(
        WorkflowNode node,
        IReadOnlyDictionary<string, WorkflowNode> index,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (node.Config?["branches"] is not JsonArray branches)
        {
            return;
        }

        foreach (var branch in branches.OfType<JsonObject>())
        {
            var target = ReadTarget(branch);
            if (target is null)
            {
                continue;
            }

            if (branch["when"] is JsonValue when && when.TryGetValue<string>(out var condition))
            {
                if (context.Evaluate(condition))
                {
                    await RunNodeByIdAsync(target, index, context, cancellationToken).ConfigureAwait(false);
                    return;
                }
            }
            else
            {
                // default branch
                await RunNodeByIdAsync(target, index, context, cancellationToken).ConfigureAwait(false);
                return;
            }
        }
    }

    private async Task RunParallelAsync(
        WorkflowNode node,
        IReadOnlyDictionary<string, WorkflowNode> index,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (node.Config?["branches"] is not JsonArray branches)
        {
            return;
        }

        // Each branch runs on an isolated copy of the scope (no shared writes — see design §6.4).
        var tasks = branches.OfType<JsonArray>()
            .Select(branch => RunBranchAsync(branch, index, context.Clone(), cancellationToken))
            .ToList();

        var join = ParseJoin(node);
        if (join == JoinMode.Any)
        {
            await Task.WhenAny(tasks).ConfigureAwait(false);
        }
        else
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }

    private async Task RunBranchAsync(
        JsonArray branch,
        IReadOnlyDictionary<string, WorkflowNode> index,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        foreach (var step in branch)
        {
            if (step is JsonValue v && v.TryGetValue<string>(out var id))
            {
                await RunNodeByIdAsync(id, index, context, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task RunActivityAsync(
        WorkflowNode node,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        var activity = context.ResolveActivity(node.Type)
            ?? throw new ActivityNotFoundException(node.Type);

        var input = ResolveConfig(node.Config, context);
        var attemptId = BuildAttemptId(context, node.Id);
        var activityContext = new ActivityContext(node.Type, attemptId, input, context.Scope, context.Services);

        var output = await activity.ExecuteAsync(activityContext, cancellationToken).ConfigureAwait(false);

        if (node.Output is not null)
        {
            context.SetVariable(node.Output, ToJsonNode(output));
        }

        context.SetNodeOutput(node.Id, ToJsonNode(output));
    }

    private static string? ReadTarget(JsonObject branch)
    {
        foreach (var key in new[] { "then", "default" })
        {
            if (branch[key] is JsonValue v && v.TryGetValue<string>(out var id))
            {
                return id;
            }
        }

        return null;
    }

    private static JoinMode ParseJoin(WorkflowNode node)
    {
        if (node.Config?["join"] is JsonValue v &&
            v.TryGetValue<string>(out var join) &&
            string.Equals(join, "any", StringComparison.OrdinalIgnoreCase))
        {
            return JoinMode.Any;
        }

        return JoinMode.All;
    }

    private static string BuildAttemptId(ExecutionContext context, string nodeId)
    {
        // Stable across retries/recovery for the same node within the same instance.
        return $"{context.InstanceId}:{nodeId}";
    }

    private static Dictionary<string, WorkflowNode> BuildIndex(WorkflowDefinition definition)
    {
        var index = new Dictionary<string, WorkflowNode>(StringComparer.Ordinal);
        foreach (var node in definition.Steps)
        {
            if (node.Id.Length > 0)
            {
                index[node.Id] = node;
            }
        }

        return index;
    }

    /// <summary>
    /// Root nodes are those not referenced as a target by any control-flow node,
    /// in declaration order (matches the flat-list definition model).
    /// </summary>
    private static IReadOnlyList<WorkflowNode> GetRootNodes(WorkflowDefinition definition)
    {
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in definition.Steps)
        {
            foreach (var id in GetTargetIds(node))
            {
                referenced.Add(id);
            }
        }

        var roots = new List<WorkflowNode>();
        foreach (var node in definition.Steps)
        {
            if (!referenced.Contains(node.Id))
            {
                roots.Add(node);
            }
        }

        return roots;
    }

    private static IEnumerable<string> GetTargetIds(WorkflowNode node)
    {
        switch (node.Type)
        {
            case "choice":
                if (node.Config?["branches"] is JsonArray branches)
                {
                    foreach (var branch in branches.OfType<JsonObject>())
                    {
                        var target = ReadTarget(branch);
                        if (target is not null)
                        {
                            yield return target;
                        }
                    }
                }
                break;

            case "parallel":
                if (node.Config?["branches"] is JsonArray parBranches)
                {
                    foreach (var branch in parBranches.OfType<JsonArray>())
                    {
                        foreach (var step in branch)
                        {
                            if (step is JsonValue v && v.TryGetValue<string>(out var id))
                            {
                                yield return id;
                            }
                        }
                    }
                }
                break;
        }
    }

    private static JsonNode? ResolveConfig(JsonNode? config, ExecutionContext context)
    {
        switch (config)
        {
            case null:
                return null;

            case JsonValue v when v.TryGetValue<string>(out var s):
                return context.Render(s);

            case JsonObject obj:
            {
                var result = new JsonObject();
                foreach (var (key, value) in obj)
                {
                    result[key] = ResolveConfig(value, context);
                }

                return result;
            }

            case JsonArray arr:
            {
                var result = new JsonArray();
                foreach (var item in arr)
                {
                    result.Add(ResolveConfig(item, context));
                }

                return result;
            }

            default:
                return config.DeepClone();
        }
    }

    private static JsonNode? ToJsonNode(object? value)
    {
        return value switch
        {
            null => null,
            JsonNode n => n,
            string s => JsonValue.Create(s),
            bool b => JsonValue.Create(b),
            int i => JsonValue.Create(i),
            long l => JsonValue.Create(l),
            double d => JsonValue.Create(d),
            decimal m => JsonValue.Create(m),
            _ => JsonSerializer.SerializeToNode(value),
        };
    }
}
