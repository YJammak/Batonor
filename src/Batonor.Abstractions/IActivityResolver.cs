namespace Batonor.Abstractions;

/// <summary>
/// Resolves activities by name. Two implementations: the source-generated static registry (AOT-safe)
/// and the runtime <c>AssemblyLoadContext</c> scanner (JIT only).
/// </summary>
public interface IActivityResolver
{
    IActivity? Resolve(string name);

    IReadOnlyCollection<string> GetRegisteredNames();
}
