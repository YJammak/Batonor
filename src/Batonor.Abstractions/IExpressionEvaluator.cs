using System.Text.Json.Nodes;

namespace Batonor.Abstractions;

/// <summary>
/// Evaluates a boolean condition against a scope. AOT-safe implementations must not use
/// runtime code generation (the default <c>Batonor.Expressions</c> uses a hand-written interpreter).
/// </summary>
public interface IExpressionEvaluator
{
    bool Evaluate(string expression, IReadOnlyDictionary<string, JsonNode?> variables);
}

/// <summary>
/// Renders a <c>${path}</c> template. A bare <c>${path}</c> returns the raw (typed) value;
/// mixed text interpolates values as strings.
/// </summary>
public interface ITemplateEngine
{
    JsonNode? Render(string template, IReadOnlyDictionary<string, JsonNode?> variables);
}
