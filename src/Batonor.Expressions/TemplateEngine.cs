using System.Text;
using System.Text.Json.Nodes;
using Batonor.Abstractions;

namespace Batonor.Expressions;

/// <summary>
/// Renders <c>${path}</c> templates. A bare <c>${path}</c> (the whole template) returns the raw
/// typed value; mixed text interpolates values as strings. Pure managed code — AOT-safe.
/// </summary>
public sealed class TemplateEngine : ITemplateEngine
{
    public JsonNode? Render(string template, IReadOnlyDictionary<string, JsonNode?> variables)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (template.Length == 0)
        {
            return null;
        }

        if (TryParseBare(template, out var barePath))
        {
            // Deep-clone: the resolved value may be a node owned by the scope; returning a
            // reference would later fail with "node already has a parent" when re-parented.
            return ScopeResolver.Resolve(variables, barePath)?.DeepClone();
        }

        var sb = new StringBuilder();
        var i = 0;
        while (i < template.Length)
        {
            var start = template.IndexOf("${", i, StringComparison.Ordinal);
            if (start < 0)
            {
                sb.Append(template, i, template.Length - i);
                break;
            }

            sb.Append(template, i, start - i);
            var end = template.IndexOf('}', start + 2);
            if (end < 0)
            {
                throw new BatonorException($"Unclosed template expression in '{template}'.");
            }

            var path = template.Substring(start + 2, end - start - 2).Trim();
            var value = ScopeResolver.Resolve(variables, path);
            sb.Append(FormatValue(value));
            i = end + 1;
        }

        return JsonValue.Create(sb.ToString());
    }

    private static bool TryParseBare(string template, out string path)
    {
        path = "";
        if (template.Length <= 3 ||
            !template.StartsWith("${", StringComparison.Ordinal) ||
            !template.EndsWith('}'))
        {
            return false;
        }

        path = template.Substring(2, template.Length - 3).Trim();
        return path.Length > 0 && template.IndexOf("${", 2, StringComparison.Ordinal) < 0;
    }

    private static string FormatValue(JsonNode? value)
    {
        if (value is null)
        {
            return "";
        }

        if (value is JsonValue v && v.TryGetValue<string>(out var s))
        {
            return s;
        }

        return value.ToJsonString();
    }
}
