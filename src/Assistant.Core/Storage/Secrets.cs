using System.Security.Cryptography;
using System.Text;
using Assistant.Core.Diagnostics;

namespace Assistant.Core.Storage;

/// <summary>
/// Cloud keys are stored encrypted with Windows' own protection (DPAPI, tied to this Windows user),
/// so copying config.json to another computer or account doesn't reveal them.
/// A key pasted into config.json by hand is accepted as plain text and encrypted on the next save.
/// </summary>
public static class Secrets
{
    const string Prefix = "dpapi:";
    static readonly byte[] Entropy = "Assistant.CloudKey"u8.ToArray();

    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(data);
    }

    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored.Trim(); // pasted by hand

        try
        {
            byte[] data = ProtectedData.Unprotect(Convert.FromBase64String(stored[Prefix.Length..]), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // From another Windows user or computer: it can't be read here, so it counts as not set.
            DiagnosticLog.Error("A saved cloud key couldn't be decrypted; paste it again in Settings", ex);
            return "";
        }
    }
}
