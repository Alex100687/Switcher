namespace Switcher;

/// <summary>
/// Physical keyboard geometry: which keys touch. <see cref="EditCost"/> prices "hit the neighbouring key" lower than
/// a random substitution — a typical fast-typing slip.
/// </summary>
public static class TypoModel
{
    // physical key grid (virtual keys), rows top to bottom; used to tell which keys are neighbours
    private static readonly int[][] Rows =
    {
        new[] { Native.VK_OEM_3, '1', '2', '3', '4', '5', '6', '7', '8', '9', '0', Native.VK_OEM_MINUS, Native.VK_OEM_PLUS },
        new[] { 'Q', 'W', 'E', 'R', 'T', 'Y', 'U', 'I', 'O', 'P', Native.VK_OEM_4, Native.VK_OEM_6, Native.VK_OEM_5 },
        new[] { 'A', 'S', 'D', 'F', 'G', 'H', 'J', 'K', 'L', Native.VK_OEM_1, Native.VK_OEM_7 },
        new[] { 'Z', 'X', 'C', 'V', 'B', 'N', 'M', Native.VK_OEM_COMMA, Native.VK_OEM_PERIOD, Native.VK_OEM_2 },
    };

    private static readonly Dictionary<int, (int row, int col)> Grid = BuildGrid();

    private static Dictionary<int, (int, int)> BuildGrid()
    {
        var g = new Dictionary<int, (int, int)>();
        for (int r = 0; r < Rows.Length; r++)
            for (int c = 0; c < Rows[r].Length; c++)
                g[Rows[r][c]] = (r, c);
        return g;
    }

    /// <summary>Two characters are neighbours if their physical keys (in the given layout) touch on the keyboard.</summary>
    public static bool AreNeighbours(char a, char b, IntPtr hkl)
    {
        int va = Native.VkKeyScanExW(a, hkl) & 0xFF;
        int vb = Native.VkKeyScanExW(b, hkl) & 0xFF;
        if (va == 0xFF || vb == 0xFF) return false;
        if (!Grid.TryGetValue(va, out var pa) || !Grid.TryGetValue(vb, out var pb)) return false;
        if (pa.row == pb.row) return Math.Abs(pa.col - pb.col) == 1;
        if (Math.Abs(pa.row - pb.row) != 1) return false;
        // rows are staggered half a key to the right: the key at (r+1, c) touches (r, c) and (r, c+1)
        var (upper, lower) = pa.row < pb.row ? (pa, pb) : (pb, pa);
        return lower.col == upper.col || lower.col == upper.col - 1;
    }
}
