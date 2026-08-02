using Moq;
using Shouldly;
using Stryker.Abstractions;
using Stryker.Abstractions.Testing;
using Stryker.TestRunner.Tests;

namespace Stryker.TestRunner.MicrosoftTestPlatform.UnitTest;

[TestClass]
public class MutationBatchPlannerTests
{
    [TestMethod]
    public void BroadOrdinarySingletonConsumesTwoWorkerSlots()
    {
        var mutant = CreateMutant(TestIdentifierList.EveryTest());

        MutationBatchPlanner.GetRequiredWorkerSlots([mutant], 12).ShouldBe(2);
    }

    [TestMethod]
    public void ProcessIsolatedSingletonConsumesOneWorkerSlot()
    {
        var mutant = CreateMutant(
            TestIdentifierList.EveryTest(),
            mustBeTestedInIsolation: true);

        MutationBatchPlanner.GetRequiredWorkerSlots([mutant], 12).ShouldBe(1);
    }

    [TestMethod]
    public void NarrowOrPackedWorkConsumesOneWorkerSlot()
    {
        var narrow = CreateMutant(new TestIdentifierList("test-1"));
        var second = CreateMutant(new TestIdentifierList("test-2"));

        MutationBatchPlanner.GetRequiredWorkerSlots([narrow], 12).ShouldBe(1);
        MutationBatchPlanner.GetRequiredWorkerSlots([narrow, second], 12).ShouldBe(1);
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
