using Moq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;
using Stryker.Abstractions;
using Stryker.Abstractions.Options;
using Stryker.Abstractions.Testing;
using Stryker.Configuration.Options;
using Stryker.Core.CoverageAnalysis;
using Stryker.Core.Mutants;
using Stryker.TestRunner.Results;
using Stryker.TestRunner.Tests;
using Stryker.Utilities.Logging;

namespace Stryker.Core.UnitTest.CoverageAnalysis;

[TestClass]
public class CoverageAnalyserTests
{
    [TestMethod]
    public void OutsideOnlyCoverageRequiresEarlyActivation()
    {
        var mutant = Analyze(
            CoverageRunResult.Create(
                "outside",
                CoverageConfidence.Exact,
                [1],
                [],
                [1]));

        mutant.MustBeTestedInIsolation.ShouldBeTrue();
        mutant.AssessingTests.GetIdentifiers().ShouldBe(["outside"]);
    }

    [TestMethod]
    public void ExactLifecycleCoverageSupersedesOutsideCoverage()
    {
        var mutant = Analyze(
            CoverageRunResult.Create(
                "outside",
                CoverageConfidence.Exact,
                [1],
                [],
                [1]),
            CoverageRunResult.Create(
                "inside",
                CoverageConfidence.Exact,
                [1],
                [],
                []));

        mutant.MustBeTestedInIsolation.ShouldBeFalse();
        mutant.AssessingTests.GetIdentifiers().ShouldBe(["inside"]);
    }

    private static Mutant Analyze(params ICoverageRunResult[] coverage)
    {
        var runner = new Mock<ITestRunner>();
        runner.Setup(candidate => candidate.CaptureCoverage(It.IsAny<IProjectAndTests>()))
            .Returns(coverage);
        var mutant = new Mutant
        {
            Id = 1,
            ResultStatus = MutantStatus.Pending,
        };
        var analyser = new CoverageAnalyser(
            TestLoggerFactory.CreateLogger<CoverageAnalyser>());

        analyser.DetermineTestCoverage(
            new StrykerOptions
            {
                OptimizationMode = OptimizationModes.CoverageBasedTest,
            },
            Mock.Of<IProjectAndTests>(),
            runner.Object,
            [mutant],
            TestIdentifierList.NoTest());

        return mutant;
    }
}
