using System.Text.Json.Nodes;
using Batonor.Abstractions;

namespace Batonor.Core;

/// <summary>
/// Holds the evaluation scope (input + variables + node outputs) and the engine's services,
/// shared across node execution. Parallel branches clone the scope to avoid shared writes.
/// </summary>
internal sealed class ExecutionContext
{
    private readonly Dictionary<string, JsonNode?> _scope = new(StringComparer.Ordinal);
    private readonly IActivityResolver _activities;
    private readonly IExpressionEvaluator _expressions;
    private readonly ITemplateEngine _templates;
    private readonly IServiceProvider? _services;

    public ExecutionContext(
        string instanceId,
        IActivityResolver activities,
        IExpressionEvaluator expressions,
        ITemplateEngine templates,
        IServiceProvider? services)
    {
        InstanceId = instanceId;
        _activities = activities;
        _expressions = expressions;
        _templates = templates;
        _services = services;
    }

    private ExecutionContext(ExecutionContext source)
    {
        InstanceId = source.InstanceId;
        foreach (var (key, value) in source._scope)
        {
            _scope[key] = value?.DeepClone();
        }

        _activities = source._activities;
        _expressions = source._expressions;
        _templates = source._templates;
        _services = source._services;
    }

    public string InstanceId { get; }

    public IReadOnlyDictionary<string, JsonNode?> Scope => _scope;

    public IServiceProvider? Services => _services;

    /// <summary>Clones this context with an isolated copy of the scope (for parallel branches).</summary>
    public ExecutionContext Clone() => new(this);

    public void SetRoot(string name, JsonNode? value) => _scope[name] = value?.DeepClone();

    public void SetVariable(string name, JsonNode? value) => _scope[name] = value?.DeepClone();

    public void SetNodeOutput(string nodeId, JsonNode? value) =>
        _scope[nodeId] = new JsonObject { ["output"] = value?.DeepClone() };

    public JsonObject Snapshot()
    {
        var obj = new JsonObject();
        foreach (var (key, value) in _scope)
        {
            obj[key] = value?.DeepClone();
        }

        return obj;
    }

    public JsonNode? Render(string template) => _templates.Render(template, _scope);

    public bool Evaluate(string expression) => _expressions.Evaluate(expression, _scope);

    public IActivity? ResolveActivity(string name) => _activities.Resolve(name);

    /// <summary>Rebuilds the scope from a persisted snapshot (used when resuming an instance).</summary>
    public void Restore(JsonObject? variables)
    {
        _scope.Clear();
        if (variables is not null)
        {
            foreach (var (name, value) in variables)
            {
                _scope[name] = value?.DeepClone();
            }
        }
    }
}
