using GridLookout.Config;

namespace GridLookout.Tests.Config;

/// <summary>Reversible fake protector so the migration branch logic in WallConfigLoader can be
/// tested without touching real Windows DPAPI. "Protect" = reverse the string + a marker prefix;
/// deterministic and trivially invertible.</summary>
public sealed class FakeSecretProtector : ISecretProtector
{
    public int ProtectCallCount { get; private set; }
    public int UnprotectCallCount { get; private set; }

    public string Protect(string plaintext)
    {
        ProtectCallCount++;
        return "FAKE:" + new string(plaintext.Reverse().ToArray());
    }

    public string Unprotect(string protectedBase64)
    {
        UnprotectCallCount++;
        if (!protectedBase64.StartsWith("FAKE:", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Not a value produced by FakeSecretProtector.Protect");
        }
        var reversed = protectedBase64.Substring("FAKE:".Length);
        return new string(reversed.Reverse().ToArray());
    }
}
