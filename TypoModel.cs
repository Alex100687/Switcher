namespace Switcher;

/// <summary>
/// Decides whether <c>typed</c> is a *mechanically plausible* fast-typing slip of <c>correct</c>:
/// two adjacent letters swapped, one letter missed, one letter doubled, or a neighbouring key hit instead.
/// Anything else (a random extra letter, a far-away substitution) is more likely a word we simply don't know.
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

    public static bool IsPlausible(string typed, string correct, IntPtr hkl)
    {
        typed = typed.ToLowerInvariant();
        correct = correct.ToLowerInvariant();
        if (typed == correct) return false;

        int n = typed.Length, m = correct.Length;

        if (n == m)
        {
            // find first and last differing positions
            int first = -1, last = -1;
            for (int i = 0; i < n; i++)
                if (typed[i] != correct[i]) { if (first < 0) first = i; last = i; }
            if (first < 0) return false;

            if (first == last)
                return AreNeighbours(typed[first], correct[first], hkl);          // adjacent-key slip

            if (last == first + 1 && typed[first] == correct[last] && typed[last] == correct[first])
                return true;                                                       // transposition

            return false;
        }

        if (m == n + 1)
        {
            // one letter missed: correct with one char removed equals typed
            return RemoveOneEquals(correct, typed);
        }

        if (n == m + 1)
        {
            // one extra letter — accept only if it doubles a neighbouring letter ("helllo")
            for (int i = 0; i < n; i++)
            {
                if (typed.Remove(i, 1) != correct) continue;
                bool doubles = (i > 0 && typed[i - 1] == typed[i]) || (i + 1 < n && typed[i + 1] == typed[i]);
                return doubles;
            }
            return false;
        }

        return false;
    }

    private static bool RemoveOneEquals(string longer, string shorter)
    {
        int i = 0;
        while (i < shorter.Length && longer[i] == shorter[i]) i++;
        return string.CompareOrdinal(longer, i + 1, shorter, i, shorter.Length - i) == 0;
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
