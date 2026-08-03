using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Stryker.Abstractions;
using Stryker.Abstractions.Options;
using Stryker.Abstractions.Testing;
using Stryker.TestRunner.MicrosoftTestPlatform.Models;
using Stryker.TestRunner.Results;
using Stryker.TestRunner.Tests;
using static Stryker.Abstractions.Testing.ITestRunner;

namespace Stryker.TestRunner.MicrosoftTestPlatform;

internal sealed class CoverageLifecycleSinkUnavailableException(string message)
    : Exception(message);

/// <summary>
/// Individual test runner instance that handles test execution with mutation-specific
/// environment variables. Used by MicrosoftTestPlatformRunnerPool.
/// Maintains persistent test server connections per assembly to reduce process startup overhead.
/// Uses file-based mutant control to allow changing the active mutant without restarting processes.
/// Ordinary mutants advance through parallel waves using a test-case activation map consumed by
/// the test framework's synchronous xUnit lifecycle sink. Overlapping tests serve their mutants
/// in successive waves. Mutants that need static-state isolation execute in fresh test
/// applications, see <see cref="RequiresProcessIsolation"/>.
/// </summary>
public class SingleMicrosoftTestPlatformRunner : IDisposable
{
    private const int MaximumWaveAssignments = 64;
    private const int InitialFreshProcessPriorityTests = 8;
    private const string IsolationTestPriorityFileVariable =
        "STRYKER_MTP_ISOLATION_TEST_PRIORITY_FILE";
    internal const string IsolationMutationProfileFileVariable =
        "STRYKER_MTP_ISOLATION_MUTATION_PROFILE_FILE";
    private static readonly ConcurrentDictionary<string, int> IsolationKillHistory =
        new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> OrdinaryTimeoutHistory =
        new(StringComparer.Ordinal);
    private const string InactiveMutantMapHeader = "stryker-mtp-activation-map-v1\toff";
    // Parallel multiplexed sessions: the injected MutantControl resolves each test's assigned
    // mutant through xUnit's ambient TestContext and acknowledges this map itself, so a
    // multi-mutant request keeps the host's normal test parallelism.
    private const string ActiveParallelMutantMapHeaderPrefix = "stryker-mtp-activation-map-v1\tactive-parallel\t";
    private const string ActiveTestJournalHeaderPrefix = "stryker-mtp-active-tests-v1\t";

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
    private readonly string _activeTestJournalFilePath;
    private readonly string _coverageFilePath;
    private readonly string _coverageMapFilePath;
    private readonly IReadOnlyDictionary<string, int> _configuredIsolationPriorities;
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>>
        _configuredIsolationMutationPriorities;
    private readonly IStrykerOptions? _options;

    private string? _expectedMutantMapAcknowledgement;

    private readonly Dictionary<string, AssemblyTestServer> _assemblyServers = new();
    private readonly Dictionary<string, CollectibleTestIsolationClient> _isolationClients = new();
    private readonly object _serverLock = new();
    private bool _disposed;
    private bool _coverageMode;
    private bool _perTestCoverageMode;

    private string RunnerId => $"MtpRunner-{_id}";
    internal string MutantFilePath => _mutantFilePath;
    internal string MutantMapFilePath => _mutantMapFilePath;
    internal string MutantMapAcknowledgementFilePath => _mutantMapAcknowledgementFilePath;
    internal string MutantMapErrorFilePath => _mutantMapErrorFilePath;
    internal string ActiveTestJournalFilePath => _activeTestJournalFilePath;
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
        _configuredIsolationPriorities = LoadIsolationTestPriorities(
            Environment.GetEnvironmentVariable(IsolationTestPriorityFileVariable));
        _configuredIsolationMutationPriorities = LoadIsolationMutationPriorities(
            Environment.GetEnvironmentVariable(IsolationMutationProfileFileVariable));

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
        _activeTestJournalFilePath = Path.Combine(Path.GetTempPath(), $"stryker-active-tests-{fileToken}.txt");
        _coverageFilePath = Path.Combine(Path.GetTempPath(), $"stryker-coverage-{fileToken}.txt");
        _coverageMapFilePath = Path.Combine(Path.GetTempPath(), $"stryker-coverage-map-{fileToken}.txt");

        // Initialize with no active mutation
        WriteMutantIdToFile(-1);
        WriteInactiveMutantMap();
    }

    private void WriteInactiveMutantMap()
    {
        DeleteIfExists(_mutantMapAcknowledgementFilePath);
        DeleteIfExists(_mutantMapErrorFilePath);
        _expectedMutantMapAcknowledgement = null;
        WriteTextAtomically(_mutantMapFilePath, InactiveMutantMapHeader + Environment.NewLine);
        DeleteIfExists(_activeTestJournalFilePath);
    }

    /// <summary>
    /// Publishes the test-to-mutant assignments of a packed parallel session. A theory
    /// with deferred data enumeration is one discovered test case, but an MTP host
    /// expands it at run time into per-row cases whose identifiers discovery never
    /// produced; each assignment therefore additionally publishes a method-display key
    /// so the injected helper can resolve an unknown row to its theory's assigned
    /// mutant. A method whose assignments span more than one mutant gets no key, so an
    /// ambiguous row still fails closed.
    /// </summary>
    private Dictionary<string, int> WriteParallelMutantMap(IReadOnlyList<IMutant> mutants)
    {
        var assignments = new Dictionary<string, int>(StringComparer.Ordinal);
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

        return WriteParallelMutantMap(assignments);
    }

    private Dictionary<string, int> WriteParallelMutantMap(
        IReadOnlyDictionary<string, int> requestedAssignments)
    {
        DeleteIfExists(_mutantMapAcknowledgementFilePath);
        DeleteIfExists(_mutantMapErrorFilePath);

        var assignments = new Dictionary<string, int>(requestedAssignments, StringComparer.Ordinal);
        foreach (var testUid in assignments.Keys)
        {
            if (testUid.Contains('\t', StringComparison.Ordinal) ||
                testUid.Contains('\r', StringComparison.Ordinal) ||
                testUid.Contains('\n', StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Test identifier '{testUid}' cannot be represented by the mutation activation protocol.");
            }
        }

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
            ActiveParallelMutantMapHeaderPrefix + acknowledgement,
        };
        lines.AddRange(
            assignments
                .OrderBy(assignment => assignment.Key, StringComparer.Ordinal)
                .Select(assignment => $"{assignment.Value}\t{assignment.Key}"));

        WriteTextAtomically(
            _mutantMapFilePath,
            string.Join(Environment.NewLine, lines) + Environment.NewLine);
        WriteTextAtomically(
            _activeTestJournalFilePath,
            ActiveTestJournalHeaderPrefix + acknowledgement + Environment.NewLine);
        _expectedMutantMapAcknowledgement = acknowledgement;
        return assignments;
    }

    /// <summary>
    /// Derives the activation-map key that binds every run-time-expanded row of a
    /// theory to its method: the display name with the argument list removed, behind a
    /// marker that cannot collide with a test identifier (identifiers containing a tab
    /// are refused by the activation protocol). Returns null for display names the
    /// protocol cannot represent.
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
            : "The injected mutation activation helper did not acknowledge the current MTP request.";
    }

    public virtual Task<bool> DiscoverTestsAsync(string assembly)
    {
        return DiscoverTestsInternalAsync(assembly);
    }

    public virtual Task<ITestRunResult> InitialTestAsync(IProjectAndTests project)
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

        // An all-isolation group pays one fresh process and uses one disjoint activation map.
        // Ordinary groups may contain overlapping assessing sets; the wave runner below assigns
        // a contested test to one mutant at a time across successive warm-host requests.
        var packedIsolation = mutants.Count > 1 && mutants.All(RequiresProcessIsolation);
        if (mutants.Count > 1 && !mutants.Any(RequiresProcessIsolation))
        {
            return await TestOrdinaryMutantsInWavesAsync(
                assemblies,
                mutants,
                update,
                timeoutCalc).ConfigureAwait(false);
        }

        if (packedIsolation)
        {
            var packedAssignments = WriteParallelMutantMap(mutants);
            try
            {
                return await RunAllTestsAsync(
                    assemblies,
                    mutantId: -1,
                    mutants,
                    update,
                    timeoutCalc,
                    BuildTestUidFilter(mutants),
                    useFreshProcess: packedIsolation,
                    packedAssignments: packedAssignments).ConfigureAwait(false);
            }
            finally
            {
                WriteInactiveMutantMap();
            }
        }

        // One mutant, one session: each mutant is activated for a whole run through the
        // control file and its covering tests execute with the framework's normal
        // parallelism - stock's exact semantics on a persistent host. A mutant whose
        // activation must precede static initialization runs in a dedicated fresh process
        // instead of the warm host. A caller-supplied group is simply processed in order;
        // its verdicts are reported per mutant through the update handler.
        ITestRunResult? lastResult = null;
        foreach (var mutant in mutants)
        {
            var testUidFilter = BuildTestUidFilter([mutant]);

            if (_logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
            {
                _logger.LogDebug(
                    "{RunnerId}: Testing mutant {MutantId} ({Mode}) against {TestScope}",
                    RunnerId,
                    mutant.Id,
                    RequiresProcessIsolation(mutant) ? "fresh process" : "warm host",
                    testUidFilter is null ? "all tests" : "covering tests only");
            }

            // A session that dies with a runtime issue must not hand the mutant a
            // terminal RuntimeError verdict on one attempt. The retry uses a pristine
            // process and lets the bounded assessing set complete: first-failure bail
            // cancels the MTP request before the host acknowledges completion, and
            // repeating that cancellation race would not provide independent evidence.
            // A mutation that kills even the pristine process without bail fails both
            // attempts and keeps its RuntimeError. A timeout is a verdict of its own
            // and is never retried.
            const int maxAttempts = 2;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                lastResult = await RunAllTestsAsync(
                    assemblies,
                    mutant.Id,
                    [mutant],
                    update,
                    timeoutCalc,
                    testUidFilter,
                    useFreshProcess: RequiresProcessIsolation(mutant) || attempt > 1,
                    bailPredicate: CreateSingleMutantBailPredicate(isRuntimeRetry: attempt > 1))
                    .ConfigureAwait(false);
                if (!lastResult.SessionHadRuntimeIssue || attempt == maxAttempts)
                {
                    break;
                }

                _logger.LogWarning(
                    "{RunnerId}: Mutant {MutantId} lost its test host " +
                    "(attempt {Attempt}/{MaxAttempts}); retrying in a fresh process",
                    RunnerId,
                    mutant.Id,
                    attempt,
                    maxAttempts);
            }
        }

        return lastResult!;
    }

    /// <summary>
    /// Advances an ordinary batch through small parallel waves. Each wave assigns a test and its
    /// run-time expansion family to at most one mutant, so overlapping coverage is served across
    /// later requests instead of fragmenting the campaign into thousands of groups. Most killed
    /// mutants resolve in the first seven test executions. A wave request is bounded so an
    /// unhealthy session can send only its assigned mutants to individual confirmation while
    /// unrelated mutants continue in later waves. Exact lifecycle-bounded coverage has already
    /// routed static and pre-test paths to isolation; only a lost-host retry needs a pristine
    /// process.
    /// </summary>
    private async Task<ITestRunResult> TestOrdinaryMutantsInWavesAsync(
        IReadOnlyList<string> assemblies,
        IReadOnlyList<IMutant> mutants,
        TestUpdateHandler? update,
        ITimeoutValueCalculator? timeoutCalc)
    {
        var states = mutants
            .Select(CreateMutantWaveState)
            .ToList();
        var stopwatch = Stopwatch.StartNew();
        var waveCount = 0;
        var waveTestCount = 0;
        var confirmationCount = 0;
        var activationFamilies = GetWaveActivationFamilies();
        var maximumWaveAssignments = MaximumWaveAssignments;

        // Cheap early waves maximize first-kill throughput. Later waves keep multiplexing the
        // exact remaining coverage instead of collapsing thousands of unresolved mutants into
        // one whole-session request apiece. A survivor is reported only after every assessing
        // test actually executed while attributed to that mutant.
        var sliceSize = 1;
        while (true)
        {
            var unresolved = states
                .Where(state => state.Unresolved && state.Remaining.Count > 0)
                .ToList();
            if (unresolved.Count == 0)
            {
                break;
            }

            var requestedAssignments = BuildWaveAssignments(
                unresolved.Select(state =>
                    (state.Mutant.Id, (IReadOnlyList<string>)state.Remaining)),
                sliceSize,
                testUid => activationFamilies.TryGetValue(testUid, out var family)
                    ? family
                    : null,
                maximumWaveAssignments,
                OrdinaryTimeoutHistory.ContainsKey);

            if (requestedAssignments.Count == 0)
            {
                break;
            }

            waveCount++;
            waveTestCount += requestedAssignments.Count;
            var assignedMutantIds = requestedAssignments.Values.ToHashSet();
            var outcome = await RunMutationWaveAsync(
                assemblies,
                unresolved
                    .Where(state => assignedMutantIds.Contains(state.Mutant.Id))
                    .Select(state => state.Mutant)
                    .ToList(),
                requestedAssignments,
                timeoutCalc).ConfigureAwait(false);

            foreach (var state in unresolved)
            {
                if (outcome.ExecutedByMutant.TryGetValue(state.Mutant.Id, out var executed))
                {
                    state.Executed.UnionWith(executed);
                    state.Remaining.RemoveAll(executed.Contains);
                    state.UnlockFallbackAfterPriorityPasses();
                }

                if (outcome.FailedByMutant.TryGetValue(state.Mutant.Id, out var failed) && failed.Count > 0)
                {
                    state.Failed.UnionWith(failed);
                    state.Unresolved = false;
                }

                if (outcome.TimedOutByMutant.TryGetValue(state.Mutant.Id, out var timedOut) && timedOut.Count > 0)
                {
                    foreach (var testUid in timedOut)
                    {
                        OrdinaryTimeoutHistory.TryAdd(testUid, 0);
                    }
                    state.TimedOut.UnionWith(timedOut);
                    state.Unresolved = false;
                }
            }

            foreach (var state in unresolved.Where(state => state.Unresolved))
            {
                DeprioritizeKnownTimeoutTests(
                    state.Remaining,
                    OrdinaryTimeoutHistory.ContainsKey);
            }

            var waveMadeProgress = outcome.ExecutedByMutant.Count > 0 ||
                outcome.FailedByMutant.Count > 0 ||
                outcome.TimedOutByMutant.Count > 0;
            var fallbackMutantIds = GetWaveFallbackMutantIds(
                requestedAssignments,
                outcome.TimedOut,
                outcome.HadRuntimeIssue,
                outcome.TimedOutByMutant.Keys,
                waveMadeProgress);
            if (fallbackMutantIds.Count > 0 && requestedAssignments.Count > 1)
            {
                // An unattributed timeout identifies a bad request, not every mutant in it.
                // Retry a smaller prefix until the failing work is isolated; only a single-test
                // request is allowed to fall back to individual confirmation.
                maximumWaveAssignments = Math.Max(1, requestedAssignments.Count / 2);
                sliceSize = 1;
                continue;
            }

            foreach (var state in unresolved.Where(state =>
                         state.Unresolved && fallbackMutantIds.Contains(state.Mutant.Id)))
            {
                state.RequiresConfirmation = true;
                state.Unresolved = false;
            }

            sliceSize = Math.Min(sliceSize * 2, 16);
            maximumWaveAssignments = Math.Min(
                maximumWaveAssignments * 2,
                MaximumWaveAssignments);
        }

        foreach (var state in states.Where(candidate => candidate.RequiresConfirmation))
        {
            confirmationCount++;
            ITestRunResult? result = null;
            const int maxAttempts = 2;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                result = await RunAllTestsAsync(
                    assemblies,
                    state.Mutant.Id,
                    [state.Mutant],
                    update: null,
                    timeoutCalc,
                    BuildTestUidFilter([state.Mutant]),
                    useFreshProcess: UseFreshProcessForOrdinaryConfirmation(attempt),
                    bailPredicate: CreateSingleMutantBailPredicate(isRuntimeRetry: attempt > 1))
                    .ConfigureAwait(false);
                if (!result.SessionHadRuntimeIssue || attempt == maxAttempts)
                {
                    break;
                }
            }

            var confirmedResult = result!;
            update?.Invoke(
                [state.Mutant],
                confirmedResult.FailingTests,
                confirmedResult.ExecutedTests,
                confirmedResult.TimedOutTests);
            if (update is null || confirmedResult.SessionTimedOut || confirmedResult.SessionHadRuntimeIssue)
            {
                state.Mutant.AnalyzeTestRun(
                    confirmedResult.FailingTests,
                    confirmedResult.ExecutedTests,
                    confirmedResult.TimedOutTests,
                    confirmedResult.SessionTimedOut,
                    confirmedResult.SessionHadRuntimeIssue);
            }

            state.Reported = true;
        }

        foreach (var state in states.Where(candidate => !candidate.Reported))
        {
            var executed = new TestIdentifierList(state.Executed);
            var failed = new TestIdentifierList(state.Failed);
            var timedOut = new TestIdentifierList(state.TimedOut);
            update?.Invoke([state.Mutant], failed, executed, timedOut);
            if (update is null)
            {
                state.Mutant.AnalyzeTestRun(
                    failed,
                    executed,
                    timedOut,
                    sessionTimedOut: state.TimedOut.Count > 0,
                    sessionRuntimeError: false);
            }
        }

        _logger.LogInformation(
            "{RunnerId}: Wave batch completed: mutants {MutantCount}, waves {WaveCount}, " +
            "wave tests {WaveTestCount}, confirmations {ConfirmationCount}, duration {DurationMs} ms",
            RunnerId,
            mutants.Count,
            waveCount,
            waveTestCount,
            confirmationCount,
            stopwatch.ElapsedMilliseconds);
        return new TestRunResult(true);
    }

    internal static IReadOnlyDictionary<string, int> BuildWaveAssignments(
        IEnumerable<(int MutantId, IReadOnlyList<string> Remaining)> states,
        int sliceSize,
        Func<string, string?>? activationFamilySelector = null,
        int maximumAssignments = MaximumWaveAssignments,
        Func<string, bool>? deferredTestSelector = null)
    {
        var materializedStates = states.ToList();

        IReadOnlyDictionary<string, int> Build(bool skipDeferred)
        {
            var assignments = new Dictionary<string, int>(StringComparer.Ordinal);
            var activationFamilyOwners = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (mutantId, remaining) in materializedStates)
            {
                var assigned = 0;
                foreach (var testUid in remaining)
                {
                    if (skipDeferred && deferredTestSelector!(testUid))
                    {
                        continue;
                    }

                    if (assignments.Count >= maximumAssignments)
                    {
                        return assignments;
                    }

                    if (assignments.ContainsKey(testUid))
                    {
                        continue;
                    }

                    var activationFamily = activationFamilySelector?.Invoke(testUid);
                    if (activationFamily is not null &&
                        activationFamilyOwners.TryGetValue(activationFamily, out var ownerId) &&
                        ownerId != mutantId)
                    {
                        continue;
                    }

                    assignments[testUid] = mutantId;
                    if (activationFamily is not null)
                    {
                        activationFamilyOwners[activationFamily] = mutantId;
                    }

                    if (++assigned >= sliceSize)
                    {
                        break;
                    }
                }
            }

            return assignments;
        }

        if (deferredTestSelector is null)
        {
            return Build(skipDeferred: false);
        }

        var untriedAssignments = Build(skipDeferred: true);
        return untriedAssignments.Count > 0
            ? untriedAssignments
            : Build(skipDeferred: false);
    }

    private Dictionary<string, string> GetWaveActivationFamilies()
    {
        var families = new Dictionary<string, string>(StringComparer.Ordinal);
        lock (_discoveryLock)
        {
            foreach (var (testUid, description) in _testDescriptions)
            {
                if (MethodAssignmentKey(description.Description.Name) is { } methodKey)
                {
                    families[testUid] = methodKey;
                }
            }
        }

        return families;
    }

    internal static IReadOnlySet<int> GetWaveFallbackMutantIds(
        IReadOnlyDictionary<string, int> requestedAssignments,
        bool sessionTimedOut,
        bool sessionHadRuntimeIssue,
        IEnumerable<int> attributedTimedOutMutantIds,
        bool waveMadeProgress = true)
    {
        var timeoutWasAttributed = attributedTimedOutMutantIds.Any();
        if (!sessionHadRuntimeIssue && waveMadeProgress &&
            (!sessionTimedOut || timeoutWasAttributed))
        {
            return new HashSet<int>();
        }

        return requestedAssignments.Values.ToHashSet();
    }

    private MutantWaveState CreateMutantWaveState(IMutant mutant)
    {
        var identifiers = !mutant.AssessingTests.IsEveryTest
            ? mutant.AssessingTests.GetIdentifiers()
            : GetDiscoveredTestIdentifiers();
        _configuredIsolationMutationPriorities.TryGetValue(
            BuildMutationProfileKey(mutant),
            out var mutationPriorities);

        lock (_discoveryLock)
        {
            var plan = BuildPrioritizedWaveTestPlan(
                identifiers,
                testUid => _testDescriptions.TryGetValue(testUid, out var description)
                    ? description.InitialRunTime
                    : null,
                testUid => ResolveMutationPriority(
                    mutationPriorities,
                    testUid,
                    _testDescriptions.TryGetValue(testUid, out var description)
                        ? description.Description.Name
                        : null));
            DeprioritizeKnownTimeoutTests(
                plan.Fallback,
                OrdinaryTimeoutHistory.ContainsKey);
            return new MutantWaveState(mutant, plan.Priority, plan.Fallback);
        }
    }

    private IReadOnlyCollection<string> GetDiscoveredTestIdentifiers()
    {
        lock (_discoveryLock)
        {
            return _testDescriptions.Keys.ToList();
        }
    }

    internal static List<string> OrderWaveTestIdentifiers(
        IEnumerable<string> identifiers,
        Func<string, TimeSpan?> durationSelector,
        Func<string, int>? killScoreSelector = null) =>
        identifiers
            .OrderByDescending(identifier => killScoreSelector?.Invoke(identifier) ?? 0)
            .ThenBy(identifier => durationSelector(identifier) ?? TimeSpan.MaxValue)
            .ThenBy(identifier => identifier, StringComparer.Ordinal)
            .ToList();

    internal static (List<string> Priority, List<string> Fallback) BuildPrioritizedWaveTestPlan(
        IEnumerable<string> identifiers,
        Func<string, TimeSpan?> durationSelector,
        Func<string, int> killScoreSelector)
    {
        var ordered = OrderWaveTestIdentifiers(
            identifiers,
            durationSelector,
            killScoreSelector);
        var priority = ordered.Where(identifier => killScoreSelector(identifier) > 0).ToList();
        return priority.Count == 0
            ? (ordered, [])
            : (priority, ordered.Where(identifier => killScoreSelector(identifier) <= 0).ToList());
    }

    internal static void DeprioritizeKnownTimeoutTests(
        List<string> identifiers,
        Func<string, bool> timeoutSelector)
    {
        var ordered = identifiers
            .OrderBy(timeoutSelector)
            .ToList();
        identifiers.Clear();
        identifiers.AddRange(ordered);
    }

    internal static List<T> OrderIsolationTests<T>(
        IEnumerable<T> tests,
        Func<T, int> killScoreSelector,
        Func<T, TimeSpan?> durationSelector,
        Func<T, string> identifierSelector) =>
        tests
            .OrderByDescending(killScoreSelector)
            .ThenBy(test => durationSelector(test) ?? TimeSpan.MaxValue)
            .ThenBy(identifierSelector, StringComparer.Ordinal)
            .ToList();

    internal static IReadOnlyList<IReadOnlyList<T>> BuildIsolationTestBatches<T>(
        IReadOnlyList<T> orderedTests,
        int priorityBatchSize = InitialFreshProcessPriorityTests)
    {
        if (priorityBatchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(priorityBatchSize));
        }

        if (orderedTests.Count <= priorityBatchSize)
        {
            return orderedTests.Count == 0 ? [] : [orderedTests];
        }

        return
        [
            orderedTests.Take(priorityBatchSize).ToList(),
            orderedTests.Skip(priorityBatchSize).ToList(),
        ];
    }

    internal static IReadOnlyDictionary<string, int> LoadIsolationTestPriorities(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new Dictionary<string, int>(StringComparer.Ordinal);
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"The configured MTP isolation test priority file does not exist: '{path}'.",
                path);
        }

        var names = File.ReadLines(path)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return names
            .Select((name, index) => (name, score: names.Count - index))
            .ToDictionary(entry => entry.name, entry => entry.score, StringComparer.Ordinal);
    }

    internal static IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>>
        LoadIsolationMutationPriorities(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.Ordinal);
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"The configured MTP isolation mutation profile does not exist: '{path}'.",
                path);
        }

        var namesByMutation = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (line, index) in File.ReadLines(path).Select((line, index) => (line.Trim(), index)))
        {
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var fields = line.Split('\t');
            if (fields.Length != 8 || fields.Any(string.IsNullOrWhiteSpace))
            {
                throw new InvalidDataException(
                    $"Isolation mutation profile line {index + 1} must contain " +
                    "'<source><tab><start-line><tab><start-column><tab><end-line>" +
                    "<tab><end-column><tab><mutator><tab><replacement-sha256><tab><test-identifier-or-name>'.");
            }

            var mutationKey = string.Join('\t', fields[..7]);
            var testName = fields[7].Trim();
            if (!namesByMutation.TryGetValue(mutationKey, out var names))
            {
                names = [];
                namesByMutation.Add(mutationKey, names);
            }

            if (!names.Contains(testName, StringComparer.Ordinal))
            {
                names.Add(testName);
            }
        }

        return namesByMutation.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyDictionary<string, int>)entry.Value
                .Select((name, index) => (name, score: entry.Value.Count - index))
                .ToDictionary(item => item.name, item => item.score, StringComparer.Ordinal),
            StringComparer.Ordinal);
    }

    internal static string BuildMutationProfileKey(IMutant mutant)
    {
        var location = mutant.Mutation.OriginalNode.GetLocation().GetMappedLineSpan();
        var path = string.IsNullOrEmpty(location.Path)
            ? mutant.Mutation.OriginalNode.SyntaxTree.FilePath
            : location.Path;
        var replacement = mutant.Mutation.ReplacementNode.ToString();
        var replacementHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(replacement))).ToLowerInvariant();
        return string.Join(
            '\t',
            NormalizeMutationSourcePath(path),
            location.StartLinePosition.Line + 1,
            location.StartLinePosition.Character + 1,
            location.EndLinePosition.Line + 1,
            location.EndLinePosition.Character + 1,
            mutant.Mutation.DisplayName,
            replacementHash);
    }

    internal static string NormalizeMutationSourcePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        var sourceMarker = normalized.LastIndexOf("/src/", StringComparison.OrdinalIgnoreCase);
        return sourceMarker >= 0 ? normalized[(sourceMarker + 1)..] : normalized;
    }

    internal static int ResolveMutationPriority(
        IReadOnlyDictionary<string, int>? priorities,
        string testUid,
        string? testName)
    {
        if (priorities is null)
        {
            return 0;
        }

        if (priorities.TryGetValue(testUid, out var uidScore))
        {
            return uidScore;
        }

        return testName is not null && priorities.TryGetValue(testName, out var nameScore)
            ? nameScore
            : 0;
    }

    private async Task<MutationWaveOutcome> RunMutationWaveAsync(
        IReadOnlyList<string> assemblies,
        IReadOnlyList<IMutant> mutants,
        IReadOnlyDictionary<string, int> requestedAssignments,
        ITimeoutValueCalculator? timeoutCalc)
    {
        var outcome = new MutationWaveOutcome();
        var stopwatch = Stopwatch.StartNew();
        var publishedAssignments = WriteParallelMutantMap(requestedAssignments);
        try
        {
            var requestedTestIds = requestedAssignments.Keys.ToHashSet(StringComparer.Ordinal);
            var result = await RunAllTestsAsync(
                assemblies,
                mutantId: -1,
                mutants,
                update: null,
                timeoutCalc,
                node => requestedTestIds.Contains(node.Uid),
                packedAssignments: publishedAssignments).ConfigureAwait(false);

            outcome.TimedOut = result.SessionTimedOut;
            outcome.HadRuntimeIssue = result.SessionHadRuntimeIssue;
            if (outcome.HadRuntimeIssue)
            {
                return outcome;
            }

            var executedIds = result.ExecutedTests.IsEveryTest
                ? requestedAssignments.Keys
                : result.ExecutedTests.GetIdentifiers();
            foreach (var testUid in executedIds)
            {
                if (requestedAssignments.TryGetValue(testUid, out var mutantId))
                {
                    AddToBucket(outcome.ExecutedByMutant, mutantId, testUid);
                }
            }

            foreach (var testUid in result.FailingTests.GetIdentifiers())
            {
                if (requestedAssignments.TryGetValue(testUid, out var mutantId))
                {
                    AddToBucket(outcome.FailedByMutant, mutantId, testUid);
                }
            }

            foreach (var testUid in result.TimedOutTests.GetIdentifiers())
            {
                if (requestedAssignments.TryGetValue(testUid, out var mutantId))
                {
                    AddToBucket(outcome.TimedOutByMutant, mutantId, testUid);
                }
            }

            return outcome;
        }
        finally
        {
            if (stopwatch.ElapsedMilliseconds >= 2_000)
            {
                _logger.LogInformation(
                    "{RunnerId}: Slow mutation wave: duration {DurationMs} ms, timed out {TimedOut}, " +
                    "attributed timeouts {AttributedTimeouts}, assignments {Assignments}",
                    RunnerId,
                    stopwatch.ElapsedMilliseconds,
                    outcome.TimedOut,
                    DescribeWaveTimeouts(outcome.TimedOutByMutant),
                    DescribeWaveAssignments(requestedAssignments));
            }

            WriteInactiveMutantMap();
        }
    }

    private string DescribeWaveTimeouts(
        IReadOnlyDictionary<int, HashSet<string>> timedOutByMutant)
    {
        lock (_discoveryLock)
        {
            return string.Join(
                " || ",
                timedOutByMutant.SelectMany(bucket => bucket.Value.Select(testUid =>
                {
                    var name = _testDescriptions.TryGetValue(testUid, out var description)
                        ? description.Description.Name
                        : testUid;
                    return $"{bucket.Key}:{name}";
                })));
        }
    }

    private string DescribeWaveAssignments(IReadOnlyDictionary<string, int> assignments)
    {
        lock (_discoveryLock)
        {
            return string.Join(
                " || ",
                assignments.Select(assignment =>
                {
                    var name = _testDescriptions.TryGetValue(assignment.Key, out var description)
                        ? description.Description.Name
                        : assignment.Key;
                    return $"{assignment.Value}:{name}";
                }));
        }
    }

    private IReadOnlyCollection<string> GetAttributedTimedOutTests(
        IReadOnlyDictionary<string, int> packedAssignments)
    {
        if (_expectedMutantMapAcknowledgement is not { } acknowledgement)
        {
            return [];
        }

        var activeMutantIds = ReadActiveMutantIds(
            _activeTestJournalFilePath,
            acknowledgement);
        if (activeMutantIds.Count == 0)
        {
            return [];
        }

        return activeMutantIds
            .Select(mutantId => packedAssignments
                .FirstOrDefault(assignment =>
                    assignment.Value == mutantId &&
                    !assignment.Key.StartsWith("method\t", StringComparison.Ordinal)))
            .Where(assignment => assignment.Key is not null)
            .Select(assignment => assignment.Key)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    internal static IReadOnlySet<int> ReadActiveMutantIds(
        string journalFilePath,
        string acknowledgement)
    {
        if (!File.Exists(journalFilePath))
        {
            return new HashSet<int>();
        }

        var lines = File.ReadAllLines(journalFilePath);
        if (lines.Length == 0 ||
            !string.Equals(
                lines[0],
                ActiveTestJournalHeaderPrefix + acknowledgement,
                StringComparison.Ordinal))
        {
            return new HashSet<int>();
        }

        var activeTests = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in lines.Skip(1))
        {
            var fields = line.Split('\t');
            if (fields.Length == 4 &&
                string.Equals(fields[0], "start", StringComparison.Ordinal) &&
                string.Equals(fields[1], acknowledgement, StringComparison.Ordinal) &&
                int.TryParse(
                    fields[3],
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var mutantId))
            {
                activeTests[fields[2]] = mutantId;
            }
            else if (fields.Length == 3 &&
                string.Equals(fields[0], "finish", StringComparison.Ordinal) &&
                string.Equals(fields[1], acknowledgement, StringComparison.Ordinal))
            {
                activeTests.Remove(fields[2]);
            }
        }

        return activeTests.Values.ToHashSet();
    }

    private static void AddToBucket(
        IDictionary<int, HashSet<string>> buckets,
        int mutantId,
        string testUid)
    {
        if (!buckets.TryGetValue(mutantId, out var bucket))
        {
            bucket = new HashSet<string>(StringComparer.Ordinal);
            buckets[mutantId] = bucket;
        }

        bucket.Add(testUid);
    }

    private sealed class MutantWaveState(
        IMutant mutant,
        List<string> remaining,
        List<string> fallback)
    {
        public IMutant Mutant { get; } = mutant;
        public List<string> Remaining { get; } = remaining;
        private List<string> Fallback { get; } = fallback;
        public HashSet<string> Executed { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Failed { get; } = new(StringComparer.Ordinal);
        public HashSet<string> TimedOut { get; } = new(StringComparer.Ordinal);
        public bool Unresolved { get; set; } = true;
        public bool RequiresConfirmation { get; set; }
        public bool Reported { get; set; }

        public void UnlockFallbackAfterPriorityPasses()
        {
            if (Unresolved && Remaining.Count == 0 && Fallback.Count > 0)
            {
                Remaining.AddRange(Fallback);
                Fallback.Clear();
            }
        }
    }

    private sealed class MutationWaveOutcome
    {
        public Dictionary<int, HashSet<string>> ExecutedByMutant { get; } = [];
        public Dictionary<int, HashSet<string>> FailedByMutant { get; } = [];
        public Dictionary<int, HashSet<string>> TimedOutByMutant { get; } = [];
        public bool TimedOut { get; set; }
        public bool HadRuntimeIssue { get; set; }
    }

    internal static bool UseFreshProcessForOrdinaryConfirmation(int attempt) => attempt > 1;

    internal static Func<TestNodeUpdate, bool>? CreateSingleMutantBailPredicate(bool isRuntimeRetry) =>
        isRuntimeRetry
            ? null
            : static update =>
                update.Node.ExecutionState is TestNodeStates.Failed or TestNodeStates.Error or TestNodeStates.TimedOut;

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
                WriteMutantIdToFile(-1);
                DeleteCoverageFile();
                DeleteCoverageMapFile();

                var execution = await ExecuteCoverageProcessAsync(assembly, tests).ConfigureAwait(false);
                // A timed-out capture retries exactly like a lost host: a boundary can
                // wedge on a load-induced race in the code under test rather than on
                // anything this boundary does deterministically, and a fresh host on a
                // second attempt usually completes. A boundary that exhausts both
                // attempts still fails the campaign closed with its name, because
                // proceeding without its coverage would silently un-cover its tests.
                var transientFault = execution.TimedOut
                    ? "The coverage capture process exceeded its execution timeout."
                    : string.IsNullOrWhiteSpace(execution.Error) ? null : execution.Error;
                if (transientFault is not null)
                {
                    if (attempt < maxCaptureAttempts)
                    {
                        _logger.LogWarning(
                            "{RunnerId}: Coverage capture for boundary {Boundary} failed " +
                            "(attempt {Attempt}/{MaxAttempts}); retrying on a fresh host: {Error}",
                            RunnerId,
                            tests[0].DisplayName,
                            attempt,
                            maxCaptureAttempts,
                            transientFault);
                        continue;
                    }

                    if (execution.TimedOut)
                    {
                        throw new TimeoutException(
                            $"Coverage capture for boundary '{tests[0].DisplayName}' timed out on every attempt.");
                    }

                    throw new InvalidOperationException(AppendSinkError(transientFault));
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
    /// Runs one coverage boundary's tests in a dedicated freshly spawned test-server
    /// process, so the boundary's static initialization executes from scratch and its
    /// coverage is attributed to this boundary alone. Virtual as a seam so tests can
    /// exercise the capture retry without a real process.
    /// </summary>
    internal virtual async Task<(bool TimedOut, string? Error)> ExecuteCoverageProcessAsync(
        string assembly,
        IReadOnlyList<TestNode> tests)
    {
        // Generous fixed budget: the process pays spawn and session build, and the
        // boundary's tests run serially under the coverage lifecycle sink.
        var timeout = TimeSpan.FromMilliseconds(60_000 + (200 * tests.Count));

        var server = new AssemblyTestServer(assembly, BuildEnvironmentVariables(), _logger, RunnerId, _options);
        try
        {
            var started = await server.StartAsync().ConfigureAwait(false);
            if (!started)
            {
                return (false, $"Failed to start the coverage capture process for {assembly}.");
            }

            var (_, timedOut) = await server.RunTestsAsync(tests.ToArray(), timeout).ConfigureAwait(false);
            return (timedOut, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            try
            {
                await server.StopAsync(force: true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "{RunnerId}: Failed to stop the coverage capture process for {Assembly}", RunnerId, assembly);
            }
            server.Dispose();
        }
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
            ["STRYKER_MUTANT_MAP_ACTIVE_FILE"] = _activeTestJournalFilePath,
        };

        ExternalEnvironmentVariables.Add(envVars);

        // Add coverage filename when in coverage mode (MutantControl will combine with temp path)
        if (_coverageMode)
        {
            envVars["STRYKER_COVERAGE_FILE"] = Path.GetFileName(_coverageFilePath);
            if (_perTestCoverageMode)
            {
                envVars["STRYKER_COVERAGE_MAP_FILE"] = _coverageMapFilePath;
            }
        }

        return envVars;
    }

    /// <summary>
    /// Enables or disables coverage capture mode. When enabled, the test process will track
    /// which mutations are covered and write the data to a file on process exit.
    /// </summary>
    public void SetCoverageMode(bool enabled, bool perTest = true)
    {
        lock (_serverLock)
        {
            if (_coverageMode == enabled &&
                (!enabled || _perTestCoverageMode == perTest))
            {
                // Already in the desired state; no action needed
                return;
            }

            _coverageMode = enabled;
            _perTestCoverageMode = enabled && perTest;
            _logger.LogDebug(
                "{RunnerId}: Coverage mode {Status}",
                RunnerId,
                !enabled ? "disabled" : perTest ? "exact" : "aggregate");

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
        const string header = "stryker-mtp-coverage-v1";
        if (!File.Exists(_coverageMapFilePath))
        {
            throw new CoverageLifecycleSinkUnavailableException(
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
                var outsideOnlyMutants = classOutsideTestMutants
                    .Except(snapshot.CoveredMutants)
                    .Except(classStaticMutants)
                    .ToList();
                return (ICoverageRunResult)CoverageRunResult.Create(
                    test.Uid,
                    confidence,
                    coveredMutants,
                    classStaticMutants,
                    outsideOnlyMutants);
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

    /// <summary>
    /// Tests one isolation-required mutant in a dedicated, freshly spawned test-server
    /// process (the approach proposed in
    /// https://github.com/stryker-mutator/stryker-net/pull/3695): the mutant id is
    /// published to the control file before the process starts, so its statics
    /// initialize under the active mutation, and the process is discarded afterwards.
    /// Unlike a collectible load context, a fresh process keeps ReadyToRun native code
    /// for the framework stack, and measured campaigns show statics that timed out in
    /// collectible contexts complete with real verdicts here.
    /// </summary>
    private async Task<(TestRunResult? Result, bool TimedOut, List<TestNode>? DiscoveredTests)>
        RunAssemblyTestsInFreshProcessAsync(
            string assembly,
            string? mutationProfileKey,
            ITimeoutValueCalculator? timeoutCalc,
            Func<TestNode, bool>? testUidFilter,
            Func<TestNodeUpdate, bool>? bailPredicate = null)
    {
        if (!File.Exists(assembly))
        {
            return (null, false, null);
        }

        var discoveredTests = GetDiscoveredTests(assembly);
        if (discoveredTests is null)
        {
            return (
                new TestRunResult(false, $"No discovered tests were available for '{assembly}'."),
                false,
                null);
        }

        var testsToRun = testUidFilter is null
            ? discoveredTests
            : discoveredTests.Where(testUidFilter).ToList();
        if (testUidFilter is not null && testsToRun.Count == 0)
        {
            return (BuildTestRunResult([], discoveredTests.Count, TimeSpan.Zero), false, discoveredTests);
        }

        // Exact budget with a cold-start allowance: the dedicated process pays spawn and
        // session build before its first result, and its tests run with xUnit's normal
        // parallelism because the lone mutant is active for the whole process.
        TimeSpan? timeout = timeoutCalc is null
            ? null
            : TimeSpan.FromMilliseconds(20_000 + (100 * testsToRun.Count));

        IReadOnlyDictionary<string, int>? mutationPriorities = null;
        if (mutationProfileKey is not null)
        {
            _configuredIsolationMutationPriorities.TryGetValue(
                mutationProfileKey,
                out mutationPriorities);
            _logger.LogDebug(
                "{RunnerId}: Isolation mutation profile {ProfileState} key {MutationProfileKey}",
                RunnerId,
                mutationPriorities is null ? "missed" : "matched",
                mutationProfileKey);
        }

        int GetMutationPriority(TestNode test)
        {
            var testName = _testDescriptions.TryGetValue(test.Uid, out var description)
                ? description.Description.Name
                : null;
            return ResolveMutationPriority(mutationPriorities, test.Uid, testName);
        }

        List<TestNode> orderedTests;
        lock (_discoveryLock)
        {
            orderedTests = OrderIsolationTests(
                testsToRun,
                test =>
                {
                    var learnedScore = IsolationKillHistory.GetValueOrDefault(
                        IsolationHistoryKey(assembly, test.Uid));
                    var mutationScore = GetMutationPriority(test);
                    if (mutationScore > 0)
                    {
                        return 100_000_000 + (mutationScore * 1_000) + learnedScore;
                    }

                    if (_testDescriptions.TryGetValue(test.Uid, out var description) &&
                        _configuredIsolationPriorities.TryGetValue(
                            description.Description.Name,
                            out var configuredScore))
                    {
                        return (configuredScore * 1_000) + learnedScore;
                    }

                    return learnedScore;
                },
                test =>
                    _testDescriptions.TryGetValue(test.Uid, out var description)
                        ? description.InitialRunTime
                        : null,
                test => test.Uid);
        }

        var exactPriorityTests = orderedTests
            .Where(test => GetMutationPriority(test) > 0)
            .ToList();
        if (exactPriorityTests.Count > 0)
        {
            // A measured killer can avoid one OS process: the persistent broker creates a
            // fresh collectible context, publishes the mutant before loading product code,
            // runs only the exact prior killer, and refuses the next request unless the context
            // unloads. Collectible execution is a fast-path proof only. A stale profile, timeout,
            // host loss, or unload failure falls through to ReadyToRun-preserving fresh-process
            // ground truth instead of assigning a verdict.
            var fastExecution = await GetOrCreateIsolationClient(assembly)
                .ExecuteAsync(
                    exactPriorityTests.Select(test => test.Uid).ToList(),
                    TimeSpan.FromSeconds(20))
                .ConfigureAwait(false);
            _logger.LogDebug(
                "{RunnerId}: Collectible isolation returned {TestCount} tests, timedOut={TimedOut}, unloaded={Unloaded}, error={Error}",
                RunnerId,
                fastExecution.Tests.Count,
                fastExecution.SessionTimedOut,
                fastExecution.Unloaded,
                fastExecution.Error);
            if (CanTrustCollectibleKill(fastExecution))
            {
                foreach (var killedTest in fastExecution.Tests.Where(test =>
                             string.Equals(test.State, "failed", StringComparison.Ordinal)))
                {
                    IsolationKillHistory.AddOrUpdate(
                        IsolationHistoryKey(assembly, killedTest.TestCaseId),
                        1,
                        static (_, count) => count + 1);
                }

                return (
                    BuildCollectibleTestRunResult(
                        fastExecution.Tests,
                        discoveredTests.Count,
                        TimeSpan.FromTicks(fastExecution.DurationTicks)),
                    false,
                    discoveredTests);
            }
        }

        var stopwatch = Stopwatch.StartNew();
        var server = new AssemblyTestServer(assembly, BuildEnvironmentVariables(), _logger, RunnerId, _options);
        try
        {
            var started = await server.StartAsync().ConfigureAwait(false);
            if (!started)
            {
                return (
                    new TestRunResult(false, $"Failed to start the dedicated test server for {assembly}."),
                    false,
                    discoveredTests);
            }

            // Give measured killers one small scheduling window, then submit the complete
            // remainder once. A single broad request lets xUnit start hundreds of tests before
            // cancellation reaches it, while progressive growth makes survivors pay repeated
            // MTP request setup. Two requests preserve fast kills and bound survivor overhead.
            var testResults = new List<TestNodeUpdate>();
            var timedOut = false;
            var exactPriorityTestCount = orderedTests.Count(test => GetMutationPriority(test) > 0);
            var priorityBatchSize = exactPriorityTestCount > 0
                ? exactPriorityTestCount
                : InitialFreshProcessPriorityTests;
            foreach (var batch in BuildIsolationTestBatches(orderedTests, priorityBatchSize))
            {
                var (batchResults, batchTimedOut) = await server
                    .RunTestsAsync(batch.ToArray(), timeout, bailPredicate, stallDetection: true)
                    .ConfigureAwait(false);
                testResults.AddRange(batchResults);
                timedOut |= batchTimedOut;
                if (batchTimedOut ||
                    (bailPredicate is not null && batchResults.Any(update =>
                        update.Node.ExecutionState is TestNodeStates.Failed or
                            TestNodeStates.Error or TestNodeStates.TimedOut)))
                {
                    break;
                }
            }

            foreach (var killedTest in testResults.Where(update =>
                         update.Node.ExecutionState is TestNodeStates.Failed or TestNodeStates.Error))
            {
                IsolationKillHistory.AddOrUpdate(
                    IsolationHistoryKey(assembly, killedTest.Node.Uid),
                    1,
                    static (_, count) => count + 1);
            }

            var result = BuildTestRunResult(
                NormalizeToDiscoveredCases(testResults, discoveredTests),
                discoveredTests.Count,
                stopwatch.Elapsed);
            return (result, timedOut, discoveredTests);
        }
        catch (Exception ex)
        {
            return (new TestRunResult(false, ex.Message), false, discoveredTests);
        }
        finally
        {
            try
            {
                await server.StopAsync(force: true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "{RunnerId}: Failed to stop the dedicated test server for {Assembly}", RunnerId, assembly);
            }
            server.Dispose();
        }
    }

    private static string IsolationHistoryKey(string assembly, string testUid) =>
        string.Concat(assembly, "\0", testUid);

    internal static bool CanTrustCollectibleKill(CollectibleIsolationResponse response) =>
        !response.SessionTimedOut &&
        response.Unloaded &&
        string.IsNullOrWhiteSpace(response.Error) &&
        response.Tests.Any(test =>
            string.Equals(test.State, "failed", StringComparison.Ordinal));

    private TestRunResult BuildCollectibleTestRunResult(
        IReadOnlyCollection<CollectibleIsolationTestResult> testResults,
        int totalDiscoveredTests,
        TimeSpan duration)
    {
        var resultsByTest = testResults
            .GroupBy(result => result.TestCaseId, StringComparer.Ordinal)
            .ToList();
        var executedIds = resultsByTest.Select(result => result.Key).ToList();
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

        IEnumerable<MtpTestDescription> descriptions;
        lock (_discoveryLock)
        {
            descriptions = _testDescriptions.Values.ToList();
        }

        return new TestRunResult(
            descriptions,
            executedTests,
            new TestIdentifierList(failedIds),
            TestIdentifierList.NoTest(),
            string.Join(Environment.NewLine, messages),
            messages,
            duration);
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

    private CollectibleTestIsolationClient GetOrCreateIsolationClient(string assembly)
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

    internal TimeSpan? CalculateAssemblyTimeout(List<TestNode> discoveredTests, ITimeoutValueCalculator timeoutCalc, string assembly, bool parallelSession = false)
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

        // The MTP protocol reports no per-test timing, so the estimate smears the initial
        // run's duration evenly across the requested tests and can undershoot badly for a
        // small set of genuinely slow tests. A tight budget would stamp slow-but-passing
        // sessions as Timeout - verdicts that hide real survivors. The floor keeps the
        // budget generous enough that a Timeout verdict means the mutant genuinely hangs
        // or drags, not that the session outran an estimate.
        var floorMs = 15_000 + (100 * discoveredTests.Count);
        if (parallelSession)
        {
            // A packed parallel session's healthy ceiling is a few seconds regardless of
            // set size, so its measured budget is exact rather than a minimum: a slow
            // mutant cannot drag its whole batch, because the batch times out cheaply and
            // its unresolved mutants retry individually under the generous default floor.
            timeoutMs = 8_000 + (10 * discoveredTests.Count);
        }
        else if (timeoutMs < floorMs)
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
        bool useFreshProcess = false,
        Func<TestNodeUpdate, bool>? bailPredicate = null,
        IReadOnlyDictionary<string, int>? packedAssignments = null)
    {
        try
        {
            // The mutant is active for the whole run through the control file - stock
            // Stryker's whole-session activation on a persistent host. Activation must
            // precede the run request so static initialization in a fresh process (and
            // fixture construction anywhere) observes the mutation. A packed parallel
            // session instead holds the control file inactive and binds mutants per test
            // through the published map.
            WriteMutantIdToFile(mutantId);

            // A packed wave is deliberately small and must finish normally. Cancelling when all
            // assigned mutants have verdicts saves only the unstarted tail of this one bounded
            // request, while MTP cannot prove that its scheduler drained before acknowledging the
            // cancellation. The only safe response was therefore to kill the reusable host. On a
            // kill-heavy campaign that turns ordinary mutation into thousands of cold process
            // starts, which costs far more than completing the remaining wave tests.

            var accumulator = new TestRunAccumulator();
            var mutationProfileKey = mutants is { Count: 1 }
                ? BuildMutationProfileKey(mutants[0])
                : null;

            foreach (var assembly in assemblies)
            {
                var (result, timedOut, discoveredTests) =
                    useFreshProcess
                        ? await RunAssemblyTestsInFreshProcessAsync(
                            assembly,
                            mutationProfileKey,
                            timeoutCalc,
                            testUidFilter,
                            bailPredicate).ConfigureAwait(false)
                        : await RunAssemblyTestsAsync(
                            assembly,
                            timeoutCalc,
                            testUidFilter,
                            bailPredicate,
                            parallelSession: packedAssignments is not null).ConfigureAwait(false);

                if (discoveredTests is not null)
                {
                    accumulator.AddDiscoveredCount(discoveredTests.Count);

                    if (timedOut)
                    {
                        accumulator.HasTimeout = true;
                        if (packedAssignments is not null)
                        {
                            // A timed-out packed session must not blanket-stamp its tests:
                            // completed verdicts stand and unresolved mutants retry alone. A
                            // warm packed host is wedged and must be discarded; a fresh packed
                            // host is already disposed by its request boundary.
                            if (!useFreshProcess)
                            {
                                await DiscardServerAsync(assembly).ConfigureAwait(false);
                            }

                            foreach (var testUid in GetAttributedTimedOutTests(packedAssignments))
                            {
                                accumulator.TimedOutTests.Add(testUid);
                            }
                        }
                        else if (useFreshProcess)
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

            if (packedAssignments is not null)
            {
                // The injected helper acknowledges the map when its per-test binding armed.
                // A missing or mismatched acknowledgement means tests ran without binding;
                // the whole session fails closed rather than reporting unattributed verdicts.
                var activationError = ValidateMutantMapAcknowledgement();
                if (!string.IsNullOrWhiteSpace(activationError))
                {
                    _logger.LogError(
                        "{RunnerId}: Mutation activation protocol failed: {ActivationError}",
                        RunnerId,
                        activationError);
                    IEnumerable<MtpTestDescription> descriptions;
                    lock (_discoveryLock)
                    {
                        descriptions = _testDescriptions.Values.ToList();
                    }

                    return TestRunResult.RuntimeError(
                        descriptions,
                        accumulator.BuildExecutedTests(),
                        TestIdentifierList.NoTest(),
                        TestIdentifierList.NoTest(),
                        activationError,
                        accumulator.Messages,
                        accumulator.TotalDuration);
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

    internal virtual async Task<(TestRunResult? Result, bool TimedOut, List<TestNode>? DiscoveredTests)> RunAssemblyTestsAsync(
        string assembly,
        ITimeoutValueCalculator? timeoutCalc,
        Func<TestNode, bool>? testUidFilter = null,
        Func<TestNodeUpdate, bool>? bailPredicate = null,
        bool parallelSession = false)
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
            timeout = CalculateAssemblyTimeout(testsToRun, timeoutCalc, assembly, parallelSession);
        }

        var (testResults, timedOut) = await RunAssemblyTestsInternalAsync(
            assembly,
            testUidFilter,
            timeout,
            bailPredicate,
            stallDetection: parallelSession).ConfigureAwait(false);

        return (testResults as TestRunResult, timedOut, discoveredTests);
    }

    internal virtual async Task<(ITestRunResult Result, bool TimedOut)> RunAssemblyTestsInternalAsync(
        string assembly,
        Func<TestNode, bool>? testUidFilter,
        TimeSpan? timeout = null,
        Func<TestNodeUpdate, bool>? bailPredicate = null,
        bool stallDetection = false,
        bool discardOnBail = false)
    {
        // A crashed test host tears down the RPC connection, so the run throws (rather than timing out).
        // Retry once on a freshly started server: a crash caused by a *previous* mutant then self-heals
        // for the current mutant instead of corrupting its result.
        const int maxRunAttempts = 2;
        Exception? lastRunException = null;

        for (var attempt = 1; attempt <= maxRunAttempts; attempt++)
        {
            var acquireStopwatch = Stopwatch.StartNew();
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

                var (testResults, timedOut) = await server.RunTestsAsync(
                    testsToRun,
                    timeout,
                    bailPredicate,
                    stallDetection,
                    discardOnBail).ConfigureAwait(false);

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
                DeleteIfExists(_activeTestJournalFilePath);
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
