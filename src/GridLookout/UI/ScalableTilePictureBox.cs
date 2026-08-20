using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using GridLookout.Config;

namespace GridLookout.UI;

/// <summary>PictureBox that adds a "Fill" scale mode PictureBoxSizeMode has no equivalent for
/// (aspect-preserving cover — crop overflow, centered; Zoom letterboxes instead of cropping).
/// Fit/Stretch stay on native SizeMode (Zoom/StretchImage) — only Fill is custom-painted.
/// DoubleBuffered avoids flicker on the custom paint path and is harmless for the native modes.</summary>
public sealed class ScalableTilePictureBox : PictureBox
{
    private TileScaleModeKind _scaleMode = TileScaleModeKind.Fit;

    public ScalableTilePictureBox()
    {
        DoubleBuffered = true;
        SizeMode = PictureBoxSizeMode.Zoom; // matches the Fit default of _scaleMode's initializer
    }

    public TileScaleModeKind ScaleMode
    {
        get => _scaleMode;
        set
        {
            _scaleMode = value;
            SizeMode = value switch
            {
                TileScaleModeKind.Stretch => PictureBoxSizeMode.StretchImage,
                TileScaleModeKind.Fill => PictureBoxSizeMode.Normal, // custom-painted below
                _ => PictureBoxSizeMode.Zoom,
            };
            Invalidate();
        }
    }

    /// <summary>Only Fill is custom-painted; Fit/Stretch fall through to the base PictureBox draw
    /// (native SizeMode already set by <see cref="ScaleMode"/>'s setter). The cover scale factor is
    /// computed from the image's ACTUAL size against the tile's CURRENT bounds on every paint, not
    /// cached at request time — still covers correctly with a smaller-than-tile requested frame
    /// (see <c>WallConfig.FitFrameSizeToTile</c>) and on any resize/compact-mode toggle.</summary>
    protected override void OnPaint(PaintEventArgs pe)
    {
        var img = Image;
        if (_scaleMode != TileScaleModeKind.Fill || img is null)
        {
            base.OnPaint(pe);
            return;
        }

        float scale = System.Math.Max((float)Width / img.Width, (float)Height / img.Height);
        int drawWidth = (int)System.Math.Round(img.Width * scale);
        int drawHeight = (int)System.Math.Round(img.Height * scale);
        int x = (Width - drawWidth) / 2;
        int y = (Height - drawHeight) / 2;

        // Plain Bilinear, not HighQualityBilinear — this runs per delivered frame, per Fill tile, on
        // the UI thread (e.g. ~12 fps x 16 tiles = ~190 rescales/sec at defaults); HighQuality costs
        // meaningfully more for a cover-crop where most of the quality gain is invisible anyway.
        pe.Graphics.InterpolationMode = InterpolationMode.Bilinear;
        pe.Graphics.DrawImage(img, new Rectangle(x, y, drawWidth, drawHeight));
    }
}
