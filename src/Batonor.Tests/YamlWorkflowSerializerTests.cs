using Batonor.Abstractions;
using Batonor.Yaml;
using System.Text.Json.Nodes;
using Xunit;

namespace Batonor.Tests;

public class YamlWorkflowSerializerTests
{
    private const string YamlDef = """
        id: aot-smoke
        version: 1
        steps:
          - id: seq
            type: sequence
            config:
              steps: [hello, double]
          - id: hello
            type: hello
            config: { name: AOT }
            output: greeting
          - id: double
            type: double
            config: { value: 21.0 }
            output: result
        """;

    [Fact]
    public void Deserializes_A_Yaml_Definition()
    {
        var ser = new YamlWorkflowSerializer();

        var def = ser.DeserializeDefinition(YamlDef);

        Assert.Equal("aot-smoke", def.Id);
        Assert.Equal(1, def.Version);
        Assert.Equal(3, def.Steps.Count);
        Assert.Equal("sequence", def.Steps[0].Type);
        Assert.Equal("hello", def.Steps[1].Type);
        Assert.Equal("double", def.Steps[2].Type);
        Assert.Equal("AOT", def.Steps[1].Config!["name"]!.GetValue<string>());
        Assert.Equal(21.0, def.Steps[2].Config!["value"]!.GetValue<double>());
    }

    [Fact]
    public void Serializes_And_Round_Trips_A_Definition()
    {
        var ser = new YamlWorkflowSerializer();

        var def = new WorkflowDefinition
        {
            Id = "wf",
            Version = 2,
            Steps = new[]
            {
                new WorkflowNode { Id = "a", Type = "echo", Config = new JsonObject { ["x"] = 1 } },
            },
        };

        var yaml = ser.SerializeDefinition(def);
        var back = ser.DeserializeDefinition(yaml);

        Assert.Equal("wf", back.Id);
        Assert.Equal(2, back.Version);
        Assert.Equal("echo", back.Steps[0].Type);
        Assert.Equal(1, back.Steps[0].Config!["x"]!.GetValue<int>());
    }
}
