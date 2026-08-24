using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Fastcull.Services;
using Xunit;

namespace Fastcull.Tests;

/// <summary>
/// PRD 1.8.2's reverse geocoding. The interesting cases are the broken ones: this is the app's
/// only network dependency, and its whole contract is about behaving well when the network does
/// not.
/// </summary>
public class PlaceLookupTests
{
    /// <summary>A resolver that never touches a network, and records exactly what it was asked.</summary>
    private sealed class FakeResolver : IPlaceResolver
    {
        private readonly Func<double, double, Task<string?>> _behaviour;
        public ConcurrentBag<(double Lat, double Lon)> Calls { get; } = new();
        public int CallCount => Calls.Count;

        public FakeResolver(Func<double, double, Task<string?>> behaviour) => _behaviour = behaviour;

        public static FakeResolver Returning(string? name)
            => new((_, _) => Task.FromResult(name));

        public static FakeResolver Throwing(Exception ex)
            => new((_, _) => Task.FromException<string?>(ex));

        public Task<string?> ResolveAsync(double latitude, double longitude, CancellationToken cancellationToken)
        {
            Calls.Add((latitude, longitude));
            return _behaviour(latitude, longitude);
        }
    }

    /// <summary>BeginResolve is fire-and-forget; this waits for the callback without a Thread.Sleep.</summary>
    private static async Task<string?> ResolveOnceAsync(PlaceLookup lookup, double lat, double lon)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lookup.BeginResolve(lat, lon, name => tcs.TrySetResult(name));

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        return completed == tcs.Task ? await tcs.Task : null;
    }

    /// <summary>Waits for a lookup to settle in the cache, success or failure.</summary>
    private static async Task SettleAsync(PlaceLookup lookup, double lat, double lon)
    {
        for (var i = 0; i < 200; i++)
        {
            if (lookup.TryGetCached(lat, lon, out _)) return;
            await Task.Delay(25);
        }
    }

    // ---- Coordinate formatting: the offline fallback ----

    [Fact]
    public void CoordinatesUseHemisphereLettersNotSigns()
    {
        Assert.Equal("51.5074° N, 0.1278° W", GeoFormat.Coordinates(51.5074, -0.1278));
        Assert.Equal("33.8688° S, 151.2093° E", GeoFormat.Coordinates(-33.8688, 151.2093));
    }

    [Fact]
    public void TheEquatorAndPrimeMeridianReadAsNorthAndEast()
        => Assert.Equal("0.0000° N, 0.0000° E", GeoFormat.Coordinates(0, 0));

    // ---- Cache key rounding ----

    [Fact]
    public void NearbyCoordinatesShareOneCacheKey()
    {
        // Roughly a metre apart - the same spot, as far as a place name is concerned.
        var a = GeoFormat.CacheKey(-6.1659001, 39.2026001);
        var b = GeoFormat.CacheKey(-6.1659009, 39.2026009);

        Assert.Equal(a, b);
    }

    [Fact]
    public void GenuinelyDifferentPlacesDoNotShareACacheKey()
        => Assert.NotEqual(GeoFormat.CacheKey(51.5074, -0.1278), GeoFormat.CacheKey(48.8566, 2.3522));

    // ---- The lookup contract ----

    [Fact]
    public async Task ASuccessfulLookupReportsTheName()
    {
        var lookup = new PlaceLookup(FakeResolver.Returning("Stone Town, Tanzania"));

        Assert.Equal("Stone Town, Tanzania", await ResolveOnceAsync(lookup, -6.1659, 39.2026));
    }

    [Fact]
    public void TryGetCachedIsAMissBeforeAnythingIsLookedUp()
        => Assert.False(new PlaceLookup(FakeResolver.Returning("x")).TryGetCached(1, 2, out _));

    [Fact]
    public async Task ASecondLookupOfTheSameSpotDoesNotHitTheResolver()
    {
        var resolver = FakeResolver.Returning("Stone Town");
        var lookup = new PlaceLookup(resolver);

        await ResolveOnceAsync(lookup, -6.1659, 39.2026);
        await ResolveOnceAsync(lookup, -6.1659, 39.2026);

        Assert.Equal(1, resolver.CallCount);
    }

    [Fact]
    public async Task ABurstFromOneSpotCollapsesToASingleLookup()
    {
        // Forty frames, each a fraction of a metre from the last - the case the rounding exists
        // for. One request, not forty.
        var resolver = FakeResolver.Returning("Stone Town");
        var lookup = new PlaceLookup(resolver);

        await ResolveOnceAsync(lookup, -6.1659, 39.2026);
        for (var i = 0; i < 40; i++)
            await ResolveOnceAsync(lookup, -6.1659 + i * 0.000001, 39.2026 + i * 0.000001);

        Assert.Equal(1, resolver.CallCount);
        Assert.Equal(1, lookup.CacheCount);
    }

    [Fact]
    public async Task AFailedLookupIsSilentAndReportsNothing()
    {
        var lookup = new PlaceLookup(FakeResolver.Throwing(new System.Net.Http.HttpRequestException("offline")));

        // No callback, no exception - the caller keeps showing raw coordinates.
        Assert.Null(await ResolveOnceAsync(lookup, 51.5074, -0.1278));
    }

    [Fact]
    public async Task AFailureIsRememberedSoItIsNotRetriedForever()
    {
        var resolver = FakeResolver.Throwing(new System.Net.Http.HttpRequestException("offline"));
        var lookup = new PlaceLookup(resolver);

        lookup.BeginResolve(51.5074, -0.1278, _ => { });
        await SettleAsync(lookup, 51.5074, -0.1278);

        lookup.BeginResolve(51.5074, -0.1278, _ => { });
        await Task.Delay(50);

        Assert.Equal(1, resolver.CallCount);
        Assert.True(lookup.TryGetCached(51.5074, -0.1278, out var cached));
        Assert.Null(cached);
    }

    [Fact]
    public async Task ACancelledOrTimedOutLookupFailsSilently()
    {
        var lookup = new PlaceLookup(FakeResolver.Throwing(new OperationCanceledException()));
        Assert.Null(await ResolveOnceAsync(lookup, 10, 20));
    }

    [Fact]
    public async Task AnEmptyNameCountsAsAFailureRatherThanAnEmptyLabel()
    {
        var lookup = new PlaceLookup(FakeResolver.Returning("   "));

        Assert.Null(await ResolveOnceAsync(lookup, 10, 20));
        await SettleAsync(lookup, 10, 20);

        Assert.True(lookup.TryGetCached(10, 20, out var cached));
        Assert.Null(cached);
    }

    [Fact]
    public async Task AThrowingCallbackDoesNotBreakTheCache()
    {
        var lookup = new PlaceLookup(FakeResolver.Returning("Somewhere"));

        lookup.BeginResolve(1, 2, _ => throw new InvalidOperationException("UI blew up"));
        await SettleAsync(lookup, 1, 2);

        Assert.True(lookup.TryGetCached(1, 2, out var cached));
        Assert.Equal("Somewhere", cached);
    }

    [Fact]
    public async Task ConcurrentRequestsForOneSpotStartOneLookup()
    {
        var gate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new FakeResolver((_, _) => gate.Task);
        var lookup = new PlaceLookup(resolver);

        for (var i = 0; i < 20; i++) lookup.BeginResolve(-6.1659, 39.2026, _ => { });

        gate.SetResult("Stone Town");
        await SettleAsync(lookup, -6.1659, 39.2026);

        Assert.Equal(1, resolver.CallCount);
    }

    // ---- Nominatim response parsing, against captured response shapes ----

    [Fact]
    public void ALocalityIsPreferredOverTheFullDisplayName()
    {
        const string json = """
        {"display_name":"Stone Town, Mjini Magharibi, Zanzibar Urban/West, 71101, Tanzania",
         "address":{"city":"Stone Town","county":"Mjini Magharibi","country":"Tanzania"}}
        """;

        Assert.Equal("Stone Town, Tanzania", NominatimPlaceResolver.ParsePlaceName(json));
    }

    [Theory]
    [InlineData("town")]
    [InlineData("village")]
    [InlineData("municipality")]
    public void CoarserLocalityFieldsAreAcceptedWhenCityIsAbsent(string field)
    {
        var json = "{\"address\":{\"" + field + "\":\"Somewhere\",\"country\":\"Nowhere\"}}";

        Assert.Equal("Somewhere, Nowhere", NominatimPlaceResolver.ParsePlaceName(json));
    }

    [Fact]
    public void CountryAloneIsUsedWhenNothingFinerExists()
        => Assert.Equal("Tanzania", NominatimPlaceResolver.ParsePlaceName("""{"address":{"country":"Tanzania"}}"""));

    [Fact]
    public void ALocalityIsNotDoubledWhenItMatchesTheCountry()
        => Assert.Equal("Singapore", NominatimPlaceResolver.ParsePlaceName("""{"address":{"city":"Singapore","country":"Singapore"}}"""));

    [Fact]
    public void ALongDisplayNameIsTrimmedToItsEnds()
    {
        const string json = """{"display_name":"A, B, C, D, E"}""";
        Assert.Equal("A, E", NominatimPlaceResolver.ParsePlaceName(json));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("<html><body>429 Too Many Requests</body></html>")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"error":"Unable to geocode"}""")]
    public void AMalformedOrErrorResponseYieldsNothingRatherThanThrowing(string json)
        => Assert.Null(NominatimPlaceResolver.ParsePlaceName(json));
}
