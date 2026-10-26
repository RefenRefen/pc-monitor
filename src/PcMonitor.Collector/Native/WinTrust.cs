using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using PcMonitor.Core;

namespace PcMonitor.Collector.Native;

/// <summary>
/// Authenticode verification through WinVerifyTrust. Embedded signatures are tried first; Windows system files
/// are catalog-signed, so a catalog lookup follows when the file has no embedded signature.
/// Revocation checks are off and only cached URL data is used so verification never touches the network.
/// </summary>
internal static unsafe class WinTrust
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_CHOICE_CATALOG = 2;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_REVOCATION_CHECK_NONE = 0x10;
    private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000;

    private const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100);
    private const int TRUST_E_SUBJECT_FORM_UNKNOWN = unchecked((int)0x800B0003);
    private const int TRUST_E_PROVIDER_UNKNOWN = unchecked((int)0x800B0001);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_CATALOG_INFO
    {
        public uint cbStruct;
        public uint dwCatalogVersion;
        public IntPtr pcwszCatalogFilePath;
        public IntPtr pcwszMemberTag;
        public IntPtr pcwszMemberFilePath;
        public IntPtr hMemberFile;
        public IntPtr pbCalculatedFileHash;
        public uint cbCalculatedFileHash;
        public IntPtr pcCatalogContext;
        public IntPtr hCatAdmin;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pUnion;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CATALOG_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string wszCatalogFile;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);

    [DllImport("wintrust.dll")] private static extern IntPtr WTHelperProvDataFromStateData(IntPtr stateData);
    [DllImport("wintrust.dll")] private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provData, uint signerIdx, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIdx);
    [DllImport("wintrust.dll")] private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint certIdx);

    [DllImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminAcquireContext2(out IntPtr hCatAdmin, IntPtr pgSubsystem,
        [MarshalAs(UnmanagedType.LPWStr)] string hashAlgorithm, IntPtr pStrongHashPolicy, uint flags);

    [DllImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr hCatAdmin, IntPtr hFile, ref uint cbHash, byte* pbHash, uint flags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr hCatAdmin, byte* pbHash, uint cbHash, uint flags, ref IntPtr hPrevCatInfo);

    [DllImport("wintrust.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATCatalogInfoFromContext(IntPtr hCatInfo, ref CATALOG_INFO info, uint flags);

    [DllImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminReleaseCatalogContext(IntPtr hCatAdmin, IntPtr hCatInfo, uint flags);

    [DllImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminReleaseContext(IntPtr hCatAdmin, uint flags);

    public readonly record struct Result(SignatureState State, string? Signer, int RawStatus);

    public static Result Verify(string path)
    {
        try
        {
            if (!File.Exists(path)) return new Result(SignatureState.Unknown, null, -1);

            var embedded = VerifyEmbedded(path);
            if (embedded.RawStatus == 0) return embedded;
            if (embedded.RawStatus is TRUST_E_NOSIGNATURE or TRUST_E_SUBJECT_FORM_UNKNOWN or TRUST_E_PROVIDER_UNKNOWN)
            {
                var cat = VerifyViaCatalog(path);
                if (cat.HasValue) return cat.Value;
                return new Result(SignatureState.Unsigned, null, embedded.RawStatus);
            }
            // Signature exists but is untrusted / tampered.
            return embedded;
        }
        catch (Exception)
        {
            return new Result(SignatureState.Unknown, null, -2);
        }
    }

    private static Result VerifyEmbedded(string path)
    {
        IntPtr pPath = Marshal.StringToHGlobalUni(path);
        var fileInfo = new WINTRUST_FILE_INFO { cbStruct = (uint)sizeof(WINTRUST_FILE_INFO), pcwszFilePath = pPath };
        var data = NewData(WTD_CHOICE_FILE, &fileInfo);
        try
        {
            var action = GenericVerifyV2;
            int rc = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            string? signer = rc == 0 || rc != TRUST_E_NOSIGNATURE ? ReadSigner(data.hWVTStateData) : null;
            Close(ref data, ref action);
            return new Result(Classify(rc), signer, rc);
        }
        finally { Marshal.FreeHGlobal(pPath); }
    }

    private static Result? VerifyViaCatalog(string path)
    {
        if (!CryptCATAdminAcquireContext2(out IntPtr hCatAdmin, IntPtr.Zero, "SHA256", IntPtr.Zero, 0)) return null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            IntPtr hFile = fs.SafeFileHandle.DangerousGetHandle();

            uint cb = 0;
            CryptCATAdminCalcHashFromFileHandle2(hCatAdmin, hFile, ref cb, null, 0);
            if (cb == 0) return null;
            byte* hash = stackalloc byte[(int)cb];
            if (!CryptCATAdminCalcHashFromFileHandle2(hCatAdmin, hFile, ref cb, hash, 0)) return null;

            IntPtr prev = IntPtr.Zero;
            IntPtr hCat = CryptCATAdminEnumCatalogFromHash(hCatAdmin, hash, cb, 0, ref prev);
            if (hCat == IntPtr.Zero) return null; // not listed in any catalog

            try
            {
                var info = new CATALOG_INFO { cbStruct = (uint)Marshal.SizeOf<CATALOG_INFO>(), wszCatalogFile = "" };
                if (!CryptCATCatalogInfoFromContext(hCat, ref info, 0)) return null;

                string tag = Convert.ToHexString(new ReadOnlySpan<byte>(hash, (int)cb));
                IntPtr pCat = Marshal.StringToHGlobalUni(info.wszCatalogFile);
                IntPtr pTag = Marshal.StringToHGlobalUni(tag);
                IntPtr pMember = Marshal.StringToHGlobalUni(path);
                try
                {
                    var cat = new WINTRUST_CATALOG_INFO
                    {
                        cbStruct = (uint)sizeof(WINTRUST_CATALOG_INFO),
                        pcwszCatalogFilePath = pCat,
                        pcwszMemberTag = pTag,
                        pcwszMemberFilePath = pMember,
                        hMemberFile = hFile,
                        pbCalculatedFileHash = (IntPtr)hash,
                        cbCalculatedFileHash = cb,
                        hCatAdmin = hCatAdmin,
                    };
                    var data = NewData(WTD_CHOICE_CATALOG, &cat);
                    var action = GenericVerifyV2;
                    int rc = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
                    string? signer = ReadSigner(data.hWVTStateData);
                    Close(ref data, ref action);
                    return new Result(Classify(rc), signer, rc);
                }
                finally
                {
                    Marshal.FreeHGlobal(pCat); Marshal.FreeHGlobal(pTag); Marshal.FreeHGlobal(pMember);
                }
            }
            finally { CryptCATAdminReleaseCatalogContext(hCatAdmin, hCat, 0); }
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        finally { CryptCATAdminReleaseContext(hCatAdmin, 0); }
    }

    private static WINTRUST_DATA NewData(uint choice, void* union) => new()
    {
        cbStruct = (uint)sizeof(WINTRUST_DATA),
        dwUIChoice = WTD_UI_NONE,
        fdwRevocationChecks = WTD_REVOKE_NONE,
        dwUnionChoice = choice,
        pUnion = (IntPtr)union,
        dwStateAction = WTD_STATEACTION_VERIFY,
        dwProvFlags = WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL,
    };

    private static void Close(ref WINTRUST_DATA data, ref Guid action)
    {
        data.dwStateAction = WTD_STATEACTION_CLOSE;
        WinVerifyTrust(IntPtr.Zero, ref action, ref data);
    }

    private static SignatureState Classify(int rc) => rc switch
    {
        0 => SignatureState.Signed,
        TRUST_E_NOSIGNATURE or TRUST_E_SUBJECT_FORM_UNKNOWN or TRUST_E_PROVIDER_UNKNOWN => SignatureState.Unsigned,
        _ => SignatureState.Invalid,
    };

    /// <summary>
    /// The organisation (O=) is the human-meaningful publisher ("Microsoft Corporation"); the CN can be a product
    /// or code-signing profile name (".NET", "Microsoft Windows"). Falls back to the CN for individuals.
    /// </summary>
    internal static string? PublisherName(X509Certificate2 cert)
    {
        try
        {
            foreach (var rdn in cert.SubjectName.EnumerateRelativeDistinguishedNames())
                if (rdn.GetSingleElementType().Value == "2.5.4.10")
                {
                    string? o = rdn.GetSingleElementValue();
                    if (!string.IsNullOrWhiteSpace(o)) return o;
                }
        }
        catch { /* fall through to CN */ }
        return cert.GetNameInfo(X509NameType.SimpleName, false);
    }

    private static string? ReadSigner(IntPtr stateData)
    {
        if (stateData == IntPtr.Zero) return null;
        try
        {
            IntPtr prov = WTHelperProvDataFromStateData(stateData);
            if (prov == IntPtr.Zero) return null;
            IntPtr sgnr = WTHelperGetProvSignerFromChain(prov, 0, false, 0);
            if (sgnr == IntPtr.Zero) return null;
            IntPtr provCert = WTHelperGetProvCertFromChain(sgnr, 0);
            if (provCert == IntPtr.Zero) return null;
            // CRYPT_PROVIDER_CERT { DWORD cbStruct; PCCERT_CONTEXT pCert; ... }
            IntPtr certContext = *(IntPtr*)((byte*)provCert + 8);
            if (certContext == IntPtr.Zero) return null;
            using var cert = new X509Certificate2(certContext);
            return PublisherName(cert);
        }
        catch { return null; }
    }
}
