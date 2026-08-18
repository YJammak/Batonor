using System.Text.Json.Nodes;
using Batonor.Expressions;
using Xunit;

namespace Batonor.Tests;

public class ExpressionTests
{
    private static Dictionary<string, JsonNode?> Scope(params (string, JsonNode?)[] entries)
    {
        var d = new Dictionary<string, JsonNode?>();
        foreach (var (k, v) in entries)
        {
            d[k] = v;
        }

        return d;
    }

    [Fact]
    public void Condition_Evaluates_Comparison()
    {
        var eval = new ConditionEvaluator();
        var scope = Scope(
            ("order", new JsonObject { ["amount"] = 150.0 }),
            ("status", JsonValue.Create("ok")));

        Assert.True(eval.Evaluate("${order.amount} > 100", scope));
        Assert.True(eval.Evaluate("${order.amount} >= 150", scope));
        Assert.False(eval.Evaluate("${order.amount} < 100", scope));
        Assert.True(eval.Evaluate("${status} == 'ok'", scope));
        Assert.False(eval.Evaluate("${status} != 'ok'", scope));
    }

    [Fact]
    public void Condition_Evaluates_Boolean_Logic()
    {
        var eval = new ConditionEvaluator();
        var scope = Scope(
            ("order", new JsonObject { ["amount"] = 150.0 }),
            ("status", JsonValue.Create("ok")));

        Assert.True(eval.Evaluate("${order.amount} > 100 && ${status} == 'ok'", scope));
        Assert.False(eval.Evaluate("${order.amount} > 100 && ${status} == 'bad'", scope));
        Assert.True(eval.Evaluate("${order.amount} < 100 || ${status} == 'ok'", scope));
        Assert.True(eval.Evaluate("!(${order.amount} < 100)", scope));
    }

    [Fact]
    public void Condition_Evaluates_Arithmetic()
    {
        var eval = new ConditionEvaluator();
        var scope = Scope(("a", JsonValue.Create(2.0)), ("b", JsonValue.Create(3.0)));

        Assert.True(eval.Evaluate("${a} + ${b} == 5", scope));
        Assert.True(eval.Evaluate("${a} * ${b} == 6", scope));
        Assert.True(eval.Evaluate("(${a} + ${b}) * 2 == 10", scope));
    }

    [Fact]
    public void Template_Renders_Bare_Value()
    {
        var tpl = new TemplateEngine();
        var scope = Scope(("order", new JsonObject { ["id"] = 42.0 }));

        var result = tpl.Render("${order.id}", scope);
        Assert.Equal(42.0, result!.GetValue<double>());
    }

    [Fact]
    public void Template_Renders_Mixed_String()
    {
        var tpl = new TemplateEngine();
        var scope = Scope(("order", new JsonObject { ["id"] = 42.0 }));

        var result = tpl.Render("id=${order.id}", scope);
        Assert.Equal("id=42", result!.GetValue<string>());
    }
}
