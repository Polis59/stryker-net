using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using Microsoft.Extensions.Logging;
using Stryker.Abstractions;
using Stryker.Abstractions.Options;
using Stryker.Abstractions.Testing;
using Stryker.TestRunner.MicrosoftTestPlatform.Models;
using Stryker.TestRunner.Results;
using Stryker.TestRunner.Tests;
using static Stryker.Abstractions.Testing.ITestRunner;

namespace Stryker.TestRunner.MicrosoftTestPlatform;

/// <summary>
/// Individual test runner instance that handles test execution with mutation-specific
/// environment variables. Used by MicrosoftTestPlatformRunnerPool.
/// Maintains persistent test server connections per assembly to reduce process startup overhead.
/// Uses file-based mutant control to allow changing the active mutant without restarting processes.
/// Disjoint ordinary mutants share a request through a test-case activation map consumed by the
/// test framework's synchronous xUnit lifecycle sink.
/// Mutants that need static-state isolation execute in fresh collectible load contexts inside a
/// broker process, see <see cref="RequiresProcessIsolation"/>.
/// </summary>
public class SingleMicrosoftTestPlatformRunner : IDisposable
{
    private const string InactiveMutantMapHeader = "threadway-stryker-map-v1\toff";
    private const string ActiveMutantMapHeaderPrefix = "threadway-stryker-map-v1\tactive\t";

    private readonly int _id;
    private readonly Dictionary<string, List<TestNode>> _testsByAssembly;
    private readonly Dictionary<string, MtpTestDescription> _testDescriptions;
    private readonly TestSet _testSet;
    private readonly object _discoveryLock;
    private readonly ILogger _logger;
    private readonly string _mutantFilePath;
    private readonly string _mutantMapFilePath;
    private readonly string _mutantMapAcknowledgementFilePath;
    private readonly string _mutantMapErrorFilePath;
    private readonly string _coverageFilePath;
    private readonly string _coverageMapFilePath;
    private readonly IStrykerOptions? _options;
    private string? _expectedMutantMapAcknowledgement;

    private readonly Dictionary<string, AssemblyTestServer> _assemblyServers = new();
    private readonly Dictionary<string, CollectibleTestIsolationClient> _isolationClients = new();
    private readonly object _serverLock = new();
    private bool _disposed;
    private bool _coverageMode;

    private string RunnerId => $"MtpRunner-{_id}";
    internal string MutantFilePath => _mutantFilePath;
    internal string MutantMapFilePath => _mutantMapFilePath;
    internal string MutantMapAcknowledgementFilePath => _mutantMapAcknowledgementFilePath;
    internal string MutantMapErrorFilePath => _mutantMapErrorFilePath;
    internal string CoverageFilePath => _coverageFilePath;
    internal string CoverageMapFilePath => _coverageMapFilePath;

    public SingleMicrosoftTestPlatformRunner(
        int id,
        Dictionary<string, List<TestNode>> testsByAssembly,
        Dictionary<string, MtpTestDescription> testDescriptions,
        TestSet testSet,
        object discoveryLock,
        ILogger logger,
        IStrykerOptions? options = null)
    {
        _id = id;
        _testsByAssembly = testsByAssembly;
        _testDescriptions = testDescriptions;
        _testSet = testSet;
        _discoveryLock = discoveryLock;
        _logger = logger;
        _options = options;

        // Stryker can create one runner pool per solution project. A pool-local
        // numeric ID would let concurrent projects overwrite each other's
        // activation and coverage channels.
        var fileToken = $"{Environment.ProcessId}-{Guid.NewGuid():N}-{_id}";
        _mutantFilePath = Path.Combine(Path.GetTempPath(), $"stryker-mutant-{fileToken}.txt");
        _mutantMapFilePath = Path.Combine(Path.GetTempPath(), $"stryker-mutant-map-{fileToken}.txt");
        _mutantMapAcknowledgementFilePath = Path.Combine(
            Path.GetTempPath(),
            $"stryker-mutant-map-ack-{fileToken}.txt");
        _mutantMapErrorFilePath = Path.Combine(Path.GetTempPath(), $"stryker-mutant-map-error-{fileToken}.txt");
        _coverageFilePath = Path.Combine(Path.GetTempPath(), $"stryker-coverage-{fileToken}.txt");
        _coverageMapFilePath = Path.Combine(Path.GetTempPath(), $"stryker-coverage-map-{fileToken}.txt");

        // Initialize with no active mutation
        WriteMutantIdToFile(-1);
        WriteMutantMap(null);
    }

    public Task<bool> DiscoverTestsAsync(string assembly)
    {
        return DiscoverTestsInternalAsync(assembly);
    }

    public Task<ITestRunResult> InitialTestAsync(IProjectAndTests project)
    {
        var assemblies = project.GetTestAssemblies();
        return RunAllTestsAsync(assemblies, mutantId: -1, mutants: null, update: null);
    }

    public async Task<ITestRunResult> TestMultipleMutantsAsync(
        IProjectAndTests project,
        ITimeoutValueCalculator? timeoutCalc,
        IReadOnlyList<IMutant> mutants,
        TestUpdateHandler? update)
    {
        var assemblies = project.GetTestAssemblies();

        if (mutants.Count == 0)
        {
            throw new ArgumentException("At least one mutant is required.", nameof(mutants));
        }

        var isolatedMutants = mutants.Where(RequiresProcessIsolation).ToList();
        if (isolatedMutants.Count == mutants.Count)
        {
            return await TestMutantsInIsolationAsync(
                assemblies,
                isolatedMutants,
                update,
                timeoutCalc).ConfigureAwait(false);
        }

        var reusableMutants = isolatedMutants.Count == 0
            ? mutants
            : mutants.Where(mutant => !RequiresProcessIsolation(mutant)).ToList();
        var testUidFilter = BuildTestUidFilter(reusableMutants);
        var mutantId = reusableMutants.Count == 1 ? reusableMutants[0].Id : -1;

        if (_logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
        {
            _logger.LogDebug("{RunnerId}: Testing mutant(s) [{Mutants}] with active mutation ID: {MutantId} against {TestScope}",
                RunnerId, string.Join(",", reusableMutants.Select(m => m.Id)), mutantId,
                testUidFilter is null ? "all tests" : "covering tests only");
        }

        if (isolatedMutants.Count == 0)
        {
            return await TestReusableMutantsAsync(
                assemblies,
                mutantId,
                reusableMutants,
                update,
                timeoutCalc,
                testUidFilter).ConfigureAwait(false);
        }

        // Stryker may place static and ordinary mutants in the same disjoint-test batch.
        // Isolate only the mutants whose activation must precede process initialization,
        // then leave the reusable host alive after the ordinary portion for the next request.
        var isolatedResult = await TestMutantsInIsolationAsync(
            assemblies,
            isolatedMutants,
            update,
            timeoutCalc).ConfigureAwait(false);
        var reusableResult = await TestReusableMutantsAsync(
            assemblies,
            mutantId,
            reusableMutants,
            update,
            timeoutCalc,
            testUidFilter).ConfigureAwait(false);

        return MergeResults([isolatedResult, reusableResult]);
    }

    private Func<TestNode, bool>? BuildTestUidFilter(IReadOnlyList<IMutant> mutants)
    {
        if (_options?.OptimizationMode.HasFlag(OptimizationModes.CoverageBasedTest) != true ||
            mutants.Any(m => m.AssessingTests.IsEveryTest))
        {
            return null;
        }

        var testUids = mutants
            .SelectMany(m => m.AssessingTests.GetIdentifiers())
            .ToHashSet(StringComparer.Ordinal);

        return testUids.Count == 0 ? _ => false : node => testUids.Contains(node.Uid);
    }

    /// <summary>
    /// A mutation inside a static initializer (or one flagged by coverage analysis as needing early
    /// activation) only takes effect while the type initializes, which happens once per assembly load
    /// context. Testing it in a reused context is wrong in both directions: the mutation cannot
    /// activate after its type initialized (false Survived), and mutated state would otherwise repeat
    /// in later sessions and kill unrelated mutants (false Killed).
    /// </summary>
    private static bool RequiresProcessIsolation(IMutant mutant) =>
        mutant.IsStaticValue || mutant.MustBeTestedInIsolation;

    /// <summary>
    /// Runs each mutant needing isolation in a fresh collectible load context inside one broker
    /// process. The session is split to one mutant at a time because the file-based control channel
    /// can activate only a single id. The broker loads the test and product assemblies only after
    /// that id is published, then proves that the complete context unloaded before accepting the
    /// next mutant.
    /// </summary>
    private async Task<ITestRunResult> TestMutantsInIsolationAsync(
        IReadOnlyList<string> assemblies,
        IReadOnlyList<IMutant> mutants,
        TestUpdateHandler? update,
        ITimeoutValueCalculator? timeoutCalc)
    {
        // The isolation host intermittently dies with a native fault. One lost
        // host must not become a terminal RuntimeError verdict — the governed
        // validator fails the whole campaign closed on any RuntimeError — so a
        // mutant whose session reports a runtime issue retries once in a fresh
        // context. A mutation that genuinely kills the process fails both
        // attempts and keeps its RuntimeError, and a timeout is a verdict of
        // its own and is never retried.
        const int maxIsolatedAttempts = 2;
        var results = new List<ITestRunResult>(mutants.Count);
        foreach (var mutant in mutants)
        {
            _logger.LogDebug("{RunnerId}: Testing mutant {MutantId} in an isolated load context",
                RunnerId, mutant.Id);

            ITestRunResult? result = null;
            MutationCampaignProgressReporter.IsolatedMutantStarted(
                RunnerId,
                mutant.Id,
                GetAssessingTestCount([mutant]));
            try
            {
                for (var attempt = 1; attempt <= maxIsolatedAttempts; attempt++)
                {
                    result = await RunAllTestsAsync(
                        assemblies,
                        mutant.Id,
                        [mutant],
                        update,
                        timeoutCalc,
                        BuildTestUidFilter([mutant]),
                        useCollectibleIsolation: true).ConfigureAwait(false);
                    if (!result.SessionHadRuntimeIssue || attempt == maxIsolatedAttempts)
                    {
                        break;
                    }

                    _logger.LogWarning(
                        "{RunnerId}: Isolated mutant {MutantId} lost its isolation host " +
                        "(attempt {Attempt}/{MaxAttempts}); retrying in a fresh context",
                        RunnerId,
                        mutant.Id,
                        attempt,
                        maxIsolatedAttempts);
                }

                results.Add(result!);
            }
            finally
            {
                MutationCampaignProgressReporter.IsolatedMutantCompleted(
                    RunnerId,
                    result?.SessionHadRuntimeIssue ?? true,
                    result?.SessionTimedOut ?? false);
                WriteMutantIdToFile(-1);
            }
        }

        return results.Count == 1 ? results[0] : MergeResults(results);
    }

    /// <summary>
    /// Runs the batch's ordinary mutants in bounded waves on the warm host. Each wave publishes
    /// one activation map assigning only the next slice of every unresolved mutant's assessing
    /// tests, then executes them in a single request — one fixture setup advances the whole
    /// batch, per-test switching keeps each test attributed to its own mutant, and a killed
    /// mutant stops consuming test executions after its killing wave. This is what the stock
    /// VSTest runner's packed sessions with bail achieve, without paying a process spawn per
    /// session. A mutant whose assessing tests are exhausted without a detection is not trusted:
    /// the warm host's caches can hide the mutated path entirely, so it re-runs once in a fresh
    /// collectible context where every path executes cold before Survived is accepted.
    /// </summary>
    private async Task<ITestRunResult> TestReusableMutantsAsync(
        IReadOnlyList<string> assemblies,
        int mutantId,
        IReadOnlyList<IMutant> mutants,
        TestUpdateHandler? update,
        ITimeoutValueCalculator? timeoutCalc,
        Func<TestNode, bool>? testUidFilter)
    {
        _ = mutantId;
        _ = testUidFilter;

        MutationCampaignProgressReporter.OrdinaryBatchStarted(
            RunnerId,
            mutants.Select(mutant => mutant.Id).ToList(),
            GetAssessingTestCount(mutants));

        var hadRuntimeIssue = false;
        var hadTimeout = false;
        try
        {
            var states = new List<MutantWaveState>(mutants.Count);
            foreach (var mutant in mutants)
            {
                var remaining = mutant.AssessingTests.IsEveryTest
                    ? null
                    : mutant.AssessingTests.GetIdentifiers()
                        .Where(uid => !uid.Contains('\t', StringComparison.Ordinal) &&
                                      !uid.Contains('\r', StringComparison.Ordinal) &&
                                      !uid.Contains('\n', StringComparison.Ordinal))
                        .ToList();

                if (remaining is { Count: 0 })
                {
                    // Nothing left to assess this mutant with; report an empty run so it
                    // resolves to Survived rather than executing the whole suite.
                    update?.Invoke([mutant], TestIdentifierList.NoTest(), TestIdentifierList.NoTest(), TestIdentifierList.NoTest());
                    continue;
                }

                states.Add(new MutantWaveState(mutant, remaining));
            }

            // Wave slice sizes: most killed mutants die on their first assessing test, so early
            // waves spend one execution per mutant and the next two widen geometrically. There
            // is deliberately no draining wave: a mutant that passed seven of its tests is very
            // probably a survivor, and a survivor's verdict is only accepted from the cold
            // collectible confirmation anyway — draining its remaining set here would execute
            // that set twice, once warm and once cold, for no additional information.
            int[] waveSlices = [1, 2, 4];

            foreach (var sliceSize in waveSlices)
            {
                var unresolved = states.Where(s => s.Unresolved && s.Remaining is { Count: > 0 }).ToList();
                if (unresolved.Count == 0)
                {
                    break;
                }

                var assignments = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var state in unresolved)
                {
                    // A test already owned by a batch-mate this wave is skipped, not consumed:
                    // the mutant simply runs it in a later wave once it is free again. Only one
                    // mutant per wave may own a test, because activation is keyed by test.
                    var assigned = 0;
                    var index = 0;
                    while (assigned < sliceSize && index < state.Remaining!.Count)
                    {
                        var testUid = state.Remaining[index];
                        if (assignments.ContainsKey(testUid))
                        {
                            index++;
                            continue;
                        }

                        assignments[testUid] = state.Mutant.Id;
                        state.Remaining.RemoveAt(index);
                        assigned++;
                    }
                }

                if (assignments.Count == 0)
                {
                    // Every remaining test is contested; the leftovers settle in collectible
                    // contexts below.
                    break;
                }

                var waveOutcome = await RunWaveAsync(assemblies, assignments, timeoutCalc).ConfigureAwait(false);
                hadTimeout |= waveOutcome.TimedOut;
                hadRuntimeIssue |= waveOutcome.HadRuntimeIssue;

                foreach (var state in unresolved)
                {
                    state.Executed.UnionWith(waveOutcome.ExecutedByMutant.TryGetValue(state.Mutant.Id, out var ran) ? ran : []);
                    if (waveOutcome.FailedByMutant.TryGetValue(state.Mutant.Id, out var failed) && failed.Count > 0)
                    {
                        state.Failed.UnionWith(failed);
                        state.Unresolved = false;
                    }
                    else if (waveOutcome.TimedOutByMutant.TryGetValue(state.Mutant.Id, out var timedOut) && timedOut.Count > 0)
                    {
                        state.TimedOutTests.UnionWith(timedOut);
                        state.Unresolved = false;
                    }
                }

                if (waveOutcome.TimedOut || waveOutcome.HadRuntimeIssue || waveOutcome.ActivationFailed)
                {
                    // The wave gave no reliable per-test outcomes for whatever did not report.
                    // Every mutant still unresolved is settled conclusively in its own fresh
                    // collectible context below rather than risking another poisoned wave.
                    break;
                }
            }

            // Mutants that ran out of assessing tests without a detection, plus everything a
            // failed wave left unresolved: prove the verdict cold in a collectible context.
            foreach (var state in states.Where(s => s.Unresolved))
            {
                var result = await RunAllTestsAsync(
                    assemblies,
                    state.Mutant.Id,
                    [state.Mutant],
                    update: null,
                    timeoutCalc,
                    BuildTestUidFilter([state.Mutant]),
                    useCollectibleIsolation: true).ConfigureAwait(false);

                hadTimeout |= result.SessionTimedOut;
                hadRuntimeIssue |= result.SessionHadRuntimeIssue;

                update?.Invoke([state.Mutant], result.FailingTests, result.ExecutedTests, result.TimedOutTests);
                if (update is null || result.SessionTimedOut || result.SessionHadRuntimeIssue)
                {
                    state.Mutant.AnalyzeTestRun(result.FailingTests, result.ExecutedTests, result.TimedOutTests,
                        result.SessionTimedOut, result.SessionHadRuntimeIssue);
                }

                state.Reported = true;
            }

            foreach (var state in states.Where(s => !s.Reported))
            {
                update?.Invoke(
                    [state.Mutant],
                    new TestIdentifierList(state.Failed),
                    new TestIdentifierList(state.Executed),
                    state.TimedOutTests.Count == 0 ? TestIdentifierList.NoTest() : new TestIdentifierList(state.TimedOutTests));
                if (update is null)
                {
                    state.Mutant.AnalyzeTestRun(
                        new TestIdentifierList(state.Failed),
                        new TestIdentifierList(state.Executed),
                        state.TimedOutTests.Count == 0 ? TestIdentifierList.NoTest() : new TestIdentifierList(state.TimedOutTests),
                        false,
                        false);
                }
            }

            // Session-level flags stay false on the merged result: every mutant above already
            // received a conclusive classification, so the executor must not re-analyze the
            // group against the union of per-mutant test results.
            return MergeWaveStates(states);
        }
        finally
        {
            MutationCampaignProgressReporter.OrdinaryBatchCompleted(
                RunnerId,
                mutants.Count,
                hadRuntimeIssue,
                hadTimeout);
        }
    }

    private sealed class MutantWaveState(IMutant mutant, List<string>? remaining)
    {
        public IMutant Mutant { get; } = mutant;
        public List<string>? Remaining { get; } = remaining;
        public HashSet<string> Executed { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Failed { get; } = new(StringComparer.Ordinal);
        public HashSet<string> TimedOutTests { get; } = new(StringComparer.Ordinal);
        public bool Unresolved { get; set; } = true;
        public bool Reported { get; set; }
    }

    private sealed class WaveOutcome
    {
        public Dictionary<int, HashSet<string>> ExecutedByMutant { get; } = [];
        public Dictionary<int, HashSet<string>> FailedByMutant { get; } = [];
        public Dictionary<int, HashSet<string>> TimedOutByMutant { get; } = [];
        public bool TimedOut { get; set; }
        public bool HadRuntimeIssue { get; set; }
        public bool ActivationFailed { get; set; }
    }

    /// <summary>
    /// Executes one wave: publishes the explicit assignment map, runs exactly the assigned tests
    /// in one request per assembly, verifies the activation acknowledgement, and buckets every
    /// finished test's outcome by its assigned mutant.
    /// </summary>
    private async Task<WaveOutcome> RunWaveAsync(
        IReadOnlyList<string> assemblies,
        Dictionary<string, int> assignments,
        ITimeoutValueCalculator? timeoutCalc)
    {
        var outcome = new WaveOutcome();

        WriteMutantMapAssignments(new Dictionary<string, int>(assignments, StringComparer.Ordinal));
        WriteMutantIdToFile(-1);

        foreach (var assembly in assemblies)
        {
            var (result, timedOut, _) = await RunAssemblyTestsAsync(
                assembly,
                timeoutCalc,
                node => assignments.ContainsKey(node.Uid),
                serialActivation: true).ConfigureAwait(false);

            outcome.TimedOut |= timedOut;
            if (result is null)
            {
                continue;
            }

            if (result.FailingTests.IsEveryTest)
            {
                // Crash sentinel: the host died and nothing in this wave is attributable.
                outcome.HadRuntimeIssue = true;
                continue;
            }

            foreach (var uid in result.ExecutedTests.IsEveryTest ? assignments.Keys : result.ExecutedTests.GetIdentifiers())
            {
                if (assignments.TryGetValue(uid, out var ownerId))
                {
                    Bucket(outcome.ExecutedByMutant, ownerId, uid);
                }
            }

            foreach (var uid in result.FailingTests.GetIdentifiers())
            {
                if (assignments.TryGetValue(uid, out var ownerId))
                {
                    Bucket(outcome.FailedByMutant, ownerId, uid);
                }
            }

            foreach (var uid in result.TimedOutTests.GetIdentifiers())
            {
                if (assignments.TryGetValue(uid, out var ownerId))
                {
                    Bucket(outcome.TimedOutByMutant, ownerId, uid);
                }
            }
        }

        var activationError = ValidateMutantMapAcknowledgement();
        if (!string.IsNullOrWhiteSpace(activationError))
        {
            _logger.LogWarning("{RunnerId}: Wave activation protocol failed: {ActivationError}", RunnerId, activationError);
            outcome.ActivationFailed = true;
            // Fail closed: nothing this wave reported can be trusted to have run mutated.
            outcome.FailedByMutant.Clear();
            outcome.TimedOutByMutant.Clear();
            outcome.ExecutedByMutant.Clear();
        }

        return outcome;

        static void Bucket(Dictionary<int, HashSet<string>> buckets, int mutantId, string uid)
        {
            if (!buckets.TryGetValue(mutantId, out var set))
            {
                buckets[mutantId] = set = new HashSet<string>(StringComparer.Ordinal);
            }

            set.Add(uid);
        }
    }

    private ITestRunResult MergeWaveStates(IReadOnlyList<MutantWaveState> states)
    {
        IEnumerable<MtpTestDescription> testDescriptionValues;
        lock (_discoveryLock)
        {
            testDescriptionValues = _testDescriptions.Values.ToList();
        }

        var executed = new TestIdentifierList(states.SelectMany(s => s.Executed).Distinct());
        var failed = new TestIdentifierList(states.SelectMany(s => s.Failed).Distinct());
        var timedOut = new TestIdentifierList(states.SelectMany(s => s.TimedOutTests).Distinct());

        return new TestRunResult(testDescriptionValues, executed, failed, timedOut, string.Empty, [], TimeSpan.Zero);
    }


    private static int? GetAssessingTestCount(IReadOnlyList<IMutant> mutants)
    {
        if (mutants.Any(mutant => mutant.AssessingTests.IsEveryTest))
        {
            return null;
        }

        return mutants
            .SelectMany(mutant => mutant.AssessingTests.GetIdentifiers())
            .Distinct(StringComparer.Ordinal)
            .Count();
    }

    /// <summary>
    /// Combines the per-mutant results of a split isolated batch into one session result. When a
    /// split session crashed or timed out, the flag is returned with empty test lists rather than
    /// the union: the executor re-analyzes every mutant of a flagged multi-mutant batch against the
    /// returned lists with the session flags dropped, so a union would let one mutant's failures
    /// (or a fully executed sibling run) overwrite the others' verdicts. Empty lists leave the
    /// affected mutants Pending, which routes them into the executor's single-mutant retry path
    /// where the flags are honored per mutant.
    /// </summary>
    private ITestRunResult MergeResults(IReadOnlyList<ITestRunResult> results)
    {
        var message = string.Join(Environment.NewLine,
            results.Select(r => r.ResultMessage).Where(m => !string.IsNullOrWhiteSpace(m)));
        var messages = results.SelectMany(r => r.Messages ?? []).ToList();
        var duration = TimeSpan.FromTicks(results.Sum(r => r.Duration.Ticks));

        IEnumerable<MtpTestDescription> testDescriptionValues;
        lock (_discoveryLock)
        {
            testDescriptionValues = _testDescriptions.Values.ToList();
        }

        if (results.Any(r => r.SessionHadRuntimeIssue))
        {
            return TestRunResult.RuntimeError(testDescriptionValues, TestIdentifierList.NoTest(),
                TestIdentifierList.NoTest(), TestIdentifierList.NoTest(), message, messages, duration);
        }

        if (results.Any(r => r.SessionTimedOut))
        {
            return TestRunResult.TimedOut(testDescriptionValues, TestIdentifierList.NoTest(),
                TestIdentifierList.NoTest(), TestIdentifierList.NoTest(), message, messages, duration);
        }

        var executedTests = results.Any(r => r.ExecutedTests.IsEveryTest)
            ? TestIdentifierList.EveryTest()
            : new TestIdentifierList(results.SelectMany(r => r.ExecutedTests.GetIdentifiers()).Distinct());
        var failedTests = new TestIdentifierList(results.SelectMany(r => r.FailingTests.GetIdentifiers()).Distinct());
        var timedOutTests = new TestIdentifierList(results.SelectMany(r => r.TimedOutTests.GetIdentifiers()).Distinct());

        return new TestRunResult(testDescriptionValues, executedTests, failedTests, timedOutTests, message, messages, duration);
    }

    public virtual async Task ResetServerAsync()
    {
        _logger.LogDebug("{RunnerId}: Resetting test servers to reload assemblies", RunnerId);

        lock (_serverLock)
        {
            foreach (var server in _assemblyServers.Values)
            {
                server.Dispose();
            }
            _assemblyServers.Clear();
            foreach (var client in _isolationClients.Values)
            {
                client.Dispose();
            }
            _isolationClients.Clear();
        }

        _logger.LogDebug("{RunnerId}: Test servers reset complete", RunnerId);
        await Task.CompletedTask;
    }

    /// <summary>
    /// Stops and removes the server for a specific assembly. This triggers ProcessExit
    /// in the test process, causing MutantControl.FlushCoverageToFile() to be called.
    /// The server is removed from the cache so a fresh one is created on next use.
    /// </summary>
    internal async Task StopAndRemoveServerAsync(string assembly)
    {
        AssemblyTestServer? server;
        lock (_serverLock)
        {
            _assemblyServers.TryGetValue(assembly, out server);
            _assemblyServers.Remove(assembly);
        }

        if (server is not null)
        {
            await server.StopAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs one test class in an isolated collectible context. The xUnit lifecycle sink
    /// records ordinary coverage for each test and widens static or outside-test
    /// coverage across the class. The class boundary prevents initialization and
    /// fixture work from leaking into another class without paying one process
    /// startup per test.
    /// The mutation phase absorbs a transient isolation-host loss as a per-mutant
    /// runtime error, but during coverage capture a transient fault would abort
    /// the whole campaign, so one boundary retries once. Two transient faults are
    /// covered: the isolation host dying before it responds, and a published
    /// coverage map that does not name every requested test (the lifecycle sink
    /// records its own failures in the activation error file rather than failing
    /// the test run). Every retry restarts from a clean control channel and an
    /// empty coverage map, so a partial crashed attempt cannot contribute records.
    /// </summary>
    internal virtual async Task<IReadOnlyList<ICoverageRunResult>> RunTestGroupForCoverageAsync(
        string assembly,
        IReadOnlyList<TestNode> tests,
        CoverageConfidence confidence)
    {
        const int maxCaptureAttempts = 2;
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                WriteMutantMap(null);
                WriteMutantIdToFile(-1);
                DeleteCoverageFile();
                DeleteCoverageMapFile();

                var execution = await ExecuteCoverageContextAsync(
                    assembly,
                    tests.Select(test => test.Uid).ToList()).ConfigureAwait(false);
                if (execution.SessionTimedOut)
                {
                    throw new TimeoutException(
                        "The collectible coverage context exceeded its execution timeout.");
                }

                var hostLoss = !string.IsNullOrWhiteSpace(execution.Error)
                    ? execution.Error
                    : execution.Unloaded
                        ? null
                        : "The collectible coverage context did not unload.";
                if (hostLoss is not null)
                {
                    if (attempt < maxCaptureAttempts)
                    {
                        _logger.LogWarning(
                            "{RunnerId}: Coverage capture for boundary {Boundary} lost its isolation host " +
                            "(attempt {Attempt}/{MaxAttempts}); retrying on a fresh host: {Error}",
                            RunnerId,
                            tests[0].DisplayName,
                            attempt,
                            maxCaptureAttempts,
                            hostLoss);
                        continue;
                    }

                    throw new InvalidOperationException(AppendSinkError(hostLoss));
                }

                IReadOnlyList<ICoverageRunResult> results;
                try
                {
                    results = ReadPerTestCoverageData(tests, confidence);
                }
                catch (InvalidDataException incompleteCapture) when (attempt < maxCaptureAttempts)
                {
                    _logger.LogWarning(
                        "{RunnerId}: Coverage capture for boundary {Boundary} published an incomplete " +
                        "per-test map (attempt {Attempt}/{MaxAttempts}); retrying in a fresh context: " +
                        "{Error} Sink error: {SinkError}",
                        RunnerId,
                        tests[0].DisplayName,
                        attempt,
                        maxCaptureAttempts,
                        incompleteCapture.Message,
                        ReadSinkError() ?? "<none>");
                    continue;
                }
                catch (InvalidDataException incompleteCapture)
                {
                    throw new InvalidDataException(
                        AppendSinkError(incompleteCapture.Message),
                        incompleteCapture);
                }

                DeleteCoverageFile();
                DeleteCoverageMapFile();

                _logger.LogDebug(
                    "{RunnerId}: Captured exact coverage for {TestCount} tests at boundary {Boundary}",
                    RunnerId,
                    results.Count,
                    tests[0].DisplayName);

                return results;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "{RunnerId}: Failed to capture coverage for test boundary {Boundary}",
                RunnerId,
                tests.Count == 0 ? "<empty>" : tests[0].DisplayName);
            throw;
        }
    }

    /// <summary>
    /// The xUnit lifecycle sink deliberately keeps its own failures out of test
    /// results and publishes them through the activation error file. A failed
    /// capture must carry that diagnostic or the campaign reports only the
    /// downstream symptom (missing per-test records).
    /// </summary>
    private string? ReadSinkError()
    {
        try
        {
            return File.Exists(_mutantMapErrorFilePath)
                ? File.ReadAllText(_mutantMapErrorFilePath).Trim()
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private string AppendSinkError(string message)
    {
        var sinkError = ReadSinkError();
        return sinkError is null
            ? message
            : $"{message} The coverage lifecycle sink reported: {sinkError}";
    }

    /// <summary>
    /// Seam for the collectible coverage execution so tests can exercise the
    /// capture retry without a real isolation host process.
    /// </summary>
    internal virtual Task<CollectibleIsolationResponse> ExecuteCoverageContextAsync(
        string assembly,
        IReadOnlyList<string> testUids) =>
        GetOrCreateIsolationClient(assembly).ExecuteAsync(testUids, timeout: null);

    private void WriteMutantMap(IReadOnlyList<IMutant>? mutants)
    {
        DeleteIfExists(_mutantMapAcknowledgementFilePath);
        DeleteIfExists(_mutantMapErrorFilePath);
        _expectedMutantMapAcknowledgement = null;

        if (mutants is null)
        {
            WriteTextAtomically(_mutantMapFilePath, InactiveMutantMapHeader + Environment.NewLine);
            return;
        }

        var assignments = new Dictionary<string, int>(StringComparer.Ordinal);
        var everyTestMutants = mutants.Where(mutant => mutant.AssessingTests.IsEveryTest).ToList();
        if (everyTestMutants.Count > 0)
        {
            if (mutants.Count != 1)
            {
                throw new InvalidOperationException(
                    "An every-test mutant cannot share a mixed-mutant MTP request.");
            }

            lock (_discoveryLock)
            {
                foreach (var testUid in _testsByAssembly.Values.SelectMany(tests => tests).Select(test => test.Uid))
                {
                    assignments[testUid] = mutants[0].Id;
                }
            }
        }
        else
        {
            foreach (var mutant in mutants)
            {
                foreach (var testUid in mutant.AssessingTests.GetIdentifiers())
                {
                    if (testUid.Contains('\t', StringComparison.Ordinal) ||
                        testUid.Contains('\r', StringComparison.Ordinal) ||
                        testUid.Contains('\n', StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Test identifier '{testUid}' cannot be represented by the mutation activation protocol.");
                    }

                    if (assignments.TryGetValue(testUid, out var existingMutantId) &&
                        existingMutantId != mutant.Id)
                    {
                        throw new InvalidOperationException(
                            $"Test '{testUid}' was mapped to more than one mutant in the same MTP request.");
                    }

                    assignments[testUid] = mutant.Id;
                }
            }
        }

        WriteMutantMapAssignments(assignments);
    }

    /// <summary>
    /// Publishes an explicit test-to-mutant assignment map. Wave execution builds these maps
    /// directly: each wave assigns only the next slice of every unresolved mutant's assessing
    /// tests, so one request (one fixture setup) advances many mutants at once while dead
    /// mutants stop consuming test executions.
    /// </summary>
    private void WriteMutantMapAssignments(Dictionary<string, int> assignments)
    {
        if (assignments.Count == 0)
        {
            WriteTextAtomically(_mutantMapFilePath, InactiveMutantMapHeader + Environment.NewLine);
            return;
        }

        // A theory with deferred data enumeration is one discovered test case, but an
        // MTP test host expands it at run time into per-row cases whose identifiers
        // discovery never produced. Those rows can only be activated through their
        // method: each assignment additionally publishes a method-display key, so the
        // xUnit hook can resolve an unknown row to its theory's assigned mutant. A
        // method whose assignments span more than one mutant gets no key at all, so
        // an ambiguous row still fails closed.
        var methodAssignments = new Dictionary<string, int>(StringComparer.Ordinal);
        var ambiguousMethods = new HashSet<string>(StringComparer.Ordinal);
        lock (_discoveryLock)
        {
            foreach (var (testUid, mutantId) in assignments)
            {
                if (!_testDescriptions.TryGetValue(testUid, out var description))
                {
                    continue;
                }

                var methodKey = MethodAssignmentKey(description.Description.Name);
                if (methodKey is null)
                {
                    continue;
                }

                if (methodAssignments.TryGetValue(methodKey, out var existingMutantId) &&
                    existingMutantId != mutantId)
                {
                    ambiguousMethods.Add(methodKey);
                    continue;
                }

                methodAssignments[methodKey] = mutantId;
            }
        }

        foreach (var ambiguousMethod in ambiguousMethods)
        {
            methodAssignments.Remove(ambiguousMethod);
        }

        foreach (var (methodKey, mutantId) in methodAssignments)
        {
            assignments[methodKey] = mutantId;
        }

        var acknowledgement = Guid.NewGuid().ToString("N");
        var lines = new List<string>(assignments.Count + 1)
        {
            ActiveMutantMapHeaderPrefix + acknowledgement,
        };
        lines.AddRange(
            assignments
                .OrderBy(assignment => assignment.Key, StringComparer.Ordinal)
                .Select(assignment => $"{assignment.Value}\t{assignment.Key}"));

        WriteTextAtomically(
            _mutantMapFilePath,
            string.Join(Environment.NewLine, lines) + Environment.NewLine);
        _expectedMutantMapAcknowledgement = acknowledgement;
    }

    /// <summary>
    /// Derives the activation-map key that binds every run-time-expanded row of a
    /// theory to its method: the display name with the argument list removed,
    /// behind a marker that cannot collide with a test identifier (identifiers
    /// containing a tab are refused by the activation protocol). Returns null for
    /// display names the protocol cannot represent.
    /// </summary>
    internal static string? MethodAssignmentKey(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName) ||
            displayName.Contains('\r', StringComparison.Ordinal) ||
            displayName.Contains('\n', StringComparison.Ordinal) ||
            displayName.Contains('\t', StringComparison.Ordinal))
        {
            return null;
        }

        var argumentsStart = displayName.IndexOf('(', StringComparison.Ordinal);
        var methodDisplay = argumentsStart < 0 ? displayName : displayName[..argumentsStart];
        return methodDisplay.Length == 0 ? null : $"method\t{methodDisplay}";
    }

    /// <summary>
    /// Maps run-time-expanded theory rows in MTP results back to their discovered
    /// test case. An MTP host expands a deferred-enumeration theory into per-row
    /// cases with identifiers discovery never produced; a mutant's assessing tests
    /// and Stryker's verdict analysis both speak in discovered identifiers, so an
    /// unmapped failing row would silently lose its kill. A row is mapped only to
    /// an argument-free discovered case whose display name equals the row's
    /// method display; anything unresolvable is preserved untouched.
    /// </summary>
    internal static IReadOnlyCollection<TestNodeUpdate> NormalizeToDiscoveredCases(
        IReadOnlyCollection<TestNodeUpdate> updates,
        IReadOnlyList<TestNode>? discoveredTests)
    {
        if (updates.Count == 0 || discoveredTests is null || discoveredTests.Count == 0)
        {
            return updates;
        }

        var discoveredUids = discoveredTests
            .Select(test => test.Uid)
            .ToHashSet(StringComparer.Ordinal);
        if (updates.All(update => discoveredUids.Contains(update.Node.Uid)))
        {
            return updates;
        }

        var methodCases = new Dictionary<string, string>(StringComparer.Ordinal);
        var ambiguousDisplays = new HashSet<string>(StringComparer.Ordinal);
        foreach (var test in discoveredTests)
        {
            // Only an unexpanded (argument-free) case can own run-time rows.
            if (test.DisplayName.Contains('(', StringComparison.Ordinal))
            {
                continue;
            }

            if (!methodCases.TryAdd(test.DisplayName, test.Uid))
            {
                ambiguousDisplays.Add(test.DisplayName);
            }
        }

        return updates
            .Select(update =>
            {
                if (discoveredUids.Contains(update.Node.Uid))
                {
                    return update;
                }

                var display = update.Node.DisplayName;
                var argumentsStart = display?.IndexOf('(', StringComparison.Ordinal) ?? -1;
                if (argumentsStart <= 0)
                {
                    return update;
                }

                var methodDisplay = display![..argumentsStart];
                return methodCases.TryGetValue(methodDisplay, out var parentUid) &&
                    !ambiguousDisplays.Contains(methodDisplay)
                        ? update with { Node = update.Node with { Uid = parentUid } }
                        : update;
            })
            .ToList();
    }

    private string? ValidateMutantMapAcknowledgement()
    {
        if (_expectedMutantMapAcknowledgement is null)
        {
            return null;
        }

        var expected = _expectedMutantMapAcknowledgement;
        _expectedMutantMapAcknowledgement = null;

        if (File.Exists(_mutantMapErrorFilePath))
        {
            return File.ReadAllText(_mutantMapErrorFilePath).Trim();
        }

        var actual = File.Exists(_mutantMapAcknowledgementFilePath)
            ? File.ReadAllText(_mutantMapAcknowledgementFilePath).Trim()
            : string.Empty;
        return string.Equals(actual, expected, StringComparison.Ordinal)
            ? null
            : "The xUnit mutation activation hook did not acknowledge the current MTP request.";
    }

    private static void WriteTextAtomically(string path, string content)
    {
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, content);

            // The destination can be transiently locked by the test host's reader or an on-close
            // antivirus scan; one refused move must not kill a campaign that is minutes from done.
            const int maxMoveAttempts = 5;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temporaryPath, path, overwrite: true);
                    return;
                }
                catch (Exception ex) when (attempt < maxMoveAttempts && ex is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(40 * attempt);
                }
            }
        }
        finally
        {
            DeleteIfExists(temporaryPath);
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private void WriteMutantIdToFile(int mutantId)
    {
        try
        {
            // Publish the active mutant id as a fixed 4-byte int through a file-backed memory-mapped view.
            // The injected MutantControl maps the same file and reads the id on every IsActive call, so the
            // reused test host always sees the current mutant with no per-call file I/O. Both sides use
            // CreateFromFile with a null map name (file-backed maps work cross-platform, unlike named maps
            // which are Windows-only), and FileShare.ReadWrite lets the host keep the file mapped while we
            // update it between runs.
            using (var stream = new FileStream(_mutantFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite))
            using (var mmf = MemoryMappedFile.CreateFromFile(stream, null, sizeof(int), MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: true))
            using (var accessor = mmf.CreateViewAccessor(0, sizeof(int), MemoryMappedFileAccess.Write))
            {
                accessor.Write(0, mutantId);
                accessor.Flush();
            }

            _logger.LogDebug("{RunnerId}: Wrote mutant ID {MutantId} to memory-mapped file {FilePath}",
                RunnerId, mutantId, _mutantFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{RunnerId}: Failed to write mutant ID to memory-mapped file {FilePath}",
                RunnerId, _mutantFilePath);
        }
    }

    private Dictionary<string, string?> BuildEnvironmentVariables()
    {
        var envVars = new Dictionary<string, string?>
        {
            ["STRYKER_MUTANT_FILE"] = _mutantFilePath,
            ["STRYKER_MUTANT_MAP_FILE"] = _mutantMapFilePath,
            ["STRYKER_MUTANT_MAP_ACK_FILE"] = _mutantMapAcknowledgementFilePath,
            ["STRYKER_MUTANT_MAP_ERROR_FILE"] = _mutantMapErrorFilePath,
        };

        ExternalEnvironmentVariables.Add(envVars);

        // Add coverage filename when in coverage mode (MutantControl will combine with temp path)
        if (_coverageMode)
        {
            envVars["STRYKER_COVERAGE_FILE"] = Path.GetFileName(_coverageFilePath);
            envVars["STRYKER_COVERAGE_MAP_FILE"] = _coverageMapFilePath;
        }

        return envVars;
    }

    /// <summary>
    /// Enables or disables coverage capture mode. When enabled, the test process will track
    /// which mutations are covered and write the data to a file on process exit.
    /// </summary>
    public void SetCoverageMode(bool enabled)
    {
        lock (_serverLock)
        {
            if (_coverageMode == enabled)
            {
                // Already in the desired state; no action needed
                return;
            }

            _coverageMode = enabled;
            _logger.LogDebug("{RunnerId}: Coverage mode {Status}", RunnerId, enabled ? "enabled" : "disabled");

            // Reset servers to apply the new environment variables
            foreach (var server in _assemblyServers.Values)
            {
                server.Dispose();
            }
            _assemblyServers.Clear();
            foreach (var client in _isolationClients.Values)
            {
                client.Dispose();
            }
            _isolationClients.Clear();
        }

        // Clean up any existing coverage file, even when enabling, to ensure we start fresh
        DeleteCoverageFile();
    }

    /// <summary>
    /// Reads coverage data from the coverage file written by the test process.
    /// Returns the covered mutants and static mutants as separate lists.
    /// </summary>
    public (IReadOnlyList<int> CoveredMutants, IReadOnlyList<int> StaticMutants) ReadCoverageData()
    {
        if (!File.Exists(_coverageFilePath))
        {
            _logger.LogDebug("{RunnerId}: Coverage file not found at {Path}", RunnerId, _coverageFilePath);
            return (Array.Empty<int>(), Array.Empty<int>());
        }

        try
        {
            var content = File.ReadAllText(_coverageFilePath).Trim();
            _logger.LogDebug("{RunnerId}: Read coverage data: {Content}", RunnerId, content);

            if (string.IsNullOrEmpty(content))
            {
                return (Array.Empty<int>(), Array.Empty<int>());
            }

            var parts = content.Split(';');
            var coveredMutants = ParseMutantIds(parts.Length > 0 ? parts[0] : string.Empty);
            var staticMutants = ParseMutantIds(parts.Length > 1 ? parts[1] : string.Empty);

            return (coveredMutants, staticMutants);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{RunnerId}: Failed to read coverage file at {Path}", RunnerId, _coverageFilePath);
            return (Array.Empty<int>(), Array.Empty<int>());
        }
    }

    private static IReadOnlyList<int> ParseMutantIds(string idString)
    {
        if (string.IsNullOrWhiteSpace(idString))
        {
            return Array.Empty<int>();
        }

        return idString
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s.Trim(), out var id) ? id : (int?)null)
            .Where(id => id.HasValue)
            .Select(id => id.Value)
            .ToList();
    }

    private void DeleteCoverageFile()
    {
        try
        {
            if (File.Exists(_coverageFilePath))
            {
                File.Delete(_coverageFilePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{RunnerId}: Failed to delete coverage file at {Path}", RunnerId, _coverageFilePath);
        }
    }

    private void DeleteCoverageMapFile()
    {
        try
        {
            DeleteIfExists(_coverageMapFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "{RunnerId}: Failed to delete per-test coverage map at {Path}",
                RunnerId,
                _coverageMapFilePath);
        }
    }

    internal IReadOnlyList<ICoverageRunResult> ReadPerTestCoverageData(
        IReadOnlyList<TestNode> tests,
        CoverageConfidence confidence)
    {
        const string header = "threadway-stryker-coverage-v1";
        if (!File.Exists(_coverageMapFilePath))
        {
            throw new InvalidDataException(
                "The xUnit coverage lifecycle sink did not publish per-test coverage.");
        }

        var lines = File.ReadAllLines(_coverageMapFilePath);
        if (lines.Length == 0 ||
            !string.Equals(lines[0], header, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The per-test coverage map has an invalid header.");
        }

        var snapshots = new Dictionary<string, CoverageSnapshot>(StringComparer.Ordinal);
        foreach (var line in lines.AsSpan(1))
        {
            var columns = line.Split('\t');
            if (columns.Length != 4 || string.IsNullOrWhiteSpace(columns[0]))
            {
                throw new InvalidDataException(
                    "The per-test coverage map contains an invalid record.");
            }

            var snapshot = new CoverageSnapshot(
                ParseCoverageMutantIds(columns[1]),
                ParseCoverageMutantIds(columns[2]),
                ParseCoverageMutantIds(columns[3]));
            if (snapshots.TryGetValue(columns[0], out var existing))
            {
                snapshots[columns[0]] = existing.Merge(snapshot);
            }
            else
            {
                snapshots.Add(columns[0], snapshot);
            }
        }

        MutationCampaignDiagnostics.CoverageMapCaptured(
            RunnerId,
            tests.Count == 0
                ? "<empty>"
                : MicrosoftTestPlatformRunnerPool.GetCoverageBoundary(tests[0]),
            tests.Select(test => test.Uid).ToList(),
            lines.Skip(1).ToList());

        var expectedTestIds = tests.Select(test => test.Uid).ToHashSet(StringComparer.Ordinal);
        var missingTestIds = expectedTestIds.Except(snapshots.Keys, StringComparer.Ordinal).ToList();
        var unexpectedTestIds = snapshots.Keys.Except(expectedTestIds, StringComparer.Ordinal).ToList();
        if (missingTestIds.Count > 0)
        {
            throw new InvalidDataException(
                "The per-test coverage map did not match the requested MTP test cases. " +
                $"Missing: [{string.Join(",", missingTestIds.Take(5))}].");
        }

        if (unexpectedTestIds.Count > 0)
        {
            _logger.LogDebug(
                "{RunnerId}: Ignoring {UnexpectedTestCount} coverage records outside the requested MTP test cases",
                RunnerId,
                unexpectedTestIds.Count);
        }

        var requestedSnapshots = expectedTestIds
            .ToDictionary(testId => testId, testId => snapshots[testId], StringComparer.Ordinal);
        var classStaticMutants = requestedSnapshots.Values
            .SelectMany(snapshot => snapshot.StaticMutants)
            .ToHashSet();
        var classOutsideTestMutants = requestedSnapshots.Values
            .SelectMany(snapshot => snapshot.OutsideTestMutants)
            .ToHashSet();

        return tests
            .Select(test =>
            {
                var snapshot = requestedSnapshots[test.Uid];
                var coveredMutants = snapshot.CoveredMutants
                    .Concat(classStaticMutants)
                    .Concat(classOutsideTestMutants)
                    .Distinct()
                    .ToList();
                return (ICoverageRunResult)CoverageRunResult.Create(
                    test.Uid,
                    confidence,
                    coveredMutants,
                    classStaticMutants,
                    classOutsideTestMutants);
            })
            .ToList();
    }

    private static IReadOnlyList<int> ParseCoverageMutantIds(string value)
    {
        if (value.Length == 0)
        {
            return [];
        }

        var mutantIds = new List<int>();
        foreach (var item in value.Split(','))
        {
            if (!int.TryParse(item, out var mutantId))
            {
                throw new InvalidDataException(
                    "The per-test coverage map contains an invalid mutant identifier.");
            }

            mutantIds.Add(mutantId);
        }

        return mutantIds;
    }

    private sealed record CoverageSnapshot(
        IReadOnlyList<int> CoveredMutants,
        IReadOnlyList<int> StaticMutants,
        IReadOnlyList<int> OutsideTestMutants)
    {
        internal CoverageSnapshot Merge(CoverageSnapshot other) =>
            new(
                CoveredMutants.Concat(other.CoveredMutants).Distinct().ToList(),
                StaticMutants.Concat(other.StaticMutants).Distinct().ToList(),
                OutsideTestMutants.Concat(other.OutsideTestMutants).Distinct().ToList());
    }

    private async Task<AssemblyTestServer> GetOrCreateServerAsync(string assembly)
    {
        AssemblyTestServer? deadServer = null;
        lock (_serverLock)
        {
            if (_assemblyServers.TryGetValue(assembly, out var existing))
            {
                if (existing.IsAlive)
                {
                    return existing;
                }

                // The server process is no longer alive (e.g. it crashed during a previous run).
                // Drop it so a fresh server is started rather than reusing a dead RPC connection,
                // which would fail every subsequent test run instantly.
                _logger.LogDebug("{RunnerId}: Test server for {Assembly} is no longer alive; recreating", RunnerId, assembly);
                _assemblyServers.Remove(assembly);
                deadServer = existing;
            }
        }

        if (deadServer is not null)
        {
            await deadServer.StopAsync(force: true).ConfigureAwait(false);
        }

        var environmentVariables = BuildEnvironmentVariables();
        var server = new AssemblyTestServer(assembly, environmentVariables, _logger, RunnerId, _options);

        var started = await server.StartAsync().ConfigureAwait(false);
        if (!started)
        {
            throw new InvalidOperationException($"Failed to start test server for {assembly}");
        }

        lock (_serverLock)
        {
            _assemblyServers[assembly] = server;
        }

        return server;
    }

    private CollectibleTestIsolationClient GetOrCreateIsolationClient(
        string assembly)
    {
        lock (_serverLock)
        {
            if (_isolationClients.TryGetValue(assembly, out var existing))
            {
                return existing;
            }

            var client = new CollectibleTestIsolationClient(
                assembly,
                BuildEnvironmentVariables(),
                _logger,
                RunnerId);
            _isolationClients.Add(assembly, client);
            return client;
        }
    }

    /// <summary>
    /// Force-stops and removes the server for the given assembly so the next run starts a fresh one.
    /// Used after a run fails because the test host crashed and tore down the RPC connection.
    /// </summary>
    private async Task DiscardServerAsync(string assembly)
    {
        AssemblyTestServer? server;
        lock (_serverLock)
        {
            _assemblyServers.TryGetValue(assembly, out server);
            _assemblyServers.Remove(assembly);
        }

        if (server is not null)
        {
            await server.StopAsync(force: true).ConfigureAwait(false);
        }
    }

    private async Task<bool> DiscoverTestsInternalAsync(string assembly)
    {
        try
        {
            var server = await GetOrCreateServerAsync(assembly).ConfigureAwait(false);
            var tests = await server.DiscoverTestsAsync().ConfigureAwait(false);

            lock (_discoveryLock)
            {
                _testsByAssembly[assembly] = tests;

                foreach (var test in tests.Where(t => !_testDescriptions.ContainsKey(t.Uid)))
                {
                    var mtpTestDescription = new MtpTestDescription(test);
                    _testDescriptions[test.Uid] = mtpTestDescription;
                    _testSet.RegisterTest(mtpTestDescription.Description);
                }
            }

            _logger.LogDebug("{RunnerId}: Discovered {TestCount} tests in {Assembly}", RunnerId, tests.Count, assembly);
            return tests.Count > 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "{RunnerId}: Failed to discover tests in {Assembly}", RunnerId, assembly);
            return false;
        }
    }

    internal List<TestNode>? GetDiscoveredTests(string assembly)
    {
        lock (_discoveryLock)
        {
            return _testsByAssembly.TryGetValue(assembly, out var tests) ? tests : null;
        }
    }

    internal TimeSpan? CalculateAssemblyTimeout(List<TestNode> discoveredTests, ITimeoutValueCalculator timeoutCalc, string assembly, bool serialActivation = false)
    {
        var estimatedTimeMs = (int)discoveredTests
            .Where(t => _testDescriptions.TryGetValue(t.Uid, out _))
            .Sum(t =>
            {
                lock (_discoveryLock)
                {
                    return _testDescriptions.TryGetValue(t.Uid, out var desc)
                        ? desc.InitialRunTime.TotalMilliseconds
                        : 0;
                }
            });

        var timeoutMs = timeoutCalc.CalculateTimeoutValue(estimatedTimeMs);

        // The MTP protocol reports no per-test timing, so InitialRunTime is the initial run's
        // duration smeared evenly across tests: a batch dominated by genuinely slow tests gets a
        // budget that is far too small. Map-activated batches additionally run serially with
        // per-test activation overhead — costs the parallel initial run never paid. A tight
        // budget then misreports slow-but-passing sessions as Timeout in bulk. Grant a floor plus
        // a per-test allowance sized to the execution mode; a genuinely hanging mutant still ends
        // within the floor.
        var floorMs = serialActivation
            ? 30_000 + (500 * discoveredTests.Count)
            : 15_000 + (100 * discoveredTests.Count);
        if (timeoutMs < floorMs)
        {
            timeoutMs = floorMs;
        }

        _logger.LogDebug("{RunnerId}: Using {TimeoutMs} ms as test run timeout for {Assembly}",
            RunnerId, timeoutMs, Path.GetFileName(assembly));

        return TimeSpan.FromMilliseconds(timeoutMs);
    }

    internal async Task HandleAssemblyTimeoutAsync(string assembly, List<TestNode> discoveredTests, List<string> allTimedOutTests)
    {
        _logger.LogDebug("{RunnerId}: Test run timed out for {Assembly}", RunnerId, Path.GetFileName(assembly));

        allTimedOutTests.AddRange(discoveredTests.Select(t => t.Uid));

        AssemblyTestServer? server;
        lock (_serverLock)
        {
            _assemblyServers.TryGetValue(assembly, out server);
            _assemblyServers.Remove(assembly);
        }

        if (server is not null)
        {
            _logger.LogDebug(
                "{RunnerId}: Discarding test server for {Assembly} after timeout",
                RunnerId,
                Path.GetFileName(assembly));
            try
            {
                // Do not eagerly start the replacement while the timed-out
                // mutant is still active. The next mutation writes its own ID
                // before lazily starting a clean application.
                await server.StopAsync(force: true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "{RunnerId}: Failed to discard test server for {Assembly} after timeout",
                    RunnerId,
                    Path.GetFileName(assembly));
            }
        }
    }

    private sealed class TestRunAccumulator
    {
        private readonly List<string> _executedTests = [];
        private readonly List<string> _failedTests = [];
        private readonly List<string> _messages = [];
        private readonly List<string> _errorMessages = [];
        private int _totalDiscoveredTests;
        private int _totalExecutedTests;

        public List<string> TimedOutTests { get; } = [];
        public bool HasTimeout { get; set; }
        public bool HasError { get; private set; }
        public TimeSpan TotalDuration { get; private set; }

        public void Aggregate(TestRunResult result, List<TestNode>? discoveredTests)
        {
            // A crash sentinel (FailingTests == EveryTest, produced only by the TestRunResult(false)
            // path when an assembly run crashes) must NOT be folded into the executed/failed sets:
            // EveryTest.GetIdentifiers() is empty, so doing so would record "every test ran, none
            // failed" and report otherwise-untested mutants as Survived. Flag it as an error instead;
            // RunAllTestsAsync then returns a RuntimeError result so the affected mutants are
            // classified as RuntimeError (excluded from the score) rather than Survived or Killed.
            if (result.FailingTests.IsEveryTest)
            {
                HasError = true;
                if (!string.IsNullOrWhiteSpace(result.ResultMessage))
                {
                    _errorMessages.Add(result.ResultMessage);
                }
                TotalDuration += result.Duration;
                return;
            }

            if (result.ExecutedTests.IsEveryTest)
            {
                // "Every test" is relative to one assembly: expand it to the discovered uids so
                // the aggregated identifier list stays complete when another assembly runs a
                // filtered subset (or is skipped) and the aggregate cannot compress to EveryTest.
                if (discoveredTests is not null)
                {
                    _executedTests.AddRange(discoveredTests.Select(t => t.Uid));
                }
                _totalExecutedTests += discoveredTests?.Count ?? 0;
            }
            else
            {
                var executedIds = result.ExecutedTests.GetIdentifiers().ToList();
                _executedTests.AddRange(executedIds);
                _totalExecutedTests += executedIds.Count;
            }

            _failedTests.AddRange(result.FailingTests.GetIdentifiers());
            TotalDuration += result.Duration;
            _messages.AddRange(result.Messages ?? []);

            if (!string.IsNullOrWhiteSpace(result.ResultMessage))
            {
                _errorMessages.Add(result.ResultMessage);
            }
        }

        public void AddDiscoveredCount(int count) => _totalDiscoveredTests += count;

        public ITestIdentifiers BuildExecutedTests() =>
            _totalDiscoveredTests > 0 && _totalExecutedTests >= _totalDiscoveredTests
                ? TestIdentifierList.EveryTest()
                : new TestIdentifierList(_executedTests);

        public ITestIdentifiers BuildFailedTests() => new TestIdentifierList(_failedTests);

        public ITestIdentifiers BuildTimedOutTests() => new TestIdentifierList(TimedOutTests);

        public string BuildErrorMessage() => string.Join(Environment.NewLine, _errorMessages);

        public IEnumerable<string> Messages => _messages;
    }

    internal async Task<ITestRunResult> RunAllTestsAsync(
        IReadOnlyList<string> assemblies,
        int mutantId,
        IReadOnlyList<IMutant>? mutants,
        TestUpdateHandler? update,
        ITimeoutValueCalculator? timeoutCalc = null,
        Func<TestNode, bool>? testUidFilter = null,
        bool useCollectibleIsolation = false,
        bool wholeSessionActivation = false,
        Func<TestNodeUpdate, bool>? bailPredicate = null)
    {
        try
        {
            // A collectible isolation run assesses exactly one mutant, and that mutant
            // requires activation outside test lifecycles: during static initialization
            // and fixture construction, which execute between the xUnit `ITestStarting`
            // and `ITestFinished` windows. The per-test activation map would reset the
            // control channel to -1 in those gaps and silently deactivate the mutation
            // in the very code that covers it (a false Survived). Publishing an inactive
            // map keeps the pre-loaded mutant id active for the whole context, matching
            // the whole-session activation stock Stryker uses for single-mutant runs.
            // Ordinary per-mutant sessions request the same whole-session activation
            // explicitly: with the mutant active for the whole request no per-test
            // switching is needed and the warm host keeps xUnit's normal parallelism.
            var inactiveMap = useCollectibleIsolation || wholeSessionActivation;
            WriteMutantMap(inactiveMap ? null : mutants);
            WriteMutantIdToFile(mutantId);

            var accumulator = new TestRunAccumulator();

            foreach (var assembly in assemblies)
            {
                var (result, timedOut, discoveredTests) =
                    useCollectibleIsolation
                        ? await RunAssemblyTestsInCollectibleContextAsync(
                            assembly,
                            timeoutCalc,
                            testUidFilter).ConfigureAwait(false)
                        : await RunAssemblyTestsAsync(
                            assembly,
                            timeoutCalc,
                            testUidFilter,
                            serialActivation: mutants is not null && !inactiveMap,
                            bailPredicate: bailPredicate).ConfigureAwait(false);

                if (discoveredTests is not null)
                {
                    accumulator.AddDiscoveredCount(discoveredTests.Count);

                    if (timedOut)
                    {
                        accumulator.HasTimeout = true;
                        if (useCollectibleIsolation)
                        {
                            accumulator.TimedOutTests.AddRange(
                                testUidFilter is null
                                    ? discoveredTests.Select(test => test.Uid)
                                    : discoveredTests
                                        .Where(testUidFilter)
                                        .Select(test => test.Uid));
                        }
                        else
                        {
                            await HandleAssemblyTimeoutAsync(
                                assembly,
                                discoveredTests,
                                accumulator.TimedOutTests).ConfigureAwait(false);
                        }
                    }
                }

                if (result is not null)
                {
                    accumulator.Aggregate(result, discoveredTests);
                }
            }

            var executedTests = accumulator.BuildExecutedTests();
            var failedTestIds = accumulator.BuildFailedTests();
            var timedOutTestIds = accumulator.BuildTimedOutTests();

            IEnumerable<MtpTestDescription> testDescriptionValues;
            lock (_discoveryLock)
            {
                testDescriptionValues = _testDescriptions.Values.ToList();
            }

            var activationError = ValidateMutantMapAcknowledgement();
            if (!string.IsNullOrWhiteSpace(activationError))
            {
                _logger.LogError(
                    "{RunnerId}: Mutation activation protocol failed: {ActivationError}",
                    RunnerId,
                    activationError);
                return TestRunResult.RuntimeError(
                    testDescriptionValues,
                    executedTests,
                    TestIdentifierList.NoTest(),
                    TestIdentifierList.NoTest(),
                    activationError,
                    accumulator.Messages,
                    accumulator.TotalDuration);
            }

            if (update is not null && mutants is not null)
            {
                update.Invoke(mutants, failedTestIds, executedTests, timedOutTestIds);
            }

            if (accumulator.HasError)
            {
                // The test host crashed (e.g. a mutation caused a fatal fault). Signal a runtime error
                // so the affected mutants are classified as RuntimeError (excluded from the score)
                // rather than reported as survived or logged as a test failure.
                _logger.LogDebug("{RunnerId}: A test host crashed during this run; reporting a runtime error for the affected mutant(s).", RunnerId);
                return TestRunResult.RuntimeError(
                    testDescriptionValues,
                    executedTests,
                    failedTestIds,
                    timedOutTestIds,
                    accumulator.BuildErrorMessage(),
                    accumulator.Messages,
                    accumulator.TotalDuration);
            }

            if (accumulator.HasTimeout)
            {
                return TestRunResult.TimedOut(
                    testDescriptionValues,
                    executedTests,
                    failedTestIds,
                    timedOutTestIds,
                    accumulator.BuildErrorMessage(),
                    accumulator.Messages,
                    accumulator.TotalDuration);
            }

            return new TestRunResult(
                testDescriptionValues,
                executedTests,
                failedTestIds,
                timedOutTestIds,
                accumulator.BuildErrorMessage(),
                accumulator.Messages,
                accumulator.TotalDuration);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "{RunnerId}: Failed to run tests for mutant ID {MutantId}", RunnerId, mutantId);
            IEnumerable<MtpTestDescription> testDescriptionValues;
            lock (_discoveryLock)
            {
                testDescriptionValues = _testDescriptions.Values.ToList();
            }

            return TestRunResult.RuntimeError(
                testDescriptionValues,
                TestIdentifierList.NoTest(),
                TestIdentifierList.NoTest(),
                TestIdentifierList.NoTest(),
                ex.Message,
                [],
                TimeSpan.Zero);
        }
        finally
        {
            // A missing xUnit finish message must not leave an ordinary
            // mutation active while the persistent host waits for its next run.
            WriteMutantIdToFile(-1);
        }
    }

    internal virtual async Task<(
        TestRunResult? Result,
        bool TimedOut,
        List<TestNode>? DiscoveredTests)> RunAssemblyTestsInCollectibleContextAsync(
            string assembly,
            ITimeoutValueCalculator? timeoutCalc,
            Func<TestNode, bool>? testUidFilter = null)
    {
        if (!File.Exists(assembly))
        {
            return (null, false, null);
        }

        var discoveredTests = GetDiscoveredTests(assembly);
        if (discoveredTests is null)
        {
            return (
                new TestRunResult(
                    false,
                    $"No discovered tests were available for '{assembly}'."),
                false,
                null);
        }

        var testsToRun = testUidFilter is null
            ? discoveredTests
            : discoveredTests.Where(testUidFilter).ToList();
        if (testUidFilter is not null && testsToRun.Count == 0)
        {
            return (
                BuildCollectibleTestRunResult(
                    [],
                    discoveredTests.Count,
                    TimeSpan.Zero),
                false,
                discoveredTests);
        }

        TimeSpan? timeout = timeoutCalc is null
            ? null
            : TimeSpan.FromMilliseconds(
                timeoutCalc.CalculateTimeoutValue(
                    (int)testsToRun.Sum(test =>
                        _testDescriptions.TryGetValue(test.Uid, out var description)
                            ? description.InitialRunTime.TotalMilliseconds
                            : 0)));
        var execution = await GetOrCreateIsolationClient(assembly)
            .ExecuteAsync(
                testsToRun.Select(test => test.Uid).ToList(),
                timeout)
            .ConfigureAwait(false);
        if (execution.SessionTimedOut)
        {
            return (
                BuildCollectibleTestRunResult(
                    [],
                    discoveredTests.Count,
                    TimeSpan.FromTicks(execution.DurationTicks)),
                true,
                discoveredTests);
        }
        if (!string.IsNullOrWhiteSpace(execution.Error) || !execution.Unloaded)
        {
            return (
                new TestRunResult(
                    false,
                    execution.Error ??
                    "The collectible test load context did not unload."),
                false,
                discoveredTests);
        }

        return (
            BuildCollectibleTestRunResult(
                execution.Tests,
                discoveredTests.Count,
                TimeSpan.FromTicks(execution.DurationTicks)),
            false,
            discoveredTests);
    }

    private TestRunResult BuildCollectibleTestRunResult(
        IReadOnlyCollection<CollectibleIsolationTestResult> testResults,
        int totalDiscoveredTests,
        TimeSpan duration)
    {
        var resultsByTest = testResults
            .GroupBy(result => result.TestCaseId, StringComparer.Ordinal)
            .ToList();
        var executedIds = resultsByTest
            .Select(result => result.Key)
            .ToList();
        var failedIds = resultsByTest
            .Where(results => results.Any(result =>
                string.Equals(result.State, "failed", StringComparison.Ordinal)))
            .Select(results => results.Key)
            .ToList();
        var messages = testResults
            .Where(result => !string.IsNullOrWhiteSpace(result.Message))
            .Select(result => result.Message!)
            .ToList();
        var executedTests =
            totalDiscoveredTests > 0 && executedIds.Count >= totalDiscoveredTests
                ? TestIdentifierList.EveryTest()
                : new TestIdentifierList(executedIds);

        IEnumerable<MtpTestDescription> testDescriptionValues;
        lock (_discoveryLock)
        {
            testDescriptionValues = _testDescriptions.Values.ToList();
        }

        return new TestRunResult(
            testDescriptionValues,
            executedTests,
            new TestIdentifierList(failedIds),
            TestIdentifierList.NoTest(),
            string.Join(Environment.NewLine, messages),
            messages,
            duration);
    }

    internal virtual async Task<(TestRunResult? Result, bool TimedOut, List<TestNode>? DiscoveredTests)> RunAssemblyTestsAsync(
        string assembly,
        ITimeoutValueCalculator? timeoutCalc,
        Func<TestNode, bool>? testUidFilter = null,
        bool serialActivation = false,
        Func<TestNodeUpdate, bool>? bailPredicate = null)
    {
        if (!File.Exists(assembly))
        {
            return (null, false, null);
        }

        var discoveredTests = GetDiscoveredTests(assembly);

        TimeSpan? timeout = null;
        if (timeoutCalc is not null && discoveredTests is not null)
        {
            // Base the timeout on the tests that will actually run, not the full suite
            var testsToRun = testUidFilter is null
                ? discoveredTests
                : discoveredTests.Where(testUidFilter).ToList();
            timeout = CalculateAssemblyTimeout(testsToRun, timeoutCalc, assembly, serialActivation);
        }

        var (testResults, timedOut) = await RunAssemblyTestsInternalAsync(assembly, testUidFilter, timeout, bailPredicate).ConfigureAwait(false);

        return (testResults as TestRunResult, timedOut, discoveredTests);
    }

    internal virtual async Task<(ITestRunResult Result, bool TimedOut)> RunAssemblyTestsInternalAsync(
        string assembly,
        Func<TestNode, bool>? testUidFilter,
        TimeSpan? timeout = null,
        Func<TestNodeUpdate, bool>? bailPredicate = null)
    {
        // A crashed test host tears down the RPC connection, so the run throws (rather than timing out).
        // Retry once on a freshly started server: a crash caused by a *previous* mutant then self-heals
        // for the current mutant instead of corrupting its result.
        const int maxRunAttempts = 2;
        Exception? lastRunException = null;

        for (var attempt = 1; attempt <= maxRunAttempts; attempt++)
        {
            AssemblyTestServer server;
            try
            {
                // Get or create the server for this assembly (reuses an existing, live server)
                server = await GetOrCreateServerAsync(assembly).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The server could not be started at all; retrying immediately would not help.
                return (new TestRunResult(false, ex.Message), false);
            }

            var startTime = DateTime.UtcNow;
            try
            {
                List<TestNode>? tests = null;
                lock (_discoveryLock)
                {
                    if (_testsByAssembly.TryGetValue(assembly, out var assemblyTests))
                    {
                        tests = assemblyTests;
                    }
                }

                var testsToRun = tests?.Where(t => testUidFilter is null || testUidFilter(t)).ToArray();

                // A filter matching no test in this assembly means the mutant is
                // covered by tests in another assembly only. Sending an empty
                // test list would make MTP run the whole assembly.
                if (testUidFilter is not null && testsToRun is { Length: 0 })
                {
                    _logger.LogDebug(
                        "{RunnerId}: No covering tests in {Assembly}; skipping test run",
                        RunnerId,
                        Path.GetFileName(assembly));
                    return (BuildTestRunResult([], tests?.Count ?? 0, TimeSpan.Zero), false);
                }

                var (testResults, timedOut) = await server.RunTestsAsync(testsToRun, timeout, bailPredicate).ConfigureAwait(false);

                var duration = DateTime.UtcNow - startTime;
                var result = BuildTestRunResult(
                    NormalizeToDiscoveredCases(testResults, tests),
                    tests?.Count ?? 0,
                    duration);

                return (result, timedOut);
            }
            catch (Exception ex)
            {
                lastRunException = ex;
                _logger.LogDebug(ex, "{RunnerId}: Test run for {Assembly} failed on attempt {Attempt}/{MaxAttempts}; discarding crashed server",
                    RunnerId, Path.GetFileName(assembly), attempt, maxRunAttempts);

                // The server most likely crashed; drop it so the next attempt starts a fresh one.
                await DiscardServerAsync(assembly).ConfigureAwait(false);
            }
        }

        // Every attempt failed. Return the crash sentinel; the accumulator recognises it and flags the
        // run as crashed, so the affected mutants are reported as RuntimeError rather than Survived.
        return (new TestRunResult(false, lastRunException!.Message), false);
    }

    /// <summary>
    /// Maps a list of <see cref="TestNodeUpdate"/>s returned by the MTP server
    /// to a <see cref="TestRunResult"/>. Exposed for unit testing.
    /// </summary>
    /// <remarks>
    /// Classification of execution states goes through <see cref="TestNodeStates"/>
    /// so that failure attribution (the bug this adapter originally had) stays in
    /// one place:
    /// <list type="bullet">
    ///   <item><description><c>failed</c>/<c>error</c>/<c>cancelled</c> → failing tests (mutant killed)</description></item>
    ///   <item><description><c>timed-out</c> → timed-out tests (mutant timeout)</description></item>
    ///   <item><description><c>passed</c>/<c>skipped</c> → executed but neither failing nor timed-out</description></item>
    ///   <item><description><c>in-progress</c>/<c>discovered</c> → excluded from executed tests</description></item>
    /// </list>
    /// </remarks>
    internal TestRunResult BuildTestRunResult(
        IReadOnlyCollection<TestNodeUpdate> testResults,
        int totalDiscoveredTests,
        TimeSpan duration)
    {
        var finishedTests = testResults
            .Where(x => TestNodeStates.IsFinished(x.Node.ExecutionState))
            .ToList();

        var failedTests = finishedTests
            .Where(x => TestNodeStates.IsFailure(x.Node.ExecutionState))
            .Select(x => x.Node.Uid)
            .ToList();

        var timedOutTests = finishedTests
            .Where(x => TestNodeStates.IsTimeout(x.Node.ExecutionState))
            .Select(x => x.Node.Uid)
            .ToList();

        lock (_discoveryLock)
        {
            // MTP doesn't report per-test timing, so approximate with the average
            var perTestDuration = finishedTests.Count > 0
                ? TimeSpan.FromTicks(duration.Ticks / finishedTests.Count)
                : TimeSpan.Zero;

            foreach (var testResult in finishedTests.Where(tr => _testDescriptions.ContainsKey(tr.Node.Uid)))
            {
                var testDescription = _testDescriptions[testResult.Node.Uid];
                testDescription.RegisterInitialTestResult(new MtpTestResult(perTestDuration));
            }
        }

        var errorMessagesStr = string.Join(Environment.NewLine,
            finishedTests
                .Where(x => TestNodeStates.IsFailure(x.Node.ExecutionState)
                         || TestNodeStates.IsTimeout(x.Node.ExecutionState))
                .Select(x => $"{x.Node.DisplayName}{Environment.NewLine}{Environment.NewLine}State: {x.Node.ExecutionState}"));

        var messages = finishedTests.Select(x =>
            $"{x.Node.DisplayName}{Environment.NewLine}{Environment.NewLine}State: {x.Node.ExecutionState}");

        var executedTestCount = finishedTests.Count;
        var executedTests = totalDiscoveredTests > 0 && executedTestCount >= totalDiscoveredTests
            ? TestIdentifierList.EveryTest()
            : new TestIdentifierList(finishedTests.Select(x => x.Node.Uid));

        var failedTestIds = new TestIdentifierList(failedTests);
        var timedOutTestIds = timedOutTests.Count == 0
            ? TestIdentifierList.NoTest()
            : new TestIdentifierList(timedOutTests);

        IEnumerable<MtpTestDescription> testDescriptionValues;
        lock (_discoveryLock)
        {
            testDescriptionValues = _testDescriptions.Values.ToList();
        }

        return new TestRunResult(
            testDescriptionValues,
            executedTests,
            failedTestIds,
            timedOutTestIds,
            errorMessagesStr,
            messages,
            duration);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            lock (_serverLock)
            {
                foreach (var server in _assemblyServers.Values)
                {
                    server.Dispose();
                }
                _assemblyServers.Clear();
                foreach (var client in _isolationClients.Values)
                {
                    client.Dispose();
                }
                _isolationClients.Clear();
            }

            // Clean up temp files
            try
            {
                if (File.Exists(_mutantFilePath))
                {
                    File.Delete(_mutantFilePath);
                }
                DeleteIfExists(_mutantMapFilePath);
                DeleteIfExists(_mutantMapAcknowledgementFilePath);
                DeleteIfExists(_mutantMapErrorFilePath);
                if (File.Exists(_coverageFilePath))
                {
                    File.Delete(_coverageFilePath);
                }
                DeleteIfExists(_coverageMapFilePath);
            }
            catch (Exception ex)
            {
                // Ignore cleanup errors
                _logger.LogWarning(ex, "{RunnerId}: Failed to clean up temp files", RunnerId);
            }
        }
        _disposed = true;
    }
}
