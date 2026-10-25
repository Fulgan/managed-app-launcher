namespace ManagedLauncher.Core.Targets;

/// <summary>
/// Orders strings so embedded numbers compare by value: <c>app-1.0.10</c> sorts after <c>app-1.0.9</c>.
/// Text runs compare ordinally, ignoring case.
/// </summary>
public sealed class VersionComparer : IComparer<string?>
{
    public static readonly VersionComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            var digits = char.IsAsciiDigit(x[i]);
            if (digits != char.IsAsciiDigit(y[j]))
                return digits ? -1 : 1;

            var a = TakeRun(x, ref i, digits);
            var b = TakeRun(y, ref j, digits);
            int c;
            if (digits)
            {
                a = a.TrimStart('0');
                b = b.TrimStart('0');
                c = a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
            }
            else
            {
                c = string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            }
            if (c != 0) return c;
        }
        return (x.Length - i).CompareTo(y.Length - j);
    }

    private static string TakeRun(string s, ref int pos, bool digits)
    {
        var start = pos;
        while (pos < s.Length && char.IsAsciiDigit(s[pos]) == digits)
            pos++;
        return s[start..pos];
    }
}
