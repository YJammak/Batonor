namespace Batonor.Abstractions;

/// <summary>
/// Marks a class as an activity and assigns the name used to reference it in workflow definitions.
/// The source generator (<c>Batonor.SourceGen</c>) scans for this attribute to build a static registry.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ActivityAttribute : Attribute
{
    public ActivityAttribute(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    public string Name { get; }
}
