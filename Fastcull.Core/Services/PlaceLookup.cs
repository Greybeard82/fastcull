using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Fastcull.Services
{
    /// <summary>
    /// Turns GPS coordinates into a human-readable place name.
    ///
    /// Separated from the lookup itself so the caching, rounding, failure and offline behaviour in
    /// <see cref="PlaceLookup"/> can be tested without a network - which matters more than usual
    /// here, because the interesting paths are the ones where the network is broken.
    /// </summary>
    public interface IPlaceResolver
    {
        Task<string?> ResolveAsync(double latitude, double longitude, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Formatting of raw coordinates, kept separate because it is the offline fallback and has to
    /// work when everything else does not.
    /// </summary>
    public static class GeoFormat
    {
        /// <summary>
        /// e.g. "51.5074° N, 0.1278° W". Hemisphere letters rather than signs: a minus sign in
        /// front of a longitude is not something most people read as "west" at a glance.
        /// </summary>
        public static string Coordinates(double latitude, double longitude)
        {
            var ns = latitude >= 0 ? "N" : "S";
            var ew = longitude >= 0 ? "E" : "W";

            return string.Create(CultureInfo.InvariantCulture,
                $"{Math.Abs(latitude):F4}° {ns}, {Math.Abs(longitude):F4}° {ew}");
        }

        /// <summary>
        /// The cache key: coordinates rounded to <see cref="CachePrecision"/> decimal places.
        ///
        /// Three places is roughly 110 m at the equator, so a burst of frames shot from one spot
        /// collapses to a single lookup - which is the whole point. Caching on the exact double
        /// would defeat itself, since consecutive frames differ in the last bits.
        /// </summary>
        public static (double Latitude, double Longitude) CacheKey(double latitude, double longitude)
            => (Math.Round(latitude, CachePrecision), Math.Round(longitude, CachePrecision));

        public const int CachePrecision = 3;
    }

    /// <summary>
    /// PRD 1.8.2. The app's only network-dependent feature, and deliberately the most defensive
    /// code in it.
    ///
    /// The contract, in order of importance:
    ///
    ///   1. **Never blocks.** <see cref="TryGetCached"/> is synchronous and answers instantly from
    ///      memory; the network path is fire-and-forget and reports back through a callback. No
    ///      caller ever awaits a lookup on the way to showing a photo.
    ///   2. **Never throws.** Every failure - offline, DNS, timeout, rate limit, malformed JSON,
    ///      an HTML error page where JSON was promised - resolves to null and the caller falls
    ///      back to raw coordinates. There is no error path for the UI to render.
    ///   3. **Never repeats work.** Results are cached by rounded coordinate, and so are failures,
    ///      so a spot that could not be resolved is not retried on every revisit.
    /// </summary>
    public sealed class PlaceLookup
    {
        /// <summary>
        /// A slow lookup is a failed lookup. Short enough that a hung request cannot keep a
        /// worker or a cache slot occupied while the user culls past twenty photos.
        /// </summary>
        public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);

        private readonly IPlaceResolver _resolver;

        /// <summary>
        /// Resolved names by rounded coordinate. A null value is a remembered failure - see rule 3
        /// above - which is why this is not a ConcurrentDictionary of non-null strings.
        /// </summary>
        private readonly ConcurrentDictionary<(double, double), string?> _cache = new();

        /// <summary>In-flight keys, so twenty photos from one spot start one request, not twenty.</summary>
        private readonly ConcurrentDictionary<(double, double), byte> _inFlight = new();

        public PlaceLookup(IPlaceResolver resolver) => _resolver = resolver;

        /// <summary>How many distinct locations have been resolved or failed. For tests and diagnostics.</summary>
        public int CacheCount => _cache.Count;

        /// <summary>
        /// The cached answer for these coordinates, if one exists.
        ///
        /// Returns true with a null name when the location is a remembered failure, which the
        /// caller should treat as "show coordinates and do not ask again" rather than as a miss.
        /// </summary>
        public bool TryGetCached(double latitude, double longitude, out string? placeName)
            => _cache.TryGetValue(GeoFormat.CacheKey(latitude, longitude), out placeName);

        /// <summary>
        /// Starts a lookup if one is warranted, and invokes <paramref name="onResolved"/> if and
        /// when a name arrives. Returns immediately either way.
        ///
        /// The callback fires only on success and only once per coordinate. It does NOT fire for a
        /// failure: the caller is already showing raw coordinates, which is the correct final state
        /// for a location that cannot be resolved, so there is nothing to update.
        /// </summary>
        public void BeginResolve(double latitude, double longitude, Action<string> onResolved)
        {
            var key = GeoFormat.CacheKey(latitude, longitude);

            if (_cache.TryGetValue(key, out var cached))
            {
                if (cached is not null) onResolved(cached);
                return;
            }

            // First writer wins; everyone else is already covered by that request.
            if (!_inFlight.TryAdd(key, 0)) return;

            _ = ResolveInBackgroundAsync(key, onResolved);
        }

        private async Task ResolveInBackgroundAsync((double Latitude, double Longitude) key, Action<string> onResolved)
        {
            string? name = null;

            try
            {
                using var cts = new CancellationTokenSource(Timeout);
                name = await _resolver.ResolveAsync(key.Latitude, key.Longitude, cts.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Rule 2. Offline, timeout, malformed response - all the same outcome, and none of
                // them is worth surfacing: the caller is already showing usable coordinates.
                name = null;
            }
            finally
            {
                // Cached even when null, so a location that cannot be resolved is not retried on
                // every revisit. Recorded before the callback so a throwing callback cannot leave
                // the key permanently in flight.
                _cache[key] = string.IsNullOrWhiteSpace(name) ? null : name;
                _inFlight.TryRemove(key, out _);
            }

            var resolved = _cache[key];
            if (resolved is null) return;

            try { onResolved(resolved); }
            catch (Exception) { /* a failing UI callback must not take the lookup path down */ }
        }
    }

    /// <summary>
    /// Reverse geocoding via OpenStreetMap's Nominatim.
    ///
    /// Chosen because it needs no API key and no account, which is the deciding factor for a
    /// personal desktop app: a keyed provider would mean either shipping a secret in the binary or
    /// asking the user to obtain one before a metadata field works. It is also the service whose
    /// terms are clearest about exactly this kind of low-volume use.
    ///
    /// Nominatim's usage policy is respected rather than assumed:
    ///   - A descriptive User-Agent identifying the application. Requests without one are refused,
    ///     and correctly so.
    ///   - At most one request per second, enforced below rather than left to chance. The rounded
    ///     coordinate cache in <see cref="PlaceLookup"/> is what keeps the real rate far under it.
    ///   - zoom=10 asks for locality-level detail: "Zanzibar City" rather than a street address.
    ///     A photo caption wants the place, not the postcode.
    /// </summary>
    public sealed class NominatimPlaceResolver : IPlaceResolver, IDisposable
    {
        /// <summary>
        /// Read from settings on construction, NOT a compiled-in constant.
        ///
        /// Nominatim's usage policy requires that an application be able to switch away from the
        /// public instance at the operator's request *"without requiring a software update"*. A
        /// `const` could not satisfy that: every installed copy would keep calling the old
        /// endpoint until a new build shipped, which for a distributed desktop app could be never.
        /// </summary>
        private readonly string _endpoint;

        /// <summary>Nominatim asks for at most one request per second from a single client.</summary>
        private static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(1);

        private readonly HttpClient _http;
        private readonly SemaphoreSlim _rateGate = new(1, 1);
        private DateTime _lastRequestUtc = DateTime.MinValue;

        /// <summary>
        /// Identifies the application and offers a route to contact whoever runs it, which is what
        /// Nominatim's policy asks for. Not cosmetic: unidentified clients are refused.
        ///
        /// **It no longer claims "personal use".** The previous string did, which was a statement
        /// made to the operator of a free service about how the software is used - and one that
        /// would become false the moment the app was sold. Describing what the software is, and
        /// where to find whoever is responsible for it, is both accurate today and stays accurate.
        /// </summary>
        public const string UserAgent = "FastCull/0.1 (photo culling tool; +https://github.com/Greybeard82/fastcull)";

        /// <param name="endpoint">
        /// Overrides the configured endpoint. Used by tests; production passes null and picks up
        /// whatever settings.json says.
        /// </param>
        public NominatimPlaceResolver(HttpClient? http = null, string? endpoint = null)
        {
            _endpoint = string.IsNullOrWhiteSpace(endpoint) ? AppSettings.GeocodingEndpoint : endpoint!;
            _http = http ?? new HttpClient();

            if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
                _http.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        }

        public async Task<string?> ResolveAsync(double latitude, double longitude, CancellationToken cancellationToken)
        {
            await ThrottleAsync(cancellationToken).ConfigureAwait(false);

            var url = string.Create(CultureInfo.InvariantCulture,
                $"{_endpoint}?format=jsonv2&zoom=10&addressdetails=1&lat={latitude:F6}&lon={longitude:F6}");

            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParsePlaceName(json);
        }

        /// <summary>
        /// Pulls the most useful label out of a Nominatim response, preferring a locality over the
        /// full display_name - which runs to "Stone Town, Zanzibar Urban/West, Tanzania" and does
        /// not fit a 232px panel.
        ///
        /// Internal rather than private so the parsing can be tested against captured responses
        /// without a network.
        /// </summary>
        internal static string? ParsePlaceName(string json)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;

                if (root.ValueKind != JsonValueKind.Object) return null;

                if (root.TryGetProperty("address", out var address) && address.ValueKind == JsonValueKind.Object)
                {
                    // Most specific useful locality first, then progressively coarser.
                    foreach (var field in new[] { "city", "town", "village", "municipality", "county", "state", "region" })
                    {
                        if (!address.TryGetProperty(field, out var value)) continue;
                        if (value.ValueKind != JsonValueKind.String) continue;

                        var locality = value.GetString();
                        if (string.IsNullOrWhiteSpace(locality)) continue;

                        // Pair it with the country when there is room for one more term.
                        if (address.TryGetProperty("country", out var countryValue) &&
                            countryValue.ValueKind == JsonValueKind.String)
                        {
                            var country = countryValue.GetString();
                            if (!string.IsNullOrWhiteSpace(country) &&
                                !string.Equals(country, locality, StringComparison.OrdinalIgnoreCase))
                            {
                                return $"{locality}, {country}";
                            }
                        }

                        return locality;
                    }

                    if (address.TryGetProperty("country", out var only) && only.ValueKind == JsonValueKind.String)
                    {
                        var country = only.GetString();
                        if (!string.IsNullOrWhiteSpace(country)) return country;
                    }
                }

                if (root.TryGetProperty("display_name", out var display) && display.ValueKind == JsonValueKind.String)
                {
                    var text = display.GetString();
                    if (string.IsNullOrWhiteSpace(text)) return null;

                    // Trim the long administrative tail to something a panel can show.
                    var parts = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    return parts.Length <= 2 ? text : $"{parts[0]}, {parts[^1]}";
                }

                return null;
            }
            catch (JsonException)
            {
                // An HTML error page where JSON was promised is a normal failure mode for a public
                // endpoint under load, not an exceptional one.
                return null;
            }
        }

        private async Task ThrottleAsync(CancellationToken cancellationToken)
        {
            await _rateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var since = DateTime.UtcNow - _lastRequestUtc;
                if (since < MinimumInterval)
                    await Task.Delay(MinimumInterval - since, cancellationToken).ConfigureAwait(false);

                _lastRequestUtc = DateTime.UtcNow;
            }
            finally
            {
                _rateGate.Release();
            }
        }

        public void Dispose()
        {
            _http.Dispose();
            _rateGate.Dispose();
        }
    }
}
