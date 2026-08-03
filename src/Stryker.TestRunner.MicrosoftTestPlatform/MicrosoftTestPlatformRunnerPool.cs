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
    // Counts available runners so checkout can await without a polling interval. The
    // semaphore is released once per runner during initialization and once per return;
    // a released count therefore always matches a runner sitting in _availableRunners.
    private readonly SemaphoreSlim _runnerAvailable = new(0);
    private readonly ConcurrentBag<SingleMicrosoftTestPlatformRunner> _availableRunners = new();
    // Instance-scoped: there is one pool per run, and a static list would leak disposed runners
    // across pool instances (notably between unit tests, and across solution-project pools).
    private readonly ConcurrentBag<SingleMicrosoftTestPlatformRunner> _allRunners = new();
    private bool _disposed;
    private readonly ILogger _logger;
    private readonly int _countOfRunners;
    private readonly TestSet _testSet = new();
    private readonly Dictionary<string, List<TestNode>> _testsByAssembly = new();
    private readonly Dictionary<string, MtpTestDescription> _testDescriptions = new();
    private readonly object _discoveryLock = new();
    private readonly object _coverageCacheLock = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<bool>>> _discoveryCache =
        new(StringComparer.Ordinal);
    private readonly Dictionary<
        CoverageConfidence,
        IReadOnlyList<ICoverageRunResult>> _perTestCoverageCache = [];
    private readonly ConcurrentDictionary<string, Lazy<Task<ITestRunResult>>> _initialRunCache =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _initialRunGate = new(1, 1);
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
            _runnerAvailable.Release();
        });
    }

    public async Task<bool> DiscoverTestsAsync(string assembly)
    {
        if (string.IsNullOrEmpty(assembly) || !File.Exists(assembly))
        {
            return false;
        }

        var path = Path.GetFullPath(assembly);
        var candidate = new Lazy<Task<bool>>(
            () => RunThisAsync(runner => runner.DiscoverTestsAsync(path)),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var discovery = _discoveryCache.GetOrAdd(path, candidate);
        if (!ReferenceEquals(discovery, candidate))
        {
            _logger.LogInformation("Reusing test discovery for {TestAssembly}", path);
        }

        return await discovery.Value.ConfigureAwait(false);
    }

    public ITestSet GetTests(IProjectAndTests project) => _testSet;

    public async Task<ITestRunResult> InitialTestAsync(IProjectAndTests project)
    {
        var assemblies = project.GetTestAssemblies()
            .Select(Path.GetFullPath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (!assemblies.Any())
        {
            return new TestRunResult(false, "No test assemblies found");
        }

        // Solution mode asks for one initial run per mutated project even when those
        // projects share the same test assemblies. The unmutated baseline and its
        // measured durations are properties of that exact assembly set, not of the
        // source project Stryker happens to be initializing. Share one task so
        // concurrent project initialization pays for the suite once. Different test
        // assembly sets remain serialized because each suite already parallelizes
        // internally and running them together oversubscribes the machine.
        var key = string.Join('\n', assemblies);
        var candidate = new Lazy<Task<ITestRunResult>>(
            () => RunInitialTestAsync(project),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var initialRun = _initialRunCache.GetOrAdd(key, candidate);
        if (!ReferenceEquals(initialRun, candidate))
        {
            _logger.LogInformation(
                "Reusing the initial test run for {AssemblyCount} shared test assemblies",
                assemblies.Length);
        }

        return await initialRun.Value.ConfigureAwait(false);
    }

    private async Task<ITestRunResult> RunInitialTestAsync(IProjectAndTests project)
    {
        await _initialRunGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var results = await RunThisAsync(runner => runner.InitialTestAsync(project)).ConfigureAwait(false);

            // Reset once after the shared baseline. Every project awaiting this task
            // receives the same complete result after the reset has finished.
            ResetTestProcesses();

            return results;
        }
        finally
        {
            _initialRunGate.Release();
        }
    }

    public IEnumerable<ICoverageRunResult> CaptureCoverage(IProjectAndTests project)
    {
        if (_options.OptimizationMode.HasFlag(OptimizationModes.CoverageBasedTest))
        {
            var confidence = _options.OptimizationMode.HasFlag(OptimizationModes.CaptureCoveragePerTest)
                ? CoverageConfidence.Exact
                : CoverageConfidence.Normal;
            try
            {
                return CaptureCoverageTestByTest(project, confidence);
            }
            catch (Exception exception) when (IsCoverageLifecycleSinkUnavailable(exception))
            {
                // Exact class-bounded coverage is an optional protocol supplied by a cooperating
                // xUnit test assembly. Stock xUnit projects do not install that lifecycle sink.
                // Fall back to Stryker's conservative aggregate coverage: every covered mutant is
                // assigned to every test, which preserves correctness at the cost of wider test
                // selections during the mutation phase.
                _logger.LogWarning(
                    "The test assembly does not provide the xUnit coverage lifecycle sink; " +
                    "falling back to conservative aggregate coverage.");
                return CaptureCoverageInOneGo(project);
            }
        }

        return CaptureCoverageInOneGo(project);
    }

    internal static bool IsCoverageLifecycleSinkUnavailable(Exception exception)
    {
        if (exception is CoverageLifecycleSinkUnavailableException)
        {
            return true;
        }

        if (exception is AggregateException aggregate)
        {
            var innerExceptions = aggregate.Flatten().InnerExceptions;
            return innerExceptions.Count > 0 &&
                innerExceptions.All(IsCoverageLifecycleSinkUnavailable);
        }

        return exception.InnerException is not null &&
            IsCoverageLifecycleSinkUnavailable(exception.InnerException);
    }

    private IEnumerable<ICoverageRunResult> CaptureCoverageInOneGo(IProjectAndTests project)
    {
        _logger.LogInformation("Starting aggregate coverage capture for MTP runner");

        // Enable coverage mode on all runners
        foreach (var runner in _allRunners)
        {
            runner.SetCoverageMode(true, perTest: false);
        }

        try
        {
            var testResult = RunThisAsync(runner => runner.InitialTestAsync(project)).GetAwaiter().GetResult();

            if (testResult.FailingTests.IsEveryTest)
            {
                _logger.LogWarning("Coverage test run failed: {Message}", testResult.ResultMessage);
            }

            ResetTestProcesses();

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

            _logger.LogInformation("Aggregate coverage capture complete: {CoveredCount} mutations covered, {StaticCount} static mutations",
                allCoveredMutants.Count, allStaticMutants.Count);

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
            foreach (var runner in _availableRunners)
            {
                runner.SetCoverageMode(false);
            }
        }
    }

    private IEnumerable<ICoverageRunResult> CaptureCoverageTestByTest(
        IProjectAndTests project, CoverageConfidence confidence)
    {
        lock (_coverageCacheLock)
        {
            if (_perTestCoverageCache.TryGetValue(confidence, out var cached))
            {
                _logger.LogInformation(
                    "Reusing {TestCount} exact per-test coverage mappings from the campaign snapshot",
                    cached.Count);
                return cached;
            }
        }

        _logger.LogInformation("Starting exact per-test coverage capture for MTP runner");

        foreach (var runner in _availableRunners)
        {
            runner.SetCoverageMode(true);
        }

        try
        {
            var allTests = new List<(string Assembly, TestNode Test)>();
            foreach (var (assembly, tests) in _testsByAssembly)
            {
                foreach (var test in tests)
                {
                    if (_testDescriptions.ContainsKey(test.Uid))
                    {
                        allTests.Add((assembly, test));
                    }
                }
            }

            var coverageGroups = allTests
                .GroupBy(test => (test.Assembly, Boundary: GetCoverageBoundary(test.Test)))
                .Select(group => (
                    group.Key.Assembly,
                    group.Key.Boundary,
                    Tests: (IReadOnlyList<TestNode>)group.Select(test => test.Test).ToList()))
                .ToList();

            _logger.LogInformation(
                "Capturing per-test coverage for {TestCount} tests in {GroupCount} collectible class boundaries across {AssemblyCount} assemblies",
                allTests.Count,
                coverageGroups.Count,
                _testsByAssembly.Count);

            var results = new ConcurrentBag<ICoverageRunResult>();

            Parallel.ForEach(coverageGroups,
                new ParallelOptions { MaxDegreeOfParallelism = _countOfRunners },
                coverageGroup =>
                {
                    var groupResults = RunThisAsync(async runner =>
                        await runner.RunTestGroupForCoverageAsync(
                            coverageGroup.Assembly,
                            coverageGroup.Tests,
                            confidence)
                            .ConfigureAwait(false))
                        .GetAwaiter().GetResult();

                    foreach (var result in groupResults)
                    {
                        results.Add(result);
                    }
                });

            var captured = results.ToList();
            _logger.LogInformation(
                "Per-test coverage capture complete: {TestCount} exact mappings captured from {GroupCount} collectible contexts",
                captured.Count,
                coverageGroups.Count);

            // Stryker 4.16 injects every solution-project assembly before it
            // sequentially asks this one runner pool to assign coverage for
            // each project. The capture therefore contains the complete mutant
            // universe; recomputing it per project repeats identical test work.
            lock (_coverageCacheLock)
            {
                _perTestCoverageCache[confidence] = captured;
            }

            return captured;
        }
        finally
        {
            foreach (var runner in _availableRunners)
            {
                runner.SetCoverageMode(false);
            }
        }
    }

    internal static string GetCoverageBoundary(TestNode test)
    {
        var displayName = test.DisplayName;
        var argumentsStart = displayName.IndexOf('(');
        var methodEnd = argumentsStart >= 0 ? argumentsStart : displayName.Length;
        if (methodEnd == 0)
        {
            return test.Uid;
        }

        var classSeparator = displayName.LastIndexOf('.', methodEnd - 1);

        // Standard xUnit display names are namespace-qualified class and method
        // names. A custom display name has no reliable class boundary, so it
        // remains a singleton rather than being grouped unsafely.
        return classSeparator > 0
            ? displayName[..classSeparator]
            : test.Uid;
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
        // The semaphore's count mirrors _availableRunners, so a successful wait
        // guarantees the bag holds a runner. Awaiting (instead of the previous
        // one-second AutoResetEvent poll, which also blocked a thread-pool thread)
        // hands a returned runner to the next waiter immediately.
        if (!await _runnerAvailable.WaitAsync(TimeSpan.FromMinutes(5)).ConfigureAwait(false))
        {
            throw new TimeoutException($"Timed out waiting for an available test runner after 300 seconds. Available runners: {_availableRunners.Count}, Total runners: {_countOfRunners}");
        }

        if (!_availableRunners.TryTake(out var runner))
        {
            _runnerAvailable.Release();
            throw new InvalidOperationException("The runner pool signalled availability but held no runner.");
        }

        try
        {
            return await task(runner).ConfigureAwait(false);
        }
        finally
        {
            _availableRunners.Add(runner);
            _runnerAvailable.Release();
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
        _runnerAvailable.Dispose();
        _initialRunGate.Dispose();
    }
}
