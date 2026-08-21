using System.Drawing;
using System.Drawing.Imaging;
using GridLookout.UI;
using Xunit;

namespace GridLookout.Tests.UI;

/// <summary>
/// Covers <see cref="JpegFrameDecoder"/> — the pure decode step extracted from
/// <c>WallForm.OnFrameReceived</c> to fix E7/I9's GDI+ stream-lifetime bug: the old inline code
/// decoded via <c>Image.FromStream</c> inside a <c>using</c> block that disposed the backing
/// <c>MemoryStream</c> while the returned <see cref="Image"/> was still displayed, which GDI+
/// documents as unsafe (the Image reads from the stream lazily, not just at decode time). The
/// regression this guards: a decoded frame must remain fully usable long after the bytes/stream
/// that produced it are gone — WITHOUT paying for a format-converting clone (a first draft of the
/// fix cloned into a fresh 32bpp Bitmap; <see cref="Decode_DoesNotUpconvertPixelFormat"/> pins
/// that regression specifically, since a passing dimensions/usability check alone would not have
/// caught it).
/// </summary>
public class JpegFrameDecoderTests
{
    private static byte[] MakeJpegBytes(int width, int height, Color fill)
    {
        using var bitmap = new Bitmap(width, height);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(fill);
        }

        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Jpeg);
        return ms.ToArray();
    }

    [Fact]
    public void Decode_ValidJpeg_ReturnsImageWithMatchingDimensions()
    {
        var jpegBytes = MakeJpegBytes(8, 6, Color.Red);

        using var result = JpegFrameDecoder.Decode(jpegBytes);

        Assert.Equal(8, result.Width);
        Assert.Equal(6, result.Height);
    }

    [Fact]
    public void Decode_DoesNotUpconvertPixelFormat()
    {
        // A JPEG decodes to Format24bppRgb. A clone-into-fresh-Bitmap "fix" (new Bitmap(decoded))
        // silently upconverts to Format32bppArgb — ~33% larger per frame, on a product whose
        // memory footprint at PageSize concurrent tiles is an open, unmeasured question (E7/L5/M9).
        // This is the one assertion that would have caught that regression; dimensions/usability
        // checks alone would not.
        var jpegBytes = MakeJpegBytes(4, 4, Color.Blue);

        using var result = JpegFrameDecoder.Decode(jpegBytes);

        Assert.Equal(PixelFormat.Format24bppRgb, result.PixelFormat);
    }

    [Fact]
    public void Decode_ResultOutlivesAndIsIndependentOfTheInputBytes()
    {
        // Regression test for E7/I9: decode, then let every reference to the source bytes/stream
        // go away and force a collection before touching the result — the pre-fix code (decoding
        // straight off a `using`-disposed MemoryStream into the displayed Image) is exactly the
        // shape of bug a GC pass between decode and use would have made visible via ObjectDisposed/
        // generic GDI+ errors on first real use (GetPixel forces GDI+ to actually touch pixel
        // data, not just read cached Width/Height). Cast to Bitmap for GetPixel: GDI+'s own
        // Image.FromStream on a JPEG always returns a Bitmap instance at runtime, even though
        // JpegFrameDecoder's public surface is the more general Image (matching Image.FromStream's
        // own return type).
        Image result;
        {
            var jpegBytes = MakeJpegBytes(4, 4, Color.Green);
            result = JpegFrameDecoder.Decode(jpegBytes);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();

        using (result)
        {
            var pixel = ((Bitmap)result).GetPixel(0, 0);
            Assert.Equal(255, pixel.A);
        }
    }

    [Fact]
    public void Decode_MalformedBytes_Throws()
    {
        var garbage = new byte[] { 0x00, 0x01, 0x02, 0x03 };

        Assert.ThrowsAny<Exception>(() => JpegFrameDecoder.Decode(garbage));
    }
}
