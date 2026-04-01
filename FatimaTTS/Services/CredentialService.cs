using System.Runtime.InteropServices;
using System.Text;

namespace FatimaTTS.Services;

/// <summary>
/// Stores and retrieves the Inworld API key using Windows Credential Manager (DPAPI).
/// The key never touches disk in plaintext.
/// </summary>
public class CredentialService
{
    private const string CredentialTarget = "FatimaTTS_InworldApiKey";

    // ── Win32 structures ──────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredWrite([In] ref CREDENTIAL userCredential, [In] uint flags);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree([In] IntPtr buffer);

    private const uint CRED_TYPE_GENERIC = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;

    // ── Public API ────────────────────────────────────────────────────────

    /// <summary>Saves the API key to Windows Credential Manager.</summary>
    public void SaveApiKey(string apiKey)
    {
        var blob = Encoding.UTF8.GetBytes(apiKey);
        var blobPtr = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);

            var cred = new CREDENTIAL
            {
                Type               = CRED_TYPE_GENERIC,
                TargetName         = CredentialTarget,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob     = blobPtr,
                Persist            = CRED_PERSIST_LOCAL_MACHINE,
                UserName           = "FatimaTTS",
                Comment            = "Inworld AI API Key — managed by Fatima TTS"
            };

            if (!CredWrite(ref cred, 0))
                throw new InvalidOperationException(
                    $"Failed to save API key to Credential Manager. Error: {Marshal.GetLastWin32Error()}");
        }
        finally
        {
            Marshal.FreeHGlobal(blobPtr);
        }
    }

    /// <summary>Retrieves the stored API key, or null if none exists.</summary>
    public string? LoadApiKey()
    {
        if (!CredRead(CredentialTarget, CRED_TYPE_GENERIC, 0, out var credPtr))
            return null;

        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
            if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == IntPtr.Zero)
                return null;

            var blob = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, blob, 0, blob.Length);
            return Encoding.UTF8.GetString(blob);
        }
        finally
        {
            CredFree(credPtr);
        }
    }

    /// <summary>Removes the stored API key from Credential Manager.</summary>
    public void DeleteApiKey()
    {
        CredDelete(CredentialTarget, CRED_TYPE_GENERIC, 0);
    }

    /// <summary>Returns true if an API key is currently stored.</summary>
    public bool HasApiKey() => LoadApiKey() is not null;
}
