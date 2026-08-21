using System.Drawing;
using System.IO;

namespace GridLookout.UI;

/// <summary>
/// E7/I9: <see cref="Image.FromStream(Stream)"/> does not copy pixel data out of the stream at
/// call time — the returned <see cref="Image"/> keeps reading from the stream lazily on first
/// paint/save (GDI+'s documented lifetime rule: "You must keep the stream open for the lifetime
/// of the Image"). <see cref="WallForm.OnFrameReceived"/> used to decode inside a
/// <c>using var stream = ...</c> block and hand the resulting <see cref="Image"/> straight to
/// <c>pictureBox.Image</c> — the backing <see cref="MemoryStream"/> was disposed at the end of
/// that block while the displayed <see cref="Image"/> still depended on it, a known cause of
/// intermittent GDI+ paint failures under load. <see cref="Decode"/> is the pure fix, isolated so
/// it can be unit-tested without WinForms.
///
/// Deliberately NOT "clone into a fresh Bitmap" (the first draft of this fix did that, via
/// <c>new Bitmap(decoded)</c>, and was corrected before shipping): <c>new Bitmap(Image)</c>
/// allocates a new Format32bppArgb buffer and blits into it via Graphics.DrawImage, regardless of
/// the source's pixel format — a JPEG decodes to Format24bppRgb, so that clone would have made
/// every one of PageSize concurrently-live tiles ~33% larger in memory AND cost a full-frame blit
/// per frame, on a product whose whole 20-tile memory footprint is an open, unmeasured question
/// (E7/L5/M9's "roughly 500 MB" claim, soak-gated per the panel-1 chair verdict). The actual fix
/// needs no clone at all: a <see cref="MemoryStream"/> over a managed <c>byte[]</c> holds no
/// unmanaged resource, so simply not disposing it costs nothing extra — it becomes garbage
/// together with the <see cref="Image"/> once the caller (<c>WallForm.OnFrameReceived</c>, which
/// already disposes the previous frame's Image every frame) disposes it. Same lifetime fix,
/// original pixel format, no extra allocation. This class stays SDK-free — see the north star's
/// "pure-logic classes stay SDK-free" rule — it only touches System.Drawing.
/// </summary>
public static class JpegFrameDecoder
{
    /// <summary>Decodes one JPEG frame into an <see cref="Image"/> that is safe to keep and use
    /// indefinitely — the backing stream is intentionally leaked to the GC rather than disposed
    /// (see the type's doc comment), never reused or exposed. Caller owns the returned Image and
    /// must dispose it. Throws on malformed/truncated <paramref name="jpegBytes"/> exactly as
    /// <see cref="Image.FromStream(Stream)"/> would — callers that must never let a single bad
    /// frame propagate (e.g. a live SDK callback) are expected to catch, same as before this type
    /// existed. Public (not internal, no InternalsVisibleTo exists in this repo) so
    /// <c>tests/GridLookout.Tests</c> can exercise it directly — same convention as
    /// <c>LayoutEngine</c>/<c>TileScaleModeParser</c>/other pure-logic types here.</summary>
    public static Image Decode(byte[] jpegBytes)
    {
        // GDI+ ties an Image built via FromStream to this stream for the Image's entire
        // lifetime — do NOT wrap it in `using`. A MemoryStream over a managed byte[] holds no
        // unmanaged resource, so leaving it undisposed here is not a leak.
        var stream = new MemoryStream(jpegBytes);
        return Image.FromStream(stream);
    }
}
