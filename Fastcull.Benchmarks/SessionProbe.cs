using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Fastcull.Models;
using Fastcull.Services;

namespace Fastcull.Benchmarks
{
    /// <summary>
    /// Reads and seeds a folder's session state through the app's own <see cref="SessionStore"/>.
    ///
    /// Exists because PRD 4.1's resume cannot otherwise be verified without driving the UI, and
    /// driving the UI needs the foreground - which on a busy desktop is not reliably obtainable.
    /// Going through SessionStore rather than raw SQL is also the better test: it exercises the
    /// same open, the same schema migration and the same write queue the app uses, so a change
    /// that broke persistence would break this too.
    /// </summary>
    internal static class SessionProbe
    {
        public static async Task<int> RunAsync(string corpus, bool seed)
        {
            var store = await SessionStore.OpenAsync(corpus).ConfigureAwait(false);

            var states = await store.LoadPhotoStatesAsync().ConfigureAwait(false);
            Console.WriteLine($"Session : {store.DisplayName}");
            Console.WriteLine($"Rows with state: {states.Count}");

            if (seed)
            {
                // Registered first, and this is not incidental: a rating is written as
                // UPDATE photos ... WHERE path = $path, which silently matches nothing when the
                // photo has no row. Seeding a fresh database without this step reports success and
                // stores exactly zero ratings - which is the same trap MainViewModel avoids by
                // registering each streamed batch before its photos reach the screen.
                var scanner = new DirectoryScanner();
                var scanned = new System.Collections.Generic.List<ScannedPhoto>();
                await foreach (var photo in scanner.ScanAsync(corpus).ConfigureAwait(false))
                    scanned.Add(photo);

                await store.RegisterPhotosAsync(scanned).ConfigureAwait(false);
                Console.WriteLine($"Registered {scanned.Count} photos");

                var paths = scanned
                    .Select(p => p.FilePath)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (paths.Count < 12)
                {
                    Console.Error.WriteLine($"Need at least 12 photos to seed; found {paths.Count}");
                    return 2;
                }

                for (var i = 0; i < 3; i++) store.QueueRating(paths[i], new CullState(Flag.Picked, 0));
                for (var i = 3; i < 5; i++) store.QueueRating(paths[i], CullState.Default.AsRejected());
                for (var i = 5; i < 9; i++) store.QueueRating(paths[i], new CullState(Flag.Picked, 3));
                for (var i = 9; i < 11; i++) store.QueueRotation(paths[i], Rotation.None.RotateRight());

                Console.WriteLine("Seeded: 3 picked, 2 rejected, 4 at three stars, 2 rotated");

                // The writer is a background queue; closing is what flushes and waits for it.
                await store.DisposeAsync().ConfigureAwait(false);

                var reopened = await SessionStore.OpenAsync(corpus).ConfigureAwait(false);
                var after = await reopened.LoadPhotoStatesAsync().ConfigureAwait(false);
                Report(after);
                await reopened.DisposeAsync().ConfigureAwait(false);
                return 0;
            }

            Report(states);
            await store.DisposeAsync().ConfigureAwait(false);
            return 0;
        }

        private static void Report(System.Collections.Generic.IReadOnlyDictionary<string, StoredPhotoState> states)
        {
            var picked = states.Values.Count(s => s.Cull.Flag == Flag.Picked && s.Cull.Stars == 0);
            var rejected = states.Values.Count(s => s.Cull.Flag == Flag.Rejected);
            var starred = states.Values.Count(s => s.Cull.Stars > 0);
            var rotated = states.Values.Count(s => s.Rotation.QuarterTurns != 0);

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"In the database: picked={picked} rejected={rejected} starred={starred} rotated={rotated}"));
        }
    }
}
