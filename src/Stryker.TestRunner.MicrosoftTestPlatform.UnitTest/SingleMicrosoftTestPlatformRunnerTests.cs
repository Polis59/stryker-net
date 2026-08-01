using Shouldly;
using Stryker.TestRunner.MicrosoftTestPlatform.Models;

namespace Stryker.TestRunner.MicrosoftTestPlatform.UnitTest;

[TestClass]
public class SingleMicrosoftTestPlatformRunnerTests
{
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
}
