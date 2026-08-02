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
    public async Task LanesShareCapacityFairlyAndOrdinaryBorrowsAfterBroadDrainsAsync()
    {
        const int concurrency = 4;
        var work = Enumerable.Range(0, 16).ToArray();
        var releaseBroad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOrdinary = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var balancedLanesStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ordinaryExpanded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var activeBroad = 0;
        var activeOrdinary = 0;
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
                    if (currentBroad >= concurrency / 2 && Volatile.Read(ref activeOrdinary) >= concurrency / 2)
                    {
                        balancedLanesStarted.TrySetResult();
                    }
                }
                else
                {
                    var currentOrdinary = Interlocked.Increment(ref activeOrdinary);
                    if (currentOrdinary >= concurrency)
                    {
                        ordinaryExpanded.TrySetResult();
                    }

                    if (currentOrdinary >= concurrency / 2 && Volatile.Read(ref activeBroad) >= concurrency / 2)
                    {
                        balancedLanesStarted.TrySetResult();
                    }
                }

                try
                {
                    await (item < 8 ? releaseBroad.Task : releaseOrdinary.Task).ConfigureAwait(false);
                }
                finally
                {
                    if (item < 8)
                    {
                        Interlocked.Decrement(ref activeBroad);
                    }
                    else
                    {
                        Interlocked.Decrement(ref activeOrdinary);
                    }

                    Interlocked.Decrement(ref active);
                }
            });

        var started = await Task.WhenAny(balancedLanesStarted.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        started.ShouldBe(balancedLanesStarted.Task);
        maximumActiveBroad.ShouldBeLessThanOrEqualTo(concurrency / 2);
        maximumActive.ShouldBeLessThanOrEqualTo(concurrency);

        releaseBroad.TrySetResult();
        var expanded = await Task.WhenAny(ordinaryExpanded.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        expanded.ShouldBe(ordinaryExpanded.Task);
        maximumActive.ShouldBeLessThanOrEqualTo(concurrency);

        releaseOrdinary.TrySetResult();
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
