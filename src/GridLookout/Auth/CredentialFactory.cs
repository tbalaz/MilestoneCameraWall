using System.Net;
using GridLookout.Config;

namespace GridLookout.Auth;

/// <summary>
/// Builds the <see cref="CredentialCache"/> the MIP SDK login path needs. SDK-free (only
/// System.Net types) — grouped under Auth/ next to the pieces that do depend on the SDK.
/// </summary>
public static class CredentialFactory
{
    public static CredentialCache Build(WallConfig config, string password, Uri managementServerUri)
    {
        if (string.IsNullOrEmpty(config.Username))
        {
            throw new InvalidOperationException(
                $"AuthMode={config.AuthMode} requires a credential, but Username is empty. " +
                "Fill in Username and Password in camerawall.json — the app authenticates to the " +
                "Management Server with exactly one config-file credential; there is no mode that " +
                "runs without one.");
        }

        if (config.AuthMode == AuthMode.Basic
            && string.Equals(managementServerUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !config.AllowInsecureBasic)
        {
            throw new InvalidOperationException(
                "AuthMode=Basic over http:// is refused because AllowInsecureBasic=false. " +
                "Either switch ManagementServerUri to https://, or set AllowInsecureBasic=true " +
                "for a lab/dev environment only — never for a production install.");
        }

        var cache = new CredentialCache();

        switch (config.AuthMode)
        {
            case AuthMode.Windows:
                cache.Add(managementServerUri, "Negotiate", new NetworkCredential(config.Username, password, config.Domain));
                break;

            case AuthMode.Basic:
                cache.Add(managementServerUri, "Basic", new NetworkCredential(config.Username, password));
                break;

            default:
                throw new InvalidOperationException($"Unsupported AuthMode: {config.AuthMode}");
        }

        return cache;
    }
}
