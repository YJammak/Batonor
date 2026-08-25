using Batonor.Abstractions;
using Batonor.Core;

namespace Batonor;

/// <summary>
/// DI-agnostic builder that assembles a <see cref="WorkflowEngine"/> from activities, expressions,
/// a template engine, and an optional workflow store. No dependency-injection container is used.
/// </summary>
public interface IBatonorBuilder
{
    /// <summary>Registers an activity by name (a parameterless-constructible <see cref="IActivity"/>).</summary>
    IBatonorBuilder AddActivity<T>(string name) where T : IActivity, new();

    /// <summary>Registers an activity instance by name.</summary>
    IBatonorBuilder AddActivity(string name, IActivity activity);

    /// <summary>Registers the built-in activities (<c>http</c>, <c>commandline</c>).</summary>
    IBatonorBuilder UseBuiltInActivities();

    /// <summary>Uses an in-memory workflow store.</summary>
    IBatonorBuilder UseInMemory();

    /// <summary>Uses a SQLite workflow store.</summary>
    IBatonorBuilder UseSqlite(string connectionString);

    /// <summary>Builds the <see cref="WorkflowEngine"/>.</summary>
    WorkflowEngine Build();
}
