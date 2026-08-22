# Product Requirement Document: FastCull

**Version:** 1.3
**Target Platform:** Windows 11 (x64, native)
**Tech Stack:** C# / .NET 9, WinUI 3 (Windows App SDK), WIC + LibRaw dual decoder, SQLite (file-backed, WAL)
**Dev Baseline:** Intel Core i5-12400, 32 GB RAM, 8 GB VRAM, NVMe SSD
**Primary Test Corpus:** Sony A7C II `.ARW` (7008 x 4672, 33 MP), mixed with JPEG and PNG exports

---

## 0. Prime Directive

**The app must never make the user wait, and must never appear to be thinking.**

Every design decision below resolves in favour of perceived speed. When speed and correctness conflict, show something immediately and refine it in place. When speed and features conflict, cut the feature.

Three rules that govern every subsystem:

1. **The UI thread does no work.** No disk I/O, no decoding, no database writes, no metadata parsing. Ever.
2. **Progressive refinement, never a blank frame.** Every image goes through a tier chain: thumbnail (instant) to display-resolution image (fast) to full-resolution pixels (slow, on demand only). The user always sees the best tier available right now, and better tiers swap in silently.
3. **Predict, do not react.** By the time the user presses Right, the next image is already decoded and resident.

### 0.1 Non-Goals (v1.0)
- No image editing, develop settings, or format conversion.
- No catalog or persistent library beyond crash recovery of the current session.
- No XMP sidecar writing (deferred, see 4.4).
- No video files. Not `.MP4`, not `.MOV`, not animated `.GIF`. A culling tool that has to decode video frames is a different application.
- No cloud, telemetry, or auto-update.
- No macOS or Linux.

---

## 1. Functional Specification

### 1.1 Supported Formats

FastCull handles all common still image formats, not RAW alone. Decoder assignment is by family:

| Family | Extensions | Primary decoder | Has embedded preview |
| :--- | :--- | :--- | :--- |
| **RAW** | `.ARW` `.CR3` `.CR2` `.NEF` `.RAF` `.ORF` `.RW2` `.DNG` `.PEF` `.SRW` | LibRaw | Yes, size varies |
| **JPEG** | `.JPG` `.JPEG` `.JFIF` | WIC | Is its own preview |
| **HEIF** | `.HEIC` `.HEIF` `.AVIF` | WIC | Usually yes |
| **PNG** | `.PNG` | WIC | No |
| **TIFF** | `.TIF` `.TIFF` | WIC | Sometimes |
| **Modern web** | `.WEBP` | WIC | No |
| **Legacy** | `.BMP` `.GIF` (first frame only) | WIC | No |

**WIC is the primary decoder for everything except RAW.** LibRaw is primary for RAW, with WIC as its fallback. The v1.2 statement that "WIC is fallback only" is now scoped to the RAW path specifically. The reason for keeping LibRaw primary on RAW is unchanged: WIC RAW support depends on the user-installed *Raw Image Extension* from the Microsoft Store, so a clean machine cannot open `.ARW` without it.

Unknown or unsupported extensions are ignored silently during the scan. Files whose extension is supported but whose content fails to decode are shown in the filmstrip with an error placeholder and can still be flagged or deleted, never silently dropped.

### 1.2 Ingestion
- Recursive discovery of all supported extensions under a chosen root.
- **The scan parses metadata headers only.** No pixel data is touched during the scan. Parsing is parallelised across `coreCount - 2` workers.
- **The filmstrip becomes interactive before the scan finishes.** Results stream into the sequence as they are found; the first image is on screen while the tail of the folder is still being enumerated. A progress pill in the sidebar shows `N files found` until the scan completes, then the final sort is applied.

### 1.3 Sort Order (CHANGED, consequence of mixed formats)

RAW files reliably carry `DateTimeOriginal`. JPEGs usually do. PNG, WebP, BMP and most exported files carry no capture date at all. The v1.2 rule of "no date, sort to the end" would dump an entire folder of PNGs in a heap after everything else, which is useless.

Sort key hierarchy, first available wins:

1. EXIF `DateTimeOriginal` plus `SubSecTimeOriginal`
2. EXIF `CreateDate` / `DateTimeDigitized`
3. Filesystem **last-write time** (more reliable than creation time, which Windows resets on copy)
4. Full path, as final tiebreaker

The HUD and filmstrip indicate which tier the displayed timestamp came from, so a folder sorted by file mtime is never mistaken for one sorted by capture time.

### 1.4 RAW + JPEG Pairing (NEW, requires a decision)

Once JPEG is a first-class format, `DSC_0001.ARW` and `DSC_0001.JPG` in the same folder are ambiguous: one item or two?

Three modes, exposed as a settings toggle:

| Mode | Behaviour |
| :--- | :--- |
| **Paired (default)** | RAW is the item. Same-basename JPEG is a companion: not shown separately, travels with the RAW on move, copy or delete. |
| **Separate** | Every file is its own item, rated independently. |
| **JPEG preferred** | Same-basename JPEG is the item and is used as the display source (fast). The RAW is the companion and still moves with it. |

Paired is the default because it matches how a camera shooting RAW+JPEG behaves, and because doubling the item count doubles the culling work for no benefit. Mode is chosen per session at folder open and can be switched without a rescan, since grouping is a view over the same scanned set.

**Companion grouping rule:** files sharing a basename in the same directory group together. `.XMP` sidecars are always companions, never items, in every mode.

### 1.5 Layout
- **Sidebar (left):** auto-hides on pointer exit, pinnable. Contains folder tree, file counts, live rating tallies, format breakdown, and **Finish Session**.
- **Filmstrip:** virtualized horizontal scroller sized for 21:9 and 32:9. Active image scales to viewport height, neighbours flank left and right at reduced height.
- **Border states:** yellow = unrated, green = picked, red = rejected. Star count renders as a separate badge overlay, independent of border colour.
- A small format chip (`ARW`, `JPG`, `PNG`) sits on each filmstrip item. In a mixed folder you need to know what you are looking at without opening the HUD.

### 1.6 Rating Model
Two independent axes.

| Axis | Values | Storage |
| :--- | :--- | :--- |
| **Flag** | `Unflagged`, `Picked`, `Rejected` | SQLite |
| **Stars** | `0` to `5` | SQLite |

Rules:
- Setting stars >= 1 on an `Unflagged` item implicitly sets `Picked`.
- Setting `Rejected` does not clear stars. Rejection is recoverable and non-destructive.
- Both the top number row and the **numeric keypad** (`NumPad1` to `NumPad5`) set stars. NumLock state must be handled: with NumLock off the numpad emits navigation keycodes, so `NumPad2` arrives as `Down`, which collides with the Reject binding. Explicit v0.1 test case, it fails silently.
- A rating keypress updates the border within one frame. The database write is fire-and-forget on a background channel and never gates the visual.

### 1.7 Zoom Inspection (GENERALISED)

`Spacebar` toggles 1:1 inspection. The source depends on what full-resolution pixels cost for this file:

**Tier A, full-resolution pixels are cheap.** Covers JPEG, HEIF, small PNG and WebP (the file *is* the image), and RAW whose embedded preview is at least 90% of sensor width. Display directly, target under 50 ms cached.

**Tier B, full-resolution pixels require a decode.** Covers RAW with an undersized or absent embedded preview, large TIFF, and any PNG or WebP above the display-tier threshold.
- The best available lower tier is shown immediately, upscaled, with a subtle "decoding" indicator.
- The full decode (LibRaw debayer, or WIC full-size decode for non-RAW) runs on a background worker.
- The decoded frame swaps in when ready, preserving the current pan offset exactly. No flicker, no reset, no scroll jump.
- Navigating away cancels the job via `CancellationToken`. Orphaned decodes are the fastest way to turn this app into a space heater.
- Decoded frames enter the same LRU cache as previews and are reused on return.

Note that Tier B is no longer RAW-specific. A 16-bit 100 MP TIFF is a heavier decode than an ARW and needs the same treatment.

Zoom level and pan offset persist across `A` / `D` navigation.

**Verification task, blocking, five minutes:** run `exiftool -PreviewImageSize -JpgFromRawLength -a *.ARW` against a real A7C II folder. If the preview is near 7008 px wide, RAW zoom is Tier A and the app is fast everywhere. If it is around 1616 x 1080, RAW zoom is Tier B for every image and the debayer performance in section 3.4 becomes the most important number in this document.

### 1.8 Metadata HUD
- Toggled by `I`. Renders as a transparent overlay: `Filename`, `Format`, `Model`, `Lens`, `ISO`, `Shutter`, `Aperture`, `Focal Length`, `Timestamp` (with its source tier from 1.3), `Dimensions`, `File Size`, and the current display tier so you always know whether you are looking at real pixels.
- Fields absent from the file's metadata are omitted rather than shown blank. A PNG has no aperture; the HUD should not pretend otherwise.
- Metadata is read once during the scan and cached in the session database as JSON. The HUD never touches disk.

### 1.9 Undo
- `Ctrl+Z` / `Ctrl+Y` over a command stack of at least 200 entries.
- Covers flag changes, star changes, and Recycle Bin deletes (restored via the shell API).
- Does not cover the Finish Session batch operation, which has its own confirmation and log.

---

## 2. Control Matrix

### 2.1 Filmstrip Mode
| Key | Behaviour |
| :--- | :--- |
| `Left` / `Right` | Previous / next image |
| `Home` / `End` | First / last image |
| `1`-`5` or `NumPad1`-`NumPad5` | Set star rating directly |
| `0` / `NumPad0` | Clear stars |
| `P` or `Up` | Set `Picked` |
| `X` or `Down` | Set `Rejected` |
| `U` | Set `Unflagged` |
| `Space` | Enter zoom mode |
| `I` | Toggle metadata HUD |
| `Delete` | Move file group to Recycle Bin (undoable) |
| `Ctrl+Z` / `Ctrl+Y` | Undo / redo |

`Up` and `Down` are retained as aliases for `Picked` and `Rejected`. Bare `Ctrl` is **not** used as a toggle: it is a modifier, it fires as part of every accelerator, and its key-repeat behaviour is inconsistent.

### 2.2 Zoom Mode
| Key | Behaviour |
| :--- | :--- |
| `Arrow keys` | Pan, with acceleration on hold |
| `A` / `D`, `PgUp` / `PgDn` | Previous / next, preserving zoom and pan |
| `1`-`5`, `NumPad1`-`NumPad5`, `P`, `X`, `U` | Rating keys stay live |
| `Space` or `Esc` | Exit to filmstrip |
| `I` | Toggle HUD |

Input routing is an explicit two-state machine, handled in one place at window level. Unmapped keys log at debug level rather than being silently swallowed.

### 2.3 Key Repeat
Holding `Right` must not queue 40 navigation events that replay after release. Navigation input is coalesced: the cursor moves at a fixed maximum rate (suggest 15 per second) and the decode pipeline targets only the settled position, not every intermediate index.

---

## 3. Technical Specification

### 3.1 Persistence
File-backed SQLite at `%LOCALAPPDATA%\FastCull\sessions\{session-guid}.db`, `journal_mode=WAL`, `synchronous=NORMAL`.

- All writes go through a single background writer consuming a bounded channel. The UI never awaits a write.
- Writes are batched on a 250 ms timer or 32 pending operations, whichever comes first.
- On launch, if a session DB exists whose root folder still resolves, offer to resume.
- The session DB is deleted after a successful Finish Session.

In-memory SQLite was rejected: it does nothing for UI threading (that is what the background writer is for) and it loses a 45-minute session on any crash.

```sql
CREATE TABLE photos (
  id             INTEGER PRIMARY KEY,
  path           TEXT NOT NULL UNIQUE,
  rel_path       TEXT NOT NULL,          -- relative to scan root, for collision-safe export
  basename       TEXT NOT NULL,
  extension      TEXT NOT NULL,
  format_family  INTEGER NOT NULL,       -- Raw, Jpeg, Heif, Png, Tiff, Other
  sort_time      TEXT NOT NULL,          -- resolved per section 1.3
  sort_time_tier INTEGER NOT NULL,       -- 1 = DateTimeOriginal ... 3 = file mtime
  capture_subsec INTEGER,
  file_bytes     INTEGER NOT NULL,
  flag           INTEGER NOT NULL DEFAULT 0,   -- 0 unflagged, 1 picked, 2 rejected
  stars          INTEGER NOT NULL DEFAULT 0,
  deleted        INTEGER NOT NULL DEFAULT 0,
  image_w        INTEGER,
  image_h        INTEGER,
  preview_w      INTEGER,                -- null when no embedded preview exists
  preview_h      INTEGER,
  thumb_blob     BLOB,                   -- generated for formats lacking an embedded thumb
  meta_json      TEXT
);
CREATE TABLE companions (photo_id INTEGER, path TEXT, kind TEXT);
CREATE INDEX idx_sort ON photos(sort_time, capture_subsec, path);
```

### 3.2 Decode Pipeline

Chain per image:
1. **Thumbnail (~160 px).** Extracted from embedded data where it exists (RAW, most JPEG, most HEIF). **Generated by downscaled decode where it does not** (PNG, WebP, BMP, most TIFF), then cached as a blob in the session DB so it is generated once per session and never again.
2. **Display tier.** Embedded preview for RAW; the file itself for JPEG and HEIF; a downscaled decode for large PNG and TIFF. This is the normal filmstrip display source.
3. **Full resolution.** Only for zoom Tier B, never speculative.

`IImageDecoder` resolves to `LibRawDecoder` or `WicDecoder` by format family at scan time. Callers never branch on file extension.

### 3.3 Prefetch and Cache
- Sliding window: active index plus 5 ahead, 2 behind, on a bounded worker pool of `min(6, coreCount - 2)`.
- Direction-aware: the window reverses after three consecutive backwards moves.
- LRU cache with a hard ceiling of 3 GB system memory, evicting furthest-from-cursor first.
- **VRAM texture cache:** decoded surfaces are uploaded to GPU textures and kept resident. At roughly 130 MB per 33 MP RGBA frame, 8 GB VRAM holds 40 to 50 images, comfortably covering the prefetch window plus zoom history. Eviction is by distance from cursor, with a VRAM budget defaulting to 60% of available.
- **Dimension guard:** any image whose decoded size would exceed 512 MB in memory (roughly 128 MP at RGBA) is capped at a downscaled decode, with a HUD notice that 1:1 is unavailable. Without this, one stitched panorama TIFF takes the app down.
- Cache is cleared on folder change, not on session end.

### 3.4 GPU Acceleration
Three stages, three different answers.

| Stage | GPU benefit | Decision |
| :--- | :--- | :--- |
| Render / scale / pan | Already GPU accelerated by the WinUI 3 compositor | Free, nothing to do |
| JPEG / PNG decode | Marginal; GPU decode exists but plumbing cost is high | CPU, multithreaded |
| RAW debayer | Substantial. Roughly 250-400 ms CPU vs under 50 ms GPU at 33 MP | **CPU now, GPU later** |

`IRawDebayer` is an interface with a single method. `CpuDebayer` (LibRaw) ships in v0.2. `GpuDebayer` (compute shader via Win2D or a D3D11 compute pass) is a drop-in replacement in v0.5 if measurement shows RAW zoom lands in Tier B. Building GPU debayer up front would stall the project for weeks in exchange for milliseconds on a path that may be rare.

### 3.5 Performance Budgets
These are the acceptance criteria. Each becomes an automated benchmark; a regression greater than 25% fails the build.

| Metric | Budget |
| :--- | :--- |
| First image on screen from folder selection | < 1.5 s |
| Full scan, 2,000 files, NVMe | < 8 s (interactive throughout) |
| Next / previous, cache hit | < 16 ms, zero dropped frames |
| Next / previous, cache miss | < 250 ms to display tier |
| Enter zoom, Tier A cached | < 50 ms |
| Enter zoom, Tier B | < 400 ms CPU, < 100 ms GPU path |
| Rating keypress to border change | < 16 ms |
| Pan at 1:1 | Sustained 60 fps minimum |
| Peak working set, 2,000 files | < 4 GB system RAM |

Benchmarks run against three fixture sets: all-RAW, all-JPEG, and mixed. The mixed set is the one that finds bugs.

---

## 4. Output and Batch Processing

### 4.1 Finish Session Modal
Triggered from the sidebar. Displays:
- **X approved** (`Picked` plus anything with stars)
- **Y rejected** (`Rejected`)
- **Z unrated** (untouched, remain in source folders)
- Star histogram, 1 through 5
- Total bytes to move or copy, and free space on the target volume

User selects **Copy** or **Move** and a target directory.

### 4.2 Destination Structure
```
/Picked      (flag = Picked, stars = 0)
/Rejected
/1_Star
/2_Star
/3_Star
/4_Star
/5_Star
```

An image with stars goes into its star folder only, not also into `/Picked`. Unrated files are never touched.

### 4.3 File Operation Rules
- **Companion files always travel with their item.** The group moves or nothing moves. In Paired mode this means a RAW+JPEG pair lands together in one bucket.
- **Collision policy:** two cards can both contain `DSC_0001.ARW`. The relative path from the scan root is preserved inside the destination bucket, so `CardA/DSC_0001.ARW` becomes `/Picked/CardA/DSC_0001.ARW`. Nothing is ever silently overwritten.
- **Same-basename, different-extension collisions** (`shot.jpg` and `shot.png` from different folders) are covered by the same relative-path rule.
- **Cross-volume moves** are copy, verify, then delete. Verification is size plus modified-time by default, with an optional hash mode in settings.
- The batch runs on a background queue with a real progress bar, a working **Cancel** button, and a plain-text log at `%LOCALAPPDATA%\FastCull\logs\`.
- **Partial failure leaves sources intact** and reports affected files. The app never ends up in a state where photos exist in neither place.
- Free disk space is checked before the operation starts, not discovered halfway through.

### 4.4 XMP Sidecars (deferred, not cancelled)
Writing `xmp:Rating` and `xmp:Label` sidecars in place would let Lightroom Classic read ratings without moving a single byte. Out of scope for v1.0 by decision. `XmpWriter` exists as a stubbed interface so adding it later is an afternoon rather than a refactor.

---

## 5. Architecture

```text
FastCull/
├── Models/
│   ├── PhotoItem.cs           # path, rel_path, format, companions, sort time, flag, stars, metadata
│   ├── FormatFamily.cs        # Raw, Jpeg, Heif, Png, Tiff, Other
├── Services/
│   ├── FormatRegistry.cs      # extension -> family -> decoder mapping, single source of truth
│   ├── DirectoryScanner.cs    # streaming discovery, parallel metadata parse, companion grouping
│   ├── IImageDecoder.cs       # thumbnail / display / full contract
│   │   ├── LibRawDecoder.cs   # RAW primary
│   │   └── WicDecoder.cs      # everything else, plus RAW fallback
│   ├── IRawDebayer.cs         # CpuDebayer now, GpuDebayer later
│   ├── ThumbnailService.cs    # extract or generate, cache as blob in session DB
│   ├── PreviewCache.cs        # prefetch window, LRU, RAM + VRAM ceilings, dimension guard, cancellation
│   ├── SessionStore.cs        # SQLite WAL, batched background writer, resume
│   ├── UndoStack.cs           # command pattern over rating and delete ops
│   ├── BatchProcessor.cs      # copy/move, collisions, verify, cancel, log
│   ├── XmpWriter.cs           # stub for v1.1
├── ViewModels/
│   ├── MainViewModel.cs       # sequence, cursor, input state machine, key coalescing
│   ├── FinishSessionViewModel.cs
├── Views/
│   ├── MainWindow.xaml
│   ├── FilmstripView.xaml     # ItemsRepeater, horizontal virtualization
│   ├── ZoomView.xaml
│   ├── MetadataHud.xaml
├── Benchmarks/
│   ├── PerfHarness.cs         # asserts section 3.5 budgets against RAW / JPEG / mixed fixtures
```

### 5.1 Model
```csharp
public enum Flag { Unflagged = 0, Picked = 1, Rejected = 2 }

public enum FormatFamily { Raw, Jpeg, Heif, Png, Tiff, Other }

public enum TimeSource { CaptureDate = 1, DigitizedDate = 2, FileModified = 3 }

public class PhotoItem
{
    public required string FilePath { get; set; }
    public required string RelativePath { get; set; }   // from scan root, for collision-safe export
    public required string FileName { get; set; }
    public required FormatFamily Family { get; set; }
    public long FileBytes { get; set; }

    public DateTime SortTime { get; set; }              // resolved per PRD 1.3, never null
    public TimeSource SortTimeSource { get; set; }
    public int? CaptureSubsec { get; set; }

    public Flag Flag { get; set; } = Flag.Unflagged;
    public int Stars { get; set; }                      // 0-5

    public List<CompanionFile> Companions { get; } = new();

    // Metadata cache. All nullable: a PNG has no aperture.
    public string? CameraModel { get; set; }
    public string? Lens { get; set; }
    public int? Iso { get; set; }
    public string? ShutterSpeed { get; set; }
    public string? Aperture { get; set; }
    public string? FocalLength { get; set; }

    public int? ImageWidth { get; set; }
    public int? ImageHeight { get; set; }
    public int? PreviewWidth { get; set; }              // null when no embedded preview exists
    public int? PreviewHeight { get; set; }

    /// True when full-resolution pixels are available without a heavy decode (PRD 1.7 Tier A).
    public bool FullResIsCheap => Family switch
    {
        FormatFamily.Jpeg or FormatFamily.Heif => true,
        FormatFamily.Raw => PreviewWidth is int p && ImageWidth is int w && p >= w * 0.9,
        _ => ImageWidth is int iw && ImageHeight is int ih && (long)iw * ih <= 24_000_000
    };
}

public record CompanionFile(string Path, CompanionKind Kind);
public enum CompanionKind { Raw, Jpeg, Heif, Xmp, Other }
```

### 5.2 Dependencies
- `Microsoft.WindowsAppSDK`
- `Microsoft.Data.Sqlite`
- `LibRawSharp` or `Magick.NET-Q16-HDRI-x64` (RAW only, spike before building on it)
- `MetadataExtractor` (fast header parsing across RAW, JPEG, PNG, TIFF, HEIF without invoking a decoder)
- `CommunityToolkit.Mvvm`
- `System.Threading.Channels` (in-box)

Licensing note: LibRaw is dual licensed LGPL 2.1 / CDDL 1.0 with a separate commercial option. Fine for personal use, needs revisiting if this is ever sold.

---

## 6. Delivery Phases

**v0.1 Walking skeleton.** Scan a folder, detect formats, group companions, resolve sort times, stream results into a filmstrip, display thumbnails and display-tier images, rate with `1`-`5` / `P` / `X`, persist to SQLite. No zoom, no HUD, no batch export. Ships with the perf harness and the first three budgets from 3.5 enforced.
*This phase proves the decode and prefetch pipeline, which is where every real risk lives. If the filmstrip is not snappy here, nothing later matters.*
*Scope note: implement JPEG and PNG first via WIC, then add the RAW path. WIC needs no external dependency, so slice 1 has no package risk.*

**v0.2 Inspection.** 1:1 zoom with Tier A and Tier B paths, CPU debayer, dimension guard, metadata HUD, undo, Recycle Bin delete.

**v0.3 Output.** Finish Session modal, batch copy/move with collision handling, verification, cancel and logging.

**v0.4 Polish.** Session resume, pairing mode toggle, ultrawide layout tuning, key repeat coalescing, VRAM texture cache, full perf harness green.

**v0.5+ Optional.** GPU debayer if measurement shows RAW zoom is Tier B. XMP sidecar mode. Multi-body time offsets.

---

## 7. Risks

| Risk | Impact | Mitigation |
| :--- | :--- | :--- |
| A7C II embedded preview is undersized | Every RAW zoom takes the slow Tier B path | Verify with exiftool before coding; GPU debayer pre-planned in 3.4 |
| RAW decoder NuGet package unmaintained or missing RAW delegate | Blocked on the core format | Ten-minute spike before v0.1 slice 3; JPEG/PNG path via WIC is unaffected |
| WIC RAW codec absent on target machine | Cannot open RAW at all | LibRaw primary for RAW |
| PNG and TIFF have no embedded thumbnail | Scan of a large PNG folder becomes a full decode storm | Generate once, cache as blob, downscaled decode only |
| Huge TIFF or stitched panorama | Out-of-memory crash | Dimension guard in 3.3, hard 512 MB decoded ceiling |
| `ItemsRepeater` horizontal virtualization with variable-width items | Filmstrip stutter, the one thing the app cannot afford | Fixed height, aspect-derived width, measured in v0.1 with 2,000 fixtures |
| NumLock ambiguity on numpad rating keys | Ratings silently do nothing | Explicit test case in v0.1 |
| Unbounded decode jobs on fast scrubbing | Thread pool starvation | Cancellation tokens, coalesced navigation, bounded worker pool |
| Scope creep past v0.3 | Never ships | Phase gates above |

---

## 8. Open Questions

1. The five-minute exiftool check in section 1.7. Blocking.
2. Confirm **Paired** as the default RAW+JPEG mode (section 1.4). Defaulted, not yet confirmed.