using System.Text.Json.Nodes;

namespace Batonor.Expressions;

/// <summary>
/// Resolves a dotted path (e.g. <c>order.amount</c>, <c>input.orderId</c>, <c>fetch-order.output</c>)
/// against a scope dictionary of root names.
/// </summary>
internal static class ScopeResolver
{
    public static JsonNode? Resolve(IReadOnlyDictionary<string, JsonNode?> scope, string path)
    {
        var segments = path.Split('.');
        if (segments.Length == 0 || segments[0].Length == 0)
        {
            return null;
        }

        if (!scope.TryGetValue(segments[0], out var current) || current is null)
        {
            return null;
        }

        for (var i = 1; i < segments.Length; i++)
        {
            if (current is JsonObject obj && obj.TryGetPropertyValue(segments[i], out var next))
            {
                current = next;
            }
            else
            {
                return null;
            }
        }

        return current;
    }
}
