using GridLookout.Config;

namespace GridLookout.Tests.Config;

/// <summary>Test double that always throws <see cref="FormatException"/> from
/// <see cref="Unprotect"/> — simulates the round-3 panel-3 T5 scenario: a corrupt (non-base64)
/// <c>PasswordProtected</c> value, which is exactly what the real <see cref="DpapiSecretProtector"/>
/// throws for (its <c>Convert.FromBase64String</c> call, before DPAPI itself ever runs). Mirrors
/// <see cref="CryptographicExceptionSecretProtector"/>'s pattern for the sibling wedge scenario.</summary>
public sealed class FormatExceptionSecretProtector : ISecretProtector
{
    public string Protect(string plaintext) => "irrelevant-for-this-test";

    public string Unprotect(string protectedBase64) =>
        throw new FormatException("The input is not a valid Base-64 string.");
}
