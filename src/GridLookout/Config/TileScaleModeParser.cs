namespace GridLookout.Config;

/// <summary>Per-tile image scaling behavior — see <see cref="WallConfig.TileScaleMode"/>.</summary>
public enum TileScaleModeKind
{
    /// <summary>Aspect-preserving letterbox — PictureBox SizeMode Zoom. Default.</summary>
    Fit,

    /// <summary>Aspect-preserving cover: scale until the tile is fully covered, crop overflow,
    /// centered. No native PictureBoxSizeMode equivalent (Zoom letterboxes instead of cropping) —
    /// drawn by <see cref="GridLookout.UI.ScalableTilePictureBox"/>.</summary>
    Fill,

    /// <summary>Ignore aspect ratio, stretch to fill the tile exactly — PictureBox SizeMode
    /// StretchImage.</summary>
    Stretch,
}

/// <summary>Parses <see cref="WallConfig.TileScaleMode"/>'s raw string. Kept a plain string on
/// <see cref="WallConfig"/> rather than a JSON-enum-converted property (unlike <see cref="AuthMode"/>)
/// so a typo degrades to <see cref="TileScaleModeKind.Fit"/> with a warning instead of throwing out
/// of <c>WallConfigLoader.LoadOrCreate</c> and taking down the whole config load over one bad field
/// — same "a wallboard never dies over a typo" rule <c>LayoutSpecParser</c> follows. SDK-free and
/// logger-free — the caller (<c>WallForm</c>'s constructor) owns emitting the warning line.</summary>
public static class TileScaleModeParser
{
    /// <param name="value"><see cref="WallConfig.TileScaleMode"/>'s raw string. Case-insensitive;
    /// null/empty/whitespace is the unset default (Fit, no warning), not an invalid value.</param>
    /// <param name="warning">Non-null only when <paramref name="value"/> was non-empty and did not
    /// match Fit/Fill/Stretch. Null for a recognized value or the empty/default case.</param>
    public static TileScaleModeKind Parse(string? value, out string? warning)
    {
        warning = null;

        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "Fit", StringComparison.OrdinalIgnoreCase))
        {
            return TileScaleModeKind.Fit;
        }

        if (string.Equals(value, "Fill", StringComparison.OrdinalIgnoreCase))
        {
            return TileScaleModeKind.Fill;
        }

        if (string.Equals(value, "Stretch", StringComparison.OrdinalIgnoreCase))
        {
            return TileScaleModeKind.Stretch;
        }

        warning = $"Unrecognized TileScaleMode '{value}' — falling back to Fit.";
        return TileScaleModeKind.Fit;
    }
}
