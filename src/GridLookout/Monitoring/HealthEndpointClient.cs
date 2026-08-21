using GridLookout.Config;

namespace GridLookout.Monitoring;

/// <summary>
/// Optional outbound HTTPS reporter for <c>--health-probe</c> mode — POSTs a JSON envelope (every
/// health.json property plus the probe's own evaluated verdict — see <c>HealthProbe.BuildPostEnvelope</c>,
/// buyer-review defect #2 fix) to a customer-configured endpoint (<see cref="HealthConfig.Endpoint"/>).
/// Never invoked by
/// the running wall process itself: the design deliberately keeps the long-lived kiosk GUI process
/// free of any network call this feature adds, so a slow/unreachable customer collector can never
/// stall the UI thread the health feature exists to WATCH for hangs on. <c>--health-probe</c> is a
/// short-lived, separate process invocation (run by the watchdog scheduled task) where a blocking
/// synchronous HTTP call carries no such risk.
///
/// Built on <see cref="System.Net.HttpWebRequest"/> rather than <see cref="System.Net.Http.HttpClient"/>
/// — net48's classic <c>System.dll</c>-hosted HTTP stack needs no extra package/assembly reference
/// (GridLookout.csproj is not a file this feature owns; adding a reference there is out of scope)
/// and is natively synchronous, which is exactly the shape a short-lived console-mode probe wants.
/// </summary>
public static class HealthEndpointClient
{
    /// <summary>
    /// POSTs <paramref name="jsonPayload"/> exactly as given — never re-derived or re-shaped HERE;
    /// the caller (<c>HealthProbe.Run</c>, via <c>BuildPostEnvelope</c>) is where health.json's
    /// content is combined with the probe's own verdict into one payload, so this method stays a
    /// pure "send these bytes" boundary regardless of what the payload's shape is — to
    /// <paramref name="health"/>'s configured endpoint, with
    /// <c>Authorization: Bearer &lt;token&gt;</c> when <paramref name="bearerToken"/> is non-empty.
    /// Never throws — every failure mode (DNS, TLS, timeout, non-2xx response) is caught and
    /// reported back as <c>(false, reason)</c>; "POST failure only logs, never affects exit code
    /// semantics beyond a field in the printed JSON" is a hard requirement of this feature (see
    /// <c>HealthProbe</c>), and this method is the boundary that guarantees it.
    /// </summary>
    /// <param name="bearerToken">Already-decrypted token (or null/empty for no
    /// Authorization header at all — a customer's own collector may not require one) — this method
    /// never touches <see cref="HealthConfig.BearerTokenProtected"/> or any
    /// <see cref="ISecretProtector"/> itself; decrypting is the caller's job (see
    /// <c>WallConfigLoader.GetBearerToken</c>) so this type never needs to know how the secret is
    /// stored, and — just as important — never risks logging or echoing it: it is never printed or
    /// included in the returned error string, only ever placed in the one outbound Authorization
    /// header.</param>
    public static (bool succeeded, string? error) PostSync(HealthConfig health, string? bearerToken, string jsonPayload)
    {
        try
        {
            // Round-5 buyer-review fix: refuse a non-HTTPS endpoint outright unless the config
            // explicitly opts in — the request below can carry an Authorization bearer token, and
            // sending that (plus the wall's health content) cleartext over http was previously
            // possible with no guard at all, undercutting the "HTTPS endpoint" contract the docs
            // state. Mirrors MilestoneSession.IsLayoutPollAllowed's rule for the layout poll; the
            // refusal surfaces through the same (false, reason) channel every other POST failure
            // uses, so it lands in the probe's printed JSON rather than being silently swallowed.
            var endpointUri = new Uri(health.Endpoint, UriKind.Absolute);
            if (!string.Equals(endpointUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                && !health.AllowInsecureEndpoint)
            {
                return (false, "refused: Health.Endpoint is not HTTPS and Health.AllowInsecureEndpoint is false — " +
                    "the POST would send its bearer token and health content in cleartext. Use an https:// endpoint, " +
                    "or set Health.AllowInsecureEndpoint=true for a lab/dev environment only.");
            }

            // net48's default SecurityProtocol does not reliably include TLS 1.2 — an HTTPS POST to
            // a modern endpoint can otherwise fail the handshake outright with an opaque error.
            System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;

            var timeoutMs = Math.Max(1, health.TimeoutSeconds) * 1000;
            var request = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(health.Endpoint);
            request.Method = "POST";
            request.ContentType = "application/json; charset=utf-8";
            request.Timeout = timeoutMs;
            request.ReadWriteTimeout = timeoutMs;
            if (!string.IsNullOrEmpty(bearerToken))
            {
                request.Headers["Authorization"] = "Bearer " + bearerToken;
            }

            var bodyBytes = System.Text.Encoding.UTF8.GetBytes(jsonPayload);
            request.ContentLength = bodyBytes.Length;
            using (var requestStream = request.GetRequestStream())
            {
                requestStream.Write(bodyBytes, 0, bodyBytes.Length);
            }

            using var response = (System.Net.HttpWebResponse)request.GetResponse();
            int statusCode = (int)response.StatusCode;
            return statusCode is >= 200 and < 300 ? (true, null) : (false, $"HTTP {statusCode}");
        }
        catch (System.Net.WebException wex)
        {
            // Nit fix (2026-08-21 external audit): the error response is a live HttpWebResponse
            // holding a connection-group slot — undisposed, repeated failing probes could pin
            // connections until finalization. `using` scopes it to exactly this branch.
            if (wex.Response is System.Net.HttpWebResponse errorResponse)
            {
                using (errorResponse)
                {
                    return (false, $"HTTP {(int)errorResponse.StatusCode}");
                }
            }

            return (false, $"{wex.Status}: {wex.Message}");
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
