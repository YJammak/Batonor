namespace Batonor;

/// <summary>DI-agnostic entry point for assembling a <see cref="WorkflowEngine"/>.</summary>
public static class BatonorEngine
{
    /// <summary>Starts a builder for assembling a <see cref="WorkflowEngine"/>.</summary>
    public static IBatonorBuilder CreateBuilder() => new BatonorBuilder();
}
