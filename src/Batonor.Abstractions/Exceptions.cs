namespace Batonor.Abstractions;

/// <summary>Base exception for all Batonor errors.</summary>
public class BatonorException : Exception
{
    public BatonorException(string message) : base(message) { }
    public BatonorException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Thrown when a workflow definition is malformed or references a missing node.</summary>
public sealed class WorkflowDefinitionException : BatonorException
{
    public WorkflowDefinitionException(string message) : base(message) { }
}

/// <summary>Thrown when an activity referenced by name is not registered.</summary>
public sealed class ActivityNotFoundException : BatonorException
{
    public ActivityNotFoundException(string name)
        : base($"No activity registered under the name '{name}'.") { }
}

/// <summary>Thrown when a variable or path referenced by a template/condition does not exist.</summary>
public sealed class VariableNotFoundException : BatonorException
{
    public VariableNotFoundException(string path)
        : base($"Variable or path '{path}' was not found in the current scope.") { }
}
