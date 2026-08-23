using Batonor.Abstractions;

namespace Batonor.Persistence.InMemory;

/// <summary>
/// Process-local <see cref="IWorkflowStore"/>. Useful for tests and short-lived runs; it cannot
/// survive a process restart (true durability requires a real provider, e.g. SQLite).
/// </summary>
public sealed class InMemoryWorkflowStore : IWorkflowStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, WorkflowDefinition> _definitions = new();
    private readonly Dictionary<string, WorkflowInstance> _instances = new();
    private readonly Dictionary<string, PendingDecision> _pending = new();

    public Task SaveDefinitionAsync(WorkflowDefinition definition, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _definitions[$"{definition.Id}:{definition.Version}"] = definition;
        }

        return Task.CompletedTask;
    }

    public Task<WorkflowDefinition?> LoadDefinitionAsync(string id, int version, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_definitions.TryGetValue($"{id}:{version}", out var d) ? d : null);
        }
    }

    public Task SaveInstanceAsync(WorkflowInstance instance, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _instances[instance.InstanceId] = instance;
        }

        return Task.CompletedTask;
    }

    public Task<WorkflowInstance?> LoadInstanceAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_instances.TryGetValue(instanceId, out var i) ? i : null);
        }
    }

    public Task<IReadOnlyList<PendingDecision>> ListPendingDecisionsAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult((IReadOnlyList<PendingDecision>)_pending.Values.ToList());
        }
    }

    public Task SavePendingDecisionAsync(PendingDecision decision, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _pending[decision.DecisionId] = decision;
        }

        return Task.CompletedTask;
    }

    public Task<PendingDecision?> LoadPendingDecisionAsync(string decisionId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_pending.TryGetValue(decisionId, out var d) ? d : null);
        }
    }

    public Task CompleteDecisionAsync(string decisionId, string choice, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _pending.Remove(decisionId);
        }

        return Task.CompletedTask;
    }
}
