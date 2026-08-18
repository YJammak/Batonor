namespace Batonor.Abstractions;

/// <summary>When a parallel node proceeds past its join point.</summary>
public enum JoinMode
{
    /// <summary>Wait for all branches to complete.</summary>
    All = 0,

    /// <summary>Proceed as soon as any branch completes.</summary>
    Any = 1,
}
