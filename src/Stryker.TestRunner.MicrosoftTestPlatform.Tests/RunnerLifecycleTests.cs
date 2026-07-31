using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Stryker.Abstractions;
using Stryker.Abstractions.Options;
using Stryker.Abstractions.Testing;
using Stryker.TestRunner.MicrosoftTestPlatform.Models;
using Stryker.TestRunner.Results;
using Stryker.TestRunner.Tests;

namespace Stryker.TestRunner.MicrosoftTestPlatform.Tests;

public sealed class RunnerLifecycleTests
{
    [Fact]
    public void BatchPlannerSeparatesStaticMutantsBeforePackingOrdinaryMutants()
    {
        var options = new Mock<IStrykerOptions>();
        options
            .SetupGet(candidate => candidate.OptimizationMode)
            .Returns(OptimizationModes.CoverageBasedTest);

        var groups = MutationBatchPlanner.Build(
            options.Object,
            [
                CreateMutant(
                    7,
                    isStaticValue: true,
                    assessingTests: new TestIdentifierList("static-only")),
                CreateMutant(5, assessingTests: new TestIdentifierList("wanted")),
                CreateMutant(6, assessingTests: new TestIdentifierList("other")),
            ]).ToList();

        Assert.Equal(2, groups.Count);
        Assert.Equal([7], groups[0].Select(mutant => mutant.Id));
        Assert.Equal([5, 6], groups[1].Select(mutant => mutant.Id));
    }

    [Fact]
    public async Task CampaignProgressReportsPlannedAndCompletedMutationWork()
    {
        var progressPath = Path.Combine(
            Path.GetTempPath(),
            $"threadway-stryker-progress-{Guid.NewGuid():N}.json");
        MutationCampaignProgressReporter.ConfigureForTests(progressPath);

        try
        {
            var options = new Mock<IStrykerOptions>();
            options
                .SetupGet(candidate => candidate.OptimizationMode)
                .Returns(OptimizationModes.CoverageBasedTest);
            var mutants = new[]
            {
                CreateMutant(
                    7,
                    isStaticValue: true,
                    assessingTests: new TestIdentifierList("static-only")),
                CreateMutant(5, assessingTests: new TestIdentifierList("wanted")),
                CreateMutant(6, assessingTests: new TestIdentifierList("other")),
            };

            _ = MutationBatchPlanner.Build(options.Object, mutants).ToList();
            using var runner = new SessionTrackingRunner(options.Object);
            await runner.TestMultipleMutantsAsync(
                CreateProject("/test.dll"),
                null,
                mutants,
                null);

            using var document = JsonDocument.Parse(
                File.ReadAllText(progressPath));
            var root = document.RootElement;

            Assert.Equal("mutation-complete", root.GetProperty("phase").GetString());
            Assert.Equal(3, root.GetProperty("plannedMutants").GetInt32());
            Assert.Equal(1, root.GetProperty("plannedIsolatedMutants").GetInt32());
            Assert.Equal(2, root.GetProperty("plannedOrdinaryMutants").GetInt32());
            Assert.Equal(1, root.GetProperty("plannedOrdinaryBatches").GetInt32());
            Assert.Equal(1, root.GetProperty("isolatedMutantsCompleted").GetInt32());
            Assert.Equal(1, root.GetProperty("ordinaryBatchesCompleted").GetInt32());
            Assert.Equal(2, root.GetProperty("ordinaryMutantsCompleted").GetInt32());
            Assert.Equal(0, root.GetProperty("isolatedMutantsRemaining").GetInt32());
            Assert.Equal(0, root.GetProperty("ordinaryMutantsRemaining").GetInt32());
            Assert.Empty(root.GetProperty("activeIsolatedRequests").EnumerateObject());
            Assert.Empty(root.GetProperty("activeOrdinaryRequests").EnumerateObject());
        }
        finally
        {
            MutationCampaignProgressReporter.ConfigureForTests(null);
            File.Delete(progressPath);
        }
    }

    [Fact]
    public async Task StaticMutantUsesCollectibleIsolationAndClearsActivation()
    {
        using var runner = new SessionTrackingRunner();

        await runner.TestMultipleMutantsAsync(
            CreateProject("/test.dll"),
            null,
            [CreateMutant(7, isStaticValue: true)],
            null);

        Assert.Equal(["isolate:/test.dll"], runner.Events);
        Assert.Equal([7], runner.ActiveMutantIds);
        Assert.Equal(-1, runner.ReadMutantFile());
    }

    [Fact]
    public async Task IsolatedMutantRunsUnderWholeContextActivation()
    {
        var options = new Mock<IStrykerOptions>();
        options
            .SetupGet(candidate => candidate.OptimizationMode)
            .Returns(OptimizationModes.CoverageBasedTest);
        using var runner = new SessionTrackingRunner(options.Object);

        await runner.TestMultipleMutantsAsync(
            CreateProject("/test.dll"),
            null,
            [
                CreateMutant(
                    9,
                    mustBeTestedInIsolation: true,
                    assessingTests: new TestIdentifierList("wanted", "other")),
            ],
            null);

        // The isolated mutant's coverage occurs between test lifecycles (static
        // initialization, fixture construction), where the per-test activation
        // hook resets the control channel to -1. The isolated context must
        // therefore run with the activation map inactive so the pre-loaded
        // mutant id stays active for the whole context.
        Assert.Equal(["isolate:/test.dll"], runner.Events);
        Assert.Equal([9], runner.ActiveMutantIds);
        Assert.Equal(
            ["threadway-stryker-map-v1\toff"],
            runner.MutantMapHeaders);
        Assert.Equal(["other", "wanted"], runner.FilteredTestIds.Order());
        Assert.Equal(-1, runner.ReadMutantFile());
    }

    [Fact]
    public async Task MixedRequestKeepsPerTestActivationOnlyForTheReusableHost()
    {
        var options = new Mock<IStrykerOptions>();
        options
            .SetupGet(candidate => candidate.OptimizationMode)
            .Returns(OptimizationModes.CoverageBasedTest);
        using var runner = new SessionTrackingRunner(options.Object);

        await runner.TestMultipleMutantsAsync(
            CreateProject("/test.dll"),
            null,
            [
                CreateMutant(
                    7,
                    isStaticValue: true,
                    assessingTests: new TestIdentifierList("static-only")),
                CreateMutant(5, assessingTests: new TestIdentifierList("wanted")),
            ],
            null);

        Assert.Equal(["isolate:/test.dll", "run:/test.dll"], runner.Events);
        Assert.Equal(
            "threadway-stryker-map-v1\toff",
            runner.MutantMapHeaders[0]);
        Assert.StartsWith(
            "threadway-stryker-map-v1\tactive\t",
            runner.MutantMapHeaders[1],
            StringComparison.Ordinal);
        Assert.Equal(5, runner.LastMutantAssignments["wanted"]);
    }

    [Fact]
    public async Task OrdinaryMutantReusesTheExistingHost()
    {
        using var runner = new SessionTrackingRunner();

        await runner.TestMultipleMutantsAsync(
            CreateProject("/test.dll"),
            null,
            [CreateMutant(5)],
            null);

        Assert.Equal(["run:/test.dll"], runner.Events);
        Assert.Equal([5], runner.ActiveMutantIds);
        Assert.Equal(-1, runner.ReadMutantFile());
    }

    [Fact]
    public async Task MixedMutantRequestUsesOneHostAndMapsDisjointTests()
    {
        var options = new Mock<IStrykerOptions>();
        options
            .SetupGet(candidate => candidate.OptimizationMode)
            .Returns(OptimizationModes.CoverageBasedTest);
        using var runner = new SessionTrackingRunner(options.Object);

        await runner.TestMultipleMutantsAsync(
            CreateProject("/test.dll"),
            null,
            [
                CreateMutant(1, assessingTests: new TestIdentifierList("wanted")),
                CreateMutant(2, assessingTests: new TestIdentifierList("other")),
            ],
            null);

        Assert.Equal(["run:/test.dll"], runner.Events);
        Assert.Equal([-1], runner.ActiveMutantIds);
        Assert.Equal(1, runner.LastMutantAssignments["wanted"]);
        Assert.Equal(2, runner.LastMutantAssignments["other"]);
        Assert.Equal(["other", "wanted"], runner.FilteredTestIds.Order());
    }

    [Fact]
    public async Task StaticMutantDoesNotForceOrdinaryMutantsIntoSeparateHosts()
    {
        var options = new Mock<IStrykerOptions>();
        options
            .SetupGet(candidate => candidate.OptimizationMode)
            .Returns(OptimizationModes.CoverageBasedTest);
        using var runner = new SessionTrackingRunner(options.Object);

        await runner.TestMultipleMutantsAsync(
            CreateProject("/test.dll"),
            null,
            [
                CreateMutant(
                    7,
                    isStaticValue: true,
                    assessingTests: new TestIdentifierList("static-only")),
                CreateMutant(5, assessingTests: new TestIdentifierList("wanted")),
                CreateMutant(6, assessingTests: new TestIdentifierList("other")),
            ],
            null);

        Assert.Equal(
            ["isolate:/test.dll", "run:/test.dll"],
            runner.Events);
        Assert.Equal([7, -1], runner.ActiveMutantIds);
        Assert.Equal(5, runner.LastMutantAssignments["wanted"]);
        Assert.Equal(6, runner.LastMutantAssignments["other"]);
        Assert.Equal(["other", "wanted"], runner.FilteredTestIds.Order());
    }

    [Fact]
    public async Task MixedMutantRequestRejectsOverlappingTestAssignments()
    {
        var options = new Mock<IStrykerOptions>();
        options
            .SetupGet(candidate => candidate.OptimizationMode)
            .Returns(OptimizationModes.CoverageBasedTest);
        using var runner = new SessionTrackingRunner(options.Object);

        var result = await runner.TestMultipleMutantsAsync(
            CreateProject("/test.dll"),
            null,
            [
                CreateMutant(1, assessingTests: new TestIdentifierList("shared")),
                CreateMutant(2, assessingTests: new TestIdentifierList("shared")),
            ],
            null);

        Assert.True(result.SessionHadRuntimeIssue);
        Assert.Contains("mapped to more than one mutant", result.ResultMessage);
        Assert.Empty(runner.Events);
    }

    [Fact]
    public async Task MutationRunReceivesOnlyTheAssessingTests()
    {
        var options = new Mock<IStrykerOptions>();
        options
            .SetupGet(candidate => candidate.OptimizationMode)
            .Returns(OptimizationModes.CoverageBasedTest);
        using var runner = new SessionTrackingRunner(options.Object);

        await runner.TestMultipleMutantsAsync(
            CreateProject("/test.dll"),
            null,
            [CreateMutant(3, assessingTests: new TestIdentifierList("wanted"))],
            null);

        Assert.Equal(["wanted"], runner.FilteredTestIds);
    }

    [Fact]
    public async Task MutantMapPublishesMethodAssignmentsForDeferredTheoryRows()
    {
        var options = new Mock<IStrykerOptions>();
        options
            .SetupGet(candidate => candidate.OptimizationMode)
            .Returns(OptimizationModes.CoverageBasedTest);
        var testDescriptions = new Dictionary<string, MtpTestDescription>
        {
            ["theory-case"] = new(new TestNode(
                "theory-case", "Example.VectorTests.PublishedVectors", "test", "discovered")),
            ["ambiguous-a"] = new(new TestNode(
                "ambiguous-a", "Example.VectorTests.Shared(value: 1)", "test", "discovered")),
            ["ambiguous-b"] = new(new TestNode(
                "ambiguous-b", "Example.VectorTests.Shared(value: 2)", "test", "discovered")),
        };
        using var runner = new SessionTrackingRunner(options.Object, testDescriptions);

        await runner.TestMultipleMutantsAsync(
            CreateProject("/test.dll"),
            null,
            [
                CreateMutant(7, assessingTests: new TestIdentifierList("theory-case", "ambiguous-a")),
                CreateMutant(8, assessingTests: new TestIdentifierList("ambiguous-b")),
            ],
            null);

        // The deferred theory's method key resolves its run-time rows to mutant 7;
        // the method whose rows are split across two mutants publishes no key, so
        // an expanded row of it still fails closed.
        Assert.Equal(7, runner.LastMutantAssignments["method\tExample.VectorTests.PublishedVectors"]);
        Assert.DoesNotContain(
            "method\tExample.VectorTests.Shared",
            runner.LastMutantAssignments.Keys);
    }

    [Fact]
    public void RowResultsNormalizeToTheirDiscoveredCase()
    {
        var discovered = new List<TestNode>
        {
            new("theory-case", "Example.VectorTests.PublishedVectors", "test", "discovered"),
            new("plain-case", "Example.VectorTests.Plain(value: 1)", "test", "discovered"),
        };
        var updates = new List<TestNodeUpdate>
        {
            new(new TestNode(
                "run-time-row", "Example.VectorTests.PublishedVectors(value: 3)", "test", "failed"), ""),
            new(new TestNode(
                "plain-case", "Example.VectorTests.Plain(value: 1)", "test", "passed"), ""),
            new(new TestNode(
                "unknown-row", "Example.VectorTests.Never(value: 9)", "test", "passed"), ""),
        };

        var normalized = SingleMicrosoftTestPlatformRunner
            .NormalizeToDiscoveredCases(updates, discovered)
            .ToList();

        Assert.Equal(
            ["theory-case", "plain-case", "unknown-row"],
            normalized.Select(update => update.Node.Uid));
        Assert.Equal("failed", normalized[0].Node.ExecutionState);
    }

    [Fact]
    public async Task IsolatedMutantRetriesOnceWhenTheIsolationHostIsLost()
    {
        using var runner = new IsolationHostLossRunner(hostLossCount: 1);

        var result = await runner.TestMultipleMutantsAsync(
            CreateProject("/test.dll"),
            null,
            [CreateMutant(7, isStaticValue: true)],
            null);

        Assert.Equal(2, runner.IsolationAttempts);
        Assert.False(result.SessionHadRuntimeIssue);
    }

    [Fact]
    public async Task IsolatedMutantKeepsItsRuntimeErrorWhenTheHostIsLostTwice()
    {
        using var runner = new IsolationHostLossRunner(hostLossCount: 2);

        var result = await runner.TestMultipleMutantsAsync(
            CreateProject("/test.dll"),
            null,
            [CreateMutant(7, isStaticValue: true)],
            null);

        Assert.Equal(2, runner.IsolationAttempts);
        Assert.True(result.SessionHadRuntimeIssue);
    }

    private sealed class IsolationHostLossRunner : SingleMicrosoftTestPlatformRunner
    {
        private static int _nextRunnerId = 6000;
        private readonly int hostLossCount;

        public IsolationHostLossRunner(int hostLossCount)
            : base(
                Interlocked.Increment(ref _nextRunnerId),
                new Dictionary<string, List<TestNode>>(),
                new Dictionary<string, MtpTestDescription>(),
                new TestSet(),
                new object(),
                NullLogger.Instance)
        {
            this.hostLossCount = hostLossCount;
        }

        public int IsolationAttempts { get; private set; }

        internal override Task<(
            TestRunResult? Result,
            bool TimedOut,
            List<TestNode>? DiscoveredTests)> RunAssemblyTestsInCollectibleContextAsync(
                string assembly,
                ITimeoutValueCalculator? timeoutCalc,
                Func<TestNode, bool>? testUidFilter = null)
        {
            IsolationAttempts++;
            if (IsolationAttempts <= hostLossCount)
            {
                // The crash sentinel a lost isolation host produces.
                return Task.FromResult<(TestRunResult?, bool, List<TestNode>?)>(
                    (new TestRunResult(false, "The collectible isolation host exited before responding."),
                        false,
                        null));
            }

            return Task.FromResult<(TestRunResult?, bool, List<TestNode>?)>(
                (new TestRunResult(
                    Array.Empty<IFrameworkTestDescription>(),
                    TestIdentifierList.EveryTest(),
                    TestIdentifierList.NoTest(),
                    TestIdentifierList.NoTest(),
                    string.Empty,
                    [],
                    TimeSpan.Zero),
                    false,
                    null));
        }
    }

    [Fact]
    public void RunnerPoolsUseIndependentControlChannels()
    {
        using var first = new SessionTrackingRunner();
        using var second = new SessionTrackingRunner();

        Assert.NotEqual(first.MutantFilePath, second.MutantFilePath);
        Assert.NotEqual(first.MutantMapFilePath, second.MutantMapFilePath);
        Assert.NotEqual(
            first.MutantMapAcknowledgementFilePath,
            second.MutantMapAcknowledgementFilePath);
        Assert.NotEqual(first.CoverageFilePath, second.CoverageFilePath);
        Assert.NotEqual(first.CoverageMapFilePath, second.CoverageMapFilePath);
    }

    [Fact]
    public void PerTestCoverageKeepsOrdinaryCoverageExactAndWidensStaticCoverage()
    {
        using var runner = new SessionTrackingRunner();
        File.WriteAllText(
            runner.CoverageMapFilePath,
            "threadway-stryker-coverage-v1" + Environment.NewLine +
            "test-1\t1,2\t2\t3" + Environment.NewLine +
            "test-2\t4\t\t" + Environment.NewLine +
            "outside-request\t9\t9\t10" + Environment.NewLine);

        var coverage = runner.ReadPerTestCoverageData(
            [
                new TestNode("test-1", "Example.First", "test", "discovered"),
                new TestNode("test-2", "Example.Second", "test", "discovered"),
            ],
            CoverageConfidence.Exact);

        Assert.Equal(
            [1, 2, 3],
            coverage.Single(result => result.TestId == "test-1").MutationsCovered.Order());
        Assert.Equal(
            [2, 3, 4],
            coverage.Single(result => result.TestId == "test-2").MutationsCovered.Order());
        Assert.All(
            coverage,
            result => Assert.True(
                result[2].HasFlag(MutationTestingRequirements.Static)));
        Assert.All(
            coverage,
            result => Assert.True(
                result[3].HasFlag(MutationTestingRequirements.NeedEarlyActivation)));
        Assert.DoesNotContain(coverage, result => result.MutationsCovered.Contains(9));
        Assert.DoesNotContain(coverage, result => result.MutationsCovered.Contains(10));
    }

    [Fact]
    public void PerTestCoverageRejectsMissingRequestedTestCases()
    {
        using var runner = new SessionTrackingRunner();
        File.WriteAllText(
            runner.CoverageMapFilePath,
            "threadway-stryker-coverage-v1" + Environment.NewLine +
            "test-1\t1\t\t" + Environment.NewLine);

        var exception = Assert.Throws<InvalidDataException>(() =>
            runner.ReadPerTestCoverageData(
                [
                    new TestNode("test-1", "Example.First", "test", "discovered"),
                    new TestNode("test-2", "Example.Second", "test", "discovered"),
                ],
                CoverageConfidence.Exact));

        Assert.Contains("Missing: [test-2]", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CoverageUsesFreshClassBoundariesSafeSupersetsAndOneCampaignSnapshot()
    {
        var options = new Mock<IStrykerOptions>();
        options.SetupGet(candidate => candidate.Concurrency).Returns(1);
        options
            .SetupGet(candidate => candidate.OptimizationMode)
            .Returns(
                OptimizationModes.CoverageBasedTest |
                OptimizationModes.CaptureCoveragePerTest);

        var runnerFactory = new PerTestCoverageRunnerFactory();
        var project = new Mock<IProjectAndTests>();
        project
            .Setup(candidate => candidate.GetTestAssemblies())
            .Returns(["assembly.dll"]);

        using var pool = new MicrosoftTestPlatformRunnerPool(
            options.Object,
            NullLogger.Instance,
            runnerFactory);

        var coverage = pool.CaptureCoverage(project.Object).ToList();
        var reusedCoverage = pool.CaptureCoverage(project.Object).ToList();

        Assert.Equal(3, coverage.Count);
        Assert.Equal(3, reusedCoverage.Count);
        Assert.Equal([1, 2], coverage.Single(result => result.TestId == "test-1").MutationsCovered);
        Assert.Equal([1, 2], coverage.Single(result => result.TestId == "test-2").MutationsCovered);
        Assert.Equal([3], coverage.Single(result => result.TestId == "test-3").MutationsCovered);
        Assert.All(
            coverage.Where(result => result.TestId is "test-1" or "test-2"),
            result => Assert.True(
                result[2].HasFlag(MutationTestingRequirements.Static)));
        Assert.Equal([2, 1], runnerFactory.CoverageGroupSizes);
        Assert.All(
            coverage,
            result => Assert.Equal(CoverageConfidence.Exact, result.Confidence));
    }

    private static IMutant CreateMutant(
        int id,
        bool isStaticValue = false,
        bool mustBeTestedInIsolation = false,
        ITestIdentifiers? assessingTests = null)
    {
        var mutant = new Mock<IMutant>();
        mutant.SetupGet(candidate => candidate.Id).Returns(id);
        mutant.SetupGet(candidate => candidate.IsStaticValue).Returns(isStaticValue);
        mutant
            .SetupGet(candidate => candidate.MustBeTestedInIsolation)
            .Returns(mustBeTestedInIsolation);
        mutant
            .SetupGet(candidate => candidate.AssessingTests)
            .Returns(assessingTests ?? TestIdentifierList.EveryTest());
        return mutant.Object;
    }

    private static IProjectAndTests CreateProject(string assembly)
    {
        var project = new Mock<IProjectAndTests>();
        project
            .Setup(candidate => candidate.GetTestAssemblies())
            .Returns([assembly]);
        return project.Object;
    }

    private sealed class SessionTrackingRunner : SingleMicrosoftTestPlatformRunner
    {
        private static int _nextRunnerId = 2000;

        public SessionTrackingRunner(
            IStrykerOptions? options = null,
            Dictionary<string, MtpTestDescription>? testDescriptions = null)
            : this(Interlocked.Increment(ref _nextRunnerId), options, testDescriptions)
        {
        }

        private SessionTrackingRunner(
            int runnerId,
            IStrykerOptions? options,
            Dictionary<string, MtpTestDescription>? testDescriptions)
            : base(
                runnerId,
                new Dictionary<string, List<TestNode>>(),
                testDescriptions ?? new Dictionary<string, MtpTestDescription>(),
                new TestSet(),
                new object(),
                NullLogger.Instance,
                options)
        {
        }

        public List<string> Events { get; } = [];
        public List<int> ActiveMutantIds { get; } = [];
        public List<string> MutantMapHeaders { get; } = [];
        public List<string> FilteredTestIds { get; } = [];
        public Dictionary<string, int> LastMutantAssignments { get; } =
            new(StringComparer.Ordinal);

        public int ReadMutantFile()
        {
            return BitConverter.ToInt32(File.ReadAllBytes(MutantFilePath), 0);
        }

        public override async Task ResetServerAsync()
        {
            Events.Add("reset");
            await base.ResetServerAsync();
        }

        internal override Task<(
            TestRunResult? Result,
            bool TimedOut,
            List<TestNode>? DiscoveredTests)> RunAssemblyTestsAsync(
                string assembly,
                ITimeoutValueCalculator? timeoutCalc,
                Func<TestNode, bool>? testUidFilter = null,
                bool serialActivation = false)
        {
            return TrackRun(assembly, testUidFilter, "run");
        }

        internal override Task<(
            TestRunResult? Result,
            bool TimedOut,
            List<TestNode>? DiscoveredTests)> RunAssemblyTestsInCollectibleContextAsync(
                string assembly,
                ITimeoutValueCalculator? timeoutCalc,
                Func<TestNode, bool>? testUidFilter = null)
        {
            return TrackRun(assembly, testUidFilter, "isolate");
        }

        private Task<(
            TestRunResult? Result,
            bool TimedOut,
            List<TestNode>? DiscoveredTests)> TrackRun(
                string assembly,
                Func<TestNode, bool>? testUidFilter,
                string mode)
        {
            Events.Add($"{mode}:{assembly}");
            ActiveMutantIds.Add(ReadMutantFile());
            MutantMapHeaders.Add(File.ReadLines(MutantMapFilePath).First());
            AcknowledgeMutantMap();

            if (testUidFilter is not null)
            {
                var candidates = new[]
                {
                    new TestNode("wanted", "Wanted", "test", "discovered"),
                    new TestNode("other", "Other", "test", "discovered")
                };
                FilteredTestIds.AddRange(
                    candidates.Where(testUidFilter).Select(test => test.Uid));
            }

            var result = new TestRunResult(
                Array.Empty<IFrameworkTestDescription>(),
                TestIdentifierList.EveryTest(),
                TestIdentifierList.NoTest(),
                TestIdentifierList.NoTest(),
                string.Empty,
                [],
                TimeSpan.Zero);
            return Task.FromResult<(
                TestRunResult?,
                bool,
                List<TestNode>?)>((result, false, null));
        }

        private void AcknowledgeMutantMap()
        {
            var lines = File.ReadAllLines(MutantMapFilePath);
            const string activeHeaderPrefix = "threadway-stryker-map-v1\tactive\t";
            if (lines.Length == 0 ||
                !lines[0].StartsWith(activeHeaderPrefix, StringComparison.Ordinal))
            {
                return;
            }

            LastMutantAssignments.Clear();
            foreach (var line in lines.AsSpan(1))
            {
                var separator = line.IndexOf('\t');
                LastMutantAssignments[line[(separator + 1)..]] =
                    int.Parse(line.AsSpan(0, separator), System.Globalization.CultureInfo.InvariantCulture);
            }

            File.WriteAllText(
                MutantMapAcknowledgementFilePath,
                lines[0][activeHeaderPrefix.Length..]);
        }
    }

    private sealed class PerTestCoverageRunnerFactory : ISingleRunnerFactory
    {
        public SingleMicrosoftTestPlatformRunner CreateRunner(
            int id,
            Dictionary<string, List<TestNode>> testsByAssembly,
            Dictionary<string, MtpTestDescription> testDescriptions,
            TestSet testSet,
            object discoveryLock,
            ILogger logger,
            IStrykerOptions? options = null)
        {
            var first = new TestNode(
                "test-1",
                "Sample.FirstTests.FirstCase",
                "test",
                "discovered");
            var second = new TestNode(
                "test-2",
                "Sample.FirstTests.SecondCase(value: 1)",
                "test",
                "discovered");
            var third = new TestNode(
                "test-3",
                "Sample.SecondTests.OnlyCase",
                "test",
                "discovered");
            testsByAssembly["assembly.dll"] = [first, second, third];
            testDescriptions[first.Uid] = new MtpTestDescription(first);
            testDescriptions[second.Uid] = new MtpTestDescription(second);
            testDescriptions[third.Uid] = new MtpTestDescription(third);
            testSet.RegisterTest(testDescriptions[first.Uid].Description);
            testSet.RegisterTest(testDescriptions[second.Uid].Description);
            testSet.RegisterTest(testDescriptions[third.Uid].Description);

            return new PerTestCoverageRunner(
                id,
                testsByAssembly,
                testDescriptions,
                testSet,
                discoveryLock,
                options,
                CoverageGroupSizes);
        }

        public List<int> CoverageGroupSizes { get; } = [];
    }

    private sealed class PerTestCoverageRunner : SingleMicrosoftTestPlatformRunner
    {
        public PerTestCoverageRunner(
            int id,
            Dictionary<string, List<TestNode>> testsByAssembly,
            Dictionary<string, MtpTestDescription> testDescriptions,
            TestSet testSet,
            object discoveryLock,
            IStrykerOptions? options,
            List<int> coverageGroupSizes)
            : base(
                id,
                testsByAssembly,
                testDescriptions,
                testSet,
                discoveryLock,
                NullLogger.Instance,
                options)
        {
            CoverageGroupSizes = coverageGroupSizes;
        }

        private List<int> CoverageGroupSizes { get; }

        internal override Task<IReadOnlyList<ICoverageRunResult>> RunTestGroupForCoverageAsync(
            string assembly,
            IReadOnlyList<TestNode> tests,
            CoverageConfidence confidence)
        {
            CoverageGroupSizes.Add(tests.Count);
            var coveredMutants = tests[0].Uid == "test-1" ? new[] { 1, 2 } : [3];
            IReadOnlyList<ICoverageRunResult> results = tests
                .Select(test => (ICoverageRunResult)CoverageRunResult.Create(
                    test.Uid,
                    confidence,
                    coveredMutants,
                    tests[0].Uid == "test-1" ? [2] : [],
                    []))
                .ToList();
            return Task.FromResult(results);
        }
    }
}
