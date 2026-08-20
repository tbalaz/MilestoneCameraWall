using System.Security.Cryptography;
using GridLookout.Config;

namespace GridLookout.Tests.Config;

/// <summary>Test double that always throws <see cref="CryptographicException"/> from
/// <see cref="Unprotect"/> — simulates the T4/R4 DPAPI "wedge" scenario: a
/// <c>PasswordProtected</c> blob created under a different Windows account than the one currently
/// running GridLookout, which is exactly what real DPAPI throws for.</summary>
public sealed class CryptographicExceptionSecretProtector : ISecretProtector
{
    public string Protect(string plaintext) => "irrelevant-for-this-test";

    public string Unprotect(string protectedBase64) =>
        throw new CryptographicException("Key not valid for use in specified state.");
}
