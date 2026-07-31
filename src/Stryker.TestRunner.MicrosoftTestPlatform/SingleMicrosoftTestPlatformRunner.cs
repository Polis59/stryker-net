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

/// <summary>
/// Individual test runner instance that handles test execution with mutation-specific
/// environment variables. Used by MicrosoftTestPlatformRunnerPool.
/// Maintains persistent test server connections per assembly to reduce process startup overhead.
/// Uses file-based mutant control to allow changing the active mutant without restarting processes.
/// </summary>
public class SingleMicrosoftTestPlatformRunner : IDisposable
{
    private readonly int _id;
    private readonly Dictionary<string, List<TestNode>> _testsByAssembly;
    private readonly Dictionary<string, MtpTestDescription> _testDescriptions;
    private readonly TestSet _testSet;
    private readonly object _discoveryLock;
    private readonly ILogger _logger;
    private readonly string _mutantFilePath;
    private readonly string _coverageFilePathBase;
    // One coverage file per test assembly. The injected MutantControl flushes coverage with an
    // unconditional overwrite on process exit, so with test hosts sharing a single file the last
    // flush to land replaces all the others: only one assembly's coverage survives, and which one
    // depends on server stop order and process shutdown timing. Giving every assembly's host its
    // own file and unioning them at read time makes coverage independent of both.
    private readonly ConcurrentDictionary<string, string> _coverageFilePaths = new();
    private readonly IStrykerOptions? _options;

    private readonly Dictionary<string, AssemblyTestServer> _assemblyServers = new();
    private readonly object _serverLock = new();
    private bool _disposed;
    private bool _coverageMode;

    // True while the warm test hosts have executed a static-value mutant. A static initializer only
    // runs on first type load, and whatever state it mutated persists in the reused process, so both
    // the static mutant itself and the first mutant tested after it need fresh hosts.
    private bool _hostRanStaticMutant;

    /// <summary>
    /// Identifiers of tests that already failed during the initial (unmutated) run. Bail must not
    /// trigger on these: cancelling a run because of a test that fails without any mutation would
    /// classify the cancelled remainder incorrectly. Set by the pool after the initial run.
    /// </summary>
    public IReadOnlySet<string> InitialRunFailingTests { get; set; } = new HashSet<string>();

    private string RunnerId => $"MtpRunner-{_id}";

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

        // Create unique file paths for this runner to communicate with the test process.
        // The coverage base name embeds the process id plus a per-instance nonce: coverage files
        // are only deleted once their path has been assigned, so a predictable name could let a
        // run read a stale file left behind by a crashed earlier run (same runner id, same
        // assembly), and concurrent Stryker processes could clobber each other's files. The nonce
        // covers what the process id alone does not (pid reuse, several runner instances with the
        // same id in one process).
        _mutantFilePath = Path.Combine(Path.GetTempPath(), $"stryker-mutant-{_id}.txt");
        _coverageFilePathBase = Path.Combine(Path.GetTempPath(),
            $"stryker-coverage-{Environment.ProcessId}-{_id}-{Guid.NewGuid().ToString("N")[..8]}");

        // Initialize with no active mutation
        WriteMutantIdToFile(-1);
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

        // The active mutant is a process-global switch, so a group of mutants cannot run in one
        // session: activating none (the previous behaviour) runs the whole group against unmutated
        // code and fabricates the verdicts. Instead each mutant runs sequentially in its own
        // session, restricted to its own assessing tests.
        _logger.LogDebug("{RunnerId}: Testing mutant(s) [{Mutants}] sequentially",
            RunnerId, string.Join(",", mutants.Select(m => m.Id)));

        var merged = new MergedSessionResult();

        foreach (var mutant in mutants)
        {
            Func<TestNode, bool>? testUidFilter = null;
            if (mutant.AssessingTests is { IsEveryTest: false } assessing)
            {
                var uids = assessing.GetIdentifiers().ToHashSet(StringComparer.Ordinal);
                if (uids.Count == 0)
                {
                    // Nothing left to assess this mutant with (the analyser normally classifies
                    // such mutants before execution). Report an empty run so it resolves to
                    // Survived rather than executing unrelated tests.
                    update?.Invoke(new[] { mutant }, TestIdentifierList.NoTest(), TestIdentifierList.NoTest(), TestIdentifierList.NoTest());
                    continue;
                }

                testUidFilter = t => uids.Contains(t.Uid);
            }

            var ranOnFreshHost = false;
            if (mutant.IsStaticValue || mutant.MustBeTestedInIsolation)
            {
                // A static initializer only executes on first type load, so the mutation must be
                // active before a fresh host starts; a warm host would never run it mutated.
                await ResetServerAsync().ConfigureAwait(false);
                _hostRanStaticMutant = true;
                ranOnFreshHost = true;
            }
            else if (_hostRanStaticMutant)
            {
                // The previous mutant ran static initializers mutated and that state persists in
                // the warm host, so it would leak into this mutant's verdict.
                await ResetServerAsync().ConfigureAwait(false);
                _hostRanStaticMutant = false;
                ranOnFreshHost = true;
            }

            // First pass runs on whatever host is available — usually warm, which is fast. The
            // handler is deliberately not invoked yet: a Survived verdict from a warm host is not
            // trustworthy, because earlier tests' fixtures and product caches persist there and
            // mutated code paths feeding those caches never re-execute.
            var result = await RunAllTestsAsync(assemblies, mutant.Id, new[] { mutant }, update: null, timeoutCalc, testUidFilter).ConfigureAwait(false);

            if (!ranOnFreshHost
                && !result.SessionTimedOut
                && !result.SessionHadRuntimeIssue
                && !KillsMutant(result))
            {
                // Looks survived, but only a fresh host proves it: there the caches are cold, so
                // every assessing test re-executes the mutated paths. A genuine survivor pays one
                // extra session; a cache-hidden kill is recovered here.
                _logger.LogDebug("{RunnerId}: Mutant {MutantId} survived on a warm host; confirming on a fresh one",
                    RunnerId, mutant.Id);
                await ResetServerAsync().ConfigureAwait(false);
                result = await RunAllTestsAsync(assemblies, mutant.Id, new[] { mutant }, update: null, timeoutCalc, testUidFilter).ConfigureAwait(false);
            }

            update?.Invoke(new[] { mutant }, result.FailingTests, result.ExecutedTests, result.TimedOutTests);

            if (update is null || result.SessionTimedOut || result.SessionHadRuntimeIssue)
            {
                // The per-run update handler never sees session-level outcomes, so classify the
                // mutant here (mirrors the executor's single-mutant path) to keep it from staying
                // Pending and forcing a redundant rerun.
                mutant.AnalyzeTestRun(result.FailingTests, result.ExecutedTests, result.TimedOutTests,
                    result.SessionTimedOut, result.SessionHadRuntimeIssue);
            }

            merged.Add(result);
        }

        // Session-level flags stay false on the merged result: every mutant above already received
        // a conclusive classification, so the executor must not re-analyze the group against the
        // union of per-mutant test results (a union would cross-attribute failures).
        IEnumerable<MtpTestDescription> testDescriptionValues;
        lock (_discoveryLock)
        {
            testDescriptionValues = _testDescriptions.Values.ToList();
        }

        return merged.Build(testDescriptionValues);
    }

    /// <summary>
    /// True when the session result detects the mutant: some test failed that was not already
    /// failing in the initial (unmutated) run, or some test timed out. Only a non-detection needs
    /// the fresh-host confirmation pass.
    /// </summary>
    private bool KillsMutant(ITestRunResult result)
    {
        if (result.FailingTests.IsEveryTest)
        {
            return true;
        }

        var initialFailing = InitialRunFailingTests;
        return result.FailingTests.GetIdentifiers().Any(id => !initialFailing.Contains(id))
            || result.TimedOutTests.GetIdentifiers().Any();
    }

    /// <summary>
    /// Accumulates per-mutant session results into the single result
    /// <see cref="TestMultipleMutantsAsync"/> returns to the executor.
    /// </summary>
    private sealed class MergedSessionResult
    {
        private readonly List<string> _executed = [];
        private readonly List<string> _failed = [];
        private readonly List<string> _timedOut = [];
        private readonly List<string> _messages = [];
        private readonly List<string> _errorMessages = [];
        private bool _everyTestExecuted;
        private TimeSpan _duration;

        public void Add(ITestRunResult result)
        {
            if (result.ExecutedTests.IsEveryTest)
            {
                _everyTestExecuted = true;
            }
            else
            {
                _executed.AddRange(result.ExecutedTests.GetIdentifiers());
            }

            if (!result.FailingTests.IsEveryTest)
            {
                _failed.AddRange(result.FailingTests.GetIdentifiers());
            }

            _timedOut.AddRange(result.TimedOutTests.GetIdentifiers());
            _messages.AddRange(result.Messages ?? []);
            if (!string.IsNullOrWhiteSpace(result.ResultMessage))
            {
                _errorMessages.Add(result.ResultMessage);
            }

            _duration += result.Duration;
        }

        public ITestRunResult Build(IEnumerable<MtpTestDescription> testDescriptions) =>
            new TestRunResult(
                testDescriptions,
                _everyTestExecuted ? TestIdentifierList.EveryTest() : new TestIdentifierList(_executed),
                new TestIdentifierList(_failed),
                _timedOut.Count == 0 ? TestIdentifierList.NoTest() : new TestIdentifierList(_timedOut),
                string.Join(Environment.NewLine, _errorMessages),
                _messages,
                _duration);
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
        }
        
        _logger.LogDebug("{RunnerId}: Test servers reset complete", RunnerId);
        await Task.CompletedTask;
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

    private Dictionary<string, string?> BuildEnvironmentVariables(string assembly)
    {
        var envVars = new Dictionary<string, string?>
        {
            ["STRYKER_MUTANT_FILE"] = _mutantFilePath
        };

        ExternalEnvironmentVariables.Add(envVars);

        // Add coverage filename when in coverage mode (MutantControl will combine with temp path)
        if (_coverageMode)
        {
            envVars["STRYKER_COVERAGE_FILE"] = Path.GetFileName(GetCoverageFilePath(assembly));

            // The control file enables per-test coverage: after each single-test run the runner
            // bumps a request counter in this file and the injected MutantControl's watcher thread
            // flushes the coverage accumulated since the last flush, then acknowledges. The file
            // must exist before the host starts so the watcher can map it immediately.
            var controlFilePath = GetCoverageControlFilePath(assembly);
            if (!File.Exists(controlFilePath))
            {
                File.WriteAllBytes(controlFilePath, new byte[CoverageControlFileSize]);
            }

            envVars["STRYKER_COVERAGE_CONTROL_FILE"] = Path.GetFileName(controlFilePath);
        }

        return envVars;
    }

    // Two 4-byte ints: request sequence at offset 0 (runner-written), acknowledge at offset 4
    // (test-host-written). See the coverage-control comments in the injected MutantControl.
    private const int CoverageControlFileSize = 8;

    /// <summary>
    /// Returns the coverage flush control file path for the given test assembly. Lives beside the
    /// assembly's coverage file (same collision-safe base name) because the two files form one
    /// protocol: the control file coordinates when the coverage file's content is complete.
    /// </summary>
    internal string GetCoverageControlFilePath(string assembly) => GetCoverageFilePath(assembly) + ".control";

    /// <summary>
    /// Builds the predicate that decides whether a streamed test update should end the session
    /// early because the mutant is already killed. Returns null when the user disabled bail.
    /// Tests that failed in the initial (unmutated) run never trigger bail: their failure proves
    /// nothing about the mutant, and cancelling the remainder of the session on their account
    /// would misclassify it. Cancelled and timed-out states never trigger bail either — cancelled
    /// updates are a *consequence* of bailing, and a per-test timeout still classifies the mutant.
    /// </summary>
    private Func<TestNodeUpdate, bool>? BuildBailPredicate()
    {
        if (_options is not null && _options.OptimizationMode.HasFlag(OptimizationModes.DisableBail))
        {
            return null;
        }

        var initialFailing = InitialRunFailingTests;
        return update =>
            update.Node.ExecutionState is TestNodeStates.Failed or TestNodeStates.Error
            && !initialFailing.Contains(update.Node.Uid);
    }

    /// <summary>
    /// Runs a single test on a fresh test host with coverage capture enabled and returns the
    /// mutants it covered. The host is fresh — never reused from a previous test — because a warm
    /// host has executed earlier tests whose fixtures and product caches persist: mutated code
    /// paths feeding those caches never re-execute, so a warm capture silently attributes their
    /// mutants to whichever test happened to run first. The control-file flush protocol makes the
    /// running host write out its coverage without waiting for a process exit. A flush that is
    /// never acknowledged is reported as empty coverage: a test that touches no mutated assembly
    /// never loads the watcher, and for it empty is the correct answer. The pool guards against
    /// the pathological case of every test reporting empty.
    /// </summary>
    internal async Task<(IReadOnlyList<int> CoveredMutants, IReadOnlyList<int> StaticMutants)> CaptureTestCoverageAsync(
        string assembly,
        TestNode test)
    {
        // Fresh host per test: discard whatever host the previous capture left behind.
        await DiscardServerAsync(assembly).ConfigureAwait(false);
        await GetOrCreateServerAsync(assembly).ConfigureAwait(false);

        var (_, timedOut) = await RunAssemblyTestsInternalAsync(
            assembly,
            t => string.Equals(t.Uid, test.Uid, StringComparison.Ordinal)).ConfigureAwait(false);

        if (timedOut)
        {
            _logger.LogDebug("{RunnerId}: Coverage run for test {Test} timed out; reporting no coverage for it",
                RunnerId, test.Uid);
            return (Array.Empty<int>(), Array.Empty<int>());
        }

        if (await RequestCoverageFlushAsync(assembly, TimeSpan.FromSeconds(20)).ConfigureAwait(false))
        {
            return await ReadCoverageDataForAssemblyAsync(assembly).ConfigureAwait(false);
        }

        _logger.LogDebug(
            "{RunnerId}: Test host for {Assembly} did not acknowledge the coverage flush for test {Test}; the test likely loads no mutated assembly",
            RunnerId, Path.GetFileName(assembly), test.Uid);
        return (Array.Empty<int>(), Array.Empty<int>());
    }

    /// <summary>
    /// Bumps the request counter in the assembly's coverage control file and waits for the test
    /// host's watcher thread to acknowledge that it has flushed coverage to the coverage file.
    /// Returns false when no acknowledgement arrives within <paramref name="timeout"/>.
    /// </summary>
    private async Task<bool> RequestCoverageFlushAsync(string assembly, TimeSpan timeout)
    {
        var controlFilePath = GetCoverageControlFilePath(assembly);

        using var stream = new FileStream(controlFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        using var mmf = MemoryMappedFile.CreateFromFile(stream, null, CoverageControlFileSize, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: true);
        using var accessor = mmf.CreateViewAccessor(0, CoverageControlFileSize, MemoryMappedFileAccess.ReadWrite);

        var request = accessor.ReadInt32(0) + 1;
        accessor.Write(0, request);
        accessor.Flush();

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (accessor.ReadInt32(4) == request)
            {
                return true;
            }

            await Task.Delay(1).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>
    /// Reads and parses the coverage file of a single assembly. Unlike <see cref="ReadCoverageData"/>,
    /// which unions all assemblies for the aggregate exit-time flush, per-test capture reads each
    /// assembly's file right after its flush is acknowledged.
    /// </summary>
    internal async Task<(IReadOnlyList<int> CoveredMutants, IReadOnlyList<int> StaticMutants)> ReadCoverageDataForAssemblyAsync(string assembly)
    {
        var coverageFilePath = GetCoverageFilePath(assembly);
        if (!File.Exists(coverageFilePath))
        {
            return (Array.Empty<int>(), Array.Empty<int>());
        }

        // The flush handshake guarantees the host closed the file before acknowledging, but other
        // readers (most commonly on-close antivirus scans) can still hold it briefly, and losing a
        // read silently drops one test's coverage — mutants covered only by that test would be
        // misreported as NoCoverage. Retry sharing violations before giving up.
        const int maxReadAttempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var content = File.ReadAllText(coverageFilePath).Trim();
                if (string.IsNullOrEmpty(content))
                {
                    return (Array.Empty<int>(), Array.Empty<int>());
                }

                var parts = content.Split(';');
                return (
                    ParseMutantIds(parts.Length > 0 ? parts[0] : string.Empty),
                    ParseMutantIds(parts.Length > 1 ? parts[1] : string.Empty));
            }
            catch (IOException ex) when (attempt < maxReadAttempts)
            {
                _logger.LogDebug(ex, "{RunnerId}: Coverage file at {Path} is transiently locked (attempt {Attempt}/{MaxAttempts}); retrying",
                    RunnerId, coverageFilePath, attempt, maxReadAttempts);
                await Task.Delay(20 * attempt).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{RunnerId}: Failed to read coverage file at {Path}", RunnerId, coverageFilePath);
                return (Array.Empty<int>(), Array.Empty<int>());
            }
        }
    }

    /// <summary>
    /// Returns the coverage file path assigned to the given test assembly, assigning one on first
    /// use. The base embeds the process id, runner id and a per-instance nonce (files from other
    /// processes, runners and runner instances must not collide); the hash of the assembly path
    /// distinguishes assemblies in different directories that share a file name. The assembly name
    /// itself is only included, truncated, to keep the file recognizable when debugging.
    /// </summary>
    internal string GetCoverageFilePath(string assembly) =>
        _coverageFilePaths.GetOrAdd(assembly, static (path, basePath) =>
        {
            var name = new string(Path.GetFileNameWithoutExtension(path)
                .Select(c => char.IsLetterOrDigit(c) ? c : '-')
                .Take(32)
                .ToArray());
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..8];
            return $"{basePath}-{name}-{hash}.txt";
        }, _coverageFilePathBase);

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
        }

        // Clean up any existing coverage files, even when enabling, to ensure we start fresh
        DeleteCoverageFiles();
    }

    /// <summary>
    /// Reads coverage data from the per-assembly coverage files written by the test processes,
    /// unioned across all assemblies this runner started a server for.
    /// Returns the covered mutants and static mutants as separate lists.
    /// </summary>
    public (IReadOnlyList<int> CoveredMutants, IReadOnlyList<int> StaticMutants) ReadCoverageData()
    {
        var coveredMutants = new HashSet<int>();
        var staticMutants = new HashSet<int>();

        foreach (var (assembly, coverageFilePath) in _coverageFilePaths)
        {
            if (!File.Exists(coverageFilePath))
            {
                _logger.LogDebug("{RunnerId}: Coverage file for {Assembly} not found at {Path}",
                    RunnerId, Path.GetFileName(assembly), coverageFilePath);
                continue;
            }

            try
            {
                var content = File.ReadAllText(coverageFilePath).Trim();
                _logger.LogDebug("{RunnerId}: Read coverage data for {Assembly}: {Content}",
                    RunnerId, Path.GetFileName(assembly), content);

                if (string.IsNullOrEmpty(content))
                {
                    continue;
                }

                var parts = content.Split(';');
                coveredMutants.UnionWith(ParseMutantIds(parts.Length > 0 ? parts[0] : string.Empty));
                staticMutants.UnionWith(ParseMutantIds(parts.Length > 1 ? parts[1] : string.Empty));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{RunnerId}: Failed to read coverage file at {Path}", RunnerId, coverageFilePath);
            }
        }

        return (coveredMutants.ToList(), staticMutants.ToList());
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

    private void DeleteCoverageFiles()
    {
        foreach (var coverageFilePath in _coverageFilePaths.Values)
        {
            foreach (var path in new[] { coverageFilePath, coverageFilePath + ".control" })
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "{RunnerId}: Failed to delete coverage file at {Path}", RunnerId, path);
                }
            }
        }
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

        var environmentVariables = BuildEnvironmentVariables(assembly);
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

    internal TimeSpan? CalculateAssemblyTimeout(List<TestNode> discoveredTests, ITimeoutValueCalculator timeoutCalc, string assembly)
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
        }
        
        if (server is not null)
        {
            _logger.LogDebug("{RunnerId}: Restarting test server for {Assembly} after timeout", RunnerId, Path.GetFileName(assembly));
            try
            {
                await server.RestartAsync(force: true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "{RunnerId}: Failed to restart test server for {Assembly} after timeout. Creating a new server on next use.", RunnerId, Path.GetFileName(assembly));
                lock (_serverLock)
                {
                    _assemblyServers.Remove(assembly);
                }
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
        Func<TestNode, bool>? testUidFilter = null)
    {
        try
        {
            WriteMutantIdToFile(mutantId);

            // Bail applies only to mutation sessions (never the initial or coverage runs): once a
            // test genuinely fails the mutant is killed and the rest of the session proves nothing.
            var bailPredicate = mutants is not null ? BuildBailPredicate() : null;

            var accumulator = new TestRunAccumulator();

            foreach (var assembly in assemblies)
            {
                var (result, timedOut, discoveredTests) = await RunAssemblyTestsAsync(assembly, timeoutCalc, testUidFilter, bailPredicate).ConfigureAwait(false);

                if (discoveredTests is not null)
                {
                    accumulator.AddDiscoveredCount(discoveredTests.Count);

                    if (timedOut)
                    {
                        accumulator.HasTimeout = true;
                        await HandleAssemblyTimeoutAsync(assembly, discoveredTests, accumulator.TimedOutTests).ConfigureAwait(false);
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
            return new TestRunResult(false, ex.Message);
        }
    }

    internal virtual async Task<(TestRunResult? Result, bool TimedOut, List<TestNode>? DiscoveredTests)> RunAssemblyTestsAsync(
        string assembly,
        ITimeoutValueCalculator? timeoutCalc,
        Func<TestNode, bool>? testUidFilter = null,
        Func<TestNodeUpdate, bool>? bailPredicate = null)
    {
        if (!File.Exists(assembly))
        {
            return (null, false, null);
        }

        var discoveredTests = GetDiscoveredTests(assembly);

        // The timeout budget and the timed-out-test attribution must both reflect the tests this
        // session actually targets, not the whole assembly, or a narrow filtered run inherits the
        // full suite's budget and a timeout blames tests that were never requested.
        var targetedTests = testUidFilter is null
            ? discoveredTests
            : discoveredTests?.Where(testUidFilter).ToList();

        TimeSpan? timeout = null;
        if (timeoutCalc is not null && targetedTests is not null)
        {
            timeout = CalculateAssemblyTimeout(targetedTests, timeoutCalc, assembly);
        }

        var (testResults, timedOut) = await RunAssemblyTestsInternalAsync(assembly, testUidFilter, timeout, bailPredicate).ConfigureAwait(false);

        return (testResults as TestRunResult, timedOut, targetedTests);
    }

    internal async Task<(ITestRunResult Result, bool TimedOut)> RunAssemblyTestsInternalAsync(
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

                var (testResults, timedOut) = await server.RunTestsAsync(testsToRun, timeout, bailPredicate).ConfigureAwait(false);

                var duration = DateTime.UtcNow - startTime;
                var result = BuildTestRunResult(testResults, tests?.Count ?? 0, duration);

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
            .Select(NormalizeToDiscoveredUid)
            .Distinct()
            .ToList();

        var timedOutTests = finishedTests
            .Where(x => TestNodeStates.IsTimeout(x.Node.ExecutionState))
            .Select(NormalizeToDiscoveredUid)
            .Distinct()
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

        var executedUids = finishedTests.Select(NormalizeToDiscoveredUid).Distinct().ToList();
        var executedTests = totalDiscoveredTests > 0 && executedUids.Count >= totalDiscoveredTests
            ? TestIdentifierList.EveryTest()
            : new TestIdentifierList(executedUids);

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

    /// <summary>
    /// Maps a run-time test node back to the discovered test it belongs to. Theories whose rows
    /// only materialize at run time report row-level uids that discovery never saw; verdicts and
    /// coverage are keyed by discovered uids, so an unmapped row would make its kill invisible
    /// (row uid ∉ any assessing set). The MTP protocol links each update to its parent node, and
    /// the parent of a run-time-expanded row is the discovered theory method.
    /// </summary>
    private string NormalizeToDiscoveredUid(TestNodeUpdate update)
    {
        lock (_discoveryLock)
        {
            if (_testDescriptions.ContainsKey(update.Node.Uid))
            {
                return update.Node.Uid;
            }

            if (!string.IsNullOrEmpty(update.ParentUid) && _testDescriptions.ContainsKey(update.ParentUid))
            {
                return update.ParentUid;
            }
        }

        return update.Node.Uid;
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
            }

            // Clean up temp files
            try
            {
                if (File.Exists(_mutantFilePath))
                {
                    File.Delete(_mutantFilePath);
                }
            }
            catch (Exception ex)
            {
                // Ignore cleanup errors
                _logger.LogWarning(ex, "{RunnerId}: Failed to clean up temp files", RunnerId);
            }
            DeleteCoverageFiles();
        }
        _disposed = true;
    }
}


