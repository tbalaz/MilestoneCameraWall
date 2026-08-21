using GridLookout.Milestone;
using Xunit;

namespace GridLookout.Tests.Milestone;

/// <summary>Covers F2 (multi-recorder walls)'s <see cref="CameraInfo.DisplayName"/> — the single
/// place the "empty RecorderName means single-recorder mode, don't qualify" rule lives, so
/// WallForm/LiveTileSource caption and log-label call sites never have to repeat it.</summary>
public class CameraInfoTests
{
    private static CameraInfo Camera(string name, string recorderName) =>
        new(name, Guid.NewGuid(), new VideoOS.Platform.Item { Name = name, Enabled = true }, true, recorderName);

    [Fact]
    public void DisplayName_EmptyRecorderName_ReturnsPlainName()
    {
        var camera = Camera("Front Gate", recorderName: string.Empty);

        Assert.Equal("Front Gate", camera.DisplayName);
    }

    [Fact]
    public void DisplayName_DefaultConstructor_NoRecorderNameGiven_ReturnsPlainName()
    {
        // Positional-record default — every RecorderLocator.Locate-produced CameraInfo (single-
        // recorder mode) constructs with exactly 4 args, relying on this default.
        var camera = new CameraInfo("Front Gate", Guid.NewGuid(), new VideoOS.Platform.Item { Name = "Front Gate", Enabled = true }, true);

        Assert.Equal(string.Empty, camera.RecorderName);
        Assert.Equal("Front Gate", camera.DisplayName);
    }

    [Fact]
    public void DisplayName_NonEmptyRecorderName_QualifiesWithSlash()
    {
        var camera = Camera("Front Gate", recorderName: "Recorder A");

        Assert.Equal("Recorder A / Front Gate", camera.DisplayName);
    }
}
