using GridLookout.Layout;
using Xunit;

namespace GridLookout.Tests.Layout;

public class LayoutEngineTests
{
    [Fact]
    public void Compute_ZeroCameras_ReturnsEmpty()
    {
        Assert.Empty(LayoutEngine.Compute(0));
    }

    [Fact]
    public void Compute_NegativeCameras_ReturnsEmpty()
    {
        Assert.Empty(LayoutEngine.Compute(-1));
    }

    [Theory]
    [InlineData(1, new[] { 1 })]
    [InlineData(2, new[] { 2 })]
    [InlineData(3, new[] { 1, 2 })]
    [InlineData(4, new[] { 2, 2 })]
    [InlineData(5, new[] { 2, 3 })]
    [InlineData(6, new[] { 3, 3 })]
    [InlineData(7, new[] { 3, 4 })]
    [InlineData(8, new[] { 4, 4 })]
    [InlineData(9, new[] { 3, 3, 3 })]
    [InlineData(10, new[] { 3, 3, 4 })]
    [InlineData(11, new[] { 3, 4, 4 })]
    [InlineData(12, new[] { 4, 4, 4 })]
    public void Compute_ExplicitTable_1To12(int n, int[] expected)
    {
        Assert.Equal(expected, LayoutEngine.Compute(n));
    }

    [Theory]
    [InlineData(13, new[] { 3, 3, 3, 4 })]
    [InlineData(14, new[] { 3, 3, 4, 4 })]
    [InlineData(15, new[] { 3, 4, 4, 4 })]
    [InlineData(16, new[] { 4, 4, 4, 4 })]
    [InlineData(17, new[] { 3, 3, 3, 4, 4 })]
    [InlineData(18, new[] { 3, 3, 4, 4, 4 })]
    [InlineData(19, new[] { 3, 4, 4, 4, 4 })]
    [InlineData(20, new[] { 4, 4, 4, 4, 4 })]
    public void Compute_Beyond12_ExactRows(int n, int[] expected)
    {
        Assert.Equal(expected, LayoutEngine.Compute(n));
    }

    [Theory]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    [InlineData(20)]
    [InlineData(25)]
    [InlineData(37)]
    public void Compute_Beyond12_SatisfiesInvariants(int n)
    {
        var rows = LayoutEngine.Compute(n);
        int expectedRowCount = (int)Math.Ceiling(n / 4.0);

        Assert.Equal(expectedRowCount, rows.Length);
        Assert.Equal(n, rows.Sum());

        // Extras are added starting from the bottom row, so no row can hold MORE than the row
        // below it (rows are non-decreasing top -> bottom).
        for (int i = 1; i < rows.Length; i++)
        {
            Assert.True(rows[i] >= rows[i - 1], $"row {i} ({rows[i]}) must be >= row {i - 1} ({rows[i - 1]})");
        }
    }
}
