using Microsoft.CodeAnalysis.CSharp;
using Moq;
using Shouldly;
using Stryker.Abstractions;
using Stryker.TestRunner.MicrosoftTestPlatform.Models;

namespace Stryker.TestRunner.MicrosoftTestPlatform.UnitTest;

[TestClass]
public class SingleMicrosoftTestPlatformRunnerTests
{
    [TestMethod]
    public void WaveTestsRunFastestFirstAndUnknownDurationsLast()
    {
        var durations = new Dictionary<string, TimeSpan>
        {
            ["slow"] = TimeSpan.FromSeconds(10),
            ["fast-b"] = TimeSpan.FromMilliseconds(5),
            ["fast-a"] = TimeSpan.FromMilliseconds(5),
        };

        var ordered = SingleMicrosoftTestPlatformRunner.OrderWaveTestIdentifiers(
            ["slow", "unknown", "fast-b", "fast-a"],
            identifier => durations.TryGetValue(identifier, out var duration)
                ? duration
                : null);

        ordered.ShouldBe(["fast-a", "fast-b", "slow", "unknown"]);
    }

    [TestMethod]
    public void WaveTestsRunProfiledKillerBeforeFasterTests()
    {
        var ordered = SingleMicrosoftTestPlatformRunner.OrderWaveTestIdentifiers(
            ["fast", "killer", "slow"],
            identifier => identifier switch
            {
                "fast" => TimeSpan.FromMilliseconds(1),
                "killer" => TimeSpan.FromSeconds(1),
                _ => TimeSpan.FromSeconds(2),
            },
            identifier => identifier == "killer" ? 1 : 0);

        ordered.ShouldBe(["killer", "fast", "slow"]);
    }

    [TestMethod]
    public void ProfiledWaveDefersFallbackTestsUntilTheKillerHasRun()
    {
        var plan = SingleMicrosoftTestPlatformRunner.BuildPrioritizedWaveTestPlan(
            ["fast-fallback", "killer", "slow-fallback"],
            identifier => identifier == "fast-fallback"
                ? TimeSpan.FromMilliseconds(1)
                : TimeSpan.FromSeconds(1),
            identifier => identifier == "killer" ? 1 : 0);

        plan.Priority.ShouldBe(["killer"]);
        plan.Fallback.ShouldBe(["fast-fallback", "slow-fallback"]);
    }

    [TestMethod]
    public void KnownTimeoutTestsMoveBehindUntriedFallbackTests()
    {
        var identifiers = new List<string> { "timeout-a", "fast", "timeout-b", "slow" };

        SingleMicrosoftTestPlatformRunner.DeprioritizeKnownTimeoutTests(
            identifiers,
            identifier => identifier.StartsWith("timeout", StringComparison.Ordinal));

        identifiers.ShouldBe(["fast", "slow", "timeout-a", "timeout-b"]);
    }

    [TestMethod]
    public void IsolationTestsPreferPriorKillsThenFastestDuration()
    {
        var scores = new Dictionary<string, int>
        {
            ["prior-slow"] = 2,
            ["prior-fast"] = 2,
            ["new-fast"] = 0,
        };
        var durations = new Dictionary<string, TimeSpan>
        {
            ["prior-slow"] = TimeSpan.FromSeconds(2),
            ["prior-fast"] = TimeSpan.FromMilliseconds(2),
            ["new-fast"] = TimeSpan.FromMilliseconds(1),
        };

        var ordered = SingleMicrosoftTestPlatformRunner.OrderIsolationTests(
            ["new-fast", "prior-slow", "prior-fast", "unknown"],
            test => scores.GetValueOrDefault(test),
            test => durations.TryGetValue(test, out var duration) ? duration : null,
            test => test);

        ordered.ShouldBe(["prior-fast", "prior-slow", "new-fast", "unknown"]);
    }

    [TestMethod]
    public void IsolationUsesOnePriorityBatchThenOneBulkRemainder()
    {
        var ordered = Enumerable.Range(0, 20).ToList();

        var batches = SingleMicrosoftTestPlatformRunner.BuildIsolationTestBatches(
            ordered,
            priorityBatchSize: 8);

        batches.Select(batch => batch.Count).ShouldBe([8, 12]);
        batches.SelectMany(batch => batch).ShouldBe(ordered);
    }

    [TestMethod]
    public void WaveAssignmentsQuarantineKnownTimeoutsWhileUntriedTestsRemain()
    {
        var assignments = SingleMicrosoftTestPlatformRunner.BuildWaveAssignments(
            [
                (1, (IReadOnlyList<string>)["timeout-a", "untried"]),
                (2, (IReadOnlyList<string>)["timeout-b"]),
            ],
            sliceSize: 2,
            deferredTestSelector: test => test.StartsWith("timeout", StringComparison.Ordinal));

        assignments.ShouldBe(new Dictionary<string, int> { ["untried"] = 1 });
    }

    [TestMethod]
    public void WaveAssignmentsBatchKnownTimeoutsWhenNoUntriedTestsRemain()
    {
        var assignments = SingleMicrosoftTestPlatformRunner.BuildWaveAssignments(
            [
                (1, (IReadOnlyList<string>)["timeout-a"]),
                (2, (IReadOnlyList<string>)["timeout-b"]),
            ],
            sliceSize: 1,
            deferredTestSelector: _ => true);

        assignments.ShouldBe(new Dictionary<string, int>
        {
            ["timeout-a"] = 1,
            ["timeout-b"] = 2,
        });
    }

    [TestMethod]
    public void MutationPriorityPrefersStableTestUidAndFallsBackToName()
    {
        var priorities = new Dictionary<string, int>
        {
            ["stable-uid"] = 2,
            ["test-name"] = 1,
        };

        SingleMicrosoftTestPlatformRunner.ResolveMutationPriority(
            priorities,
            "stable-uid",
            "test-name").ShouldBe(2);
        SingleMicrosoftTestPlatformRunner.ResolveMutationPriority(
            priorities,
            "other-uid",
            "test-name").ShouldBe(1);
        SingleMicrosoftTestPlatformRunner.ResolveMutationPriority(
            priorities,
            "other-uid",
            "other-name").ShouldBe(0);
    }

    [TestMethod]
    public void IsolationBatchSizeMustBePositive()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            SingleMicrosoftTestPlatformRunner.BuildIsolationTestBatches([1], 0));
    }

    [TestMethod]
    public void IsolationPriorityFilePreservesDeclaredOrderAndIgnoresComments()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(path, ["# measured profile", "first", "second", "first", ""]);

            var priorities = SingleMicrosoftTestPlatformRunner.LoadIsolationTestPriorities(path);

            priorities.Count.ShouldBe(2);
            priorities["first"].ShouldBeGreaterThan(priorities["second"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void MissingIsolationPriorityFileFailsClosed()
    {
        Should.Throw<FileNotFoundException>(() =>
            SingleMicrosoftTestPlatformRunner.LoadIsolationTestPriorities(
                Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.txt")));
    }

    [TestMethod]
    public void IsolationMutationProfilePreservesPerMutationOrder()
    {
        var path = Path.GetTempFileName();
        try
        {
            const string mutation = "src/a.cs\t1\t2\t1\t3\tBoolean literal\tabc";
            const string otherMutation = "src/b.cs\t4\t5\t4\t6\tString literal\tdef";
            File.WriteAllLines(
                path,
                ["# measured profile", $"{mutation}\tfirst", $"{mutation}\tsecond", $"{otherMutation}\tother"]);

            var priorities =
                SingleMicrosoftTestPlatformRunner.LoadIsolationMutationPriorities(path);

            priorities.Keys.ShouldBe([mutation, otherMutation], ignoreOrder: true);
            priorities[mutation]["first"].ShouldBeGreaterThan(priorities[mutation]["second"]);
            priorities[otherMutation].Keys.ShouldBe(["other"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void MalformedIsolationMutationProfileFailsClosed()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "not-a-mutant-profile");

            Should.Throw<InvalidDataException>(() =>
                SingleMicrosoftTestPlatformRunner.LoadIsolationMutationPriorities(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void CollectibleKillRequiresFailureAndVerifiedUnload()
    {
        var failed = new CollectibleIsolationTestResult("test", "failed", "mutant detected");
        var passed = new CollectibleIsolationTestResult("test", "passed", null);

        SingleMicrosoftTestPlatformRunner.CanTrustCollectibleKill(
            new([failed], null, 1, Unloaded: true)).ShouldBeTrue();
        SingleMicrosoftTestPlatformRunner.CanTrustCollectibleKill(
            new([passed], null, 1, Unloaded: true)).ShouldBeFalse();
        SingleMicrosoftTestPlatformRunner.CanTrustCollectibleKill(
            new([failed], null, 1, Unloaded: false)).ShouldBeFalse();
        SingleMicrosoftTestPlatformRunner.CanTrustCollectibleKill(
            new([failed], "host error", 1, Unloaded: true)).ShouldBeFalse();
        SingleMicrosoftTestPlatformRunner.CanTrustCollectibleKill(
            new([failed], null, 1, Unloaded: true, SessionTimedOut: true)).ShouldBeFalse();
    }

    [TestMethod]
    public void MutationProfileKeyIsStableAcrossWorkspaceRoots()
    {
        var tree = CSharpSyntaxTree.ParseText(
            "namespace Example;\nclass Value { bool Get() => true; }\n",
            path: @"C:\agent\work\repository\src\Example\Value.cs");
        var original = tree.GetRoot().DescendantTokens().Single(token => token.ValueText == "true").Parent!;
        var mutant = new Mock<IMutant>();
        mutant.SetupGet(item => item.Mutation).Returns(new Mutation
        {
            OriginalNode = original,
            ReplacementNode = SyntaxFactory.LiteralExpression(
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.FalseLiteralExpression),
            DisplayName = "Boolean literal mutation",
        });

        var key = SingleMicrosoftTestPlatformRunner.BuildMutationProfileKey(mutant.Object);

        key.ShouldBe(
            "src/Example/Value.cs\t2\t29\t2\t33\tBoolean literal mutation\t" +
            "fcbcf165908dd18a9e49f7ff27810176db8e9f63b4352213741664245224f8aa");
    }

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
    public void WaveAssignmentsCanShrinkAfterAnUnattributedTimeout()
    {
        var states = Enumerable.Range(0, 10)
            .Select(mutantId =>
                (mutantId, (IReadOnlyList<string>)[$"test-{mutantId}"]));

        var assignments = SingleMicrosoftTestPlatformRunner.BuildWaveAssignments(
            states,
            sliceSize: 1,
            activationFamilySelector: null,
            maximumAssignments: 3);

        assignments.Count.ShouldBe(3);
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
    public void ActiveTestJournalAttributesOnlyTestsStillRunningAtTimeout()
    {
        var path = Path.GetTempFileName();
        const string acknowledgement = "request-token";
        try
        {
            File.WriteAllLines(
                path,
                [
                    $"stryker-mtp-active-tests-v1\t{acknowledgement}",
                    $"start\t{acknowledgement}\ttest-1\t101",
                    $"start\t{acknowledgement}\ttest-2\t102",
                    $"finish\t{acknowledgement}\ttest-1",
                    $"start\told-token\tstale-test\t999",
                ]);

            var active = SingleMicrosoftTestPlatformRunner.ReadActiveMutantIds(
                path,
                acknowledgement);

            active.ShouldBe([102], ignoreOrder: true);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void ActiveTestJournalRejectsRecordsFromAnotherRequest()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(
                path,
                [
                    "stryker-mtp-active-tests-v1\told-token",
                    "start\told-token\ttest-1\t101",
                ]);

            var active = SingleMicrosoftTestPlatformRunner.ReadActiveMutantIds(
                path,
                "current-token");

            active.ShouldBeEmpty();
        }
        finally
        {
            File.Delete(path);
        }
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
