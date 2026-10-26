using System.Security.AccessControl;
using System.Security.Principal;

namespace PcMonitor.Collector;

/// <summary>
/// The usage database is a detailed diary of everything that ran on the machine, so it is readable only by
/// SYSTEM, Administrators and the account running the collector. The widget never opens it; it asks over the pipe.
/// </summary>
internal static class DataDirectorySecurity
{
    public static void Apply(string directory)
    {
        Directory.CreateDirectory(directory);
        var info = new DirectoryInfo(directory);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var flags = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        void Allow(SecurityIdentifier sid) =>
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, flags, PropagationFlags.None, AccessControlType.Allow));

        Allow(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        Allow(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        using (var self = WindowsIdentity.GetCurrent())
            if (self.User != null) Allow(self.User);

        info.SetAccessControl(security);
    }
}
