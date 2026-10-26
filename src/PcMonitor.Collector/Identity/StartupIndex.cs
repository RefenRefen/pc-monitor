using System.Xml.Linq;
using Microsoft.Win32;
using PcMonitor.Core;

namespace PcMonitor.Collector.Identity;

/// <summary>A local user profile whose registry hive / startup folder we can inspect.</summary>
internal sealed record UserProfile(string Sid, string Name, string? ProfilePath);

internal static class UserProfiles
{
    public static List<UserProfile> Enumerate()
    {
        var list = new List<UserProfile>();
        try
        {
            using var profileList = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            if (profileList == null) return list;
            foreach (string sid in profileList.GetSubKeyNames())
            {
                if (!sid.StartsWith("S-1-5-21-", StringComparison.Ordinal)) continue; // real local/domain users only
                using var k = profileList.OpenSubKey(sid);
                string? path = (k?.GetValue("ProfileImagePath") as string);
                if (path != null) path = Environment.ExpandEnvironmentVariables(path);
                string name = path != null ? Path.GetFileName(path.TrimEnd('\\')) : sid;
                list.Add(new UserProfile(sid, name, path));
            }
        }
        catch { /* best effort */ }
        return list;
    }
}

/// <summary>
/// Answers "why does this executable start by itself?" by indexing every autostart location:
/// Run/RunOnce keys (machine and every loaded user hive), Startup folders, boot/logon scheduled tasks,
/// and auto-start services and drivers. Keyed by normalised executable path.
/// </summary>
internal sealed class StartupIndex
{
    private static readonly string[] RunSubKeys = { "Run", "RunOnce" };
    private const string CurrentVersion = @"SOFTWARE\Microsoft\Windows\CurrentVersion";
    private const string ApprovedRoot = CurrentVersion + @"\Explorer\StartupApproved";

    private readonly Dictionary<string, List<StartupEntry>> _byPath = new(StringComparer.OrdinalIgnoreCase);
    public int EntryCount { get; private set; }

    public IReadOnlyList<StartupEntry> Lookup(string exePath) =>
        _byPath.TryGetValue(AppKey.Normalize(exePath), out var l) ? l : Array.Empty<StartupEntry>();

    public static StartupIndex Build()
    {
        var idx = new StartupIndex();
        var profiles = UserProfiles.Enumerate();
        Try(() => idx.AddRunKeys(profiles));
        Try(() => idx.AddStartupFolders(profiles));
        Try(idx.AddScheduledTasks);
        Try(idx.AddServices);
        return idx;
    }

    private static void Try(Action a)
    {
        try { a(); } catch { /* an unreadable location must not break the others */ }
    }

    private void Add(string? exePath, StartupEntry entry)
    {
        if (string.IsNullOrEmpty(exePath)) return;
        string key = AppKey.Normalize(exePath);
        if (!_byPath.TryGetValue(key, out var list)) _byPath[key] = list = new List<StartupEntry>();
        list.Add(entry);
        EntryCount++;
    }

    // ------------------------------------------------------------ Run / RunOnce

    private void AddRunKeys(List<UserProfile> profiles)
    {
        // Machine-wide (64-bit view, then the 32-bit view)
        foreach (string sub in RunSubKeys)
        {
            ReadRunKey(Registry.LocalMachine, $@"{CurrentVersion}\{sub}", sub == "Run" ? StartupKind.RunKey : StartupKind.RunOnceKey,
                "Machine", $@"HKLM\{CurrentVersion}\{sub}", ApprovedRoot + @"\Run", Registry.LocalMachine);
            ReadRunKey(Registry.LocalMachine, $@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\{sub}",
                sub == "Run" ? StartupKind.RunKey : StartupKind.RunOnceKey,
                "Machine (32-bit)", $@"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\{sub}", ApprovedRoot + @"\Run32", Registry.LocalMachine);
        }

        // Every user whose hive is loaded (the logged-on users). A service runs as SYSTEM, so HKCU would be wrong.
        foreach (var p in profiles)
        {
            foreach (string sub in RunSubKeys)
            {
                string hive = $@"{p.Sid}\{CurrentVersion}\{sub}";
                ReadRunKey(Registry.Users, hive, sub == "Run" ? StartupKind.RunKey : StartupKind.RunOnceKey,
                    p.Name, $@"HKU\{hive}", $@"{p.Sid}\{ApprovedRoot}\Run", Registry.Users);
            }
        }
    }

    private void ReadRunKey(RegistryKey root, string subKey, StartupKind kind, string scope, string display,
        string approvedPath, RegistryKey approvedRoot)
    {
        using var key = root.OpenSubKey(subKey);
        if (key == null) return;
        using var approved = approvedRoot.OpenSubKey(approvedPath);
        foreach (string name in key.GetValueNames())
        {
            if (key.GetValue(name) is not string cmd || string.IsNullOrWhiteSpace(cmd)) continue;
            bool enabled = IsApproved(approved?.GetValue(name) as byte[]);
            Add(CommandParser.ExtractExePath(cmd), new StartupEntry
            {
                Kind = kind, Scope = scope, Location = display, Name = name, Command = cmd,
                Enabled = enabled, CanDisable = kind == StartupKind.RunKey,
                ApprovedKey = kind == StartupKind.RunKey ? ApprovedKeyPath(approvedRoot, approvedPath) : null,
            });
        }
    }

    internal static string ApprovedKeyPath(RegistryKey root, string path) =>
        (root == Registry.Users ? @"HKU\" : @"HKLM\") + path;

    /// <summary>StartupApproved values: first byte even (02/06) = enabled, odd (03/07) = disabled. Absent = enabled.</summary>
    public static bool IsApproved(byte[]? data) => data == null || data.Length == 0 || (data[0] & 1) == 0;

    // ------------------------------------------------------------ Startup folders

    private void AddStartupFolders(List<UserProfile> profiles)
    {
        string common = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            @"Microsoft\Windows\Start Menu\Programs\StartUp");
        ReadStartupFolder(common, "Machine", $@"HKLM\{ApprovedRoot}\StartupFolder", Registry.LocalMachine, $@"{ApprovedRoot}\StartupFolder");

        foreach (var p in profiles)
        {
            if (p.ProfilePath == null) continue;
            string dir = Path.Combine(p.ProfilePath, @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup");
            ReadStartupFolder(dir, p.Name, $@"HKU\{p.Sid}\{ApprovedRoot}\StartupFolder", Registry.Users, $@"{p.Sid}\{ApprovedRoot}\StartupFolder");
        }
    }

    private void ReadStartupFolder(string dir, string scope, string approvedDisplay, RegistryKey approvedRoot, string approvedPath)
    {
        if (!Directory.Exists(dir)) return;
        using var approved = approvedRoot.OpenSubKey(approvedPath);
        foreach (string file in Directory.EnumerateFiles(dir))
        {
            string fileName = Path.GetFileName(file);
            if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;

            string? command = file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ShellLink.ReadTarget(file) : $"\"{file}\"";
            Add(CommandParser.ExtractExePath(command), new StartupEntry
            {
                Kind = StartupKind.StartupFolder, Scope = scope, Location = dir, Name = fileName, Command = command ?? file,
                Enabled = IsApproved(approved?.GetValue(fileName) as byte[]), CanDisable = true,
                ApprovedKey = ApprovedKeyPath(approvedRoot, approvedPath),
            });
        }
    }

    // ------------------------------------------------------------ Scheduled tasks

    private void AddScheduledTasks()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Tasks");
        if (!Directory.Exists(root)) return;
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try { ReadTask(root, file); }
            catch { /* unreadable or malformed task */ }
        }
    }

    private void ReadTask(string root, string file)
    {
        var doc = XDocument.Load(file);
        var task = doc.Root;
        if (task == null) return;

        static IEnumerable<XElement> Elems(XElement e, string name) => e.Descendants().Where(x => x.Name.LocalName == name);

        bool taskEnabled = !Elems(task, "Settings").Elements().Any(e => e.Name.LocalName == "Enabled" && e.Value.Equals("false", StringComparison.OrdinalIgnoreCase));
        var triggers = Elems(task, "Triggers").Elements()
            .Where(t => t.Name.LocalName is "BootTrigger" or "LogonTrigger")
            .Where(t => !t.Elements().Any(e => e.Name.LocalName == "Enabled" && e.Value.Equals("false", StringComparison.OrdinalIgnoreCase)))
            .Select(t => t.Name.LocalName.Replace("Trigger", ""))
            .Distinct().ToList();
        if (triggers.Count == 0) return;

        string taskPath = "\\" + Path.GetRelativePath(root, file);
        foreach (var exec in Elems(task, "Actions").Elements().Where(a => a.Name.LocalName == "Exec"))
        {
            string? cmd = exec.Elements().FirstOrDefault(e => e.Name.LocalName == "Command")?.Value;
            string? args = exec.Elements().FirstOrDefault(e => e.Name.LocalName == "Arguments")?.Value;
            if (string.IsNullOrWhiteSpace(cmd)) continue;
            string full = string.IsNullOrWhiteSpace(args) ? cmd.Trim() : $"{cmd.Trim()} {args.Trim()}";
            Add(CommandParser.ExtractExePath(cmd.Trim()), new StartupEntry
            {
                Kind = StartupKind.ScheduledTask, Scope = "Machine (" + string.Join("/", triggers).ToLowerInvariant() + ")",
                Location = taskPath, Name = Path.GetFileName(file), Command = full, Enabled = taskEnabled, CanDisable = false,
            });
        }
    }

    // ------------------------------------------------------------ Services & drivers

    private void AddServices()
    {
        using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        if (services == null) return;
        foreach (string name in services.GetSubKeyNames())
        {
            using var k = services.OpenSubKey(name);
            if (k == null) continue;
            // Start: 0 boot, 1 system, 2 automatic (3 manual and 4 disabled do not start by themselves).
            if (k.GetValue("Start") is not int start || start > 2) continue;
            if (k.GetValue("Type") is not int type) continue;
            if (k.GetValue("ImagePath") is not string image || string.IsNullOrWhiteSpace(image)) continue;

            string? exe = CommandParser.ExtractExePath(image);
            if (exe == null) continue;
            // svchost hosts hundreds of services; listing them all would drown the real signal.
            if (Path.GetFileName(exe).Equals("svchost.exe", StringComparison.OrdinalIgnoreCase)) continue;

            bool driver = (type & (0x1 | 0x2 | 0x8)) != 0;
            string display = k.GetValue("DisplayName") as string ?? name;
            Add(exe, new StartupEntry
            {
                Kind = driver ? StartupKind.Driver : StartupKind.Service, Scope = "Machine",
                Location = @"HKLM\SYSTEM\CurrentControlSet\Services\" + name,
                Name = display.StartsWith("@", StringComparison.Ordinal) ? name : display,
                Command = image, Enabled = true, CanDisable = false,
            });
        }
    }
}
