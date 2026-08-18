namespace Batonor.Abstractions;

/// <summary>
/// Persistence contract for workflow definitions, instance state and pending decisions.
/// Pure persistence layer — does not handle authorization (that lives in the engine).
/// Implementations: Sqlite (default), File, MySql/Postgres/SqlServer, InMemory, EfCore (JIT only).
/// </summary>
public interface IWorkflowStore
{
    Task SaveDefinitionAsync(WorkflowDefinition definition, CancellationToken cancellationToken = default);

    Task<WorkflowDefinition?> LoadDefinitionAsync(string id, int version, CancellationToken cancellationToken = default);

    Task SaveInstanceAsync(WorkflowInstance instance, CancellationToken cancellationToken = default);

    Task<WorkflowInstance?> LoadInstanceAsync(string instanceId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PendingDecision>> ListPendingDecisionsAsync(CancellationToken cancellationToken = default);

    Task CompleteDecisionAsync(string decisionId, string choice, CancellationToken cancellationToken = default);
}
