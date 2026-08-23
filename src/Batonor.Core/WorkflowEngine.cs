using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Batonor.Abstractions;

namespace Batonor.Core;

/// <summary>
/// Executes a workflow definition in memory. The first slice supports the <c>sequence</c>,
/// <c>choice</c> and <c>parallel</c> control flows plus activity leaf nodes. Decision
/// (suspend/resume) is wired to an <see cref="IWorkflowStore"/>; loops, retry and persistence
/// are added in later steps.
/// </summary>
public sealed class WorkflowEngine
{
    private readonly IActivityResolver _activities;
    private readonly IExpressionEvaluator _expressions;
    private readonly ITemplateEngine _templates;
    private readonly IServiceProvider? _services;
    private readonly IWorkflowStore? _store;

    public WorkflowEngine(
        IActivityResolver activities,
        IExpressionEvaluator expressions,
        ITemplateEngine templates,
        IWorkflowStore? store = null,
        IServiceProvider? services = null)
    {
        _activities = activities;
        _expressions = expressions;
        _templates = templates;
        _store = store;
        _services = services;
    }

    /// <summary>A suspension signal: the position that was reached and the decision that paused it.</summary>
    private sealed record SuspendInfo(ExecutionPosition Position, PendingDecision Decision);

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

        SuspendInfo? suspend = null;
        try
        {
            foreach (var root in GetRootNodes(definition))
            {
                suspend = await RunNodeAsync(root, index, context, cancellationToken, null, null);
                if (suspend is not null)
                {
                    break;
                }
            }

            if (suspend is null)
            {
                instance.Status = WorkflowStatus.Completed;
            }
            else
            {
                if (_store is null)
                {
                    throw new BatonorException(
                        "Workflow suspended at a decision but no workflow store is configured.");
                }

                instance.Status = WorkflowStatus.Suspended;
                instance.Position = suspend.Position;
                await _store.SavePendingDecisionAsync(suspend.Decision, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            instance.Status = WorkflowStatus.Cancelled;
        }
        catch (BatonorException)
        {
            // Domain errors (bad definition, missing activity, no store) are surfaced to the caller;
            // only unexpected runtime failures are captured as a failed instance.
            throw;
        }
        catch (Exception ex)
        {
            instance.Status = WorkflowStatus.Failed;
            instance.Error = ex.Message;
        }
        finally
        {
            instance.Variables = context.Snapshot();
        }

        if (_store is not null)
        {
            await _store.SaveInstanceAsync(instance, cancellationToken);
        }

        return instance;
    }

    private async Task<SuspendInfo?> RunNodeAsync(
        WorkflowNode node,
        IReadOnlyDictionary<string, WorkflowNode> index,
        ExecutionContext context,
        CancellationToken cancellationToken,
        ExecutionPosition? frame,
        IReadOnlyDictionary<string, string>? pendingChoices)
    {
        cancellationToken.ThrowIfCancellationRequested();

        switch (node.Type)
        {
            case "sequence": return await RunSequenceAsync(node, index, context, cancellationToken, frame, pendingChoices);
            case "choice": return await RunChoiceAsync(node, index, context, cancellationToken, frame, pendingChoices);
            case "parallel": return await RunParallelAsync(node, index, context, cancellationToken);
            case "decision": return await RunDecisionAsync(node, index, context, cancellationToken, frame, pendingChoices);
            default: await RunActivityAsync(node, context, cancellationToken); return null;
        }
    }

    private async Task<SuspendInfo?> RunNodeByIdAsync(
        string id,
        IReadOnlyDictionary<string, WorkflowNode> index,
        ExecutionContext context,
        CancellationToken cancellationToken,
        ExecutionPosition? frame,
        IReadOnlyDictionary<string, string>? pendingChoices)
    {
        if (!index.TryGetValue(id, out var node))
        {
            throw new WorkflowDefinitionException($"Referenced node '{id}' was not found.");
        }

        return await RunNodeAsync(node, index, context, cancellationToken, frame, pendingChoices);
    }

    private async Task<SuspendInfo?> RunSequenceAsync(
        WorkflowNode node,
        IReadOnlyDictionary<string, WorkflowNode> index,
        ExecutionContext context,
        CancellationToken cancellationToken,
        ExecutionPosition? frame,
        IReadOnlyDictionary<string, string>? pendingChoices)
    {
        if (node.Config?["steps"] is not JsonArray steps)
        {
            return null;
        }

        var startIndex = frame?.SequenceIndex ?? 0;
        for (var i = startIndex; i < steps.Count; i++)
        {
            if (steps[i] is not JsonValue v || !v.TryGetValue<string>(out var id))
            {
                continue;
            }

            var stepFrame = frame is not null && i == frame.SequenceIndex ? frame.Child : null;
            var suspend = await RunNodeByIdAsync(id, index, context, cancellationToken, stepFrame, pendingChoices);
            if (suspend is not null)
            {
                return suspend with { Position = WrapSequence(node.Id, i, suspend.Position) };
            }
        }

        return null;
    }

    private static ExecutionPosition WrapSequence(string nodeId, int stepIndex, ExecutionPosition child) => new()
    {
        NodeId = nodeId,
        State = ExecutionPositionState.Running,
        SequenceIndex = stepIndex,
        Child = child,
    };

    private async Task<SuspendInfo?> RunChoiceAsync(
        WorkflowNode node,
        IReadOnlyDictionary<string, WorkflowNode> index,
        ExecutionContext context,
        CancellationToken cancellationToken,
        ExecutionPosition? frame,
        IReadOnlyDictionary<string, string>? pendingChoices)
    {
        if (node.Config?["branches"] is not JsonArray branches)
        {
            return null;
        }

        // On resume, honour the branch already selected instead of re-evaluating conditions.
        var target = frame?.ChosenBranch ?? SelectChoiceTarget(branches, context);
        if (target is null)
        {
            return null;
        }

        var childFrame = frame is not null ? frame.Child : null;
        var suspend = await RunNodeByIdAsync(target, index, context, cancellationToken, childFrame, pendingChoices);
        if (suspend is not null)
        {
            return suspend with { Position = WrapChoice(node.Id, target, suspend.Position) };
        }

        return null;
    }

    private static ExecutionPosition WrapChoice(string nodeId, string chosenBranch, ExecutionPosition child) => new()
    {
        NodeId = nodeId,
        State = ExecutionPositionState.Running,
        ChosenBranch = chosenBranch,
        Child = child,
    };

    private static string? SelectChoiceTarget(JsonArray branches, ExecutionContext context)
    {
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
                    return target;
                }
            }
            else
            {
                return target; // default branch
            }
        }

        return null;
    }

    private async Task<SuspendInfo?> RunParallelAsync(
        WorkflowNode node,
        IReadOnlyDictionary<string, WorkflowNode> index,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (node.Config?["branches"] is not JsonArray branches)
        {
            return null;
        }

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

        return null;
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
                var suspend = await RunNodeByIdAsync(id, index, context, cancellationToken, null, null);
                if (suspend is not null)
                {
                    throw new NotSupportedException(
                        "Suspending a decision inside a parallel branch is not supported yet.");
                }
            }
        }
    }

    private async Task<SuspendInfo?> RunDecisionAsync(
        WorkflowNode node,
        IReadOnlyDictionary<string, WorkflowNode> index,
        ExecutionContext context,
        CancellationToken cancellationToken,
        ExecutionPosition? frame,
        IReadOnlyDictionary<string, string>? pendingChoices)
    {
        // Fresh hit: create a pending decision and suspend.
        if (frame is null)
        {
            var config = (ResolveConfig(node.Config, context) as JsonObject) ?? new JsonObject();
            var decision = CreatePendingDecision(node, context, config);
            var position = new ExecutionPosition
            {
                NodeId = node.Id,
                State = ExecutionPositionState.Running,
                SuspendedDecisionId = decision.DecisionId,
            };
            return new SuspendInfo(position, decision);
        }

        // Resume: inject the chosen value, route to a branch, run it fresh.
        var chosen = frame.SuspendedDecisionId is not null &&
                     pendingChoices is not null &&
                     pendingChoices.TryGetValue(frame.SuspendedDecisionId, out var value)
            ? value
            : null;

        var route = ResolveDecisionRoute(node, chosen);
        if (route is null)
        {
            return null;
        }

        return await RunNodeByIdAsync(route, index, context, cancellationToken, null, pendingChoices);
    }

    private static PendingDecision CreatePendingDecision(WorkflowNode node, ExecutionContext context, JsonObject config)
    {
        var options = (config["options"] as JsonArray)?
            .OfType<JsonObject>()
            .Select(o => new DecisionOption
            {
                Label = o["label"]?.GetValue<string>() ?? "",
                Value = o["value"]?.GetValue<string>() ?? "",
                IsDefault = o["isDefault"]?.GetValue<bool>() ?? false,
            })
            .ToList() ?? new List<DecisionOption>();

        return new PendingDecision
        {
            DecisionId = Guid.NewGuid().ToString("N"),
            InstanceId = context.InstanceId,
            NodeId = node.Id,
            Prompt = config["prompt"]?.GetValue<string>(),
            Options = options,
        };
    }

    private static string? ResolveDecisionRoute(WorkflowNode node, string? chosenValue)
    {
        if (node.Config?["branches"] is not JsonObject branches)
        {
            return null;
        }

        if (chosenValue is not null &&
            branches[chosenValue] is JsonValue v &&
            v.TryGetValue<string>(out var id))
        {
            return id;
        }

        if (branches["default"] is JsonValue d && d.TryGetValue<string>(out var defaultId))
        {
            return defaultId;
        }

        return null;
    }

    /// <summary>Returns true if <paramref name="choice"/> is one of the declared option values.</summary>
    private static bool IsValidChoice(WorkflowNode node, string choice)
    {
        if (node.Config?["options"] is not JsonArray options)
        {
            return false;
        }

        foreach (var option in options.OfType<JsonObject>())
        {
            if (option["value"]?.GetValue<string>() == choice)
            {
                return true;
            }
        }

        return false;
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
