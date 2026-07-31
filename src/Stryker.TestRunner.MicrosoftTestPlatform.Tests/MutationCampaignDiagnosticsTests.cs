using System.Text.Json;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Stryker.Abstractions;
using Stryker.Abstractions.Options;
using Stryker.Abstractions.Testing;
using Stryker.TestRunner.MicrosoftTestPlatform.Models;
using Stryker.TestRunner.Tests;

namespace Stryker.TestRunner.MicrosoftTestPlatform.Tests;

public sealed class MutationCampaignDiagnosticsTests
{
    [Fact]
    public void PlanDetailNamesEveryMutantWithClassificationAndAssessingWidth()
    {
        var planPath = Path.Combine(
            Path.GetTempPath(),
            $"threadway-stryker-plan-{Guid.NewGuid():N}.jsonl");
        MutationCampaignDiagnostics.ConfigureForTests(planPath, null);

        try
        {
            var options = new Mock<IStrykerOptions>();
            options
                .SetupGet(candidate => candidate.OptimizationMode)
                .Returns(OptimizationModes.CoverageBasedTest);
            var tree = CSharpSyntaxTree.ParseText(
                "class Example { int Answer() { return 1 + 2; } }",
                path: "Example.cs");
            var mutation = new Mutation
            {
                OriginalNode = tree.GetRoot().DescendantNodes().Last(),
                DisplayName = "Arithmetic mutation",
                Type = Mutator.Arithmetic,
            };

            _ = MutationBatchPlanner.Build(
                options.Object,
                [
                    CreateMutant(
                        7,
                        isStaticValue: true,
                        assessingTests: new TestIdentifierList("t1", "t2")),
                    CreateMutant(
                        8,
                        mustBeTestedInIsolation: true,
                        assessingTests: new TestIdentifierList("t3")),
                    CreateMutant(
                        5,
                        assessingTests: new TestIdentifierList("t1"),
                        mutation: mutation),
                ]).ToList();

            // Other test classes may run the planner concurrently with this
            // seam configured, so the segment under test is selected by its
            // mutant identifiers rather than by exclusive file ownership.
            var segment = Assert.Single(
                File.ReadAllLines(planPath)
                    .Select(line => JsonDocument.Parse(line).RootElement),
                candidate => candidate.GetProperty("mutants")
                    .EnumerateArray()
                    .Select(mutant => mutant.GetProperty("id").GetInt32())
                    .Order()
                    .SequenceEqual([5, 7, 8]));
            Assert.Equal(
                [1],
                segment.GetProperty("ordinaryGroupSizes")
                    .EnumerateArray()
                    .Select(size => size.GetInt32()));

            var mutants = segment.GetProperty("mutants")
                .EnumerateArray()
                .ToDictionary(mutant => mutant.GetProperty("id").GetInt32());
            Assert.Equal(3, mutants.Count);

            var staticMutant = mutants[7];
            Assert.True(staticMutant.GetProperty("isStaticValue").GetBoolean());
            Assert.False(staticMutant.GetProperty("mustBeTestedInIsolation").GetBoolean());
            Assert.Equal(2, staticMutant.GetProperty("assessingTestCount").GetInt32());
            Assert.Equal(
                ["t1", "t2"],
                staticMutant.GetProperty("assessingTests")
                    .EnumerateArray()
                    .Select(test => test.GetString()));

            var isolationMutant = mutants[8];
            Assert.True(isolationMutant.GetProperty("mustBeTestedInIsolation").GetBoolean());
            Assert.Equal(
                ["t3"],
                isolationMutant.GetProperty("assessingTests")
                    .EnumerateArray()
                    .Select(test => test.GetString()));

            var ordinaryMutant = mutants[5];
            Assert.False(ordinaryMutant.GetProperty("isStaticValue").GetBoolean());
            Assert.Equal(1, ordinaryMutant.GetProperty("assessingTestCount").GetInt32());
            Assert.False(ordinaryMutant.TryGetProperty("assessingTests", out _));
            Assert.Equal("Arithmetic", ordinaryMutant.GetProperty("mutator").GetString());
            Assert.Equal("Example.cs:1", ordinaryMutant.GetProperty("location").GetString());
        }
        finally
        {
            MutationCampaignDiagnostics.ConfigureForTests(null, null);
            File.Delete(planPath);
        }
    }

    [Fact]
    public void CoverageTracePreservesRawPerTestRecordsBeforeWidening()
    {
        var traceDirectory = Path.Combine(
            Path.GetTempPath(),
            $"threadway-stryker-coverage-trace-{Guid.NewGuid():N}");
        MutationCampaignDiagnostics.ConfigureForTests(null, traceDirectory);

        using var runner = new SingleMicrosoftTestPlatformRunner(
            3001,
            new Dictionary<string, List<TestNode>>(),
            new Dictionary<string, MtpTestDescription>(),
            new TestSet(),
            new object(),
            NullLogger.Instance);
        try
        {
            File.WriteAllText(
                runner.CoverageMapFilePath,
                "threadway-stryker-coverage-v1" + Environment.NewLine +
                "test-1\t1,2\t2\t3" + Environment.NewLine +
                "test-2\t4\t\t" + Environment.NewLine);

            _ = runner.ReadPerTestCoverageData(
                [
                    new TestNode("test-1", "Example.FirstTests.FirstCase", "test", "discovered"),
                    new TestNode("test-2", "Example.FirstTests.SecondCase", "test", "discovered"),
                ],
                CoverageConfidence.Exact);

            var traceFile = Assert.Single(Directory.GetFiles(traceDirectory));
            var trace = JsonDocument.Parse(File.ReadAllText(traceFile)).RootElement;
            Assert.Equal("Example.FirstTests", trace.GetProperty("boundary").GetString());
            Assert.Equal(
                ["test-1", "test-2"],
                trace.GetProperty("requestedTestIds")
                    .EnumerateArray()
                    .Select(test => test.GetString()));
            Assert.Equal(
                ["test-1\t1,2\t2\t3", "test-2\t4\t\t"],
                trace.GetProperty("records")
                    .EnumerateArray()
                    .Select(record => record.GetString()));
        }
        finally
        {
            MutationCampaignDiagnostics.ConfigureForTests(null, null);
            if (Directory.Exists(traceDirectory))
            {
                Directory.Delete(traceDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void DiagnosticsStayInertWithoutAnExplicitOptIn()
    {
        MutationCampaignDiagnostics.ConfigureForTests(null, null);
        var options = new Mock<IStrykerOptions>();
        options
            .SetupGet(candidate => candidate.OptimizationMode)
            .Returns(OptimizationModes.CoverageBasedTest);

        var groups = MutationBatchPlanner.Build(
            options.Object,
            [CreateMutant(5, assessingTests: new TestIdentifierList("t1"))]).ToList();

        Assert.Single(groups);
    }

    [Fact]
    public async Task CoverageCaptureRetriesOnceWhenTheIsolationHostIsLost()
    {
        using var runner = new HostLossCoverageRunner(hostLossCount: 1);

        var coverage = await runner.RunTestGroupForCoverageAsync(
            "assembly.dll",
            [new TestNode("test-1", "Example.FirstTests.FirstCase", "test", "discovered")],
            CoverageConfidence.Exact);

        Assert.Equal(2, runner.Attempts);
        Assert.Equal([1, 2], Assert.Single(coverage).MutationsCovered.Order());
    }

    [Fact]
    public async Task CoverageCaptureFailsClosedWhenTheIsolationHostIsLostTwice()
    {
        using var runner = new HostLossCoverageRunner(hostLossCount: 2);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunTestGroupForCoverageAsync(
                "assembly.dll",
                [new TestNode("test-1", "Example.FirstTests.FirstCase", "test", "discovered")],
                CoverageConfidence.Exact));

        Assert.Equal(2, runner.Attempts);
        Assert.Contains("exited before responding", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CoverageCaptureRetriesOnceWhenThePerTestMapIsIncomplete()
    {
        using var runner = new IncompleteMapCoverageRunner(incompleteAttempts: 1);

        var coverage = await runner.RunTestGroupForCoverageAsync(
            "assembly.dll",
            [
                new TestNode("test-1", "Example.FirstTests.FirstCase", "test", "discovered"),
                new TestNode("test-2", "Example.FirstTests.SecondCase", "test", "discovered"),
            ],
            CoverageConfidence.Exact);

        Assert.Equal(2, runner.Attempts);
        Assert.Equal(2, coverage.Count);
        Assert.Equal(
            [3, 4],
            coverage.Single(result => result.TestId == "test-2").MutationsCovered.Order());
    }

    [Fact]
    public async Task CoverageCaptureFailsClosedWithTheSinkErrorWhenTheMapStaysIncomplete()
    {
        using var runner = new IncompleteMapCoverageRunner(incompleteAttempts: 2);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            runner.RunTestGroupForCoverageAsync(
                "assembly.dll",
                [
                    new TestNode("test-1", "Example.FirstTests.FirstCase", "test", "discovered"),
                    new TestNode("test-2", "Example.FirstTests.SecondCase", "test", "discovered"),
                ],
                CoverageConfidence.Exact));

        Assert.Equal(2, runner.Attempts);
        Assert.Contains("Missing: [test-2]", exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            "The coverage lifecycle sink reported: IOException: simulated append failure",
            exception.Message,
            StringComparison.Ordinal);
    }

    private sealed class IncompleteMapCoverageRunner : SingleMicrosoftTestPlatformRunner
    {
        private static int _nextRunnerId = 5000;
        private readonly int incompleteAttempts;

        public IncompleteMapCoverageRunner(int incompleteAttempts)
            : base(
                Interlocked.Increment(ref _nextRunnerId),
                new Dictionary<string, List<TestNode>>(),
                new Dictionary<string, MtpTestDescription>(),
                new TestSet(),
                new object(),
                NullLogger.Instance)
        {
            this.incompleteAttempts = incompleteAttempts;
        }

        public int Attempts { get; private set; }

        internal override Task<CollectibleIsolationResponse> ExecuteCoverageContextAsync(
            string assembly,
            IReadOnlyList<string> testUids)
        {
            Attempts++;
            if (Attempts <= incompleteAttempts)
            {
                // The lifecycle sink records its own failure and publishes a map
                // missing the affected test, exactly as a failed append behaves.
                File.WriteAllText(
                    MutantMapErrorFilePath,
                    "IOException: simulated append failure");
                File.WriteAllText(
                    CoverageMapFilePath,
                    "threadway-stryker-coverage-v1" + Environment.NewLine +
                    "test-1\t1,2\t\t" + Environment.NewLine);
            }
            else
            {
                File.WriteAllText(
                    CoverageMapFilePath,
                    "threadway-stryker-coverage-v1" + Environment.NewLine +
                    "test-1\t1,2\t\t" + Environment.NewLine +
                    "test-2\t3,4\t\t" + Environment.NewLine);
            }

            return Task.FromResult(new CollectibleIsolationResponse(
                [],
                Error: null,
                DurationTicks: 0,
                Unloaded: true));
        }
    }

    private sealed class HostLossCoverageRunner : SingleMicrosoftTestPlatformRunner
    {
        private static int _nextRunnerId = 4000;
        private readonly int hostLossCount;

        public HostLossCoverageRunner(int hostLossCount)
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

        public int Attempts { get; private set; }

        internal override Task<CollectibleIsolationResponse> ExecuteCoverageContextAsync(
            string assembly,
            IReadOnlyList<string> testUids)
        {
            Attempts++;
            if (Attempts <= hostLossCount)
            {
                return Task.FromResult(CollectibleIsolationResponse.RuntimeError(
                    "The collectible isolation host exited before responding (exit code -1073741819)."));
            }

            File.WriteAllText(
                CoverageMapFilePath,
                "threadway-stryker-coverage-v1" + Environment.NewLine +
                "test-1\t1,2\t\t" + Environment.NewLine);
            return Task.FromResult(new CollectibleIsolationResponse(
                [],
                Error: null,
                DurationTicks: 0,
                Unloaded: true));
        }
    }

    private static IMutant CreateMutant(
        int id,
        bool isStaticValue = false,
        bool mustBeTestedInIsolation = false,
        ITestIdentifiers? assessingTests = null,
        Mutation? mutation = null)
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
        if (mutation is not null)
        {
            mutant.SetupGet(candidate => candidate.Mutation).Returns(mutation);
        }

        return mutant.Object;
    }
}
