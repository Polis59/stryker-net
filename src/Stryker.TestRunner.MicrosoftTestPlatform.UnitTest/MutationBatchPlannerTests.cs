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
    public void PackedGroupsHaveBoundedMutantCounts()
    {
        var options = new Mock<IStrykerOptions>();
        options.SetupGet(candidate => candidate.OptimizationMode)
            .Returns(OptimizationModes.CoverageBasedTest);
        var mutants = Enumerable.Range(0, 100)
            .Select(index => CreateMutant(new TestIdentifierList($"test-{index}")))
            .ToList();

        var groups = MutationBatchPlanner.Build(options.Object, mutants).ToList();

        groups.Sum(group => group.Count).ShouldBe(mutants.Count);
        groups.Max(group => group.Count).ShouldBe(16);
        groups.ShouldAllBe(group => group.Count <= 16);
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
