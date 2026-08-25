using Batonor.Abstractions;

namespace Batonor.Sample.Host;

/// <summary>
/// Source-generated activity: responds with a greeting. Registered under the name "hello" by the
/// Batonor.SourceGen analyzer, which scans compilation units for <c>[Activity]</c>-annotated classes.
/// </summary>
[Activity("hello")]
public sealed class HelloActivity : IActivity
{
    public ValueTask<object?> ExecuteAsync(IActivityContext context, CancellationToken ct)
        => ValueTask.FromResult<object?>("Hello," + (context.Input?["name"]?.GetValue<string>() ?? "world"));
}

/// <summary>
/// Source-generated activity: doubles its numeric input. Registered under the name "double" by the
/// Batonor.SourceGen analyzer.
/// </summary>
[Activity("double")]
public sealed class DoubleActivity : IActivity
{
    public ValueTask<object?> ExecuteAsync(IActivityContext context, CancellationToken ct)
        => ValueTask.FromResult<object?>((context.Input?["value"]?.GetValue<double>() ?? 0) * 2);
}
