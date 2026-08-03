using Moq;
using Shouldly;
using Stryker.Abstractions;
using Stryker.Abstractions.Options;
using Stryker.Abstractions.Testing;
using Stryker.TestRunner.Tests;

namespace Stryker.TestRunner.MicrosoftTestPlatform.UnitTest;

[TestClass]
public class MutationBatchPlannerTests
{
    [TestMethod]
    public void BroadOrdinarySingletonUsesBroadSessionLimit()
    {
        var mutant = CreateMutant(TestIdentifierList.EveryTest());

        MutationBatchPlanner.RequiresBroadSessionLimit([mutant]).ShouldBeTrue();
    }

    [TestMethod]
    public void ProcessIsolatedSingletonDoesNotUseBroadSessionLimit()
    {
        var mutant = CreateMutant(
            TestIdentifierList.EveryTest(),
            mustBeTestedInIsolation: true);

        MutationBatchPlanner.RequiresBroadSessionLimit([mutant]).ShouldBeFalse();
    }

    [TestMethod]
    public void NarrowOrPackedWorkDoesNotUseBroadSessionLimit()
    {
        var narrow = CreateMutant(new TestIdentifierList("test-1"));
        var second = CreateMutant(new TestIdentifierList("test-2"));

        MutationBatchPlanner.RequiresBroadSessionLimit([narrow]).ShouldBeFalse();
        MutationBatchPlanner.RequiresBroadSessionLimit([narrow, second]).ShouldBeFalse();
    }

    [TestMethod]
    public void WaveBatchesAreSpreadAcrossTwiceTheWorkerCount()
    {
        var options = new Mock<IStrykerOptions>();
        options.SetupGet(candidate => candidate.OptimizationMode)
            .Returns(OptimizationModes.CoverageBasedTest);
        options.SetupGet(candidate => candidate.Concurrency).Returns(4);
        var mutants = Enumerable.Range(0, 100)
            .Select(index => CreateMutant(new TestIdentifierList($"test-{index}")))
            .ToList();

        var groups = MutationBatchPlanner.Build(options.Object, mutants).ToList();

        groups.Sum(group => group.Count).ShouldBe(mutants.Count);
        groups.Count.ShouldBe(8);
        groups.Max(group => group.Count).ShouldBe(13);
    }

    [TestMethod]
    public void OverlappingOrdinaryMutantsShareWaveBatches()
    {
        var options = new Mock<IStrykerOptions>();
        options.SetupGet(candidate => candidate.OptimizationMode)
            .Returns(OptimizationModes.CoverageBasedTest);
        options.SetupGet(candidate => candidate.Concurrency).Returns(1);
        var mutants = Enumerable.Range(0, 4)
            .Select(_ => CreateMutant(new TestIdentifierList("shared-test")))
            .ToList();

        var groups = MutationBatchPlanner.Build(options.Object, mutants).ToList();

        groups.Count.ShouldBe(2);
        groups.ShouldAllBe(group => group.Count == 2);
    }

    [TestMethod]
    public void EveryTestOrdinaryMutantsShareWaveBatches()
    {
        var options = new Mock<IStrykerOptions>();
        options.SetupGet(candidate => candidate.OptimizationMode)
            .Returns(OptimizationModes.CoverageBasedTest);
        options.SetupGet(candidate => candidate.Concurrency).Returns(1);
        var mutants = Enumerable.Range(0, 4)
            .Select(_ => CreateMutant(TestIdentifierList.EveryTest()))
            .ToList();

        var groups = MutationBatchPlanner.Build(options.Object, mutants).ToList();

        groups.Count.ShouldBe(2);
        groups.ShouldAllBe(group => group.Count == 2);
    }

    [TestMethod]
    public void DisjointIsolationMutantsShareOneFreshProcessGroup()
    {
        var options = new Mock<IStrykerOptions>();
        options.SetupGet(candidate => candidate.OptimizationMode)
            .Returns(OptimizationModes.CoverageBasedTest);
        var mutants = Enumerable.Range(0, 16)
            .Select(index => CreateMutant(
                new TestIdentifierList($"test-{index}"),
                mustBeTestedInIsolation: true))
            .ToList();

        var groups = MutationBatchPlanner.Build(options.Object, mutants).ToList();

        groups.Count.ShouldBe(1);
        groups[0].ShouldBe(mutants);
    }

    [TestMethod]
    public void IsolationMutantsNeverShareAGroupWithOrdinaryMutants()
    {
        var options = new Mock<IStrykerOptions>();
        options.SetupGet(candidate => candidate.OptimizationMode)
            .Returns(OptimizationModes.CoverageBasedTest);
        var isolated = CreateMutant(
            new TestIdentifierList("isolated-test"),
            mustBeTestedInIsolation: true);
        var ordinary = CreateMutant(new TestIdentifierList("ordinary-test"));

        var groups = MutationBatchPlanner.Build(options.Object, [isolated, ordinary]).ToList();

        groups.Count.ShouldBe(2);
        groups.ShouldContain(group => group.Count == 1 && ReferenceEquals(group[0], isolated));
        groups.ShouldContain(group => group.Count == 1 && ReferenceEquals(group[0], ordinary));
    }

    [TestMethod]
    public void IsolationMutantsWithOverlappingTestsKeepDistinctFreshProcesses()
    {
        var options = new Mock<IStrykerOptions>();
        options.SetupGet(candidate => candidate.OptimizationMode)
            .Returns(OptimizationModes.CoverageBasedTest);
        var mutants = Enumerable.Range(0, 2)
            .Select(_ => CreateMutant(
                new TestIdentifierList("shared-test"),
                mustBeTestedInIsolation: true))
            .ToList();

        var groups = MutationBatchPlanner.Build(options.Object, mutants).ToList();

        groups.Count.ShouldBe(2);
        groups.ShouldAllBe(group => group.Count == 1);
    }

    private static IMutant CreateMutant(
        ITestIdentifiers assessingTests,
        bool mustBeTestedInIsolation = false)
    {
        var mutant = new Mock<IMutant>();
        mutant.SetupGet(candidate => candidate.AssessingTests).Returns(assessingTests);
        mutant.SetupGet(candidate => candidate.MustBeTestedInIsolation)
            .Returns(mustBeTestedInIsolation);
        return mutant.Object;
    }
}
