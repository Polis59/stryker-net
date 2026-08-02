using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;
using Stryker.Abstractions;
using Stryker.Abstractions.Options;
using Stryker.Abstractions.Testing;
using Stryker.TestRunner.MicrosoftTestPlatform.Models;
using Stryker.TestRunner.Results;
using Stryker.TestRunner.Tests;

namespace Stryker.TestRunner.MicrosoftTestPlatform.UnitTest;

[TestClass]
public class MicrosoftTestPlatformRunnerPoolTests : TestBase
{
    [TestMethod]
    public async Task InitialTestAsync_ReusesOneRunForSharedTestAssemblies()
    {
        var firstRunStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRun = new TaskCompletionSource<ITestRunResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = CreateRunner();
        var calls = 0;
        runner
            .Setup(candidate => candidate.InitialTestAsync(It.IsAny<IProjectAndTests>()))
            .Callback(() =>
            {
                Interlocked.Increment(ref calls);
                firstRunStarted.TrySetResult();
            })
            .Returns(releaseFirstRun.Task);
        runner.Setup(candidate => candidate.ResetServerAsync()).Returns(Task.CompletedTask);

        using var pool = CreatePool(runner.Object);
        var first = pool.InitialTestAsync(CreateProject("shared-tests.dll"));
        var second = pool.InitialTestAsync(CreateProject("shared-tests.dll"));

        await firstRunStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        calls.ShouldBe(1);

        var expected = new TestRunResult(true);
        releaseFirstRun.SetResult(expected);

        (await first).ShouldBeSameAs(expected);
        (await second).ShouldBeSameAs(expected);
        calls.ShouldBe(1);
        runner.Verify(candidate => candidate.ResetServerAsync(), Times.Once);
    }

    [TestMethod]
    public async Task InitialTestAsync_RunsDistinctTestAssemblySetsSerially()
    {
        var firstRunStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRun = new TaskCompletionSource<ITestRunResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRunStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new TestRunResult(true);
        var runner = CreateRunner();
        var calls = 0;
        runner
            .Setup(candidate => candidate.InitialTestAsync(It.IsAny<IProjectAndTests>()))
            .Returns(() =>
            {
                var call = Interlocked.Increment(ref calls);
                if (call == 1)
                {
                    firstRunStarted.TrySetResult();
                    return releaseFirstRun.Task;
                }

                secondRunStarted.TrySetResult();
                return Task.FromResult<ITestRunResult>(expected);
            });
        runner.Setup(candidate => candidate.ResetServerAsync()).Returns(Task.CompletedTask);

        using var pool = CreatePool(runner.Object);
        var first = pool.InitialTestAsync(CreateProject("first-tests.dll"));
        var second = pool.InitialTestAsync(CreateProject("second-tests.dll"));

        await firstRunStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);
        secondRunStarted.Task.IsCompleted.ShouldBeFalse();

        releaseFirstRun.SetResult(expected);

        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        calls.ShouldBe(2);
        runner.Verify(candidate => candidate.ResetServerAsync(), Times.Exactly(2));
    }

    private static MicrosoftTestPlatformRunnerPool CreatePool(
        SingleMicrosoftTestPlatformRunner runner)
    {
        var options = new Mock<IStrykerOptions>();
        options.SetupGet(candidate => candidate.Concurrency).Returns(1);
        var factory = new Mock<ISingleRunnerFactory>();
        factory
            .Setup(candidate => candidate.CreateRunner(
                It.IsAny<int>(),
                It.IsAny<Dictionary<string, List<TestNode>>>(),
                It.IsAny<Dictionary<string, MtpTestDescription>>(),
                It.IsAny<TestSet>(),
                It.IsAny<object>(),
                It.IsAny<ILogger>(),
                It.IsAny<IStrykerOptions>()))
            .Returns(runner);
        return new MicrosoftTestPlatformRunnerPool(
            options.Object,
            NullLogger.Instance,
            factory.Object);
    }

    private static Mock<SingleMicrosoftTestPlatformRunner> CreateRunner() =>
        new(
            MockBehavior.Loose,
            0,
            new Dictionary<string, List<TestNode>>(),
            new Dictionary<string, MtpTestDescription>(),
            new TestSet(),
            new object(),
            NullLogger.Instance,
            Mock.Of<IStrykerOptions>());

    private static IProjectAndTests CreateProject(string assembly)
    {
        var project = new Mock<IProjectAndTests>();
        project.Setup(candidate => candidate.GetTestAssemblies()).Returns([assembly]);
        return project.Object;
    }
}
