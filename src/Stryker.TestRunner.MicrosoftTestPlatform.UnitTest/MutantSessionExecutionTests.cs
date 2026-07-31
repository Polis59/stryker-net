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

        // Assert: one session per mutant, each with that mutant's mutation active. The previous
        // behaviour published mutant id -1 for any group larger than one, so the group's tests
        // all ran against unmutated code.
        runner.Sessions.Select(s => s.ActiveMutantId).ShouldBe([11, 22]);
    }

    [TestMethod, Timeout(5000)]
    public async Task TestMultipleMutantsAsync_RestrictsEachSessionToTheMutantsAssessingTests()
    {
        // Arrange
        using var runner = CreateRunner(32, "test-a", "test-b", "test-c");
        var mutant = CreateMutant(7, new TestIdentifierList("test-b", "test-c"));

        // Act
        await runner.TestMultipleMutantsAsync(CreateProject().Object, null, [mutant.Object], (_, _, _, _) => true);

        // Assert
        runner.Sessions.ShouldHaveSingleItem().TargetedUids.ShouldBe(["test-b", "test-c"]);
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
        // mutant's initializer side effects persist in the warm host).
        runner.ServerResets.ShouldBe(2);
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

        public int ServerResets { get; private set; }

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
