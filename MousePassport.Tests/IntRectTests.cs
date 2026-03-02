using MousePassport.App.Models;
using Xunit;

namespace MousePassport.Tests;

public sealed class IntRectTests
{
    [Fact]
    public void Contains_returns_true_for_point_inside_bounds()
    {
        var rect = new IntRect(10, 20, 100, 200);
        Assert.True(rect.Contains(new IntPoint(50, 50)));
        Assert.True(rect.Contains(new IntPoint(10, 20)));
        Assert.True(rect.Contains(new IntPoint(99, 199)));
    }

    [Fact]
    public void Contains_returns_false_for_point_on_or_outside_right_bottom()
    {
        var rect = new IntRect(10, 20, 100, 200);
        Assert.False(rect.Contains(new IntPoint(100, 100)));
        Assert.False(rect.Contains(new IntPoint(50, 200)));
        Assert.False(rect.Contains(new IntPoint(0, 0)));
    }

    [Fact]
    public void DistanceSquaredTo_returns_zero_for_point_inside()
    {
        var rect = new IntRect(0, 0, 100, 100);
        Assert.Equal(0, rect.DistanceSquaredTo(new IntPoint(50, 50)));
    }

    [Fact]
    public void DistanceSquaredTo_returns_positive_for_point_outside()
    {
        var rect = new IntRect(10, 10, 20, 20);
        Assert.Equal(25, rect.DistanceSquaredTo(new IntPoint(5, 10)));  // 5 units left -> 5^2 = 25
    }
}
