using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Xunit;

namespace Fastcull.Tests;

/// <summary>
/// Whether consuming the scanner's channel lets the UI thread run anything else.
///
/// DirectoryScanner hands photos over an UNBOUNDED channel, and MainViewModel consumes it with
/// `await foreach` on the UI thread with no ConfigureAwait - so every iteration is meant to give
/// the message pump a turn. These tests check whether it actually does, because if the producer
/// has run ahead the awaits complete synchronously and the loop becomes an uninterruptible
/// spin over everything already buffered.
///
/// The context below stands in for the WinUI DispatcherQueue: it counts how many continuations
/// were posted to it, which is the same thing as "how many chances did the message loop get".
/// </summary>
// xUnit1031 is suppressed for this file, deliberately and narrowly. The analyser objects to
// blocking on tasks in a test; blocking is precisely what is under examination here. These tests
// stand a synchronous message loop up around the consumer and drive it by hand, exactly as the
// WinUI dispatcher does, because the question being asked is whether that loop ever gets a turn.
// An async test would use the xUnit context instead and could not observe the property at all.
#pragma warning disable xUnit1031
public class ScanConsumerYieldTests
{
    /// <summary>A single-threaded context that counts posts, like a dispatcher would receive.</summary>
    private sealed class CountingContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();

        public int Posts { get; private set; }

        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (_queue)
            {
                Posts++;
                _queue.Enqueue((d, state));
            }
        }

        /// <summary>Runs queued work until the supplied task finishes, like a message loop.</summary>
        public void RunUntil(Task task)
        {
            while (!task.IsCompleted)
            {
                (SendOrPostCallback Callback, object? State) item;
                lock (_queue)
                {
                    // Yield rather than Sleep(1): the Windows timer granularity is ~15 ms, and
                    // sleeping once per idle turn took this test from milliseconds to 16 seconds.
                    if (_queue.Count == 0) { Thread.Yield(); continue; }
                    item = _queue.Dequeue();
                }

                item.Callback(item.State);
            }

            task.GetAwaiter().GetResult();
        }
    }

    private static async Task<int> ConsumeAsync(ChannelReader<int> reader, Action perItem)
    {
        var seen = 0;

        // Deliberately written the way MainViewModel.OpenFolderAsync writes it: no
        // ConfigureAwait, so continuations come back to the captured context.
        await foreach (var _ in reader.ReadAllAsync())
        {
            seen++;
            perItem();
        }

        return seen;
    }

    [Fact]
    public void AFullyBufferedChannelIsConsumedWithoutYieldingOnce()
    {
        // The scan's worst case, stated plainly: the producer finished before the consumer
        // started, so every item is already sitting in the channel.
        const int items = 5_000;

        var channel = Channel.CreateUnbounded<int>(new UnboundedChannelOptions { SingleReader = true });
        for (var i = 0; i < items; i++) channel.Writer.TryWrite(i);
        channel.Writer.Complete();

        var context = new CountingContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);

        try
        {
            var task = ConsumeAsync(channel.Reader, () => { });
            context.RunUntil(task);

            Assert.Equal(items, task.Result);

            // THE FINDING. With everything buffered, ReadAllAsync's MoveNextAsync returns a
            // ValueTask that is already complete, so `await` never suspends and the whole loop
            // runs as one uninterrupted block. On the UI thread that means 5,000 iterations with
            // the message pump getting no turn at all - the window stops answering, and Windows
            // paints it "Not Responding".
            Assert.True(context.Posts <= 1,
                $"expected the loop to complete without yielding; it posted {context.Posts} times");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Fact]
    public void AnEmptyChannelDoesYieldPerItem()
    {
        // The contrast case, and why the bug is intermittent: when the consumer outruns the
        // producer it genuinely awaits, the pump gets a turn per item, and the app stays
        // responsive. Which of these two regimes you land in is decided by how fast the disk
        // happens to be feeding the producer - so the same folder hangs on one attempt and
        // loads fine on the next.
        const int items = 200;

        var channel = Channel.CreateUnbounded<int>(new UnboundedChannelOptions { SingleReader = true });

        var context = new CountingContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);

        try
        {
            // A handshake rather than a delay. Timing the producer with Task.Delay made this test
            // flaky: at millisecond granularity it sometimes wrote two items before the consumer
            // took the first, so the channel was briefly non-empty and the consumer skipped a
            // yield. Waiting for the consumer to signal each item makes "the channel is empty
            // when the consumer asks" true by construction instead of by luck.
            using var consumed = new SemaphoreSlim(0);
            var task = ConsumeAsync(channel.Reader, () => consumed.Release());

            var producer = Task.Run(async () =>
            {
                for (var i = 0; i < items; i++)
                {
                    channel.Writer.TryWrite(i);
                    await consumed.WaitAsync().ConfigureAwait(false);
                }

                channel.Writer.Complete();
            });

            context.RunUntil(task);
            producer.GetAwaiter().GetResult();

            Assert.Equal(items, task.Result);
            // A majority, deliberately not an exact count. Even with the handshake the producer
            // is sometimes woken and writes again before the consumer has got back to
            // MoveNextAsync, so a handful of items are found already waiting and taken without a
            // yield - typically around 190 of 200. Asserting an exact figure made this test
            // flaky, which is worse than useless.
            //
            // The contrast is what the test is for, and it is not marginal: the large majority of
            // 200 items here, against at most ONE post for five thousand in the buffered case.
            Assert.True(context.Posts > items / 2,
                $"a starved consumer should yield for most items; it posted {context.Posts} of {items}");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
}
#pragma warning restore xUnit1031
