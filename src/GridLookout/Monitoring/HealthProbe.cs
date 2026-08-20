using System.Text.Json;
using GridLookout.Config;
using GridLookout.Logging;

namespace GridLookout.Monitoring;

/// <summary>
/// Orchestrates <c>GridLookout.exe --health-probe</c> end-to-end: reads health.json via the same
/// <see cref="AtomicStateStore"/>/<see cref="IStateDirectory"/> mechanism the running wall writes
/// through, matches the recorded pid+process-start-time against a live process, computes the
/// verdict via <see cref="HealthProbeEvaluator"/>, prints a one-line JSON verdict to stdout, and —
/// if <see cref="HealthConfig.Endpoint"/> is configured — POSTs the health.json content to it, with
/// the probe's OWN evaluated verdict attached (buyer-review defect #2 fix — see
/// <see cref="BuildPostEnvelope"/>'s own doc comment for why a raw, untouched copy of health.json was
/// insufficient). Returns the process exit code <c>Program.Main</c> should use.
///
/// Called from <c>Program.Main</c> BEFORE the single-instance mutex and any WinForms/MIP
/// initialization — a probe invocation runs ALONGSIDE a live wall process (not instead of it), so it
/// must never fight it for the mutex, never spin up its own SDK session, and never mutate
/// camerawall.json (see <see cref="WallConfigLoader.LoadReadOnly"/>, which the caller uses to load
/// <paramref name="config"/> for exactly this reason — no seeding, no secret migration, no writes).
/// </summary>
public static class HealthProbe
{
    public const string HealthFileName = "health.json";

    public static int Run(IStateDirectory stateDirectory, string baseDir, WallConfig config, WallConfigLoader loader, TextWriter stdout, FileLogger? logger = null)
    {
        var store = new AtomicStateStore(stateDirectory, baseDir);

        WallHealthState? state = null;
        string? raw = null;
        try
        {
            raw = store.Read(HealthFileName);
            if (raw is not null)
            {
                state = JsonSerializer.Deserialize<WallHealthState>(raw, HealthJsonOptions.Default);
            }
        }
        catch (Exception ex)
        {
            // A torn/corrupt health.json should never happen given AtomicStateStore's write
            // discipline, but this is a diagnostic tool run unattended by a scheduled task — treat
            // it exactly like "absent" rather than throwing, and log the detail for whoever
            // eventually investigates.
            logger?.Warning($"--health-probe: health.json unreadable/malformed ({ex.GetType().Name}: {ex.Message}) — treating as absent.");
            state = null;
        }

        bool pidMatches = false;
        if (state is not null)
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(state.Pid);
                pidMatches = HealthProbeEvaluator.ProcessStartMatches(state.ProcessStartUtc, process.StartTime.ToUniversalTime());
            }
            catch
            {
                // No such process (exited, or the pid was reused by something unrelated), or access
                // denied reading its start time — either way, not a match.
                pidMatches = false;
            }
        }

        var verdict = HealthProbeEvaluator.Evaluate(state, pidMatches, DateTime.UtcNow, config.Health.StaleAfterSeconds);
        // Computed ONCE, reused for both the posted envelope's "probeVerdict" and the printed
        // stdout verdict's "status" below — buyer-review defect #2 explicitly asks for "the same
        // string as stdout verdict"; sharing one expression is what GUARANTEES they can never drift,
        // rather than two independently-written copies of the same "?.ToString().ToLowerInvariant()
        // ?? "absent"" logic silently diverging under a future edit to one but not the other.
        var verdictString = verdict.Status?.ToString().ToLowerInvariant() ?? "absent";

        bool? reportSucceeded = null;
        string? reportError = null;
        if (raw is not null && !string.IsNullOrWhiteSpace(config.Health.Endpoint))
        {
            string? envelopeJson = null;
            try
            {
                envelopeJson = BuildPostEnvelope(raw, verdictString);
            }
            catch (Exception ex)
            {
                // raw parsed cleanly into WallHealthState earlier whenever we reach here (state is
                // non-null on every path that leaves raw non-null and reaches this block — see the
                // read/deserialize block above), so JsonDocument re-parsing the SAME bytes failing
                // here should not happen in practice; treated exactly like any other "can't build a
                // valid POST body" failure — never send anything, record why, same never-throw
                // discipline this whole method already follows.
                reportError = $"Could not attach the probe's verdict to the POST body ({ex.GetType().Name}) — POST not sent.";
                logger?.Warning($"--health-probe: failed to build the POST envelope: {ex.Message}");
            }

            if (envelopeJson is not null)
            {
                string? bearerToken = null;
                try
                {
                    bearerToken = loader.GetBearerToken(config.Health);
                }
                catch (Exception ex)
                {
                    // GetBearerToken already logged the specific DPAPI/corrupt-blob detail — never let a
                    // decrypt failure block the POST outright; send unauthenticated and let the
                    // customer's own endpoint reject it if it requires the header.
                    reportError = $"BearerToken could not be decrypted ({ex.GetType().Name}) — POST sent without Authorization header.";
                }

                var (succeeded, postError) = HealthEndpointClient.PostSync(config.Health, bearerToken, envelopeJson);
                reportSucceeded = succeeded;
                reportError ??= postError;
            }
        }

        var printed = new
        {
            schemaVersion = 1,
            status = verdictString,
            exitCode = (int)verdict.ExitCode,
            reason = verdict.Reason,
            reportedUtc = DateTime.UtcNow,
            endpointConfigured = !string.IsNullOrWhiteSpace(config.Health.Endpoint),
            reportSucceeded,
            reportError,
        };
        stdout.WriteLine(JsonSerializer.Serialize(printed));

        return (int)verdict.ExitCode;
    }

    /// <summary>
    /// Buyer-review defect #2 fix: the probe used to POST an UNTOUCHED copy of health.json — the
    /// controller's own SELF-report, which (see <c>OverallStatus</c>'s own doc comment) can only
    /// ever be Healthy or Degraded, NEVER Unhealthy — while the probe's independently-evaluated
    /// <paramref name="verdictString"/> (which CAN be "unhealthy"/"absent", the whole reason this
    /// out-of-process probe exists) went to stdout only. A remote collector reading just the POST
    /// body could therefore see a hung wall keep reporting "healthy" forever. Fixed by attaching the
    /// probe's own verdict AS A FIELD alongside every original health.json property, rather than
    /// replacing any of them — every existing consumer reading the original fields still works
    /// unchanged; "probeVerdict" is purely additive.
    ///
    /// Built via <see cref="JsonDocument"/> property copy (append one field, mutate nothing) rather
    /// than deserializing <paramref name="rawHealthJson"/> into <see cref="WallHealthState"/> and
    /// re-serializing it — a round trip through that type would silently DROP any property the type
    /// doesn't know about (a newer controller build's field an older probe build doesn't recognize
    /// yet) and re-format the rest (property order, number formatting) — <see cref="HealthEndpointClient.PostSync"/>'s
    /// own doc comment already establishes "the exact bytes… never re-derived or re-shaped" as the
    /// contract for what gets POSTed; this method is the one place that contract is deliberately
    /// widened, by exactly one additive field, to satisfy this fix.
    /// </summary>
    private static string BuildPostEnvelope(string rawHealthJson, string verdictString)
    {
        using var doc = JsonDocument.Parse(rawHealthJson);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                property.WriteTo(writer);
            }
            writer.WriteString("probeVerdict", verdictString);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
