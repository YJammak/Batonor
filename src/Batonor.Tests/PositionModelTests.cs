using Batonor.Abstractions;
using Xunit;

namespace Batonor.Tests;

public class PositionModelTests
{
    [Fact]
    public void Position_Builds_A_Chain_Of_Frames()
    {
        var root = new ExecutionPosition
        {
            NodeId = "outer",
            State = ExecutionPositionState.Running,
            SequenceIndex = 1,
            Child = new ExecutionPosition
            {
                NodeId = "decide",
                State = ExecutionPositionState.Running,
                SuspendedDecisionId = "abc",
            },
        };

        Assert.Equal("outer", root.NodeId);
        Assert.Equal(1, root.SequenceIndex);
        Assert.Equal(ExecutionPositionState.Running, root.State);
        Assert.NotNull(root.Child);
        Assert.Equal("abc", root.Child!.SuspendedDecisionId);
        Assert.Equal("decide", root.Child!.NodeId);
    }
}
