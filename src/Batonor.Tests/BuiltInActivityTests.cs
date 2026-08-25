using Batonor.Abstractions;
using Batonor.Activities;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Batonor.Tests;

public class BuiltInActivityTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string Body { get; set; } = "{\"ok\":true}";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class TestActivityContext : IActivityContext
    {
        public TestActivityContext(JsonNode? input) => Input = input;
        public string ActivityName => "http";
        public string AttemptId => "i:node";
        public JsonNode? Input { get; }
        public IReadOnlyDictionary<string, JsonNode?> Variables => new Dictionary<string, JsonNode?>();
        public IServiceProvider? ServiceProvider => null;
    }

    [Fact]
    public async Task HttpActivity_Posts_Json_Body_And_Returns_Response()
    {
        var handler = new FakeHandler();
        var activity = new HttpActivity(new HttpClient(handler));

        var output = await activity.ExecuteAsync(new TestActivityContext(new JsonObject
        {
            ["method"] = "POST",
            ["url"] = "https://example.com/api",
            ["headers"] = new JsonObject { ["X-Test"] = "yes" },
            ["body"] = new JsonObject { ["a"] = 1 },
        }), CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("https://example.com/api", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("yes", handler.LastRequest!.Headers.GetValues("X-Test").Single());

        var result = output as JsonObject;
        Assert.NotNull(result);
        Assert.True(result!["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task CommandLineActivity_Captures_Stdout()
    {
        var activity = new CommandLineActivity();

        var output = await activity.ExecuteAsync(new TestActivityContext(new JsonObject
        {
            ["executable"] = "cmd.exe",
            ["args"] = new JsonArray(JsonValue.Create("/c"), JsonValue.Create("echo"), JsonValue.Create("hello")),
            ["captureStdout"] = true,
        }), CancellationToken.None);

        Assert.NotNull(output);
        Assert.Contains("hello", (string)output!);
    }

    [Fact]
    public async Task HttpActivity_Returns_Raw_Text_For_NonJson_Body()
    {
        var handler = new FakeHandler { Body = "<html>error</html>" };
        var activity = new HttpActivity(new HttpClient(handler));

        var output = await activity.ExecuteAsync(new TestActivityContext(new JsonObject
        {
            ["url"] = "https://example.com/error",
        }), CancellationToken.None);

        Assert.Equal("<html>error</html>", output!.ToString());
    }

    [Fact]
    public async Task CommandLineActivity_Throws_On_NonZero_Exit()
    {
        var activity = new CommandLineActivity();

        await Assert.ThrowsAsync<BatonorException>(() => activity.ExecuteAsync(new TestActivityContext(new JsonObject
        {
            ["executable"] = "cmd.exe",
            ["args"] = new JsonArray(JsonValue.Create("/c"), JsonValue.Create("exit"), JsonValue.Create("1")),
            ["captureStdout"] = true,
        }), CancellationToken.None).AsTask());
    }
}
