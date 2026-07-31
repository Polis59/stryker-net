using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;
using Stryker.Abstractions;
using Stryker.Abstractions.Testing;
using Stryker.TestRunner.MicrosoftTestPlatform.Models;
using Stryker.TestRunner.Results;
using Stryker.TestRunner.Tests;

namespace Stryker.TestRunner.MicrosoftTestPlatform.UnitTest;

/// <summary>
/// Pins the per-mutant session semantics of
/// <see cref="SingleMicrosoftTestPlatformRunner.TestMultipleMutantsAsync"/>: every mutant in a
/// group must run in its own session with its own mutation active and only its assessing tests,
/// and static-value mutants must run on fresh test hosts. The original implementation ran a whole
/// group in one session with no mutation active (mutant id -1), fabricating every verdict in the
/// group.
/// </summary>
[TestClass]
public class MutantSessionExecutionTests
{
    private const string Assembly = "/fake/assembly.dll";

    private readonly Dictionary<string, List<TestNode>> _testsByAssembly = new();
    private readonly Dictionary<string, MtpTestDescription> _testDescriptions = new();
    private readonly TestSet _testSet = new();
    private readonly object _discoveryLock = new();

    private RecordingRunner CreateRunner(int id, params string[] testUids)
    {
        var nodes = testUids.Select(uid => new TestNode(uid, uid, "test", "discovered")).ToList();
        _testsByAssembly[Assembly] = nodes;
        foreach (var node in nodes.Where(n => !_testDescriptions.ContainsKey(n.Uid)))
        {
            _testDescriptions[node.Uid] = new MtpTestDescription(node);
        }

        return new RecordingRunner(id, _testsByAssembly, _testDescriptions, _testSet, _discoveryLock);
    }

    private static Mock<IProjectAndTests> CreateProject()
    {
        var project = new Mock<IProjectAndTests>();
        project.Setup(x => x.GetTestAssemblies()).Returns([Assembly]);
        return project;
    }

    private static Mock<IMutant> CreateMutant(int id, ITestIdentifiers? assessingTests = null)
    {
        var mutant = new Mock<IMutant>();
        mutant.Setup(x => x.Id).Returns(id);
        mutant.Setup(x => x.AssessingTests).Returns(assessingTests ?? TestIdentifierList.EveryTest());
        return mutant;
    }

    [TestMethod, Timeout(5000)]
    public async Task TestMultipleMutantsAsync_ActivatesEachMutantInItsOwnSession()
    {
        // Arrange
        using var runner = CreateRunner(31, "test-a", "test-b");
        var mutants = new[] { CreateMutant(11).Object, CreateMutant(22).Object };

        // Act
        await runner.TestMultipleMutantsAsync(CreateProject().Object, null, mutants, (_, _, _, _) => true);

        // Assert: sessions activate each mutant's own mutation. The previous behaviour published
        // mutant id -1 for any group larger than one, so the group's tests all ran against
        // unmutated code. Each mutant appears twice because a warm-host non-detection is
        // confirmed once on a fresh host before Survived is accepted.
        runner.Sessions.Select(s => s.ActiveMutantId).ShouldBe([11, 11, 22, 22]);
    }

    [TestMethod, Timeout(5000)]
    public async Task TestMultipleMutantsAsync_RestrictsEachSessionToTheMutantsAssessingTests()
    {
        // Arrange
        using var runner = CreateRunner(32, "test-a", "test-b", "test-c");
        var mutant = CreateMutant(7, new TestIdentifierList("test-b", "test-c"));

        // Act
        await runner.TestMultipleMutantsAsync(CreateProject().Object, null, [mutant.Object], (_, _, _, _) => true);

        // Assert: both the warm pass and the fresh-host confirmation stay restricted to the
        // mutant's assessing tests.
        runner.Sessions.Count.ShouldBe(2);
        foreach (var session in runner.Sessions)
        {
            session.TargetedUids.ShouldBe(["test-b", "test-c"]);
        }
    }

    [TestMethod, Timeout(5000)]
    public async Task TestMultipleMutantsAsync_UsesFreshHostsAroundStaticMutants()
    {
        // Arrange
        using var runner = CreateRunner(33, "test-a");
        var staticMutant = CreateMutant(1);
        staticMutant.Setup(x => x.IsStaticValue).Returns(true);
        var ordinaryMutant = CreateMutant(2);

        // Act
        await runner.TestMultipleMutantsAsync(
            CreateProject().Object, null, [staticMutant.Object, ordinaryMutant.Object], (_, _, _, _) => true);

        // Assert: once before the static mutant (its static initializers must execute with the
        // mutation already active) and once before the following ordinary mutant (the static
        // mutant's initializer side effects persist in the reused host). Neither mutant needs a
        // survival confirmation because both already ran on fresh hosts.
        runner.ServerResets.ShouldBe(2);
    }

    [TestMethod, Timeout(5000)]
    public async Task TestMultipleMutantsAsync_ConfirmsAWarmSurvivorOnAFreshHost_AndKeepsAFreshKill()
    {
        // Arrange: the mutant's assessing test passes on the warm host (the mutated path is
        // hidden behind warmed caches) and fails on the fresh-host confirmation.
        using var runner = CreateRunner(35, "test-a");
        var mutant = CreateMutant(9);
        runner.FailingUidsByMutantIdOnFreshHost[9] = ["test-a"];
        var updates = new List<string[]>();

        // Act
        await runner.TestMultipleMutantsAsync(
            CreateProject().Object, null, [mutant.Object],
            (_, failed, _, _) =>
            {
                updates.Add(failed.GetIdentifiers().ToArray());
                return true;
            });

        // Assert: the handler saw only the confirmed (fresh-host) result, so the mutant is
        // killed, not misreported as survived from the warm pass.
        runner.Sessions.Count.ShouldBe(2);
        runner.ServerResets.ShouldBe(1);
        updates.ShouldHaveSingleItem().ShouldBe(["test-a"]);
    }

    [TestMethod, Timeout(5000)]
    public async Task TestMultipleMutantsAsync_ReportsSessionOutcomesPerMutant_NotOnTheMergedResult()
    {
        // Arrange
        using var runner = CreateRunner(34, "test-a");
        var killed = CreateMutant(1);
        var survived = CreateMutant(2);
        runner.FailingUidsByMutantId[1] = ["test-a"];
        var updates = new List<(int[] MutantIds, string[] FailedUids)>();

        // Act
        var result = await runner.TestMultipleMutantsAsync(
            CreateProject().Object, null, [killed.Object, survived.Object],
            (testedMutants, failed, _, _) =>
            {
                updates.Add((testedMutants.Select(m => m.Id).ToArray(), failed.GetIdentifiers().ToArray()));
                return true;
            });

        // Assert: the update handler sees each mutant with its own session's results, so a test
        // failing under one mutant can never kill another mutant of the group.
        updates.Count.ShouldBe(2);
        updates[0].MutantIds.ShouldBe([1]);
        updates[0].FailedUids.ShouldBe(["test-a"]);
        updates[1].MutantIds.ShouldBe([2]);
        updates[1].FailedUids.ShouldBeEmpty();
        result.SessionTimedOut.ShouldBeFalse();
        result.SessionHadRuntimeIssue.ShouldBeFalse();
    }

    private sealed class RecordingRunner : SingleMicrosoftTestPlatformRunner
    {
        private readonly int _id;

        public List<(string Assembly, int ActiveMutantId, string[]? TargetedUids)> Sessions { get; } = [];

        public Dictionary<int, string[]> FailingUidsByMutantId { get; } = [];

        // Fails the mutant's tests only when the session runs on a "fresh" host (one reset since
        // the previous session) — models a kill hidden behind warm-host caches.
        public Dictionary<int, string[]> FailingUidsByMutantIdOnFreshHost { get; } = [];

        public int ServerResets { get; private set; }

        // Starts false so the first session models a warm host; ResetServerAsync marks the next
        // session fresh.
        private bool _freshHost;

        public RecordingRunner(
            int id,
            Dictionary<string, List<TestNode>> testsByAssembly,
            Dictionary<string, MtpTestDescription> testDescriptions,
            TestSet testSet,
            object discoveryLock)
            : base(id, testsByAssembly, testDescriptions, testSet, discoveryLock, NullLogger.Instance)
            => _id = id;

        public override Task ResetServerAsync()
        {
            ServerResets++;
            _freshHost = true;
            return Task.CompletedTask;
        }

        internal override Task<(TestRunResult? Result, bool TimedOut, List<TestNode>? DiscoveredTests)> RunAssemblyTestsAsync(
            string assembly, ITimeoutValueCalculator? timeoutCalc, Func<TestNode, bool>? testUidFilter = null, Func<TestNodeUpdate, bool>? bailPredicate = null)
        {
            var activeMutantId = ReadActiveMutantId();
            var discovered = GetDiscoveredTests(assembly);
            var targeted = testUidFilter is null ? discovered : discovered?.Where(testUidFilter).ToList();
            Sessions.Add((assembly, activeMutantId, targeted?.Select(t => t.Uid).ToArray()));

            var failingUids = FailingUidsByMutantId.TryGetValue(activeMutantId, out var uids) ? uids : [];
            if (_freshHost && FailingUidsByMutantIdOnFreshHost.TryGetValue(activeMutantId, out var freshUids))
            {
                failingUids = freshUids;
            }
            _freshHost = false;
            var result = new TestRunResult(
                Array.Empty<IFrameworkTestDescription>(),
                new TestIdentifierList(targeted?.Select(t => t.Uid) ?? []),
                new TestIdentifierList(failingUids),
                TestIdentifierList.NoTest(),
                string.Empty,
                [],
                TimeSpan.Zero);

            return Task.FromResult<(TestRunResult?, bool, List<TestNode>?)>((result, false, targeted));
        }

        private int ReadActiveMutantId()
        {
            // The runner publishes the active mutant id as a 4-byte int in its control file before
            // starting each session; reading it back here observes exactly what a test host sees.
            var path = Path.Combine(Path.GetTempPath(), $"stryker-mutant-{_id}.txt");
            var bytes = File.ReadAllBytes(path);
            return BitConverter.ToInt32(bytes, 0);
        }
    }
}
