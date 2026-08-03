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
    public void WaveAssignmentsBoundTheWorkSentToOneTestSession()
    {
        var states = Enumerable.Range(0, 100)
            .Select(mutantId =>
                (mutantId, (IReadOnlyList<string>)[$"test-{mutantId}"]));

        var assignments = SingleMicrosoftTestPlatformRunner.BuildWaveAssignments(
            states,
            sliceSize: 1);

        assignments.Count.ShouldBe(64);
    }

    [TestMethod]
    public void WaveAssignmentsDoNotSplitOneRuntimeExpandedMethodAcrossMutants()
    {
        var states = new[]
        {
            (1, (IReadOnlyList<string>)["theory-row-1"]),
            (2, (IReadOnlyList<string>)["theory-row-2"]),
            (3, (IReadOnlyList<string>)["fact"]),
        };

        var assignments = SingleMicrosoftTestPlatformRunner.BuildWaveAssignments(
            states,
            sliceSize: 1,
            testUid => testUid.StartsWith("theory", StringComparison.Ordinal)
                ? "method\ttheory"
                : $"method\t{testUid}");

        assignments["theory-row-1"].ShouldBe(1);
        assignments.ContainsKey("theory-row-2").ShouldBeFalse();
        assignments["fact"].ShouldBe(3);
    }

    [TestMethod]
    public void IdentitylessWaveFallbackIsLimitedToAssignedMutants()
    {
        var assignments = Enumerable.Range(0, 64)
            .ToDictionary(index => $"test-{index}", index => index);

        var fallback = SingleMicrosoftTestPlatformRunner.GetWaveFallbackMutantIds(
            assignments,
            sessionTimedOut: true,
            sessionHadRuntimeIssue: false,
            attributedTimedOutMutantIds: []);

        fallback.ShouldBe(assignments.Values.ToHashSet(), ignoreOrder: true);
        fallback.ShouldNotContain(64);
    }

    [TestMethod]
    public void AttributedWaveTimeoutDoesNotTriggerConfirmationFallback()
    {
        var assignments = new Dictionary<string, int>
        {
            ["test-1"] = 1,
            ["test-2"] = 2,
        };

        var fallback = SingleMicrosoftTestPlatformRunner.GetWaveFallbackMutantIds(
            assignments,
            sessionTimedOut: true,
            sessionHadRuntimeIssue: false,
            attributedTimedOutMutantIds: [1]);

        fallback.ShouldBeEmpty();
    }

    [TestMethod]
    public void EmptyWaveOutcomeConfirmsOnlyAssignedMutants()
    {
        var assignments = new Dictionary<string, int>
        {
            ["test-1"] = 1,
            ["test-2"] = 2,
        };

        var fallback = SingleMicrosoftTestPlatformRunner.GetWaveFallbackMutantIds(
            assignments,
            sessionTimedOut: false,
            sessionHadRuntimeIssue: false,
            attributedTimedOutMutantIds: [],
            waveMadeProgress: false);

        fallback.ShouldBe([1, 2], ignoreOrder: true);
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
