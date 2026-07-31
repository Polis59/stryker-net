using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Stryker.TestRunner.MicrosoftTestPlatform;

/// <summary>
/// Persists an opt-in, low-overhead snapshot of mutation campaign progress.
/// The snapshot exists because redirected Stryker progress output does not expose
/// which runner request owns the wall clock when a release budget expires.
/// </summary>
internal static class MutationCampaignProgressReporter
{
    internal const string OutputPathEnvironmentVariable =
        "THREADWAY_STRYKER_PROGRESS_FILE";

    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        };
    private static readonly TimeSpan PeriodicWriteInterval = TimeSpan.FromSeconds(1);

    private static string? outputPath = NormalizeOutputPath(
        Environment.GetEnvironmentVariable(OutputPathEnvironmentVariable));
    private static MutationCampaignProgress state = new();
    private static long lastWriteTimestamp;

    internal static void RunnerPoolCreated(int concurrency)
    {
        if (outputPath is null)
        {
            return;
        }

        lock (Sync)
        {
            state.RunnerPoolsCreated++;
            state.ConfiguredConcurrency = Math.Max(state.ConfiguredConcurrency, concurrency);
            state.Phase = "runner-initialization";
            PersistLocked(force: true);
        }
    }

    internal static void CoverageCaptureStarted(int testCount, int contextCount)
    {
        if (outputPath is null)
        {
            return;
        }

        lock (Sync)
        {
            state.CoverageCapturesStarted++;
            state.CoverageTestsRequested += testCount;
            state.CoverageContextsPlanned += contextCount;
            state.Phase = "coverage";
            PersistLocked(force: true);
        }
    }

    internal static void CoverageCaptureCompleted(int mappingCount, int contextCount)
    {
        if (outputPath is null)
        {
            return;
        }

        lock (Sync)
        {
            state.CoverageCapturesCompleted++;
            state.CoverageMappingsCaptured += mappingCount;
            state.CoverageContextsCompleted += contextCount;
            UpdateIdlePhaseLocked();
            PersistLocked(force: true);
        }
    }

    internal static void CoverageSnapshotReused(int mappingCount)
    {
        if (outputPath is null)
        {
            return;
        }

        lock (Sync)
        {
            state.CoverageSnapshotsReused++;
            state.CoverageMappingsReused += mappingCount;
            PersistLocked(force: true);
        }
    }

    internal static void MutationPlanBuilt(
        int mutantCount,
        int isolatedMutantCount,
        int ordinaryMutantCount,
        int ordinaryBatchCount)
    {
        if (outputPath is null)
        {
            return;
        }

        lock (Sync)
        {
            state.PlanSegments++;
            state.PlannedMutants += mutantCount;
            state.PlannedIsolatedMutants += isolatedMutantCount;
            state.PlannedOrdinaryMutants += ordinaryMutantCount;
            state.PlannedOrdinaryBatches += ordinaryBatchCount;
            UpdateIdlePhaseLocked();
            PersistLocked(force: true);
        }
    }

    internal static void IsolatedMutantStarted(
        string runnerId,
        int mutantId,
        int? testCount)
    {
        if (outputPath is null)
        {
            return;
        }

        lock (Sync)
        {
            state.IsolatedMutantsStarted++;
            state.ActiveIsolatedRequests[runnerId] = new MutationWorkItem(
                runnerId,
                [mutantId],
                testCount,
                DateTimeOffset.UtcNow);
            state.Phase = "mutation-isolated";
            PersistLocked(force: true);
        }
    }

    internal static void IsolatedMutantCompleted(
        string runnerId,
        bool runtimeIssue,
        bool timedOut)
    {
        if (outputPath is null)
        {
            return;
        }

        lock (Sync)
        {
            state.IsolatedMutantsCompleted++;
            if (runtimeIssue)
            {
                state.IsolatedRuntimeIssues++;
            }
            if (timedOut)
            {
                state.IsolatedTimeouts++;
            }

            state.ActiveIsolatedRequests.Remove(runnerId);
            UpdateIdlePhaseLocked();
            PersistLocked(force: true);
        }
    }

    internal static void OrdinaryBatchStarted(
        string runnerId,
        IReadOnlyList<int> mutantIds,
        int? testCount)
    {
        if (outputPath is null)
        {
            return;
        }

        lock (Sync)
        {
            state.OrdinaryBatchesStarted++;
            state.OrdinaryMutantsStarted += mutantIds.Count;
            state.ActiveOrdinaryRequests[runnerId] = new MutationWorkItem(
                runnerId,
                mutantIds.ToArray(),
                testCount,
                DateTimeOffset.UtcNow);
            state.Phase = "mutation-ordinary";
            PersistLocked(force: state.OrdinaryBatchesStarted == 1);
        }
    }

    internal static void OrdinaryBatchCompleted(
        string runnerId,
        int mutantCount,
        bool runtimeIssue,
        bool timedOut)
    {
        if (outputPath is null)
        {
            return;
        }

        lock (Sync)
        {
            state.OrdinaryBatchesCompleted++;
            state.OrdinaryMutantsCompleted += mutantCount;
            if (runtimeIssue)
            {
                state.OrdinaryRuntimeIssues++;
            }
            if (timedOut)
            {
                state.OrdinaryTimeouts++;
            }

            state.ActiveOrdinaryRequests.Remove(runnerId);
            UpdateIdlePhaseLocked();
            PersistLocked(
                force: state.ActiveOrdinaryRequests.Count == 0 &&
                    state.OrdinaryBatchesCompleted >= state.PlannedOrdinaryBatches);
        }
    }

    internal static void IsolationHostStarted(
        string runnerId,
        string assembly,
        int processId)
    {
        if (outputPath is null)
        {
            return;
        }

        lock (Sync)
        {
            state.IsolationHostStarts++;
            if (state.Phase.StartsWith("mutation-", StringComparison.Ordinal))
            {
                state.MutationIsolationHostStarts++;
            }
            else if (state.Phase == "coverage")
            {
                state.CoverageIsolationHostStarts++;
            }

            state.ActiveIsolationHosts[processId] = new IsolationHost(
                runnerId,
                assembly,
                processId,
                DateTimeOffset.UtcNow);
            PersistLocked(force: true);
        }
    }

    internal static void IsolationHostStopped(int processId, string reason)
    {
        if (outputPath is null)
        {
            return;
        }

        lock (Sync)
        {
            state.IsolationHostStops++;
            state.LastIsolationHostStopReason = reason;
            state.ActiveIsolationHosts.Remove(processId);
            PersistLocked(force: true);
        }
    }

    internal static void ConfigureForTests(string? path)
    {
        lock (Sync)
        {
            outputPath = NormalizeOutputPath(path);
            state = new MutationCampaignProgress();
            lastWriteTimestamp = 0;
        }
    }

    private static void UpdateIdlePhaseLocked()
    {
        if (state.ActiveIsolatedRequests.Count > 0)
        {
            state.Phase = "mutation-isolated";
            return;
        }
        if (state.ActiveOrdinaryRequests.Count > 0)
        {
            state.Phase = "mutation-ordinary";
            return;
        }
        if (state.CoverageCapturesStarted > state.CoverageCapturesCompleted)
        {
            state.Phase = "coverage";
            return;
        }
        if (state.PlannedIsolatedMutants > state.IsolatedMutantsCompleted)
        {
            state.Phase = "mutation-awaiting-isolated";
            return;
        }
        if (state.PlannedOrdinaryBatches > state.OrdinaryBatchesCompleted)
        {
            state.Phase = "mutation-awaiting-ordinary";
            return;
        }

        state.Phase = state.PlannedMutants > 0
            ? "mutation-complete"
            : "coverage-complete";
    }

    private static void PersistLocked(bool force)
    {
        if (outputPath is null)
        {
            return;
        }

        var timestamp = Stopwatch.GetTimestamp();
        if (!force &&
            lastWriteTimestamp != 0 &&
            Stopwatch.GetElapsedTime(lastWriteTimestamp, timestamp) < PeriodicWriteInterval)
        {
            return;
        }

        state.UpdatedAtUtc = DateTimeOffset.UtcNow;
        state.ElapsedSeconds = Math.Round(
            (state.UpdatedAtUtc - state.StartedAtUtc).TotalSeconds,
            3);
        state.IsolatedMutantsRemaining = Math.Max(
            0,
            state.PlannedIsolatedMutants - state.IsolatedMutantsCompleted);
        state.OrdinaryMutantsRemaining = Math.Max(
            0,
            state.PlannedOrdinaryMutants - state.OrdinaryMutantsCompleted);
        state.OrdinaryBatchesRemaining = Math.Max(
            0,
            state.PlannedOrdinaryBatches - state.OrdinaryBatchesCompleted);

        var temporaryPath = $"{outputPath}.tmp-{Environment.ProcessId}";
        try
        {
            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(state, SerializerOptions),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, outputPath, overwrite: true);
            lastWriteTimestamp = timestamp;
        }
        catch
        {
            // Diagnostics must never change the mutation result.
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
                // A failed cleanup is confined to the explicitly selected diagnostic path.
            }
        }
    }

    private static string? NormalizeOutputPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
    }
}

internal sealed class MutationCampaignProgress
{
    public int SchemaVersion { get; } = 1;
    public int ProcessId { get; } = Environment.ProcessId;
    public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public double ElapsedSeconds { get; set; }
    public string Phase { get; set; } = "runner-initialization";
    public int RunnerPoolsCreated { get; set; }
    public int ConfiguredConcurrency { get; set; }
    public int CoverageCapturesStarted { get; set; }
    public int CoverageCapturesCompleted { get; set; }
    public int CoverageSnapshotsReused { get; set; }
    public int CoverageTestsRequested { get; set; }
    public int CoverageContextsPlanned { get; set; }
    public int CoverageContextsCompleted { get; set; }
    public int CoverageMappingsCaptured { get; set; }
    public int CoverageMappingsReused { get; set; }
    public int PlanSegments { get; set; }
    public int PlannedMutants { get; set; }
    public int PlannedIsolatedMutants { get; set; }
    public int PlannedOrdinaryMutants { get; set; }
    public int PlannedOrdinaryBatches { get; set; }
    public int IsolatedMutantsStarted { get; set; }
    public int IsolatedMutantsCompleted { get; set; }
    public int IsolatedMutantsRemaining { get; set; }
    public int IsolatedRuntimeIssues { get; set; }
    public int IsolatedTimeouts { get; set; }
    public int OrdinaryBatchesStarted { get; set; }
    public int OrdinaryBatchesCompleted { get; set; }
    public int OrdinaryBatchesRemaining { get; set; }
    public int OrdinaryMutantsStarted { get; set; }
    public int OrdinaryMutantsCompleted { get; set; }
    public int OrdinaryMutantsRemaining { get; set; }
    public int OrdinaryRuntimeIssues { get; set; }
    public int OrdinaryTimeouts { get; set; }
    public int IsolationHostStarts { get; set; }
    public int CoverageIsolationHostStarts { get; set; }
    public int MutationIsolationHostStarts { get; set; }
    public int IsolationHostStops { get; set; }
    public string? LastIsolationHostStopReason { get; set; }
    public Dictionary<string, MutationWorkItem> ActiveIsolatedRequests { get; } = [];
    public Dictionary<string, MutationWorkItem> ActiveOrdinaryRequests { get; } = [];
    public Dictionary<int, IsolationHost> ActiveIsolationHosts { get; } = [];
}

internal sealed record MutationWorkItem(
    string RunnerId,
    IReadOnlyList<int> MutantIds,
    int? TestCount,
    DateTimeOffset StartedAtUtc);

internal sealed record IsolationHost(
    string RunnerId,
    string Assembly,
    int ProcessId,
    DateTimeOffset StartedAtUtc);
