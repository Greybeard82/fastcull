namespace Fastcull.ViewModels
{
    /// <summary>
    /// What the prefetch/eviction machinery needs from a photo, without knowing it is a WinUI
    /// view-model.
    ///
    /// This seam exists for a concrete reason: <c>FilmstripItemViewModel</c> lives in the WinUI
    /// project, which cannot be referenced from a test project (its MSIX targets refuse to build
    /// ProcessorArchitecture-neutral). Everything interesting about caching - which photos load,
    /// which get cancelled, which get evicted first - is therefore expressed against this
    /// interface in Fastcull.Core, where it can actually be tested against fakes.
    /// </summary>
    public interface ICacheableItem
    {
        /// <summary>Position in the sorted sequence. Distance from the cursor drives eviction order.</summary>
        int Index { get; }

        /// <summary>True once this item holds decoded pixels that eviction could reclaim.</summary>
        bool IsResident { get; }

        /// <summary>
        /// Roughly how much memory this item is holding. An estimate is fine - eviction only needs
        /// to know when it has freed enough, not to balance a ledger.
        /// </summary>
        long ResidentBytes { get; }

        /// <summary>
        /// The part of <see cref="ResidentBytes"/> that <see cref="Evict"/> would actually give
        /// back. Zero means evicting this item is a no-op.
        ///
        /// This is not the same as ResidentBytes and the difference is not academic: Evict keeps
        /// the bottom filmstrip's thumbnail alive on purpose, so an item the cursor has already
        /// passed typically holds a thumbnail and nothing else. Without this, the eviction sweep
        /// credits itself the thumbnail's bytes for freeing nothing, and since those items sort
        /// furthest-from-cursor they absorb the entire pass - measured at 133 useless evictions
        /// per navigation step, while the display images that should have been dropped survived.
        /// </summary>
        long EvictableBytes { get; }

        /// <summary>
        /// True while something on screen is bound to this item's images. Pinned items are never
        /// eviction candidates: dropping one would blank a photo the user is looking at, which is
        /// a visible bug rather than a memory optimisation.
        /// </summary>
        bool IsPinned { get; }

        /// <summary>Starts decoding if not already loaded or loading. Must not block.</summary>
        void BeginLoad();

        /// <summary>Cancels an in-flight load. Safe to call when nothing is running.</summary>
        void CancelLoad();

        /// <summary>
        /// Drops this item's decoded images so the GC can reclaim them.
        ///
        /// Implementations must NOT call Dispose on a bitmap that has been handed to
        /// SoftwareBitmapSource.SetBitmapAsync - XAML copies those pixels into a composition
        /// surface on its own worker thread, and destroying one mid-copy fail-fasts the process
        /// with 0xC000027B. This project has already paid for that bug once. Releasing the last
        /// managed reference is the safe equivalent.
        /// </summary>
        void Evict();
    }
}
