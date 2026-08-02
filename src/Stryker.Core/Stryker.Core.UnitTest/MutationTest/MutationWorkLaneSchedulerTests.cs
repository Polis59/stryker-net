using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;
using Stryker.Core.MutationTest;

namespace Stryker.Core.UnitTest.MutationTest;

[TestClass]
public class MutationWorkLaneSchedulerTests
{
    [TestMethod]
    public async Task BroadBacklogDoesNotStarveOrdinaryWorkAsync()
    {
        const int concurrency = 4;
        var work = Enumerable.Range(0, 10).ToArray();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ordinaryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var activeBroad = 0;
        var maximumActive = 0;
        var maximumActiveBroad = 0;

        var run = MutationWorkLaneScheduler.RunAsync(
            work,
            item => item < 8,
            concurrency,
            async (item, _) =>
            {
                var currentActive = Interlocked.Increment(ref active);
                UpdateMaximum(ref maximumActive, currentActive);
                if (item < 8)
                {
                    var currentBroad = Interlocked.Increment(ref activeBroad);
                    UpdateMaximum(ref maximumActiveBroad, currentBroad);
                }
                else
                {
                    ordinaryStarted.TrySetResult();
                }

                try
                {
                    await release.Task.ConfigureAwait(false);
                }
                finally
                {
                    if (item < 8)
                    {
                        Interlocked.Decrement(ref activeBroad);
                    }

                    Interlocked.Decrement(ref active);
                }
            });

        var started = await Task.WhenAny(ordinaryStarted.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        started.ShouldBe(ordinaryStarted.Task);
        maximumActiveBroad.ShouldBeLessThanOrEqualTo(concurrency / 2);
        maximumActive.ShouldBeLessThanOrEqualTo(concurrency);

        release.TrySetResult();
        await run;
    }

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        var observed = Volatile.Read(ref maximum);
        while (candidate > observed)
        {
            var prior = Interlocked.CompareExchange(ref maximum, candidate, observed);
            if (prior == observed)
            {
                return;
            }

            observed = prior;
        }
    }
}
