# Batonor

一个可嵌入的 .NET **durable** 工作流引擎。你用 JSON 描述整条流程（控制流 + 活动 + 参数），`Batonor` 引擎解释它并调用你的活动。它对 **Native-AOT**（`PublishAot`）友好——无反射、源生成序列化、源生成活动注册。

## 快速上手

```csharp
using Batonor;
using Batonor.Abstractions;
using System.Text.Json.Nodes;

// 装配引擎（不需要 DI 容器）。
var engine = BatonorEngine.CreateBuilder()
    .UseBuiltInActivities()                       // 注册 "http" 和 "commandline"
    .UseSqlite("Data Source=batonor.db")          // durable 状态；测试可用 .UseInMemory()
    .Build();

// 工作流定义是节点的扁平列表；控制流节点通过 id 引用子节点。
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
            ["prompt"] = "金额超过 5000，是否批准？",
            ["options"] = new JsonArray(
                new JsonObject { ["label"] = "批准", ["value"] = "approve", ["isDefault"] = true },
                new JsonObject { ["label"] = "拒绝", ["value"] = "reject" }),
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

// 运行。若走到 "decision" 节点，StartAsync 返回 Suspended 并带一个待定决策。
var instance = await engine.StartAsync(definition, new JsonObject { ["orderId"] = "A-123" });

if (instance.Status == WorkflowStatus.Suspended)
{
    // 在任意地方展示待定决策（来自 store 的 ListPendingDecisionsAsync）——Web UI、CLI、桌面。
    // 然后沿所选分支续跑：
    //   var resumed = await engine.CompleteDecisionAsync(decisionId, "approve");
}
```

也可以注册自定义活动：

```csharp
[Activity("double")]                    // Batonor.SourceGen 为其生成 AOT 保根的注册表
public sealed class DoubleActivity : IActivity
{
    public ValueTask<object?> ExecuteAsync(IActivityContext context, CancellationToken ct)
        => ValueTask.FromResult<object?>((context.Input?["value"]?.GetValue<double>() ?? 0) * 2);
}
```

## 特性

- **控制流** — `sequence`、`parallel`（fork–join，`join: all|any`）、`choice`（条件路由）、`decision`（交互式挂起/续跑）。
- **活动** — 内置 `http` 与 `commandline`，或你自己的 `IActivity`。通过 `AddActivity<T>()` 或 `[Activity("name")]` 源生成器按名注册。
- **durable** — `StartAsync` 持久化到 store；`decision` 挂起并产生 `PendingDecision`；`CompleteDecisionAsync` 沿所选分支续跑。运行中崩溃可按节点的 `AtLeastOnce`/`AtMostOnce` 语义恢复。
- **AOT** — 可 `PublishAot`（源生成 `System.Text.Json` 上下文、源生成活动注册表、无反射）。

## 项目

| 项目 | 职责 |
|---|---|
| `Batonor.Abstractions` | 契约、DTO、SPI（活动、store、表达式、模板）。 |
| `Batonor.Core` | 解释器（`WorkflowEngine`）——不依赖 DI。 |
| `Batonor.Expressions` | AOT 安全的模板与条件求值器。 |
| `Batonor.Activities` | 内置活动（HTTP、命令行）。 |
| `Batonor.Json` | 源生成的 `System.Text.Json` 序列化上下文。 |
| `Batonor.Persistence.InMemory` / `.Sqlite` | 工作流 store。 |
| `Batonor.SourceGen` | `[Activity]` 生成器 → AOT 安全的 `ActivityRegistry`。 |
| `Batonor` | 门面——`BatonorEngine.CreateBuilder()`（无 DI）。 |

## 构建与测试

需要 .NET 10 SDK。

```
dotnet build src/Batonor.slnx
dotnet test src/Batonor.slnx
```

## AOT 冒烟

`samples/Batonor.Sample.Host` 是一个 `PublishAot=true` 的宿主，运行一条源生成 + SQLite 的工作流。发布需要原生工具链（VS 开发者命令提示符 / CI）：

```
dotnet publish samples/Batonor.Sample.Host -c Release -p:PublishAot=true -r win-x64 --self-contained
```

发布出的原生二进制运行成功后打印 `AOT_SMOKE_OK:...`。
