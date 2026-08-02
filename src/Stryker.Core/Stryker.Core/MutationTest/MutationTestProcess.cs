using System;
using System.Collections.Generic;
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
        var mutantGroups = BuildMutantGroupsForTest(mutantsToTest.ToList());

        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = _options.Concurrency };
        using var workerSlots = new SemaphoreSlim(_options.Concurrency, _options.Concurrency);

        await Parallel.ForEachAsync(mutantGroups, parallelOptions, async (mutants, cancellationToken) =>
        {
            var requiredSlots = TestRunner.MicrosoftTestPlatform.MutationBatchPlanner
                .GetRequiredWorkerSlots(mutants, _options.Concurrency);
            var acquiredSlots = 0;
            try
            {
                while (acquiredSlots < requiredSlots)
                {
                    await workerSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
                    acquiredSlots++;
                }

                var reportedMutants = new HashSet<IMutant>();

                await _mutationTestExecutor.TestAsync(Input.SourceProjectInfo, mutants,
                    Input.InitialTestRun.TimeoutValueCalculator,
                    (testedMutants, tests, ranTests, outTests) =>
                        TestUpdateHandler(testedMutants, tests, ranTests, outTests, reportedMutants)).ConfigureAwait(false);

                OnMutantsTested(mutants, reportedMutants);
            }
            finally
            {
                if (acquiredSlots > 0)
                {
                    workerSlots.Release(acquiredSlots);
                }
            }
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
