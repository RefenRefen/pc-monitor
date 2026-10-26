using Microsoft.Extensions.Logging.Abstractions;
using PcMonitor.Collector.Identity;
using PcMonitor.Collector.Native;
using PcMonitor.Collector.Storage;
using PcMonitor.Core;
using Xunit;

namespace PcMonitor.Collector.Tests;

public class CommandParserTests
{
    private static string? Parse(string cmd, params string[] existing)
    {
        var set = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        return CommandParser.ExtractExePath(cmd, set.Contains);
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\Foo\\foo.exe\" --minimized", "C:\\Program Files\\Foo\\foo.exe")]
    [InlineData("C:\\Program Files\\Foo\\foo.exe --minimized", "C:\\Program Files\\Foo\\foo.exe")]
    [InlineData("C:\\Tools\\tool.exe", "C:\\Tools\\tool.exe")]
    [InlineData("\"C:\\a b\\c.exe\"", "C:\\a b\\c.exe")]
    [InlineData("C:\\x\\y.exe,0", "C:\\x\\y.exe")]
    [InlineData("\"C:\\x\\y.exe\",1", "C:\\x\\y.exe")]
    [InlineData("\\??\\C:\\Windows\\system32\\drivers\\foo.sys", "C:\\Windows\\system32\\drivers\\foo.sys")]
    public void ExtractsAbsolutePaths(string command, string expected)
        => Assert.Equal(expected, Parse(command), ignoreCase: true);

    [Fact]
    public void ExpandsEnvironmentVariables()
    {
        string win = Environment.GetEnvironmentVariable("SystemRoot")!;
        Assert.Equal(Path.Combine(win, "system32", "x.exe"), Parse("%SystemRoot%\\system32\\x.exe /a"), ignoreCase: true);
    }

    [Fact]
    public void ResolvesBareNamesAgainstSystem32AndSystemRootPrefix()
    {
        string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string expected = Path.Combine(sys, "rundll32.exe");
        Assert.Equal(expected, Parse("rundll32.exe shell32.dll,Foo", expected), ignoreCase: true);
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.Equal(Path.Combine(win, "System32", "drivers", "a.sys"),
            Parse(@"\SystemRoot\System32\drivers\a.sys"), ignoreCase: true);
        Assert.Null(Parse("definitely-not-here.exe"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ReturnsNullForEmptyInput(string? input) => Assert.Null(CommandParser.ExtractExePath(input));
}

public class SignatureTests
{
    private static string Sys(string name) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), name);

    [Fact]
    public void CatalogSignedWindowsBinaryIsSignedByMicrosoft()
    {
        // notepad.exe / cmd.exe have no embedded signature on many builds: they are verified through the catalog.
        var r = WinTrust.Verify(Sys("cmd.exe"));
        Assert.Equal(SignatureState.Signed, r.State);
        Assert.Contains("Microsoft", r.Signer ?? "");
    }

    [Fact]
    public void EmbeddedSignatureIsRecognised()
    {
        string dotnet = Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles")!, "dotnet", "dotnet.exe");
        if (!File.Exists(dotnet)) return;
        var r = WinTrust.Verify(dotnet);
        Assert.Equal(SignatureState.Signed, r.State);
        Assert.Contains("Microsoft", r.Signer ?? "");
    }

    [Fact]
    public void UnsignedFileIsReportedUnsigned()
    {
        string dll = typeof(PcMonitor.Core.AppKey).Assembly.Location;
        var r = WinTrust.Verify(dll);
        Assert.Equal(SignatureState.Unsigned, r.State);
        Assert.Null(r.Signer);
    }

    [Fact]
    public void MissingFileIsUnknownNotUnsigned()
        => Assert.Equal(SignatureState.Unknown, WinTrust.Verify(@"C:\definitely\missing\file.exe").State);
}

public class InstallSourceTests
{
    [Theory]
    [InlineData(@"C:\", false)]
    [InlineData(@"C:\Windows", false)]
    [InlineData(@"C:\Program Files", false)]
    [InlineData(@"C:\Program Files\Foo", true)]
    [InlineData(@"D:\Games\Foo", true)]
    [InlineData(@"D:\Games", false)]
    public void SpecificDirectoryFilter(string dir, bool expected)
        => Assert.Equal(expected, InstalledPrograms.IsSpecificDirectory(dir));

    [Fact]
    public void MatchesByInstallLocationLongestPrefixWins()
    {
        var idx = new InstalledPrograms();
        idx.Register(new InstalledProgram("Suite", "Acme", @"D:\Apps\Suite", "2024-01-02", null, null, "1.0"));
        idx.Register(new InstalledProgram("Suite Plugin", "Acme", @"D:\Apps\Suite\Plugins\P1", null, null, null, "2.0"));
        Assert.Equal("Suite", idx.Match(@"D:\Apps\Suite\bin\suite.exe")!.DisplayName);
        Assert.Equal("Suite Plugin", idx.Match(@"D:\Apps\Suite\Plugins\P1\p.exe")!.DisplayName);
        Assert.Null(idx.Match(@"D:\Other\x.exe"));
        // Prefix must end at a directory boundary.
        Assert.Null(idx.Match(@"D:\Apps\SuiteX\x.exe"));
    }

    [Fact]
    public void MatchesByDisplayIconAndUninstallDirectory()
    {
        var idx = new InstalledPrograms();
        idx.Register(new InstalledProgram("IconApp", "P", null, null, null, "\"E:\\Stuff\\IconApp\\app.exe\",0", null));
        idx.Register(new InstalledProgram("UninApp", "P", null, null, "\"F:\\Programs\\UninApp\\unins000.exe\" /SILENT", null, null));
        Assert.Equal("IconApp", idx.Match(@"E:\Stuff\IconApp\app.exe")!.DisplayName);
        Assert.Equal("IconApp", idx.Match(@"E:\Stuff\IconApp\helper\h.exe")!.DisplayName);
        Assert.Equal("UninApp", idx.Match(@"F:\Programs\UninApp\Main.exe")!.DisplayName);
    }

    [Fact]
    public void RealMachineHasInstalledPrograms()
        => Assert.True(InstalledPrograms.Build().Count > 0);

    [Fact]
    public void AppxHelpers()
    {
        Assert.Equal("Windows Calculator", AppxResolver.PrettifyPackageName("Microsoft.WindowsCalculator"));
        Assert.Equal(@"C:\Program Files\WindowsApps\Foo_1.0_x64__abc",
            AppxResolver.FindPackageDir(@"C:\Program Files\WindowsApps\Foo_1.0_x64__abc\app\foo.exe"));
        Assert.Null(AppxResolver.FindPackageDir(@"C:\Program Files\Foo\foo.exe"));
    }

    [Fact]
    public void AppxManifestAndLogoAreResolved()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcmon-appx-" + Guid.NewGuid().ToString("N"));
        string pkg = Path.Combine(root, "WindowsApps", "Contoso.CoolApp_1.2.3.0_x64__8wekyb3d8bbwe");
        Directory.CreateDirectory(Path.Combine(pkg, "Assets"));
        try
        {
            File.WriteAllText(Path.Combine(pkg, "AppxManifest.xml"), @"<?xml version='1.0' encoding='utf-8'?>
<Package xmlns='http://schemas.microsoft.com/appx/manifest/foundation/windows10' xmlns:uap='http://schemas.microsoft.com/appx/manifest/uap/windows10'>
  <Identity Name='Contoso.CoolApp' Publisher='CN=Contoso' Version='1.2.3.0' />
  <Properties><DisplayName>ms-resource:AppName</DisplayName><PublisherDisplayName>Contoso Ltd</PublisherDisplayName><Logo>Assets\StoreLogo.png</Logo></Properties>
  <Applications><Application Id='App'><uap:VisualElements Square44x44Logo='Assets\Square44x44Logo.png' DisplayName='ms-resource:AppName' /></Application></Applications>
</Package>");
            File.WriteAllBytes(Path.Combine(pkg, "Assets", "Square44x44Logo.scale-100.png"), new byte[10]);
            File.WriteAllBytes(Path.Combine(pkg, "Assets", "Square44x44Logo.scale-200.png"), new byte[20]);
            string exe = Path.Combine(pkg, "CoolApp.exe");

            var info = AppxResolver.TryResolve(exe)!;
            Assert.Equal("Cool App", info.DisplayName);            // ms-resource: falls back to the pretty identity name
            Assert.Equal("Contoso Ltd", info.Publisher);
            Assert.Equal("1.2.3.0", info.Version);
            Assert.EndsWith("Square44x44Logo.scale-200.png", info.LogoPath);
        }
        finally { Directory.Delete(root, true); }
    }
}

public class StartupIndexTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData(new byte[] { 0x02, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x06, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x03, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 0x07, 0, 0, 0 }, false)]
    public void StartupApprovedSemantics(byte[]? data, bool enabled) => Assert.Equal(enabled, StartupIndex.IsApproved(data));

    [Fact]
    public void BuildsFromTheRealMachineWithoutThrowing()
    {
        var idx = StartupIndex.Build();
        Assert.True(idx.EntryCount > 0, "expected at least services/tasks on any Windows machine");
        Assert.Empty(idx.Lookup(@"C:\definitely\not\an\app.exe"));
    }

    [Fact]
    public void RunKeyEntriesOfTheCurrentUserAreFound()
    {
        // Write a Run value into HKCU, then index through the loaded hive of the same user.
        const string valueName = "PcMonitorTestEntry";
        string exe = Path.Combine(Path.GetTempPath(), "pcmon-startup-test.exe");
        using var run = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")!;
        run.SetValue(valueName, $"\"{exe}\" --silent");
        try
        {
            // The file does not need to exist for rooted paths.
            var entries = StartupIndex.Build().Lookup(exe);
            var e = Assert.Single(entries, x => x.Name == valueName);
            Assert.Equal(StartupKind.RunKey, e.Kind);
            Assert.True(e.Enabled);
            Assert.True(e.CanDisable);
            Assert.Contains("--silent", e.Command);
        }
        finally { run.DeleteValue(valueName, false); }
    }
}

public class IdentityResolverTests : IDisposable
{
    private readonly string _dbPath = TestData.TempDb();
    private readonly UsageDatabase _db;

    public IdentityResolverTests() => _db = new UsageDatabase(_dbPath);

    public void Dispose() { _db.Dispose(); TestData.Cleanup(_dbPath); }

    private IdentityResolver NewResolver() => new(_db, NullLogger.Instance, autoStart: false);

    [Fact]
    public void ResolvesAMicrosoftSystemBinary()
    {
        string cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        using var r = NewResolver();
        long id = AppKey.FromPath(cmd);
        var identity = r.Resolve(id, cmd);

        Assert.True(identity.Complete);
        Assert.Contains("Command Processor", identity.Name, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Microsoft", identity.Company);
        Assert.Equal(SignatureState.Signed, identity.Signature);
        Assert.Equal(64, identity.Sha256?.Length);
        Assert.True(identity.FileSize > 10_000);
        Assert.Equal(AppFlags.None, identity.StaticFlags);
        Assert.False(string.IsNullOrEmpty(identity.Version));

        // Persisted and reused for an unchanged file.
        var stored = _db.LoadIdentity(id)!;
        Assert.Equal(identity.Sha256, stored.Identity.Sha256);
        var again = r.Resolve(id, cmd);
        Assert.Equal(identity.Sha256, again.Sha256);
        Assert.Equal(SignatureState.Signed, again.Signature);
        Assert.Equal(identity.Name, r.GetDisplayName(id));
    }

    [Fact]
    public void FlagsUnsignedBinariesRunningFromUserWritableLocations()
    {
        string dir = Path.Combine(Path.GetTempPath(), "Downloads-test", "Downloads");
        Directory.CreateDirectory(dir);
        string copy = Path.Combine(dir, "sketchy.dll");
        File.Copy(typeof(AppKey).Assembly.Location, copy, true);
        try
        {
            using var r = NewResolver();
            long id = AppKey.FromPath(copy);
            var identity = r.Resolve(id, copy);
            Assert.Equal(SignatureState.Unsigned, identity.Signature);
            Assert.True(identity.StaticFlags.HasFlag(AppFlags.Unsigned));
            Assert.True(identity.StaticFlags.HasFlag(AppFlags.UserWritablePath));
            Assert.Equal(identity.StaticFlags, r.GetStaticFlags(id));
        }
        finally { Directory.Delete(Path.GetDirectoryName(dir)!, true); }
    }

    [Fact]
    public void ChangedFilesAreReHashed()
    {
        string path = Path.Combine(Path.GetTempPath(), "pcmon-changing-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllText(path, "version one");
        try
        {
            using var r = NewResolver();
            long id = AppKey.FromPath(path);
            string h1 = r.Resolve(id, path).Sha256!;
            File.WriteAllText(path, "version two, longer");
            string h2 = r.Resolve(id, path).Sha256!;
            Assert.NotEqual(h1, h2);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ObserveHandlesKernelPseudoProcessesAndQueuesRealOnes()
    {
        using var r = NewResolver();
        long sys = AppKey.FromPath("?system");
        r.Observe(sys, null, "System");
        var id = r.GetIdentity(sys)!;
        Assert.Equal("System", id.Name);
        Assert.False(id.FileExists);
        Assert.True(id.Complete);

        string cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        long cmdId = AppKey.FromPath(cmd);
        r.Observe(cmdId, cmd, "cmd.exe");
        // Placeholder identity is available instantly (worker not started in this test).
        Assert.Equal("cmd", r.GetDisplayName(cmdId));
        r.Observe(cmdId, cmd, "cmd.exe"); // idempotent
    }

    [Fact]
    public void BackgroundWorkerCompletesIdentitiesAsynchronously()
    {
        string cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe");
        using var r = new IdentityResolver(_db, NullLogger.Instance, autoStart: true);
        long id = AppKey.FromPath(cmd);
        r.Observe(id, cmd, "where.exe");

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && r.GetIdentity(id)?.Complete != true) Thread.Sleep(50);
        var identity = r.GetIdentity(id)!;
        Assert.True(identity.Complete);
        Assert.Equal(SignatureState.Signed, identity.Signature);
    }

    [Fact]
    public void ChooseNamePrefersDescriptionThenProduct()
    {
        var id = new AppIdentity { Description = "Google Chrome", ProductName = "Chrome", InstallName = "Chrome Browser" };
        Assert.Equal("Google Chrome", IdentityResolver.ChooseName(id, @"C:\x\chrome.exe", null));
        id.Description = null;
        Assert.Equal("Chrome", IdentityResolver.ChooseName(id, @"C:\x\chrome.exe", null));
        id.ProductName = null;
        Assert.Equal("Chrome Browser", IdentityResolver.ChooseName(id, @"C:\x\chrome.exe", null));
        id.InstallName = null;
        Assert.Equal("chrome", IdentityResolver.ChooseName(id, @"C:\x\chrome.exe", null));
    }
}
