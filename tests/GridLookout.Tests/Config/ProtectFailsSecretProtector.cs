using System.Security.Cryptography;
using GridLookout.Config;

namespace GridLookout.Tests.Config;

/// <summary>Test double whose <see cref="Protect"/> throws <see cref="CryptographicException"/> —
/// simulates DPAPI being unavailable for the whole Windows logon session (observed live
/// 2026-08-19: <c>ProtectedData.Protect</c> returning "Access is denied" machine-wide until the
/// next clean logon). Distinct from <see cref="CryptographicExceptionSecretProtector"/>, whose
/// failure is on the <see cref="Unprotect"/> side (wrong-account wedge).</summary>
public sealed class ProtectFailsSecretProtector : ISecretProtector
{
    public string Protect(string plaintext) =>
        throw new CryptographicException("Access is denied.");

    public string Unprotect(string protectedBase64) => "unprotected-value";
}
