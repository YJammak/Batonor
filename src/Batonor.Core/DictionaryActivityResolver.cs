using Batonor.Abstractions;

namespace Batonor.Core;

/// <summary>
/// Minimal in-memory <see cref="IActivityResolver"/> backed by a name-to-activity dictionary.
/// The source-generated static registry and the runtime plugin loader replace this in production.
/// </summary>
public sealed class DictionaryActivityResolver : IActivityResolver
{
    private readonly IReadOnlyDictionary<string, IActivity> _activities;

    public DictionaryActivityResolver(IReadOnlyDictionary<string, IActivity> activities)
        => _activities = activities;

    public DictionaryActivityResolver(IEnumerable<KeyValuePair<string, IActivity>> activities)
        => _activities = new Dictionary<string, IActivity>(activities, StringComparer.Ordinal);

    public IActivity? Resolve(string name) => _activities.TryGetValue(name, out var a) ? a : null;

    public IReadOnlyCollection<string> GetRegisteredNames() => _activities.Keys.ToArray();
}
