using System.Security.Cryptography;
using System.Text;

namespace GridLookout.Config;

/// <summary>
/// DPAPI CurrentUser-scope protector — only the Windows account that encrypted the blob can
/// decrypt it. LocalMachine scope was deliberately not used: it would let any account on the box
/// decrypt the credential.
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    public string Protect(string plaintext)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var protectedBytes = ProtectedData.Protect(plainBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    public string Unprotect(string protectedBase64)
    {
        var protectedBytes = Convert.FromBase64String(protectedBase64);
        var plainBytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plainBytes);
    }
}
