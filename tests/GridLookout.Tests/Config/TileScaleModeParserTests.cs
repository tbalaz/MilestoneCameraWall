using GridLookout.Config;
using Xunit;

namespace GridLookout.Tests.Config;

public class TileScaleModeParserTests
{
    [Theory]
    [InlineData("Fit", TileScaleModeKind.Fit)]
    [InlineData("fit", TileScaleModeKind.Fit)]
    [InlineData("FIT", TileScaleModeKind.Fit)]
    [InlineData("Fill", TileScaleModeKind.Fill)]
    [InlineData("fill", TileScaleModeKind.Fill)]
    [InlineData("FILL", TileScaleModeKind.Fill)]
    [InlineData("Stretch", TileScaleModeKind.Stretch)]
    [InlineData("stretch", TileScaleModeKind.Stretch)]
    [InlineData("STRETCH", TileScaleModeKind.Stretch)]
    public void Parse_RecognizedValue_ReturnsExpectedKind_NoWarning(string input, TileScaleModeKind expected)
    {
        var result = TileScaleModeParser.Parse(input, out var warning);

        Assert.Equal(expected, result);
        Assert.Null(warning);
    }

    [Fact]
    public void Parse_Null_ReturnsFit_NoWarning()
    {
        var result = TileScaleModeParser.Parse(null, out var warning);

        Assert.Equal(TileScaleModeKind.Fit, result);
        Assert.Null(warning);
    }

    [Fact]
    public void Parse_Empty_ReturnsFit_NoWarning()
    {
        var result = TileScaleModeParser.Parse(string.Empty, out var warning);

        Assert.Equal(TileScaleModeKind.Fit, result);
        Assert.Null(warning);
    }

    [Fact]
    public void Parse_Whitespace_ReturnsFit_NoWarning()
    {
        var result = TileScaleModeParser.Parse("   ", out var warning);

        Assert.Equal(TileScaleModeKind.Fit, result);
        Assert.Null(warning);
    }

    [Theory]
    [InlineData("Cover")]
    [InlineData("zoom")]
    [InlineData("bogus")]
    public void Parse_InvalidValue_FallsBackToFit_WithWarning(string input)
    {
        var result = TileScaleModeParser.Parse(input, out var warning);

        Assert.Equal(TileScaleModeKind.Fit, result);
        Assert.NotNull(warning);
        Assert.Contains(input, warning);
    }
}
