# Batonor

An embeddable, **durable** workflow engine for .NET. You describe the whole flow (control flow + activities + parameters) in JSON; the `Batonor` engine interprets it and calls your activities. It is written to be **Native-AOT** (`PublishAot`) friendly — no reflection, source-generated serialization, source-generated activity registration.

## Quick start

```csharp
using Batonor;
using Batonor.Abstractions;
using System.Text.Json.Nodes;

// Assemble the engine (no DI container required).
var engine = BatonorEngine.CreateBuilder()
    .UseBuiltInActivities()                       // registers "http" and "commandline"
    .UseSqlite("Data Source=batonor.db")          // durable state; or .UseInMemory() for tests
    .Build();

// A workflow definition is a flat list of nodes; control-flow nodes reference children by id.
var definition = new WorkflowDefinition
{
    Id = "order-process",
    Version = 1,
    Steps = new[]
    {
        new WorkflowNode { Id = "fetch", Type = "http", Config = new JsonObject
        {
            ["method"] = "GET",
            ["url"] = "https://example.com/orders/${input.orderId}",
        }, Output = "order" },
        new WorkflowNode { Id = "gate", Type = "choice", Config = new JsonObject
        {
            ["branches"] = new JsonArray(
                new JsonObject { ["when"] = "${order.amount} > 5000", ["then"] = "approve" },
                new JsonObject { ["default"] = "ship" }),
        }},
        new WorkflowNode { Id = "approve", Type = "decision", Config = new JsonObject
        {
            ["prompt"] = "Amount exceeds 5000 — approve?",
            ["options"] = new JsonArray(
                new JsonObject { ["label"] = "Yes", ["value"] = "approve", ["isDefault"] = true },
                new JsonObject { ["label"] = "No", ["value"] = "reject" }),
            ["branches"] = new JsonObject { ["approve"] = "ship", ["reject"] = "cancel", ["default"] = "cancel" },
        }},
        new WorkflowNode { Id = "ship", Type = "commandline", Config = new JsonObject
        {
            ["executable"] = "ship.exe", ["args"] = new JsonArray("--order", "${order.id}"),
        }},
        new WorkflowNode { Id = "cancel", Type = "commandline", Config = new JsonObject
        { ["executable"] = "cancel.exe", ["args"] = new JsonArray("--order", "${order.id}") } },
    },
};

// Run it. If it reaches the "decision" node, StartAsync returns Suspended with a pending decision.
var instance = await engine.StartAsync(definition, new JsonObject { ["orderId"] = "A-123" });

if (instance.Status == WorkflowStatus.Suspended)
{
    // Present the pending decision (from the store's ListPendingDecisionsAsync) wherever you want
    // — web UI, CLI, desktop. Then resume along the chosen branch:
    //   var resumed = await engine.CompleteDecisionAsync(decisionId, "approve");
}
```

Or register a custom activity:

```csharp
[Activity("double")]                    // Batonor.SourceGen emits an AOT-safe registry for it
public sealed class DoubleActivity : IActivity
{
    public ValueTask<object?> ExecuteAsync(IActivityContext context, CancellationToken ct)
        => ValueTask.FromResult<object?>((context.Input?["value"]?.GetValue<double>() ?? 0) * 2);
}
```

## Features

- **Control flow** — `sequence`, `parallel` (fork–join with `join: all|any`), `choice` (condition routing), `decision` (interactive suspend/resume).
- **Activities** — built-in `http` and `commandline`, or your own `IActivity`. Register by name via `AddActivity<T>()` or the `[Activity("name")]` source generator.
- **Durable** — `StartAsync` persists to a store; `decision` suspends with a `PendingDecision`; `CompleteDecisionAsync` resumes along the chosen branch. A crash mid-flight is recoverable with per-node `AtLeastOnce`/`AtMostOnce` semantics.
- **AOT** — safe to `PublishAot` (source-generated `System.Text.Json` context, source-generated activity registry, no reflection).

## Projects

| Project | Responsibility |
|---|---|
| `Batonor.Abstractions` | Contracts, DTOs, SPIs (activities, store, expressions, templates). |
| `Batonor.Core` | The interpreter (`WorkflowEngine`) — no DI dependency. |
| `Batonor.Expressions` | AOT-safe template + condition evaluator. |
| `Batonor.Activities` | Built-in activities (HTTP, command line). |
| `Batonor.Json` | Source-generated `System.Text.Json` serialization context. |
| `Batonor.Persistence.InMemory` / `.Sqlite` | Workflow stores. |
| `Batonor.SourceGen` | `[Activity]` generator → AOT-safe `ActivityRegistry`. |
| `Batonor` | Facade — `BatonorEngine.CreateBuilder()` (no DI). |

## Build & test

Requires the .NET 10 SDK.

```
dotnet build src/Batonor.slnx
dotnet test src/Batonor.slnx
```

## AOT smoke

`samples/Batonor.Sample.Host` is a `PublishAot=true` host that runs a source-generated + SQLite workflow. Publishing requires the native toolchain (VS Developer Command Prompt / CI):

```
dotnet publish samples/Batonor.Sample.Host -c Release -p:PublishAot=true -r win-x64 --self-contained
```

The published binary prints `AOT_SMOKE_OK:...` on success.
