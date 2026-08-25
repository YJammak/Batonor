using Batonor.SourceGen;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Batonor.Tests;

public class ActivityGeneratorTests
{
    private const string Source = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Batonor.Abstractions;

        namespace Sample;

        [Activity("greet")]
        public sealed class GreetActivity : IActivity
        {
            public ValueTask<object?> ExecuteAsync(IActivityContext context, CancellationToken ct)
                => ValueTask.FromResult<object?>("hi");
        }
        """;

    private static readonly MetadataReference[] References = BuildReferences();

    private static MetadataReference[] BuildReferences()
    {
        // All assemblies the runtime itself loads (core + framework), plus Batonor.Abstractions.
        var tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        return tpa
            .Split(Path.PathSeparator)
            .Select(p => MetadataReference.CreateFromFile(p))
            .Append(MetadataReference.CreateFromFile(typeof(Batonor.Abstractions.IActivity).Assembly.Location))
            .ToArray();
    }

    private static CSharpCompilation CreateCompilation(SyntaxTree sourceTree) =>
        CSharpCompilation.Create(
            "Sample",
            new[] { sourceTree },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    [Fact]
    public void Generator_Emits_ActivityRegistry_With_Registration()
    {
        var sourceTree = CSharpSyntaxTree.ParseText(Source);
        var compilation = CreateCompilation(sourceTree);

        var driver = CSharpGeneratorDriver.Create(new ActivityGenerator());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);

        // output.SyntaxTrees = the original Source tree + the generated tree; pick the generated one.
        var generated = output.SyntaxTrees.Single(t => t != sourceTree).ToString();
        Assert.Contains("ActivityRegistry", generated);
        Assert.Contains("new global::Sample.GreetActivity()", generated);
        Assert.Contains("\"greet\"", generated);
    }

    [Fact]
    public async Task Generated_Registry_Resolves_An_Activity()
    {
        var sourceTree = CSharpSyntaxTree.ParseText(Source);
        var compilation = CreateCompilation(sourceTree);

        var driver = CSharpGeneratorDriver.Create(new ActivityGenerator());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);

        using var ms = new MemoryStream();
        var emit = output.Emit(ms);
        Assert.True(emit.Success, string.Join("\n", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        ms.Position = 0;
        var assembly = System.Reflection.Assembly.Load(ms.ToArray());
        var registryType = assembly.GetType("Sample.ActivityRegistry")!;
        var resolver = (Batonor.Abstractions.IActivityResolver)Activator.CreateInstance(registryType)!;

        var activity = resolver.Resolve("greet");
        Assert.NotNull(activity);

        var result = await activity!.ExecuteAsync(new FakeContext(), default);
        Assert.Equal("hi", result);
    }

    private sealed class FakeContext : Batonor.Abstractions.IActivityContext
    {
        public string ActivityName => "greet";
        public string AttemptId => "i:n";
        public System.Text.Json.Nodes.JsonNode? Input => null;
        public IReadOnlyDictionary<string, System.Text.Json.Nodes.JsonNode?> Variables => new Dictionary<string, System.Text.Json.Nodes.JsonNode?>();
        public IServiceProvider? ServiceProvider => null;
    }
}
