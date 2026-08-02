using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Abstractions;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Stryker.Abstractions;
using Stryker.Abstractions.Exceptions;
using Stryker.Abstractions.Options;
using Stryker.Abstractions.ProjectComponents;
using Stryker.Abstractions.Reporting;
using Stryker.Abstractions.Testing;
using Stryker.Core.CoverageAnalysis;
using Stryker.TestRunner.Tests;
using Stryker.Utilities.Buildalyzer;

namespace Stryker.Core.MutationTest;

public interface IMutationTestProcess
{
    MutationTestInput Input { get; }
    void Initialize(MutationTestInput input, IStrykerOptions options, IReporter reporter);
    void Mutate();
    Task<StrykerRunResult> TestAsync(IEnumerable<IMutant> mutantsToTest);
    void Restore();
    void GetCoverage();
    void FilterMutants();
}

public class MutationTestProcess : IMutationTestProcess
{
    public MutationTestInput Input { get; set; }

    private IStrykerOptions _options;
    private IReadOnlyProjectComponent _projectContents;
    private IReporter _reporter;
    private readonly ILogger _logger;
    private readonly IMutationTestExecutor _mutationTestExecutor;
    private readonly ICoverageAnalyser _coverageAnalyser;
    private readonly IMutationProcess _mutationProcess;

    public MutationTestProcess(
        IMutationTestExecutor executor,
        ICoverageAnalyser coverageAnalyzer,
        IMutationProcess mutationProcess,
        ILogger<MutationTestProcess> logger)
    {
        _mutationTestExecutor = executor ?? throw new ArgumentNullException(nameof(executor));
        _mutationProcess = mutationProcess ?? throw new ArgumentNullException(nameof(mutationProcess));
        _coverageAnalyser = coverageAnalyzer ?? throw new ArgumentNullException(nameof(coverageAnalyzer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Initialize(MutationTestInput input, IStrykerOptions options, IReporter reporter)
    {
        Input = input;
        _options = options;
        _reporter = reporter;
        _projectContents = input.SourceProjectInfo.ProjectContents;
        Input.TestProjectsInfo.BackupOriginalAssembly(Input.SourceProjectInfo.AnalyzerResult);
    }

    public void Mutate()
    {
        _mutationProcess.Mutate(Input, _options);
    }

    public void FilterMutants() => _mutationProcess.FilterMutants(Input);

    public async Task<StrykerRunResult> TestAsync(IEnumerable<IMutant> mutantsToTest)
    {
        if (!MutantsToTest(mutantsToTest))
        {
            return new StrykerRunResult(_options, double.NaN);
        }

        await TestMutantsAsync(mutantsToTest).ConfigureAwait(false);

        return new StrykerRunResult(_options, _projectContents.GetMutationScore());
    }

    public void Restore() => Input.TestProjectsInfo.RestoreOriginalAssembly(Input.SourceProjectInfo.AnalyzerResult);

    private async Task TestMutantsAsync(IEnumerable<IMutant> mutantsToTest)
    {
        var mutantGroups = BuildMutantGroupsForTest(mutantsToTest.ToList()).ToList();
        var broadGroupCount = mutantGroups.Count(
            TestRunner.MicrosoftTestPlatform.MutationBatchPlanner.RequiresBroadSessionLimit);
        var packedGroups = mutantGroups.Where(group => group.Count > 1).ToList();
        var totalMutantCount = mutantGroups.Sum(group => group.Count);
        var projectFilePath = Input.SourceProjectInfo.AnalyzerResult.ProjectFilePath;
        var indexedGroups = mutantGroups
            .Select((mutants, index) => (Index: index + 1, Mutants: mutants))
            .ToList();
        var campaignTimer = Stopwatch.StartNew();
        var completedGroupCount = 0;
        var completedMutantCount = 0;
        _logger.LogInformation(
            "Mutation execution plan: {MutantCount} mutants in {GroupCount} groups; " +
            "{BroadGroupCount} broad singletons, {PackedGroupCount} packed groups, " +
            "largest packed group {LargestPackedGroupCount}",
            totalMutantCount,
            mutantGroups.Count,
            broadGroupCount,
            packedGroups.Count,
            packedGroups.Count == 0 ? 0 : packedGroups.Max(group => group.Count));

        await MutationWorkLaneScheduler.RunAsync(
            indexedGroups,
            group => TestRunner.MicrosoftTestPlatform.MutationBatchPlanner.RequiresBroadSessionLimit(group.Mutants),
            _options.Concurrency,
            async (group, _) =>
            {
                var mutants = group.Mutants;
                var groupTimer = Stopwatch.StartNew();
                _logger.LogInformation(
                    "Mutation group started: project {ProjectFilePath}, group {GroupIndex}/{TotalGroupCount}, mutants {GroupMutantCount}",
                    projectFilePath,
                    group.Index,
                    indexedGroups.Count,
                    mutants.Count);
                var reportedMutants = new HashSet<IMutant>();

                await _mutationTestExecutor.TestAsync(Input.SourceProjectInfo, mutants,
                    Input.InitialTestRun.TimeoutValueCalculator,
                    (testedMutants, tests, ranTests, outTests) =>
                        TestUpdateHandler(testedMutants, tests, ranTests, outTests, reportedMutants)).ConfigureAwait(false);

                OnMutantsTested(mutants, reportedMutants);
                var groupsCompleted = Interlocked.Increment(ref completedGroupCount);
                var mutantsCompleted = Interlocked.Add(ref completedMutantCount, mutants.Count);
                _logger.LogInformation(
                    "Mutation group completed: project {ProjectFilePath}, group {GroupIndex}/{TotalGroupCount}, " +
                    "mutants {GroupMutantCount}, completed groups {CompletedGroupCount}/{TotalGroupCount}, " +
                    "completed mutants {CompletedMutantCount}/{TotalMutantCount}, duration {GroupDurationMs} ms, elapsed {ElapsedMs} ms",
                    projectFilePath,
                    group.Index,
                    indexedGroups.Count,
                    mutants.Count,
                    groupsCompleted,
                    indexedGroups.Count,
                    mutantsCompleted,
                    totalMutantCount,
                    groupTimer.ElapsedMilliseconds,
                    campaignTimer.ElapsedMilliseconds);
            }).ConfigureAwait(false);
    }

    private bool TestUpdateHandler(IEnumerable<IMutant> testedMutants, ITestIdentifiers failedTests, ITestIdentifiers ranTests,
        ITestIdentifiers timedOutTest, ISet<IMutant> reportedMutants)
    {
        var testsFailingInitially = Input.InitialTestRun.Result.FailingTests.GetIdentifiers().ToHashSet();
        var continueTestRun = _options.OptimizationMode.HasFlag(OptimizationModes.DisableBail);
        if (testsFailingInitially.Count > 0 && failedTests.GetIdentifiers().Any(id => testsFailingInitially.Contains(id)))
        {
            // some of the failing tests where failing without any mutation
            // we discard those tests
            failedTests = new TestIdentifierList(
                failedTests.GetIdentifiers().Where(t => !testsFailingInitially.Contains(t)));
        }

        foreach (var mutant in testedMutants)
        {
            mutant.AnalyzeTestRun(failedTests, ranTests, timedOutTest, false, false);

            if (mutant.ResultStatus == MutantStatus.Pending)
            {
                continueTestRun = true; // Not all mutants in this group were tested so we continue
            }

            OnMutantTested(mutant, reportedMutants); // Report on mutant that has been tested
        }

        return continueTestRun;
    }

    private void OnMutantsTested(IEnumerable<IMutant> mutants, ISet<IMutant> reportedMutants)
    {
        foreach (var mutant in mutants)
        {
            if (mutant.ResultStatus == MutantStatus.Pending)
            {
                _logger.LogWarning("Mutation {Id} was not fully tested.", mutant.Id);
            }

            OnMutantTested(mutant, reportedMutants);
        }
    }

    private void OnMutantTested(IMutant mutant, ISet<IMutant> reportedMutants)
    {
        if (mutant.ResultStatus == MutantStatus.Pending || reportedMutants.Contains(mutant))
        {
            // skip duplicates or useless notifications
            return;
        }

        _reporter?.OnMutantTested(mutant);
        reportedMutants.Add(mutant);
    }

    private static bool MutantsToTest(IEnumerable<IMutant> mutantsToTest)
    {
        if (!mutantsToTest.Any())
        {
            return false;
        }

        if (mutantsToTest.Any(x => x.ResultStatus != MutantStatus.Pending))
        {
            throw new GeneralStrykerException(
                "Only mutants to run should be passed to the mutation test process. If you see this message please report an issue.");
        }

        return true;
    }

    // The stock packer lets a static or early-activation mutant consume test slots in an
    // otherwise reusable ordinary batch, after which the MTP runner must split the request
    // again for correctness. The isolation-aware planner separates process-isolated mutants
    // before packing ordinary mutants with disjoint assessing tests.
    private IEnumerable<List<IMutant>> BuildMutantGroupsForTest(IReadOnlyCollection<IMutant> mutantsNotRun) =>
        TestRunner.MicrosoftTestPlatform.MutationBatchPlanner.Build(_options, mutantsNotRun);

    public void GetCoverage() => _coverageAnalyser.DetermineTestCoverage(_options, Input.SourceProjectInfo,
        _mutationTestExecutor.TestRunner, _projectContents.Mutants, Input.InitialTestRun.Result.FailingTests);
}

/// <summary>
/// Runs broad and ordinary mutation work in independent producer lanes while
/// sharing one exact worker budget. A broad item waiting for its lane cannot
/// occupy an ordinary producer iteration, which prevents the nested-semaphore
/// starvation caused by one mixed <see cref="Parallel.ForEachAsync{TSource}(IEnumerable{TSource}, ParallelOptions, Func{TSource, CancellationToken, ValueTask})"/>
/// queue.
/// </summary>
internal static class MutationWorkLaneScheduler
{
    internal static async Task RunAsync<T>(
        IReadOnlyCollection<T> work,
        Func<T, bool> isBroad,
        int concurrency,
        Func<T, CancellationToken, Task> execute,
        CancellationToken cancellationToken = default)
    {
        var workerCount = Math.Max(1, concurrency);
        if (workerCount == 1)
        {
            await Parallel.ForEachAsync(
                work,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = 1,
                    CancellationToken = cancellationToken,
                },
                async (item, token) => await execute(item, token).ConfigureAwait(false)).ConfigureAwait(false);
            return;
        }

        var broadWorkerCount = Math.Max(1, workerCount / 2);
        var broad = work.Where(isBroad).ToList();
        var ordinary = work.Where(item => !isBroad(item)).ToList();
        var ordinaryWorkerCount = broad.Count == 0
            ? workerCount
            : workerCount - broadWorkerCount;
        using var broadSlots = new SemaphoreSlim(broadWorkerCount, workerCount);
        using var ordinarySlots = new SemaphoreSlim(ordinaryWorkerCount, workerCount);

        async ValueTask ExecuteBroadAsync(T item, CancellationToken token)
        {
            await broadSlots.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await execute(item, token).ConfigureAwait(false);
            }
            finally
            {
                broadSlots.Release();
            }
        }

        async ValueTask ExecuteOrdinaryAsync(T item, CancellationToken token)
        {
            await ordinarySlots.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await execute(item, token).ConfigureAwait(false);
            }
            finally
            {
                ordinarySlots.Release();
            }
        }

        var broadTask = Parallel.ForEachAsync(
            broad,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = workerCount,
                CancellationToken = cancellationToken,
            },
            ExecuteBroadAsync);
        var ordinaryTask = Parallel.ForEachAsync(
            ordinary,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = workerCount,
                CancellationToken = cancellationToken,
            },
            ExecuteOrdinaryAsync);

        async Task LendBroadCapacityAsync()
        {
            try
            {
                await broadTask.ConfigureAwait(false);
            }
            finally
            {
                if (ordinaryWorkerCount < workerCount)
                {
                    ordinarySlots.Release(workerCount - ordinaryWorkerCount);
                }
            }
        }

        async Task LendOrdinaryCapacityAsync()
        {
            try
            {
                await ordinaryTask.ConfigureAwait(false);
            }
            finally
            {
                if (broadWorkerCount < workerCount)
                {
                    broadSlots.Release(workerCount - broadWorkerCount);
                }
            }
        }

        await Task.WhenAll(
            broadTask,
            ordinaryTask,
            LendBroadCapacityAsync(),
            LendOrdinaryCapacityAsync()).ConfigureAwait(false);
    }
}
