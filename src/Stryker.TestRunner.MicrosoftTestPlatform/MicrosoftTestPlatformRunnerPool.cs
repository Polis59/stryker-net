using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Stryker.Abstractions;
using Stryker.Abstractions.Options;
using Stryker.Abstractions.Testing;
using Stryker.TestRunner.MicrosoftTestPlatform.Models;
using Stryker.TestRunner.Results;
using Stryker.TestRunner.Tests;
using Stryker.Utilities.Logging;
using static Stryker.Abstractions.Testing.ITestRunner;

namespace Stryker.TestRunner.MicrosoftTestPlatform;

/// <summary>
/// Manages a pool of MicrosoftTestPlatformRunner instances to enable parallel mutation testing
/// with isolated environment variables per runner.
/// </summary>
public sealed class MicrosoftTestPlatformRunnerPool : ITestRunner
{
    private readonly AutoResetEvent _runnerAvailableHandler = new(false);
    private readonly ConcurrentBag<SingleMicrosoftTestPlatformRunner> _availableRunners = new();
    private static readonly List<SingleMicrosoftTestPlatformRunner> _allRunners = new();
    private bool _disposed;
    private readonly ILogger _logger;
    private readonly int _countOfRunners;
    private readonly TestSet _testSet = new();
    private readonly Dictionary<string, List<TestNode>> _testsByAssembly = new();
    private readonly Dictionary<string, MtpTestDescription> _testDescriptions = new();
    private readonly object _discoveryLock = new();
    private readonly ISingleRunnerFactory _runnerFactory;
    private readonly IStrykerOptions _options;

    public IEnumerable<SingleMicrosoftTestPlatformRunner> Runners => _availableRunners;

    public MicrosoftTestPlatformRunnerPool(IStrykerOptions options, ILogger? logger = null, ISingleRunnerFactory? runnerFactory = null)
    {
        _logger = logger ?? ApplicationLogging.LoggerFactory.CreateLogger<MicrosoftTestPlatformRunnerPool>();
        _options = options;
        _countOfRunners = Math.Max(1, options.Concurrency);
        _runnerFactory = runnerFactory ?? new DefaultRunnerFactory();
        _logger.LogWarning("The Microsoft Test Platform testrunner is currently in preview. Results should be verified since this feature is still being tested.");

        Initialize();
    }

    public void ResetTestProcesses()
    {
        _logger.LogDebug("Resetting all test server processes in the pool");
        var tasks = _availableRunners.Select(runner => runner.ResetServerAsync());
        Task.WhenAll(tasks).Wait();
        _logger.LogDebug("All test server processes have been reset");
    }

    private void Initialize()
    {
        // Create and initialize all runners in parallel to speed up startup time
        Parallel.For(0, _countOfRunners, (int i, ParallelLoopState _) =>
        {
            var runner = _runnerFactory.CreateRunner(
                i,
                _testsByAssembly,
                _testDescriptions,
                _testSet,
                _discoveryLock,
                _logger,
                _options);
            _availableRunners.Add(runner);
            _allRunners.Add(runner);
            _runnerAvailableHandler.Set();
        });
    }

    public async Task<bool> DiscoverTestsAsync(string assembly)
    {
        if (string.IsNullOrEmpty(assembly) || !File.Exists(assembly))
        {
            return false;
        }

        return await RunThisAsync(runner => runner.DiscoverTestsAsync(assembly)).ConfigureAwait(false);
    }

    public ITestSet GetTests(IProjectAndTests project) => _testSet;

    public async Task<ITestRunResult> InitialTestAsync(IProjectAndTests project)
    {
        var assemblies = project.GetTestAssemblies();
        if (!assemblies.Any())
        {
            return new TestRunResult(false, "No test assemblies found");
        }

        var results = await RunThisAsync(runner => runner.InitialTestAsync(project)).ConfigureAwait(false);

        // Bail must never trigger on a test that fails without any mutation, so every runner needs
        // the initial run's failing tests. The EveryTest sentinel means the run itself broke; the
        // campaign aborts on that, so there is nothing useful to record.
        if (!results.FailingTests.IsEveryTest)
        {
            var initialFailing = results.FailingTests.GetIdentifiers().ToHashSet(StringComparer.Ordinal);
            foreach (var runner in _allRunners)
            {
                runner.InitialRunFailingTests = initialFailing;
            }
        }

        // reset all test processes after the initial test run
        ResetTestProcesses();

        return results;
    }

    public IEnumerable<ICoverageRunResult> CaptureCoverage(IProjectAndTests project)
    {
        // Per-test coverage is what CoverageBasedTest ("perTest", the default) asks for: each
        // mutant is then assessed only by the tests that actually cover it. The aggregate capture
        // remains the fallback — it attributes every covered mutant to every test, which is safe
        // but forces each mutation session to run the full suite.
        if (_options.OptimizationMode.HasFlag(OptimizationModes.CoverageBasedTest))
        {
            try
            {
                return CaptureCoveragePerTest(project);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Per-test coverage capture failed; falling back to aggregate coverage (every test is assumed to cover every mutant).");
            }
        }

        return CaptureAggregateCoverage(project);
    }

    // Coverage is a property of the test suite, not of the source project under mutation, yet in
    // solution mode every source project's mutation-test process asks for it. Cache the sweep per
    // test-assembly set so the suite runs once instead of once per source project.
    private readonly object _coverageCacheLock = new();
    private (string Key, List<ICoverageRunResult> Results)? _perTestCoverageCache;

    private IEnumerable<ICoverageRunResult> CaptureCoveragePerTest(IProjectAndTests project)
    {
        var cacheKey = string.Join(";", project.GetTestAssemblies().OrderBy(a => a, StringComparer.OrdinalIgnoreCase));
        lock (_coverageCacheLock)
        {
            if (_perTestCoverageCache is { } cached && cached.Key == cacheKey)
            {
                _logger.LogInformation("Reusing per-test mutation coverage captured earlier for the same test assemblies");
                return cached.Results;
            }
        }

        var captured = CaptureCoveragePerTestUncached(project);
        lock (_coverageCacheLock)
        {
            _perTestCoverageCache = (cacheKey, captured);
        }

        return captured;
    }

    private List<ICoverageRunResult> CaptureCoveragePerTestUncached(IProjectAndTests project)
    {
        _logger.LogInformation("Capturing per-test mutation coverage on persistent test hosts");

        foreach (var runner in _allRunners)
        {
            runner.SetCoverageMode(true);
        }

        try
        {
            var results = new List<ICoverageRunResult>();

            foreach (var assembly in project.GetTestAssemblies())
            {
                List<TestNode>? tests;
                lock (_discoveryLock)
                {
                    _testsByAssembly.TryGetValue(assembly, out tests);
                    tests = tests?.ToList();
                }

                if (tests is null || tests.Count == 0)
                {
                    continue;
                }

                // Partition the tests across the pool and let each slice hold one runner for its
                // whole run: one blocking pool acquisition per runner, not one per test.
                var slices = tests
                    .Select((test, index) => (test, index))
                    .GroupBy(pair => pair.index % _countOfRunners)
                    .Select(group => group.Select(pair => pair.test).ToList())
                    .ToList();

                var sliceTasks = slices.Select(slice => RunThisAsync(async runner =>
                {
                    var sliceResults = new List<(string Uid, IReadOnlyList<int> Covered, IReadOnlyList<int> Statics)>(slice.Count);
                    foreach (var test in slice)
                    {
                        var (covered, statics) = await runner.CaptureTestCoverageAsync(assembly, test).ConfigureAwait(false);
                        sliceResults.Add((test.Uid, covered, statics));
                    }

                    return sliceResults;
                })).ToArray();

                foreach (var (uid, covered, statics) in Task.WhenAll(sliceTasks).GetAwaiter().GetResult().SelectMany(r => r))
                {
                    string testId;
                    lock (_discoveryLock)
                    {
                        testId = _testDescriptions.TryGetValue(uid, out var description) ? description.Id : uid;
                    }

                    // Normal confidence is deliberate: the analyser then assesses static mutants
                    // against every test (a warm host cannot observe which tests would rerun a
                    // static initializer) while ordinary mutants get exactly their covering tests.
                    results.Add(CoverageRunResult.Create(testId, CoverageConfidence.Normal, covered, statics, []));
                }
            }

            var distinctCovered = results.SelectMany(r => r.MutationsCovered).Distinct().Count();
            _logger.LogInformation("Per-test coverage capture complete: {TestCount} tests, {CoveredCount} distinct mutations covered",
                results.Count, distinctCovered);

            if (results.Count > 0 && distinctCovered == 0)
            {
                // Individual tests may legitimately cover nothing, but a whole suite covering no
                // mutant at all means the flush protocol never worked; surface it so the caller
                // falls back to aggregate coverage instead of classifying every mutant NoCoverage.
                throw new InvalidOperationException(
                    "Per-test coverage capture produced no coverage for any test; the coverage flush protocol is not functioning.");
            }

            return results;
        }
        finally
        {
            foreach (var runner in _allRunners)
            {
                runner.SetCoverageMode(false);
            }
        }
    }

    private IEnumerable<ICoverageRunResult> CaptureAggregateCoverage(IProjectAndTests project)
    {
        _logger.LogInformation("Starting coverage capture for MTP runner");

        // Enable coverage mode on all runners
        foreach (var runner in _allRunners)
        {
            runner.SetCoverageMode(true);
        }

        try
        {
            // Run all tests with coverage tracking enabled
            var testResult = RunThisAsync(runner => runner.InitialTestAsync(project)).GetAwaiter().GetResult();

            if (testResult.FailingTests.IsEveryTest)
            {
                _logger.LogWarning("Coverage test run failed: {Message}", testResult.ResultMessage);
            }

            // Reset test processes to trigger coverage file flush (process exit writes coverage)
            ResetTestProcesses();

            // Aggregate coverage data from all runners
            var allCoveredMutants = new HashSet<int>();
            var allStaticMutants = new HashSet<int>();

            foreach (var runner in _availableRunners)
            {
                var (coveredMutants, staticMutants) = runner.ReadCoverageData();
                foreach (var mutantId in coveredMutants)
                {
                    allCoveredMutants.Add(mutantId);
                }
                foreach (var mutantId in staticMutants)
                {
                    allStaticMutants.Add(mutantId);
                }
            }

            _logger.LogInformation("Coverage capture complete: {CoveredCount} mutations covered, {StaticCount} static mutations",
                allCoveredMutants.Count, allStaticMutants.Count);

            // For cumulative coverage, we return a single coverage result that applies to all tests
            // Each test is assumed to cover all the mutations that were covered during the full test run
            // Static mutants are marked as such for proper handling during mutation testing
            return _testDescriptions.Values.Select(testDescription =>
                CoverageRunResult.Create(
                    testDescription.Id,
                    CoverageConfidence.Normal,
                    allCoveredMutants,
                    allStaticMutants,
                    []));
        }
        finally
        {
            // Disable coverage mode on all runners for subsequent mutation testing
            foreach (var runner in _availableRunners)
            {
                runner.SetCoverageMode(false);
            }
        }
    }

    public async Task<ITestRunResult> TestMultipleMutantsAsync(
        IProjectAndTests project,
        ITimeoutValueCalculator? timeoutCalc,
        IReadOnlyList<IMutant> mutants,
        TestUpdateHandler? update)
    {
        var assemblies = project.GetTestAssemblies();
        if (!assemblies.Any())
        {
            return new TestRunResult(false, "No test assemblies found");
        }

        return await RunThisAsync(runner => runner.TestMultipleMutantsAsync(project, timeoutCalc, mutants, update)).ConfigureAwait(false);
    }

    private async Task<T> RunThisAsync<T>(Func<SingleMicrosoftTestPlatformRunner, Task<T>> task)
    {
        SingleMicrosoftTestPlatformRunner? runner;

        // Try to get a runner with a timeout to prevent indefinite blocking
        var attempts = 0;
        const int maxWaitTimeSeconds = 300; // 5 minutes max wait
        const int waitIntervalMs = 1000; // Check every second
        var maxAttempts = maxWaitTimeSeconds * 1000 / waitIntervalMs;

        while (!_availableRunners.TryTake(out runner))
        {
            if (!_runnerAvailableHandler.WaitOne(waitIntervalMs))
            {
                attempts++;
                if (attempts >= maxAttempts)
                {
                    throw new TimeoutException($"Timed out waiting for an available test runner after {maxWaitTimeSeconds} seconds. Available runners: {_availableRunners.Count}, Total runners: {_countOfRunners}");
                }

                if (attempts % 30 == 0) // Log every 30 seconds
                {
                    _logger.LogWarning("Waiting for available test runner... ({Attempts}s elapsed, {Available}/{Total} runners available)",
                        attempts, _availableRunners.Count, _countOfRunners);
                }
            }
        }

        try
        {
            return await task(runner).ConfigureAwait(false);
        }
        finally
        {
            _availableRunners.Add(runner);
            _runnerAvailableHandler.Set();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var runner in _allRunners)
        {
            runner.Dispose();
        }
        _runnerAvailableHandler.Dispose();
    }
}

