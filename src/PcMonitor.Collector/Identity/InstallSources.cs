using System.Xml.Linq;
using Microsoft.Win32;
using PcMonitor.Core;

namespace PcMonitor.Collector.Identity;

internal sealed record InstalledProgram(
    string DisplayName, string? Publisher, string? InstallLocation, string? InstallDate,
    string? UninstallString, string? DisplayIcon, string? Version);

/// <summary>Matches an executable to its entry in "Apps &amp; features" (the Uninstall registry keys).</summary>
internal sealed class InstalledPrograms
{
    private readonly List<(string Dir, InstalledProgram Program)> _byDir = new();
    private readonly Dictionary<string, InstalledProgram> _byIconPath = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _byDir.Count;

    public static InstalledPrograms Build()
    {
        var idx = new InstalledPrograms();
        const string unin = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        try { idx.Read(Registry.LocalMachine, unin); } catch { }
        try { idx.Read(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"); } catch { }
        // Per-user installs live in each loaded user hive (HKCU of the service would be SYSTEM's).
        foreach (var p in UserProfiles.Enumerate())
            try { idx.Read(Registry.Users, $@"{p.Sid}\{unin}"); } catch { }
        return idx;
    }

    private void Read(RegistryKey root, string path)
    {
        using var key = root.OpenSubKey(path);
        if (key == null) return;
        foreach (string sub in key.GetSubKeyNames())
        {
            using var k = key.OpenSubKey(sub);
            if (k == null) continue;
            string? name = k.GetValue("DisplayName") as string;
            if (string.IsNullOrWhiteSpace(name)) continue;

            var prog = new InstalledProgram(
                name,
                k.GetValue("Publisher") as string,
                (k.GetValue("InstallLocation") as string)?.Trim().Trim('"'),
                FormatDate(k.GetValue("InstallDate") as string),
                k.GetValue("UninstallString") as string,
                k.GetValue("DisplayIcon") as string,
                k.GetValue("DisplayVersion") as string);
            Register(prog);
        }
    }

    internal void Register(InstalledProgram prog)
    {
        _sorted = false;
        string? loc = prog.InstallLocation;
        if (!string.IsNullOrWhiteSpace(loc) && IsSpecificDirectory(loc))
            _byDir.Add((AppKey.Normalize(loc).TrimEnd('\\'), prog));

        // Many installers only record DisplayIcon / UninstallString; their directory is the install directory.
        string? icon = CommandParser.ExtractExePath(prog.DisplayIcon, _ => true);
        if (icon != null)
        {
            _byIconPath.TryAdd(AppKey.Normalize(icon), prog);
            string? iconDir = Path.GetDirectoryName(icon);
            if (iconDir != null && IsSpecificDirectory(iconDir)) _byDir.Add((AppKey.Normalize(iconDir).TrimEnd('\\'), prog));
        }
        string? unin = CommandParser.ExtractExePath(prog.UninstallString, _ => true);
        string? uninDir = unin == null ? null : Path.GetDirectoryName(unin);
        if (uninDir != null && IsSpecificDirectory(uninDir) &&
            !unin!.EndsWith("msiexec.exe", StringComparison.OrdinalIgnoreCase))
            _byDir.Add((AppKey.Normalize(uninDir).TrimEnd('\\'), prog));
    }

    private bool _sorted;

    public InstalledProgram? Match(string exePath)
    {
        if (!_sorted)
        {
            _byDir.Sort((a, b) => b.Dir.Length.CompareTo(a.Dir.Length)); // longest (most specific) first
            _sorted = true;
        }
        string p = AppKey.Normalize(exePath);
        if (_byIconPath.TryGetValue(p, out var byIcon)) return byIcon;
        foreach (var (dir, prog) in _byDir)
            if (p.StartsWith(dir + "\\", StringComparison.Ordinal)) return prog;
        return null;
    }

    /// <summary>Ignore generic roots (C:\, C:\Windows, C:\Program Files ...) that would match everything.</summary>
    internal static bool IsSpecificDirectory(string dir)
    {
        string d = AppKey.Normalize(dir).TrimEnd('\\');
        string[] segments = d.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3 && !d.StartsWith(@"\\")) return false;
        string[] generic =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        };
        foreach (string g in generic)
            if (!string.IsNullOrEmpty(g) && d == AppKey.Normalize(g).TrimEnd('\\')) return false;
        return true;
    }

    private static string? FormatDate(string? yyyymmdd) =>
        yyyymmdd is { Length: 8 } && DateTime.TryParseExact(yyyymmdd, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var dt)
            ? dt.ToString("yyyy-MM-dd")
            : yyyymmdd;
}

internal sealed record AppxInfo(string PackageFullName, string Name, string DisplayName, string? Publisher, string? Version, string? LogoPath);

/// <summary>Understands Store/MSIX apps by reading the AppxManifest.xml of the package folder.</summary>
internal static class AppxResolver
{
    public static AppxInfo? TryResolve(string exePath)
    {
        string? packageDir = FindPackageDir(exePath);
        if (packageDir == null) return null;
        string manifestPath = Path.Combine(packageDir, "AppxManifest.xml");
        if (!File.Exists(manifestPath)) return null;

        try
        {
            var doc = XDocument.Load(manifestPath);
            var root = doc.Root;
            if (root == null) return null;
            static IEnumerable<XElement> Els(XElement e, string n) => e.Descendants().Where(x => x.Name.LocalName == n);

            var identity = Els(root, "Identity").FirstOrDefault();
            string name = identity?.Attribute("Name")?.Value ?? Path.GetFileName(packageDir);
            string? version = identity?.Attribute("Version")?.Value;
            var props = Els(root, "Properties").FirstOrDefault();
            string? display = props?.Elements().FirstOrDefault(e => e.Name.LocalName == "DisplayName")?.Value;
            string? publisher = props?.Elements().FirstOrDefault(e => e.Name.LocalName == "PublisherDisplayName")?.Value;
            string? logo = Els(root, "VisualElements").Select(v => v.Attribute("Square44x44Logo")?.Value).FirstOrDefault(v => v != null)
                           ?? props?.Elements().FirstOrDefault(e => e.Name.LocalName == "Logo")?.Value;

            if (string.IsNullOrWhiteSpace(display) || display.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                display = PrettifyPackageName(name);
            if (publisher != null && publisher.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) publisher = null;

            return new AppxInfo(Path.GetFileName(packageDir), name, display, publisher, version,
                logo == null ? null : FindLogoFile(packageDir, logo));
        }
        catch { return null; }
    }

    internal static string? FindPackageDir(string exePath)
    {
        const string marker = @"\WindowsApps\";
        int i = exePath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        int start = i + marker.Length;
        int end = exePath.IndexOf('\\', start);
        return end < 0 ? null : exePath.Substring(0, end);
    }

    /// <summary>"Microsoft.WindowsCalculator" -&gt; "Windows Calculator".</summary>
    internal static string PrettifyPackageName(string identityName)
    {
        string n = identityName;
        int dot = n.IndexOf('.');
        if (dot > 0 && dot < n.Length - 1) n = n.Substring(dot + 1);
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < n.Length; i++)
        {
            if (i > 0 && char.IsUpper(n[i]) && !char.IsUpper(n[i - 1])) sb.Append(' ');
            sb.Append(n[i] == '.' ? ' ' : n[i]);
        }
        return sb.ToString();
    }

    /// <summary>Logo assets are usually shipped as Name.scale-200.png / Name.targetsize-48.png variants.</summary>
    internal static string? FindLogoFile(string packageDir, string relativeLogo)
    {
        string rel = relativeLogo.Replace('/', '\\');
        string exact = Path.Combine(packageDir, rel);
        string dir = Path.GetDirectoryName(exact) ?? packageDir;
        if (!Directory.Exists(dir)) return null;

        string baseName = Path.GetFileNameWithoutExtension(exact);
        string ext = Path.GetExtension(exact);
        var candidates = Directory.EnumerateFiles(dir, baseName + "*" + ext).ToList();
        if (File.Exists(exact) && !candidates.Contains(exact, StringComparer.OrdinalIgnoreCase)) candidates.Add(exact);
        if (candidates.Count == 0) return null;

        string[] preferred = { "scale-200", "targetsize-48", "scale-100", "targetsize-64", "targetsize-32", "scale-400" };
        foreach (string pref in preferred)
        {
            string? hit = candidates.FirstOrDefault(c =>
                c.Contains(pref, StringComparison.OrdinalIgnoreCase) && !c.Contains("altform", StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }
        return candidates.OrderByDescending(c => new FileInfo(c).Length).First();
    }
}
