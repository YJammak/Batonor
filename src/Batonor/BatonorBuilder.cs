using Batonor.Abstractions;
using Batonor.Activities;
using Batonor.Core;
using Batonor.Expressions;
using Batonor.Persistence.InMemory;
using Batonor.Persistence.Sqlite;

namespace Batonor;

/// <summary>
/// Default <see cref="IBatonorBuilder"/> implementation. Assembles a <see cref="WorkflowEngine"/>
/// from user-registered activities, built-in activities, the AOT-safe expression/template engines,
/// and a chosen workflow store. Pure object assembly — no DI container.
/// </summary>
public sealed class BatonorBuilder : IBatonorBuilder
{
    private readonly Dictionary<string, IActivity> _activities = new(StringComparer.Ordinal);
    private IWorkflowStore? _store;

    public IBatonorBuilder AddActivity<T>(string name) where T : IActivity, new()
        => AddActivity(name, new T());

    public IBatonorBuilder AddActivity(string name, IActivity activity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _activities[name] = activity;
        return this;
    }

    public IBatonorBuilder UseBuiltInActivities()
    {
        AddActivity("http", new HttpActivity());
        AddActivity("commandline", new CommandLineActivity());
        return this;
    }

    public IBatonorBuilder UseInMemory()
    {
        _store = new InMemoryWorkflowStore();
        return this;
    }

    public IBatonorBuilder UseSqlite(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _store = new SqliteWorkflowStore(connectionString);
        return this;
    }

    public WorkflowEngine Build()
        => new(new DictionaryActivityResolver(_activities), new ConditionEvaluator(), new TemplateEngine(), _store);
}
