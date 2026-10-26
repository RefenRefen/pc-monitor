using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using PcMonitor.Collector.Aggregation;
using PcMonitor.Collector.Native;
using PcMonitor.Collector.Storage;
using PcMonitor.Core;

namespace PcMonitor.Collector.Identity;

/// <summary>
/// Resolves "what is this app and why is it running" lazily, once per executable, on a low-priority thread:
/// version info, publisher, install source, autostart mechanism, then SHA-256 and digital signature.
/// Results are cached in memory and in the <c>apps</c> table (re-resolved when the file changes).
/// </summary>
internal sealed class IdentityResolver : IAppMetadata, IDisposable
{
    public const long MaxHashBytes = 512L * 1024 * 1024;
    private static readonly TimeSpan IndexMaxAge = TimeSpan.FromMinutes(10);

    private readonly UsageDatabase _db;
    private readonly ILogger _log;
    private readonly Func<long> _clock;
    private readonly ConcurrentDictionary<long, AppIdentity> _cache = new();
    private readonly ConcurrentDictionary<long, byte> _requested = new();
    private readonly BlockingCollection<(long AppId, string Path)> _queue = new(new ConcurrentQueue<(long, string)>(), 4096);
    private readonly Thread _worker;
    private readonly CancellationTokenSource _stop = new();

    private StartupIndex? _startup;
    private InstalledPrograms? _installed;
    private DateTime _indexBuiltAt = DateTime.MinValue;

    public IdentityResolver(UsageDatabase db, ILogger log, Func<long>? clock = null, bool autoStart = true)
    {
        _db = db;
        _log = log;
        _clock = clock ?? TimeUtil.NowUnix;
        _worker = new Thread(Run) { IsBackground = true, Name = "identity", Priority = ThreadPriority.BelowNormal };
        if (autoStart) _worker.Start();
    }

    // ------------------------------------------------------------------ IAppMetadata

    public void Observe(long appId, string? exePath, string processName)
    {
        if (!_requested.TryAdd(appId, 0)) return;

        if (string.IsNullOrEmpty(exePath))
        {
            // Kernel pseudo-processes (System, Registry, Memory Compression ...) have no file.
            _cache[appId] = new AppIdentity
            {
                AppId = appId, ExePath = "", Name = PrettyProcessName(processName), FileExists = false,
                Description = "Windows kernel / system process", Complete = true,
                FirstSeenUnix = _clock(), LastSeenUnix = _clock(),
            };
            return;
        }

        // Show something useful immediately; the worker fills in the rest.
        _cache[appId] = new AppIdentity
        {
            AppId = appId, ExePath = exePath, Name = Path.GetFileNameWithoutExtension(exePath),
            FirstSeenUnix = _clock(), LastSeenUnix = _clock(),
            StaticFlags = SuspicionState.StaticFlagsFor(exePath, SignatureState.Unknown),
        };
        if (!_queue.TryAdd((appId, exePath))) _requested.TryRemove(appId, out _); // queue full: retry on a later sighting
    }

    public string? GetDisplayName(long appId) => _cache.TryGetValue(appId, out var id) ? id.Name : null;

    public AppFlags GetStaticFlags(long appId) => _cache.TryGetValue(appId, out var id) ? id.StaticFlags : AppFlags.None;

    // ------------------------------------------------------------------ queries

    public AppIdentity? GetIdentity(long appId)
    {
        if (_cache.TryGetValue(appId, out var id)) return id;
        var stored = _db.LoadIdentity(appId);
        if (stored == null) return null;
        _cache.TryAdd(appId, stored.Identity);   // historic apps stay available without re-reading the database
        return stored.Identity;
    }

    public (string Name, string? Path) GetNameAndPath(long appId)
    {
        var id = GetIdentity(appId);
        return id == null ? ($"app {appId:x}", null) : (id.Name, string.IsNullOrEmpty(id.ExePath) ? null : id.ExePath);
    }

    /// <summary>Forces the next lookup to re-scan autostart locations (after the user changed one).</summary>
    public void InvalidateStartupIndex() => _indexBuiltAt = DateTime.MinValue;

    /// <summary>Re-reads the startup entries for an app (cheap; used when opening the details view).</summary>
    public void RefreshStartup(AppIdentity id)
    {
        if (string.IsNullOrEmpty(id.ExePath)) return;
        EnsureIndexes(force: false);
        id.Startup = _startup?.Lookup(id.ExePath).Select(e => e).ToList() ?? new();
    }

    // ------------------------------------------------------------------ worker

    private void Run()
    {
        try
        {
            foreach (var (appId, path) in _queue.GetConsumingEnumerable(_stop.Token))
            {
                try { Resolve(appId, path); }
                catch (Exception ex) { _log.LogDebug(ex, "Identity resolution failed for {Path}", path); }
                Thread.Sleep(15); // be gentle: this thread reads files and hashes them
            }
        }
        catch (OperationCanceledException) { }
    }

    private void EnsureIndexes(bool force)
    {
        if (!force && _startup != null && DateTime.UtcNow - _indexBuiltAt < IndexMaxAge) return;
        lock (this)
        {
            if (!force && _startup != null && DateTime.UtcNow - _indexBuiltAt < IndexMaxAge) return;
            var sw = Stopwatch.StartNew();
            _startup = StartupIndex.Build();
            _installed = InstalledPrograms.Build();
            _indexBuiltAt = DateTime.UtcNow;
            _log.LogDebug("Built startup/install indexes in {Ms} ms ({S} startup entries, {I} installed programs)",
                sw.ElapsedMilliseconds, _startup.EntryCount, _installed.Count);
        }
    }

    internal AppIdentity Resolve(long appId, string path)
    {
        var info = new FileInfo(path);
        bool exists = info.Exists;
        long mtime = exists ? new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds() : 0;
        long size = exists ? info.Length : 0;

        EnsureIndexes(force: false);

        // Unchanged file seen before: reuse the expensive part (hash + signature) from the database.
        var stored = _db.LoadIdentity(appId);
        bool reuse = stored != null && stored.Identity.Complete && stored.FileSize == size && stored.FileMtimeUnix == mtime && exists;

        var id = QuickIdentity(appId, path, info, exists, stored?.Identity);
        if (reuse)
        {
            id.Sha256 = stored!.Identity.Sha256;
            id.Signature = stored.Identity.Signature;
            id.Signer = stored.Identity.Signer;
            id.Complete = true;
            id.StaticFlags = SuspicionState.StaticFlagsFor(path, id.Signature);
            Publish(id, mtime);
            return id;
        }

        Publish(id, mtime); // names and install info are available right away

        if (exists)
        {
            if (size <= MaxHashBytes) id.Sha256 = TryHash(path);
            var trust = WinTrust.Verify(path);
            id.Signature = trust.State;
            id.Signer = trust.Signer;
        }
        id.StaticFlags = SuspicionState.StaticFlagsFor(path, id.Signature);
        id.Complete = true;
        Publish(id, mtime);
        return id;
    }

    private AppIdentity QuickIdentity(long appId, string path, FileInfo info, bool exists, AppIdentity? previous)
    {
        var id = new AppIdentity
        {
            AppId = appId, ExePath = path, FileExists = exists, FileSize = exists ? info.Length : 0,
            FirstSeenUnix = previous?.FirstSeenUnix is > 0 ? previous.FirstSeenUnix : _clock(),
            LastSeenUnix = _clock(),
            Name = Path.GetFileNameWithoutExtension(path),
        };

        if (exists)
        {
            try
            {
                var v = FileVersionInfo.GetVersionInfo(path);
                id.Description = Clean(v.FileDescription);
                id.ProductName = Clean(v.ProductName);
                id.Company = Clean(v.CompanyName);
                id.Version = Clean(v.ProductVersion) ?? Clean(v.FileVersion);
            }
            catch { /* unreadable version resource */ }
        }

        var appx = AppxResolver.TryResolve(path);
        if (appx != null)
        {
            id.PackageFullName = appx.PackageFullName;
            id.PackageLogoPath = appx.LogoPath;
            id.InstallName = appx.DisplayName;
            id.InstallPublisher = appx.Publisher;
            id.Version ??= appx.Version;
            id.InstallLocation = AppxResolver.FindPackageDir(path);
        }
        else
        {
            var prog = _installed?.Match(path);
            if (prog != null)
            {
                id.InstallName = prog.DisplayName;
                id.InstallPublisher = prog.Publisher;
                id.InstallLocation = prog.InstallLocation;
                id.InstallDate = prog.InstallDate;
                id.UninstallString = prog.UninstallString;
                id.Version ??= prog.Version;
            }
        }

        id.Company ??= id.InstallPublisher;
        id.Name = ChooseName(id, path, appx);
        id.Startup = _startup?.Lookup(path).ToList() ?? new();
        id.StaticFlags = SuspicionState.StaticFlagsFor(path, SignatureState.Unknown);
        return id;
    }

    internal static string ChooseName(AppIdentity id, string path, AppxInfo? appx)
    {
        string file = Path.GetFileNameWithoutExtension(path);
        if (appx != null && !string.IsNullOrWhiteSpace(appx.DisplayName)) return appx.DisplayName;
        // Task Manager uses the file description ("Google Chrome", "Host Process for Windows Services").
        if (!string.IsNullOrWhiteSpace(id.Description) && !LooksGeneric(id.Description, file)) return id.Description!;
        if (!string.IsNullOrWhiteSpace(id.ProductName)) return id.ProductName!;
        if (!string.IsNullOrWhiteSpace(id.InstallName)) return id.InstallName!;
        return file;
    }

    private static bool LooksGeneric(string description, string fileName) =>
        description.Length < 2 || description.Equals("application", StringComparison.OrdinalIgnoreCase)
        || description.Equals(fileName, StringComparison.OrdinalIgnoreCase) && description.Length < 3;

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string PrettyProcessName(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    private void Publish(AppIdentity id, long mtime)
    {
        _cache[id.AppId] = id;
        _db.UpsertApp(id, mtime);
    }

    internal static string? TryHash(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1024, FileOptions.SequentialScan);
            return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _queue.CompleteAdding();
        if (_worker.IsAlive) _worker.Join(2000);
        _stop.Dispose();
    }
}
