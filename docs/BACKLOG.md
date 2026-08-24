# FastCull backlog

Real but unbuilt work, with the reasoning that deferred it. Created 2026-08-24.

This is not a wish list. Everything here is either specified in `docs/PRD.md` and not yet
implemented, or a deliberate architectural deviation with a known upgrade path. Items that are
simply later delivery phases (PRD 6) are listed too, so nothing has to be rediscovered by reading
the code and finding it absent.

---

## 1. Stream the scan into the UI — first image on screen during active scanning

**PRD 1.2. Deferred deliberately, 2026-08-24.**

The scanner is not the problem. `DirectoryScanner.ScanAsync` is already a proper
`IAsyncEnumerable<ScannedPhoto>` over a `System.Threading.Channels` channel, fed by
`Parallel.ForEachAsync` across `coreCount - 2` workers, and it yields each file as it is parsed.
**`MainViewModel.LoadAsync` drains it into a `List<ScannedPhoto>` before building a single
view-model**, so the UI is not interactive during the scan despite the stream supporting it.

The progress pill (PRD 1.2) is built and driven by real scan progress. What remains is the first
image appearing mid-scan, and that is a **sequence-identity change**, not a UI change. Three
things need rework together:

1. **`FilmstripItemViewModel.Index` is immutable**, assigned at construction. It is also
   `ICacheableItem.Index`, which drives `PrefetchRange.Contains()` and the furthest-from-cursor
   eviction sort in PRD 3.3. Inserting a photo mid-sequence invalidates every later index.
2. **PRD 1.3 sorts by capture time; files arrive in filesystem / parallel-completion order.**
   Two options, both with costs:
   - *Append then sort at the end* — the filmstrip visibly reshuffles under the cursor mid-cull.
     Worse than waiting, and precisely the experience PRD 1.2 is trying to buy.
   - *Insert in sorted position* — keeps the sequence always correct, but needs a mutable `Index`
     and an O(n) reindex per insert (O(n²) over a 2,000-file folder).
3. **`SessionStore.RegisterPhotosAsync` takes the complete sorted list in one pass**, as does the
   rating restore. Streaming means either per-photo DB writes or ratings visibly popping in late
   on photos already on screen.

**Suggested approach if picked up:** make `Index` settable and owned by `MainViewModel`, insert in
sorted position, and batch `SessionStore` registration on a debounce rather than per photo. Measure
the reindex cost against PRD 3.5's 16 ms navigation budget before committing to it — a naive
implementation will blow that budget on a large folder.

---

## 2. True RAW debayering — an upgrade path, not a hole

**PRD 3.2 / 3.4 / 5. Not built, and currently not needed.**

RAW is decoded by **embedded-JPEG extraction** (`RawPreviewDecoder`), not by debayering. This is
the real, working, shipping RAW path and it is fast — roughly 30 ms against ~1000 ms for WIC's full
debayer of the same file. The originally planned `IRawDebayer` / `CpuDebayer` / `GpuDebayer` chain
was never built, and the `LibRawSharp` spike was rejected (PRD 5.2: it ships no native binary and
exposes no preview-extraction API).

**Why it is sufficient today:** the PRD 1.7 survey found 100% of surveyed RAW files (96 files, two
bodies, `.ARW` and `.CR2`) carry an embedded JPEG at full sensor width. Every tier the app uses is
therefore already present as ordinary JPEG.

**What would force this to be built:**

- A RAW file whose embedded preview falls below `FullResIsCheap`'s 90%-of-sensor-width threshold.
  That file would today have **no** full-resolution path at all and would sit on an upscaled
  preview indefinitely. `FullResIsCheap` already routes correctly — only the destination is
  missing.
- True 1:1 critical-focus inspection (item 3 below) revealing that embedded-preview **JPEG
  compression quality** does not survive pixel-peeping, even at correct dimensions. This is the
  open caveat in PRD 1.7 and is unmeasurable until panning at 1:1 exists.
- A camera body or RAW variant whose preview this scanner cannot locate.

**Suggested approach if picked up:** spike `Magick.NET-Q16-HDRI-x64` (PRD 3.4). Keep
`RawPreviewDecoder` as the fast path and fall through to a debayer only when `FullResIsCheap` is
false — never speculatively.

---

## 3. Zoom: true 1:1 inspection, panning, Tier A/B

**PRD 1.7 / 2.2. Partially built.**

What ships is a reduced-scope zoom: `Space` toggles fullscreen and fit-to-stage together, sized to
the photo's rendered box, re-requested on stage resize, with RAW and JPEG on the same
embedded-preview path. It answers "is this frame sharp enough and well composed". It does **not**
answer "is critical focus on the eye".

Missing:

- **Panning at 1:1**, with acceleration on hold, preserving pan offset across navigation.
- **The Tier A / Tier B distinction** — there is currently one path, the embedded preview, at
  whatever size the container holds. No `FullResIsCheap` routing at decode time, no "decoding"
  indicator, no swap-in-when-ready preserving pan.
- **`A` / `D` navigation while zoomed** — `A`/`S` remain rotation in both modes.
- **A genuine two-state input router.** `InputRouter` is currently mode-agnostic, which is correct
  while there is nothing for a second state to change, and will not be once panning exists.

---

## 4. Metadata HUD

**PRD 1.8. Not built.** `I` is unmapped. PRD 1.8 also specifies that metadata is read once during
the scan and cached in the session DB as JSON — that caching does not exist either; the scanner
currently reads only what PRD 1.3's sort needs plus EXIF orientation.

Note one dependency: PRD 3.3's dimension guard is specified to surface "a HUD notice that 1:1 is
unavailable". With no HUD, that is currently an on-photo badge instead. If the HUD is built, decide
whether the notice moves into it or stays on the photo.

---

## 5. Undo / redo

**PRD 1.9. Not built.** `Ctrl+Z` / `Ctrl+Y` are unmapped and `UndoStack.cs` does not exist. Needs a
command stack of at least 200 entries over flag changes, star changes, and Recycle Bin deletes.

Blocked-ish: it is specified to cover Recycle Bin deletes, and delete is not implemented either
(`Delete` is unmapped).

---

## 6. Finish Session and batch export

**PRD 4. Not built.** The sidebar carries a **disabled placeholder button** with a "coming soon"
tooltip, deliberately non-functional. Nothing behind it exists: no modal, no destination structure,
no copy/move, no collision handling, no verification, no cancel, no log.

---

## 7. VRAM texture cache

**PRD 3.3, v0.4 scope.** Not built, and explicitly out of scope for the PRD 3.3 work that landed
2026-08-23/24.

Consequence worth remembering: **every memory figure in the PRD and in `docs/benchmarks/` is system
RAM only.** A `SoftwareBitmapSource` additionally copies pixels into a XAML composition surface that
may live in GPU memory and is not this process's working set to sample.

---

## 8. Smaller real gaps

| Item | Where | Note |
| :--- | :--- | :--- |
| Key repeat coalescing | PRD 2.3 | Holding `Right` is not rate-limited; the PRD specifies ~15/sec with the decode pipeline targeting only the settled position |
| RAW + JPEG companion pairing | PRD 1.4 | `ScannedPhoto` has no companion grouping. Pairing mode is undecided (PRD 8, open question 2) and unimplemented; every file is currently its own item |
| Format chip on filmstrip items | PRD 1.5 | Dropped by the Chromeless direction, and the HUD that would replace it is unbuilt — so **format is not visible anywhere in the stage or strip UI**. The sidebar's format breakdown gives folder-level counts, not per-photo identity. Open point, not a decision |
| Thumbnail blob cache in the session DB | PRD 3.2 | Specified so thumbnails are generated once per session; not implemented. Thumbnails are decoded per run |
| Folder picker | — | The scan root is found by walking up from the executable for a `SampleImages` folder. There is no way to open an arbitrary folder from the UI |
| 25% regression build gate | PRD 3.5 | The harness exists and writes results, but nothing compares runs or fails a build. The `REFERENCE` rows are already excluded from the exit code so a gate could be wired without them tripping it |
| Cold-cache measurement | `docs/benchmarks/` | Every timing committed so far is warm-cache. A genuinely cold 2,000-file NVMe folder is unmeasured and will be slower |

---

## 9. Environment note, not a backlog item

The app currently **cannot be launched unpackaged on this machine**: it targets WindowsAppSDK
2.4.0 while only `Microsoft.WindowsAppRuntime.2` **2.3.1.0** is registered, so
`Microsoft.UI.Xaml.Application` fails to activate with `REGDB_E_CLASSNOTREG`. UI verification during
development has been done via a self-contained publish
(`--self-contained true -p:WindowsAppSDKSelfContained=true -p:WindowsPackageType=None`), which
bundles the runtime. Worth resolving properly if the launch path matters.
