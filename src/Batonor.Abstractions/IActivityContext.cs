using System.Text.Json.Nodes;

namespace Batonor.Abstractions;

/// <summary>
/// Execution context passed to an activity. Exposes the resolved input, workflow variables and services.
/// </summary>
public interface IActivityContext
{
    /// <summary>Name this activity was registered under.</summary>
    string ActivityName { get; }

    /// <summary>
    /// Stable idempotency key, invariant across retries/recovery of the same logical execution.
    /// Activities with external side effects should use it for deduplication.
    /// </summary>
    string AttemptId { get; }

    /// <summary>Input payload for this activity (already template-resolved).</summary>
    JsonNode? Input { get; }

    /// <summary>Read-only view of the current workflow scope (input, variables, node outputs).</summary>
    IReadOnlyDictionary<string, JsonNode?> Variables { get; }

    /// <summary>Optional service provider; null in no-DI scenarios.</summary>
    IServiceProvider? ServiceProvider { get; }
}
