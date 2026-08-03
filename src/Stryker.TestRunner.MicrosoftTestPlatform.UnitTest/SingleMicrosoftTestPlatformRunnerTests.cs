using Shouldly;
using Stryker.TestRunner.MicrosoftTestPlatform.Models;

namespace Stryker.TestRunner.MicrosoftTestPlatform.UnitTest;

[TestClass]
public class SingleMicrosoftTestPlatformRunnerTests
{
    [TestMethod]
    public void CompleteWaveAssignmentsAdvanceContendedMutants()
    {
        var remaining = new Dictionary<int, List<string>>
        {
            [1] = ["shared", "one"],
            [2] = ["shared", "two"],
            [3] = ["shared", "three"],
        };
        var assignments = new List<IReadOnlyDictionary<string, int>>();

        while (remaining.Values.Any(tests => tests.Count > 0))
        {
            var wave = SingleMicrosoftTestPlatformRunner.BuildWaveAssignments(
                remaining.Select(pair => (pair.Key, (IReadOnlyList<string>)pair.Value)),
                sliceSize: 2);
            assignments.Add(wave);
            foreach (var (testUid, mutantId) in wave)
            {
                remaining[mutantId].Remove(testUid);
            }
        }

        assignments.Count.ShouldBe(3);
        assignments.ShouldAllBe(wave => wave.Count == wave.Keys.Distinct().Count());
        assignments.SelectMany(wave => wave.Values).Distinct().Count().ShouldBe(3);
    }

    [TestMethod]
    public void FirstSingleMutantAttemptBailsOnATerminalTestVerdict()
    {
        var predicate = SingleMicrosoftTestPlatformRunner.CreateSingleMutantBailPredicate(
            isRuntimeRetry: false);

        predicate.ShouldNotBeNull();
        predicate(new TestNodeUpdate(
            new TestNode("failed", "Failed", "action", TestNodeStates.Failed),
            "parent")).ShouldBeTrue();
        predicate(new TestNodeUpdate(
            new TestNode("passed", "Passed", "action", TestNodeStates.Passed),
            "parent")).ShouldBeFalse();
    }

    [TestMethod]
    public void RuntimeRetryCollectsTheCompleteBoundedTestSet()
    {
        SingleMicrosoftTestPlatformRunner.CreateSingleMutantBailPredicate(
            isRuntimeRetry: true).ShouldBeNull();
    }

    [TestMethod]
    public void OrdinaryWaveConfirmationReusesTheWarmHostUntilARuntimeRetry()
    {
        SingleMicrosoftTestPlatformRunner.UseFreshProcessForOrdinaryConfirmation(
            attempt: 1).ShouldBeFalse();
        SingleMicrosoftTestPlatformRunner.UseFreshProcessForOrdinaryConfirmation(
            attempt: 2).ShouldBeTrue();
    }
}
