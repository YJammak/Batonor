using System.Security.Claims;
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
                suspend = await RunNodeAsync(root, index, context, cancellationToken, null, null, instance, isRecovery: false, Leaf(root.Id)).ConfigureAwait(false);
                if (suspend is not null)
                {
                    break;
                }
            }

            if (suspend is null)
            {
                instance.Status = WorkflowStatus.Completed;
                instance.CompletedAt = DateTimeOffset.UtcNow;
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
                await _store.SavePendingDecisionAsync(suspend.Decision, cancellationToken).ConfigureAwait(false);
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
        catch (NotSupportedException)
        {
            // Scoped-out constructs (e.g. a decision inside a parallel branch) surface as a throw,
            // not as a failed instance.
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
            await _store.SaveInstanceAsync(instance, cancellationToken).ConfigureAwait(false);
        }

        return instance;
    }

    /// <summary>
    /// Resumes a suspended decision with a human choice and continues the workflow from its saved
    /// execution position. The definition snapshot taken from the instance is used, never a newer
    /// published version.
    /// </summary>
    public async Task<WorkflowInstance> CompleteDecisionAsync(
        string decisionId,
        string choice,
        ClaimsPrincipal? principal = null,
        CancellationToken cancellationToken = default)
    {
        if (_store is null)
        {
            throw new BatonorException("A workflow store is required to resume a decision.");
        }

        var pending = await _store.LoadPendingDecisionAsync(decisionId, cancellationToken).ConfigureAwait(false)
            ?? throw new BatonorException($"Unknown pending decision '{decisionId}'.");

        var instance = await _store.LoadInstanceAsync(pending.InstanceId, cancellationToken).ConfigureAwait(false)
            ?? throw new BatonorException($"Instance '{pending.InstanceId}' not found.");

        var definition = instance.Definition
            ?? throw new BatonorException($"Instance '{pending.InstanceId}' has no definition snapshot.");

        var index = BuildIndex(definition);

        // Reject an unrecognised human choice (a caller error, not a workflow failure).
        if (!index.TryGetValue(pending.NodeId, out var decisionNode))
        {
            throw new BatonorException($"Node '{pending.NodeId}' not found in definition '{definition.Id}'.");
        }

        if (!IsValidChoice(decisionNode, choice))
        {
            throw new BatonorException(
                $"Choice '{choice}' is not a valid option for decision node '{pending.NodeId}'.");
        }

        var context = new ExecutionContext(instance.InstanceId, _activities, _expressions, _templates, _services);
        context.Restore(instance.Variables);

        var pendingChoices = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [decisionId] = choice,
        };

        SuspendInfo? suspend = null;
        try
        {
            var rootFrame = instance.Position
                ?? throw new BatonorException($"Instance '{instance.InstanceId}' is not suspended at a position.");

            if (!index.TryGetValue(rootFrame.NodeId, out var resumeNode))
            {
                throw new BatonorException($"Node '{rootFrame.NodeId}' not found in definition '{definition.Id}'.");
            }

            suspend = await RunNodeAsync(resumeNode, index, context, cancellationToken, rootFrame, pendingChoices, instance, isRecovery: false, Leaf(rootFrame.NodeId)).ConfigureAwait(false);

            if (suspend is null)
            {
                instance.Status = WorkflowStatus.Completed;
                instance.CompletedAt = DateTimeOffset.UtcNow;
                instance.Position = null;
            }
            else
            {
                instance.Status = WorkflowStatus.Suspended;
                instance.Position = suspend.Position;
            }
        }
        catch (OperationCanceledException)
        {
            instance.Status = WorkflowStatus.Cancelled;
        }
        catch (BatonorException)
        {
            // Domain errors (bad definition, missing activity, invalid branch) surface to the caller;
            // only unexpected runtime failures are captured as a failed instance.
            throw;
        }
        catch (NotSupportedException)
        {
            // Scoped-out constructs (e.g. a decision inside a parallel branch) surface as a throw,
            // not as a failed instance.
            throw;
        }
        catch (Exception ex)
        {
            instance.Status = WorkflowStatus.Failed;
            instance.Error = ex.Message;
            instance.Position = null;
        }

        instance.Variables = context.Snapshot();

        if (suspend is not null && instance.Status == WorkflowStatus.Suspended)
        {
            await _store.SavePendingDecisionAsync(suspend.Decision, cancellationToken).ConfigureAwait(false);
        }

        await _store.SaveInstanceAsync(instance, cancellationToken).ConfigureAwait(false);
        await _store.CompleteDecisionAsync(decisionId, choice, cancellationToken).ConfigureAwait(false);
        return instance;
    }

    /// <summary>
    /// Resumes a crashed in-flight instance from its last persisted position, applying the
    /// per-node recovery policy: <see cref="RecoveryPolicy.AtLeastOnce"/> nodes re-run, while
    /// <see cref="RecoveryPolicy.AtMostOnce"/> nodes are skipped. The instance is only recovered
    /// when its status is <see cref="WorkflowStatus.Running"/>; otherwise it is returned unchanged.
    /// </summary>
    public async Task<WorkflowInstance> RecoverAsync(
        string instanceId,
        ClaimsPrincipal? principal = null,
        CancellationToken cancellationToken = default)
    {
        if (_store is null)
        {
            throw new BatonorException("A workflow store is required to recover an instance.");
        }

        var instance = await _store.LoadInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false)
            ?? throw new BatonorException($"Instance '{instanceId}' not found.");

        if (instance.Status != WorkflowStatus.Running)
        {
            // Not a crashed in-flight instance — nothing to recover.
            return instance;
        }

        var definition = instance.Definition
            ?? throw new BatonorException($"Instance '{instanceId}' has no definition snapshot.");

        var index = BuildIndex(definition);
        var context = new ExecutionContext(instance.InstanceId, _activities, _expressions, _templates, _services);
        context.Restore(instance.Variables);

        var rootFrame = instance.Position
            ?? throw new BatonorException($"Instance '{instanceId}' is not at a recoverable position.");

        SuspendInfo? suspend = null;
        try
        {
            if (!index.TryGetValue(rootFrame.NodeId, out var resumeNode))
            {
                throw new BatonorException($"Node '{rootFrame.NodeId}' not found in definition '{definition.Id}'.");
            }

            suspend = await RunNodeAsync(resumeNode, index, context, cancellationToken, rootFrame, null, instance, isRecovery: true, Leaf(rootFrame.NodeId)).ConfigureAwait(false);

            if (suspend is null)
            {
                instance.Status = WorkflowStatus.Completed;
                instance.CompletedAt = DateTimeOffset.UtcNow;
                instance.Position = null;
            }
            else
            {
                instance.Status = WorkflowStatus.Suspended;
                instance.Position = suspend.Position;
            }
        }
        catch (OperationCanceledException)
        {
            instance.Status = WorkflowStatus.Cancelled;
        }
        catch (BatonorException)
        {
            throw;
        }
        catch (NotSupportedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            instance.Status = WorkflowStatus.Failed;
            instance.Error = ex.Message;
            instance.Position = null;
        }

        instance.Variables = context.Snapshot();
        if (suspend is not null && instance.Status == WorkflowStatus.Suspended)
        {
            await _store.SavePendingDecisionAsync(suspend.Decision, cancellationToken).ConfigureAwait(false);
        }

        await _store.SaveInstanceAsync(instance, cancellationToken).ConfigureAwait(false);
        return instance;
    }

    private async Task<SuspendInfo?> RunNodeAsync(
        WorkflowNode node,
        IReadOnlyDictionary<string, WorkflowNode> index,
        ExecutionContext context,
        CancellationToken cancellationToken,
        ExecutionPosition? frame,
        IReadOnlyDictionary<string, string>? pendingChoices,
        WorkflowInstance instance,
        bool isRecovery,
        ExecutionPosition? checkpointPosition)
    {
        cancellationToken.ThrowIfCancellationRequested();

        switch (node.Type)
        {
            case "sequence": return await RunSequenceAsync(node, index, context, cancellationToken, frame, pendingChoices, instance, isRecovery, checkpointPosition).ConfigureAwait(false);
            case "choice": return await RunChoiceAsync(node, index, context, cancellationToken, frame, pendingChoices, instance, isRecovery, checkpointPosition).ConfigureAwait(false);
            case "parallel": return await RunParallelAsync(node, index, context, cancellationToken, instance, isRecovery, checkpointPosition).ConfigureAwait(false);
            case "decision": return await RunDecisionAsync(node, index, context, cancellationToken, frame, pendingChoices, instance, isRecovery, checkpointPosition).ConfigureAwait(false);
            default: await RunActivityAsync(node, context, cancellationToken, instance, isRecovery, frame, checkpointPosition).ConfigureAwait(false); return null;
        }
    }

    private async Task<SuspendInfo?> RunNodeByIdAsync(
        string id,
        IReadOnlyDictionary<string, WorkflowNode> index,
        ExecutionContext context,
        CancellationToken cancellationToken,
        ExecutionPosition? frame,
        IReadOnlyDictionary<string, string>? pendingChoices,
        WorkflowInstance instance,
        bool isRecovery,
        ExecutionPosition? checkpointPosition)
    {
        if (!index.TryGetValue(id, out var node))
        {
            throw new WorkflowDefinitionException($"Referenced node '{id}' was not found.");
        }

        return await RunNodeAsync(node, index, context, cancellationToken, frame, pendingChoices, instance, isRecovery, checkpointPosition).ConfigureAwait(false);
    }

    private async Task<SuspendInfo?> RunSequenceAsync(
        WorkflowNode node,
        IReadOnlyDictionary<string, WorkflowNode> index,
        ExecutionContext context,
        CancellationToken cancellationToken,
        ExecutionPosition? frame,
        IReadOnlyDictionary<string, string>? pendingChoices,
        WorkflowInstance instance,
        bool isRecovery,
        ExecutionPosition? checkpointPosition)
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
            var childCheckpoint = checkpointPosition is null
                ? WrapSequence(node.Id, i, Leaf(id))
                : ReplaceTerminal(checkpointPosition, WrapSequence(node.Id, i, Leaf(id)));
            var suspend = await RunNodeByIdAsync(id, index, context, cancellationToken, stepFrame, pendingChoices, instance, isRecovery, childCheckpoint).ConfigureAwait(false);
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
        IReadOnlyDictionary<string, string>? pendingChoices,
        WorkflowInstance instance,
        bool isRecovery,
        ExecutionPosition? checkpointPosition)
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
        var childCheckpoint = checkpointPosition is null
            ? WrapChoice(node.Id, target, Leaf(target))
            : ReplaceTerminal(checkpointPosition, WrapChoice(node.Id, target, Leaf(target)));
        var suspend = await RunNodeByIdAsync(target, index, context, cancellationToken, childFrame, pendingChoices, instance, isRecovery, childCheckpoint).ConfigureAwait(false);
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

    /// <summary>A checkpoint position for a single activity that is about to run.</summary>
    private static ExecutionPosition Leaf(string nodeId) => new()
    {
        NodeId = nodeId,
        State = ExecutionPositionState.Running,
    };

    /// <summary>
    /// Rebuilds a checkpoint chain, replacing its terminal leaf frame with <paramref name="newTerminal"/>.
    /// Used when a control-flow node nested inside another control flow extends the ancestor chain with
    /// its own wrap + <see cref="Leaf"/> rather than dropping that ancestor prefix.
    /// </summary>
    private static ExecutionPosition ReplaceTerminal(ExecutionPosition chain, ExecutionPosition newTerminal)
    {
        if (chain.Child is not null)
        {
            return new ExecutionPosition
            {
                NodeId = chain.NodeId,
                State = chain.State,
                SequenceIndex = chain.SequenceIndex,
                ChosenBranch = chain.ChosenBranch,
                SuspendedDecisionId = chain.SuspendedDecisionId,
                Child = ReplaceTerminal(chain.Child, newTerminal),
            };
        }

        return newTerminal;
    }

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

    /// <summary>
    /// Runs a <c>parallel</c> node's branches. Note: the single-instance <see cref="checkpointPosition"/>
    /// is threaded through unchanged to every branch, so concurrent branches intentionally share the one
    /// checkpoint path on the instance. Branch-aware per-branch checkpointing is intentionally out of
    /// scope for now (future work) and would require a position model that can fork per branch.
    /// </summary>
    private async Task<SuspendInfo?> RunParallelAsync(
        WorkflowNode node,
        IReadOnlyDictionary<string, WorkflowNode> index,
        ExecutionContext context,
        CancellationToken cancellationToken,
        WorkflowInstance instance,
        bool isRecovery,
        ExecutionPosition? checkpointPosition)
    {
        if (node.Config?["branches"] is not JsonArray branches)
        {
            return null;
        }

        var tasks = branches.OfType<JsonArray>()
            .Select(branch => RunBranchAsync(branch, index, context.Clone(), cancellationToken, instance, isRecovery, checkpointPosition))
            .ToList();

        var join = ParseJoin(node);
        if (join == JoinMode.Any)
        {
            // WhenAny itself never throws; observe the first-completed task so a faulted branch
            // (e.g. the decision-in-parallel guard) surfaces to the caller rather than being
            // silently ignored.
            var completed = await Task.WhenAny(tasks).ConfigureAwait(false);
            await completed.ConfigureAwait(false);
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
        CancellationToken cancellationToken,
        WorkflowInstance instance,
        bool isRecovery,
        ExecutionPosition? checkpointPosition)
    {
        // Each branch reuses the shared checkpointPosition on the single instance; there is no
        // per-branch position today (see RunParallelAsync for why that is out of scope).
        foreach (var step in branch)
        {
            if (step is JsonValue v && v.TryGetValue<string>(out var id))
            {
                var suspend = await RunNodeByIdAsync(id, index, context, cancellationToken, null, null, instance, isRecovery, checkpointPosition).ConfigureAwait(false);
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
        IReadOnlyDictionary<string, string>? pendingChoices,
        WorkflowInstance instance,
        bool isRecovery,
        ExecutionPosition? checkpointPosition)
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

        // Resume: inject the chosen value, route to a branch, run it fresh. During recovery there are
        // no pendingChoices, so a decision frame that recorded its chosen branch (ChosenBranch) is what
        // lets us resume the routed activity directly without re-consulting this decision (which, with
        // no pendingChoices, would fall back to the default branch and run the wrong one).
        var chosenValue = frame.SuspendedDecisionId is not null &&
                          pendingChoices is not null &&
                          pendingChoices.TryGetValue(frame.SuspendedDecisionId, out var value)
            ? value
            : null;

        var route = frame.ChosenBranch ?? ResolveDecisionRoute(node, chosenValue);
        if (route is null)
        {
            return null;
        }

        // Replace this decision's terminal frame with one that records the chosen branch, so a crash
        // while the routed activity runs is recoverable. The routed activity runs with a non-null
        // frame (frame.Child) so the AtMostOnce skip still applies to the interrupted node.
        var decisionFrame = new ExecutionPosition
        {
            NodeId = node.Id,
            State = ExecutionPositionState.Running,
            ChosenBranch = route,
            Child = Leaf(route),
        };
        var routedCheckpoint = checkpointPosition is null
            ? decisionFrame
            : ReplaceTerminal(checkpointPosition, decisionFrame);
        return await RunNodeByIdAsync(route, index, context, cancellationToken, frame.Child, pendingChoices, instance, isRecovery, routedCheckpoint).ConfigureAwait(false);
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
        CancellationToken cancellationToken,
        WorkflowInstance instance,
        bool isRecovery,
        ExecutionPosition? frame,
        ExecutionPosition? checkpointPosition)
    {
        if (isRecovery && frame is not null && node.Recovery == RecoveryPolicy.AtMostOnce)
        {
            // Skip only the AtMostOnce activity that was actually interrupted — the node the
            // recovery frame points at. A later sibling that is also AtMostOnce still runs.
            return;
        }

        // Resolve the activity before persisting any Running checkpoint, so a missing/unregistered
        // activity surfaces as a definition error rather than leaving a stale recoverable snapshot.
        var activity = context.ResolveActivity(node.Type)
            ?? throw new ActivityNotFoundException(node.Type);

        // Checkpoint the Running state before executing, so a crash mid-activity leaves a
        // recoverable Running snapshot whose Position points at the activity about to run.
        if (_store is not null)
        {
            instance.Status = WorkflowStatus.Running;
            instance.Variables = context.Snapshot();
            instance.Position = checkpointPosition;
            await _store.SaveInstanceAsync(instance, cancellationToken).ConfigureAwait(false);
        }

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
            case "sequence":
                if (node.Config?["steps"] is JsonArray steps)
                {
                    foreach (var step in steps)
                    {
                        if (step is JsonValue sv && sv.TryGetValue<string>(out var stepId))
                        {
                            yield return stepId;
                        }
                    }
                }
                break;

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

            case "decision":
                if (node.Config?["branches"] is JsonObject decisionBranches)
                {
                    foreach (var branch in decisionBranches)
                    {
                        if (branch.Value is JsonValue dv && dv.TryGetValue<string>(out var id))
                        {
                            yield return id;
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
        // AOT-safe conversion: activities must produce a JsonNode or a JSON primitive. The previous
        // `JsonSerializer.SerializeToNode(object)` fallback used reflection and is not AOT-safe.
        return value switch
        {
            null => null,
            JsonNode n => n,
            string s => JsonValue.Create(s),
            bool b => JsonValue.Create(b),
            byte b => JsonValue.Create(b),
            sbyte sb => JsonValue.Create(sb),
            short sh => JsonValue.Create(sh),
            ushort us => JsonValue.Create(us),
            int i => JsonValue.Create(i),
            uint ui => JsonValue.Create(ui),
            long l => JsonValue.Create(l),
            ulong ul => JsonValue.Create(ul),
            float f => JsonValue.Create(f),
            double d => JsonValue.Create(d),
            decimal m => JsonValue.Create(m),
            Guid g => JsonValue.Create(g),
            DateTime dt => JsonValue.Create(dt),
            DateTimeOffset dto => JsonValue.Create(dto),
            _ => throw new BatonorException(
                $"Activity output of type '{value.GetType()}' is not serializable; return a JsonNode or one of the supported JSON primitives (string, bool, numeric, Guid, DateTime, DateTimeOffset)."),
        };
    }
}
