# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build / test commands

Requires the .NET 10 SDK (10.0.400 installed). The solution uses the new `.slnx` format.

- Build the whole solution: `dotnet build src/Batonor.slnx`
- Run all tests: `dotnet test src/Batonor.slnx`
- Run a single test class: `dotnet test src/Batonor.Tests/Batonor.Tests.csproj --filter "FullyQualifiedName~ExpressionTests"`
- Run a single test method: `dotnet test src/Batonor.Tests/Batonor.Tests.csproj --filter "FullyQualifiedName~WorkflowEngineTests.Engine_Runs_Sequence_And_Choice"`

Tests use xunit. There is no separate lint step beyond the build (the SDK target produces warnings; keep them at zero).

Note: `src/Directory.Build.props` sets `TargetFramework=net10.0` (plus Nullable/ImplicitUsings/LangVersion/Deterministic/InvariantGlobalization) globally, so the library `.csproj` files omit an explicit `TargetFramework`; the test project sets `net10.0` explicitly. Do not add a conflicting TFM.

## What this is

Batonor is an embeddable, durable-workflow engine for .NET: a JSON-configured control flow (sequence / parallel / choice) that calls user-supplied activities. It is written to be Native-AOT (`PublishAot`) friendly. It follows a "kernel + plugins" architecture: the core interprets a workflow definition and depends only on contracts (interfaces), never on concrete implementations.

## Architecture

The solution is a strict 4-project, dependency-inverted layering. Dependencies point one way only:

- **`Batonor.Abstractions`** — everything the engine needs to know about, as contracts. No dependencies. `IActivity`, `IActivityContext`, `IActivityResolver`, `IExpressionEvaluator`, `ITemplateEngine`, `IWorkflowStore`, `IWorkflowSerializer`, the `[Activity]` attribute, the DTOs (`WorkflowDefinition`, `WorkflowNode`, `WorkflowInstance`), enums (`JoinMode`, `RecoveryPolicy`, `WorkflowStatus`), `PendingDecision`, and the exception hierarchy.
- **`Batonor.Core`** — the interpreter. `WorkflowEngine` (sealed; construct it directly), the internal `ExecutionContext` (the variable scope + cloning for parallel), `DictionaryActivityResolver` (an in-memory name→`IActivity` map, the only resolver implemented so far), and the internal `ActivityContext` that backs `IActivityContext`.
- **`Batonor.Expressions`** — the default AOT-safe expression engine, independently testable: `TemplateEngine` (`${path}` templates) and `ConditionEvaluator` (a hand-written recursive-descent parser — no Roslyn). `ScopeResolver` resolves dotted paths.
- **`Batonor.Tests`** — xunit; references the three above. `ExpressionTests` and `WorkflowEngineTests`.

### The execution model (the non-obvious part)

A `WorkflowDefinition` is a **flat list of `WorkflowNode`s** in `Steps`, not a nested tree.

- Control-flow nodes reference other nodes **by Id**. There is no containment.
  - `sequence` → `Config.steps` = `["id1","id2"]`
  - `choice` → `Config.branches` = `[{ "when":"<expr>", "then":"id" }, { "default":"id" }]` (first matching `when` wins, else `default`)
  - `parallel` → `Config.branches` = `[["id1","id2"], ["id3"]]`, plus `Config.join` = `"all"` (default) or `"any"`
- **Any other `node.Type` is treated as an activity name** and resolved through `IActivityResolver`. An unregistered name throws `ActivityNotFoundException`.
- **Root nodes** are computed by `GetRootNodes`: any node that is never referenced as a control-flow target. Roots run in declaration order. So a "sequence at the top level" is just the set of roots.
- **Activities** are executed via `RunActivityAsync`: the `Config` is template-resolved (recursively renders `$` references in strings) into `IActivityContext.Input`, then `ExecuteAsync` is called. The result is written both to the `node.Output` variable name (if set) and to the `nodeId.output` scope path so later nodes/templates can reference it.

### Scope and expression semantics

`ExecutionContext` holds one flat `Dictionary<string, JsonNode?>` keyed by root name. Built-ins: `input` (the start payload), each definition `Variables` entry, each node's `output` variable, and each node's `nodeId.output`. Parallel branches run on `context.Clone()` (a deep-cloned scope) so branches never write shared variables.

- Template rule: a **bare** `${path}` (the entire template) returns the raw typed `JsonNode`; mixed text interpolates as a string.
- Condition evaluator supports `== != < > <= >= && || ! + - * / %`, parentheses, string/number/bool/null literals, and `${path}` or bare identifier references.

### The design doc is the source of truth — and is mostly NOT implemented

`doc/Batonor-设计文档.md` (in Chinese) is the authoritative design spec for the whole project. **Treat it as the intent, not the current state.** It names many packages that do not exist in the repo: `Batonor.Activities`, `Batonor.SourceGen`, `Batonor.Plugins.Dynamic`, `Batonor.Persistence.*`, `Batonor.Json`, the `Batonor` facade / `BatonorEngine.CreateBuilder()`, and the optional `DependencyInjection`/`Http`/`Grpc`/`Server` packages. Only Abstractions, Core, Expressions, and Tests are implemented.

Key unimplemented gaps you may be asked to build (the doc has detailed specs for each):
- Deciding on **suspend/resume**: a `decision` node that persists a `PendingDecision`, suspends the instance, and resumes on `CompleteDecisionAsync`. The `IWorkflowStore`/`PendingDecision` types exist, but the engine does not use them.
- Loops (`foreach`, `while`), **retry/`onError` handling**, and **durable persistence** (store save/load) are not wired into `WorkflowEngine` yet.
- Parallel join-back (**mapping branch outputs to a shared variable via `node.Output`**) is described in the doc but not implemented in `RunParallelAsync`.
- Activity registration via **source generation** (`Batonor.SourceGen`) or runtime plugin loading (`Batonor.Plugins.Dynamic`) — only the hand-built `DictionaryActivityResolver` exists.

## Hard constraints (carry these into every change)

- **Native-AOT is a first-class requirement.** The AOT path must not use reflection, `Assembly.Load`, `Reflection.Emit`, or Roslyn runtime compilation. Favored patterns: source generators for activity registration, hand-written parsers, System.Text.Json source generation, `HttpClient`, ADO.NET. Anything dynamic belongs in a separate JIT-only package that never enters the AOT build.
- **`Batonor.Core` has no DI dependency.** Registration is meant to go through a builder (`BatonorEngine.CreateBuilder()` + `.UseXxx()` / `.AddActivity<T>()`), with an optional MS.DI bridge in a separate package. `IActivityContext.ServiceProvider` is nullable and null in no-DI scenarios.
- **Definitions are immutable per version.** A published definition is treated as fixed; the `WorkflowInstance` snapshots the `WorkflowDefinition` it started under, and resume uses that snapshot, not any newer version.
- **`IWorkflowStore` is pure persistence** — it must not do authorization. Auth stays in the engine layer (`StartAsync` / `CompleteDecisionAsync` accept a nullable `ClaimsPrincipal`).
- Activity `AttemptId` is a stable idempotency key (`{instanceId}:{nodeId}`), invariant across retries/recovery — used by any activity with external side effects.

## Conventions

- **Language:** Design docs and implementation plans (e.g. `doc/Batonor-设计文档.md`, `doc/superpowers/plans/*.md`) are written in Chinese. Source code, code comments, and identifiers are written in English.
- File-scoped namespaces, `sealed` on concrete/leaf types, `init`-only on immutable DTOs, XML doc comments on all public API, nullable reference types enabled, `ValueTask` for async activity/workflow boundaries.
- Match the existing comment density and idiom when adding or editing code. All public API surface is documented with `<summary>` XML comments — keep that up.
