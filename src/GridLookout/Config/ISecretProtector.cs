namespace GridLookout.Config;

/// <summary>
/// Abstraction over DPAPI so <see cref="WallConfigLoader"/>'s password-migration branch logic is
/// unit-testable without touching the real Windows DPAPI store.
/// </summary>
public interface ISecretProtector
{
    /// <summary>Encrypts <paramref name="plaintext"/>, returns a base64 blob.</summary>
    string Protect(string plaintext);

    /// <summary>Decrypts a base64 blob previously returned by <see cref="Protect"/>.</summary>
    string Unprotect(string protectedBase64);
}
