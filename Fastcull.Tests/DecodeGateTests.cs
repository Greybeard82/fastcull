using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fastcull.Services;
using Xunit;

namespace Fastcull.Tests;

/// <summary>
/// The bounded worker pool from PRD 3.3. Before this existed, constructing a view-model started
/// two decodes immediately, so opening a 2,000-photo folder launched 4,000 at once.
/// </summary>
public class DecodeGateTests
{
    [Fact]
    public void MaxConcurrency_IsMinOfSixAndCoresMinusTwo_FlooredAtOne()
    {
        var expected = Math.Max(1, Math.Min(6, Environment.ProcessorCount - 2));

        Assert.Equal(expected, DecodeGate.MaxConcurrency);
        Assert.InRange(DecodeGate.MaxConcurrency, 1, 6);
    }

    [Fact]
    public async Task NeverRunsMoreThanTheCapAtOnce()
    {
        var running = 0;
        var peak = 0;
        var gate = new object();

        var work = Enumerable.Range(0, 60).Select(async _ =>
        {
            await DecodeGate.RunAsync<object?>(async () =>
            {
                lock (gate)
                {
                    running++;
                    if (running > peak) peak = running;
                }

                await Task.Delay(15);

                lock (gate) running--;
                return null;
            }, CancellationToken.None);
        }).ToArray();

        await Task.WhenAll(work);

        Assert.Equal(0, running);
        Assert.InRange(peak, 1, DecodeGate.MaxConcurrency);
    }

    [Fact]
    public async Task CancellingWhileQueued_SkipsTheDecodeEntirely()
    {
        // A photo that leaves the prefetch window before its turn comes up must never decode.
        // This is what makes cancel-on-window-exit worth anything: without it, a fast scrub
        // through a folder would still decode every photo it passed over.
        using var block = new SemaphoreSlim(0, 1);
        var hogs = new Task[DecodeGate.MaxConcurrency];

        for (var i = 0; i < hogs.Length; i++)
        {
            hogs[i] = DecodeGate.RunAsync<object?>(async () =>
            {
                await block.WaitAsync();
                return null;
            }, CancellationToken.None);
        }

        using var cts = new CancellationTokenSource();
        var decoded = false;

        var queued = DecodeGate.RunAsync<object?>(() =>
        {
            decoded = true;
            return Task.FromResult<object?>(null);
        }, cts.Token);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.False(decoded, "a cancelled decode must not run after its slot frees up");

        // Let the hogs finish so the gate returns to a clean state for other tests.
        for (var i = 0; i < hogs.Length; i++) block.Release();
        await Task.WhenAll(hogs);
    }

    [Fact]
    public async Task ReleasesItsSlotWhenTheDecodeThrows()
    {
        // A failing decode must not permanently consume a slot, or a handful of corrupt files
        // would starve the pool for the rest of the session.
        for (var i = 0; i < DecodeGate.MaxConcurrency + 2; i++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                DecodeGate.RunAsync<object?>(() => throw new InvalidOperationException("boom"),
                    CancellationToken.None));
        }

        Assert.Equal(0, DecodeGate.InFlight);
    }
}
