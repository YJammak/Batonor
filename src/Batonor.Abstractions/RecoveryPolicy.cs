namespace Batonor.Abstractions;

/// <summary>
/// Execution semantics of a node on crash recovery.
/// </summary>
public enum RecoveryPolicy
{
    /// <summary>Re-run the node on recovery; may duplicate side effects, never silently skips.</summary>
    AtLeastOnce = 0,

    /// <summary>Skip the node on recovery; never duplicates, may silently skip a side effect.</summary>
    AtMostOnce = 1,
}
