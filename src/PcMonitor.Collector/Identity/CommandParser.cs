namespace PcMonitor.Collector.Identity;

/// <summary>Extracts the executable path out of the command-line strings found in Run keys, tasks and services.</summary>
public static class CommandParser
{
    private static readonly string[] ExecutableExtensions = { ".exe", ".com", ".bat", ".cmd", ".sys", ".msc", ".lnk", ".cpl", ".scr" };

    /// <summary>
    /// Returns an absolute path (environment variables expanded) or null if none can be determined.
    /// Handles quoted paths, unquoted paths containing spaces, \??\ and \SystemRoot\ prefixes and
    /// bare file names that live in System32.
    /// </summary>
    public static string? ExtractExePath(string? command, Func<string, bool>? exists = null)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        exists ??= File.Exists;
        string s = command.Trim();

        string candidate;
        if (s[0] == '"')
        {
            int close = s.IndexOf('"', 1);
            candidate = close > 1 ? s.Substring(1, close - 1) : s.Substring(1);
        }
        else
        {
            candidate = FirstUnquotedPath(s, exists);
        }

        candidate = Normalize(candidate);
        if (string.IsNullOrEmpty(candidate)) return null;

        if (!Path.IsPathRooted(candidate) || candidate.StartsWith(@"\", StringComparison.Ordinal) && !candidate.StartsWith(@"\\", StringComparison.Ordinal))
        {
            string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            foreach (string root in new[] { sys, win, Path.Combine(sys, "drivers") })
            {
                string probe = Path.Combine(root, candidate.TrimStart('\\'));
                if (exists(probe)) return probe;
            }
            return null;
        }
        return candidate;
    }

    private static string FirstUnquotedPath(string s, Func<string, bool> exists)
    {
        // Prefer the longest prefix that ends with an executable extension at a token boundary.
        foreach (string ext in ExecutableExtensions)
        {
            int idx = s.IndexOf(ext, StringComparison.OrdinalIgnoreCase);
            while (idx >= 0)
            {
                int end = idx + ext.Length;
                if (end == s.Length || char.IsWhiteSpace(s[end]) || s[end] == ',' || s[end] == '"')
                    return s.Substring(0, end);
                idx = s.IndexOf(ext, end, StringComparison.OrdinalIgnoreCase);
            }
        }
        int sp = s.IndexOfAny(new[] { ' ', '\t' });
        return sp > 0 ? s.Substring(0, sp) : s;
    }

    private static string Normalize(string p)
    {
        p = p.Trim().Trim('"');
        if (p.StartsWith(@"\??\", StringComparison.Ordinal)) p = p.Substring(4);
        if (p.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), p.Substring(12));
        p = Environment.ExpandEnvironmentVariables(p);
        // "C:\Foo\bar.exe,0" style icon references
        int comma = p.LastIndexOf(',');
        if (comma > 3 && int.TryParse(p.AsSpan(comma + 1), out _)) p = p.Substring(0, comma);
        return p;
    }
}
