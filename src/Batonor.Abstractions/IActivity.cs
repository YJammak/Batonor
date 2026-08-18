namespace Batonor.Abstractions;

/// <summary>
/// A unit of work (a leaf node) executed by the workflow engine.
/// Implementations may be built-in (Http, CommandLine, Python, gRPC) or user plugins.
/// </summary>
public interface IActivity
{
    /// <summary>
    /// Executes the activity and returns its output (written back to the workflow variable named
    /// by <see cref="WorkflowNode.Output"/>).
    /// </summary>
    ValueTask<object?> ExecuteAsync(IActivityContext context, CancellationToken cancellationToken);
}
