namespace GridLookout.Layout;

/// <summary>
/// Computes automatic (near-square) camera-grid layouts from a camera count. Pure logic, no MIP
/// SDK dependency.
/// </summary>
public static class LayoutEngine
{
    // Explicit table for n=1..12 exactly as specified — the >12 formula below does NOT reproduce
    // n=3 ([1,2], formula would give [3]) or n=4 ([2,2], formula would give [4]), so those rows
    // must stay hardcoded rather than "unified" into one formula.
    private static readonly int[][] ExplicitTable =
    {
        Array.Empty<int>(), // index 0 unused (n=0 handled separately below)
        new[] { 1 },        // 1
        new[] { 2 },        // 2
        new[] { 1, 2 },     // 3
        new[] { 2, 2 },     // 4
        new[] { 2, 3 },     // 5
        new[] { 3, 3 },     // 6
        new[] { 3, 4 },     // 7
        new[] { 4, 4 },     // 8
        new[] { 3, 3, 3 },  // 9
        new[] { 3, 3, 4 },  // 10
        new[] { 3, 4, 4 },  // 11
        new[] { 4, 4, 4 },  // 12
    };

    /// <summary>
    /// Returns cameras-per-row, top to bottom. n=0 (recorder with zero enabled cameras) returns
    /// an empty array — callers must render a "no cameras" card rather than an empty grid.
    /// </summary>
    public static int[] Compute(int n)
    {
        if (n <= 0)
        {
            return Array.Empty<int>();
        }

        if (n <= 12)
        {
            return (int[])ExplicitTable[n].Clone();
        }

        // Beyond 12: rows = ceil(n / 4), floor(n / rows) per row, one extra camera per row
        // starting from the BOTTOM row — continues the 5->[2,3], 7->[3,4], 10->[3,3,4] pattern.
        int rows = (int)Math.Ceiling(n / 4.0);
        int baseCount = n / rows;
        int extra = n - baseCount * rows;

        var result = new int[rows];
        for (int i = 0; i < rows; i++)
        {
            result[i] = baseCount;
        }
        for (int i = 0; i < extra; i++)
        {
            result[rows - 1 - i]++;
        }

        return result;
    }
}
