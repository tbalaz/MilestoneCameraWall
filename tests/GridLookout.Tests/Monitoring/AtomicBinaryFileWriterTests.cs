using GridLookout.Monitoring;
using Xunit;

namespace GridLookout.Tests.Monitoring;

public class AtomicBinaryFileWriterTests : IDisposable
{
    private readonly string _dir;

    public AtomicBinaryFileWriterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "GridLookout.Tests.AtomicBinaryFileWriter." + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public void Write_ThenRead_RoundTrips()
    {
        var destination = Path.Combine(_dir, "screen-1.png");
        var content = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };

        AtomicBinaryFileWriter.Write(destination, content);

        Assert.Equal(content, File.ReadAllBytes(destination));
    }

    [Fact]
    public void Write_CreatesDestinationDirectoryIfMissing()
    {
        var nested = Path.Combine(_dir, "nested", "screenshots");
        var destination = Path.Combine(nested, "screen-1.png");

        AtomicBinaryFileWriter.Write(destination, new byte[] { 1 });

        Assert.True(Directory.Exists(nested));
        Assert.True(File.Exists(destination));
    }

    [Fact]
    public void Write_OverwritesExistingFile_UsingReplaceNotAppend()
    {
        var destination = Path.Combine(_dir, "screen-1.png");

        AtomicBinaryFileWriter.Write(destination, new byte[] { 1, 1, 1, 1 });
        AtomicBinaryFileWriter.Write(destination, new byte[] { 2, 2 });

        // If this were an append, the file would be 6 bytes and start with the first payload.
        Assert.Equal(new byte[] { 2, 2 }, File.ReadAllBytes(destination));
    }

    [Fact]
    public void Write_LeavesNoTempFilesBehindOnSuccess()
    {
        var destination = Path.Combine(_dir, "screen-1.png");

        AtomicBinaryFileWriter.Write(destination, new byte[] { 1 });

        var leftoverTempFiles = Directory.GetFiles(_dir, ".screen-1.png.tmp-*");
        Assert.Empty(leftoverTempFiles);
    }

    [Fact]
    public void Write_FirstEverWrite_UsesMoveNotReplace_NoPriorFileNeeded()
    {
        // File.Replace requires the destination to already exist — the very first write to a fresh
        // directory has no prior screen-1.png; this must succeed via File.Move instead of throwing
        // FileNotFoundException.
        var destination = Path.Combine(_dir, "screen-1.png");

        var ex = Record.Exception(() => AtomicBinaryFileWriter.Write(destination, new byte[] { 9 }));

        Assert.Null(ex);
        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(destination));
    }

    [Fact]
    public void Write_DestinationUntouchedWhenPathIsInvalid()
    {
        // Simulate a write failure (an invalid filename character) after a valid prior write — the
        // destination must still hold the LAST GOOD content, never a torn/partial file.
        var destination = Path.Combine(_dir, "screen-1.png");
        AtomicBinaryFileWriter.Write(destination, new byte[] { 5, 5, 5 });

        // Path.Combine itself rejects the embedded NUL — evaluated INSIDE the lambda (not
        // precomputed above) so Assert.ThrowsAny actually observes it, rather than the test method
        // throwing before the assertion even runs.
        Assert.ThrowsAny<Exception>(() => AtomicBinaryFileWriter.Write(Path.Combine(_dir, "bad\0name.png"), new byte[] { 9, 9 }));

        Assert.Equal(new byte[] { 5, 5, 5 }, File.ReadAllBytes(destination));
    }

    [Fact]
    public void Write_ThrowsArgumentException_WhenDestinationHasNoDirectoryComponent()
    {
        Assert.Throws<ArgumentException>(() => AtomicBinaryFileWriter.Write("screen-1.png", new byte[] { 1 }));
    }
}
