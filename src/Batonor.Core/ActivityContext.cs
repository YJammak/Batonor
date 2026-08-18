using System.Text.Json.Nodes;
using Batonor.Abstractions;

namespace Batonor.Core;

internal sealed class ActivityContext : IActivityContext
{
    public ActivityContext(
        string activityName,
        string attemptId,
        JsonNode? input,
        IReadOnlyDictionary<string, JsonNode?> variables,
        IServiceProvider? serviceProvider)
    {
        ActivityName = activityName;
        AttemptId = attemptId;
        Input = input;
        Variables = variables;
        ServiceProvider = serviceProvider;
    }

    public string ActivityName { get; }

    public string AttemptId { get; }

    public JsonNode? Input { get; }

    public IReadOnlyDictionary<string, JsonNode?> Variables { get; }

    public IServiceProvider? ServiceProvider { get; }
}
