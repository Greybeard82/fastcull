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

#### 1.1.1 Choosing a folder

The scan root is **chosen by the user through the native Windows folder picker**. There is no default folder, no configured root, and no path baked into the application.

**Launch: reopen the last folder and resume it.** On startup the app reads the last folder it was pointed at, and if that path still resolves it loads and resumes that session immediately — no picker, no prompt, no click.

This is a deliberate stance about what a folder *is* in this app. A folder here is **an unfinished job**: a card the photographer is working through to completion over one or more sittings, with §3.1's ratings and rotations accumulating against it. Reopening it is the overwhelmingly likely intent, and asking every time would put a modal in front of the one action that is almost always right. The nearest comparison is an editor reopening the project you were last in, not a viewer showing an empty canvas.

**Switching folders mid-session.** A control in the sidebar (§1.5) opens the same picker at any time. Choosing a folder runs exactly the same path as launch — full scan, then resume whatever state §3.1 already holds for it. There is no separate "open" code path to drift out of step with the startup one.

Switching is **non-destructive to the folder being left**. Its ratings are already durable (§3.1 writes them as they happen), so nothing needs saving and nothing is lost; returning to it later resumes exactly where it was left.

**First run, and a folder that has gone away.** Both land in the same place, deliberately:

- **First run**, with no last folder recorded, shows an **empty state**: the app's name, a line explaining that it needs a folder of photos, and a button that opens the picker. Not a modal — a modal on first launch is a wall, and it cannot be dismissed into anything useful.
- **A remembered folder that no longer resolves** — deleted, renamed, or on a card that is not plugged in — shows the same empty state rather than an error. It says which folder it could not open, then offers the same button. A missing card is an ordinary event for this app's users and is not an error condition.
- **Neither case may crash or fail silently.** An app that opens to a blank window with no explanation is indistinguishable from one that is broken.

### 1.2 Ingestion
- Recursive discovery of all supported extensions under a chosen root.
- **The scan parses metadata headers only.** No pixel data is touched during the scan. Parsing is parallelised across `coreCount - 2` workers.
- **The filmstrip becomes interactive before the scan finishes.** Results stream into the sequence as they are found; the first image is on screen while the tail of the folder is still being enumerated. A progress pill in the sidebar shows `N files found` until the scan completes, then the final sort is applied.

**Build status, 2026-08-24 — partially built, and the gap is not where it looks.**

- **The scanner already streams.** `DirectoryScanner.ScanAsync` is an `IAsyncEnumerable<ScannedPhoto>` over a `System.Threading.Channels` channel, fed by `Parallel.ForEachAsync` across `coreCount - 2` workers. It yields each file as it is parsed. Nothing about the scanner needs changing.
- **The consumer does not.** `MainViewModel.LoadAsync` drains that stream into a `List<ScannedPhoto>` before it builds a single view-model, so **the UI is not yet interactive during the scan** despite the underlying stream supporting it. The divergence from this section is one method, not the pipeline.
- **The progress pill is built and is driven by real progress** — the count comes from the scanner as it yields, not from a timer or an estimate. It is gated so the sidebar auto-reveals only once a scan has run past **400 ms**; below that the reveal is a flash at startup rather than information. Measured: a 2,000-file folder crosses the threshold and shows the pill (observed at "1,957 files found"); a 100-file folder does not.

**Deferred, deliberately: first image on screen during active scanning.** This is a sequence-identity change rather than a UI change, and three things would need rework together:

1. **`FilmstripItemViewModel.Index` is immutable**, assigned at construction. It is also `ICacheableItem.Index`, which drives `PrefetchRange.Contains()` and the furthest-from-cursor eviction sort in §3.3. Inserting a photo mid-sequence invalidates every later index.
2. **§1.3 sorts by capture time; files arrive in filesystem order.** So either append-then-sort — the filmstrip visibly reshuffles under the cursor mid-cull, which is worse than waiting — or sorted insert, which needs a mutable `Index` and an O(n) reindex per insert.
3. **`SessionStore.RegisterPhotosAsync` takes the complete sorted list in one pass**, as does the rating restore. Streaming means either per-photo writes or ratings popping in visibly late.

Tracked in `docs/BACKLOG.md`. Rushing it alongside UI work would produce exactly the mid-cull reshuffle this section exists to avoid.

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

  **As built, 2026-08-24.** 232px wide, pure `#FF000000` per §1.10, divided from the stage by a single hairline rule rather than a lighter fill.

  - **Reveal and hide.** A 12px hot zone along the window's left edge reveals it on pointer entry; it hides on pointer exit. The pin toggle (top right) holds it open regardless. **Pin state is session-only** and deliberately not persisted — §3.1's database stores per-photo state, and a UI preference would mean a schema migration for a toggle.
  - **Overlay when unpinned, reflow when pinned.** Unpinned it floats over the stage, so a photo being compared never resizes because the pointer drifted left. Pinned, the stage gives up a gutter of exactly the panel's width and reflows into what is left — which costs nothing extra, since the stage already recomputes its slot count on any width change (§1.5 variable slot count).
  - **Live session tallies:** `Total`, `Picked`, `Rejected`, `Remaining`, plus an `X of Y decided` line. Picked and Rejected carry the flag colours from §1.5's state mark; Remaining is neutral. These update **in the same frame as the weight bar under the photo** — a tally that lagged the mark it describes would read as a bug.
  - **Star histogram:** one row per level 1–5, count plus a bar. Bars scale to the **largest single count, not the total** — a folder with three 5-star photos out of two thousand would otherwise draw five bars all indistinguishable from zero.
  - **Format breakdown: grouped by file extension, not by `FormatFamily`.** The family is too coarse to be useful here — `.ARW` and `.CR2` are both `Raw`, and a mixed two-body card is exactly the case §1.3 exists for and the one a photographer most wants broken out. Renders as `ARW 77 / CR2 20 / JPG 4`, ordered by count descending, bars scaled to the largest count as above. The section hides itself when empty.
  - **Folder tree.** A **flat indented list, not a WinUI `TreeView`** — `TreeView` carries its own selection and hover brushes that would each have to be beaten back to black for §1.10, and an indent plus a chevron is the whole of what it was going to provide. Each row shows the folder name and the photo count for its **entire subtree**. The chevron expands and collapses; the root starts open so immediate subfolders are visible without a click, deeper levels start closed so a deep tree cannot flood a 232px panel.
    - **Selecting a folder moves the cursor to the first photo in its subtree. It does not filter the sequence.** Filtering would change what the cull sequence *is*, and `ActiveIndex`, the §3.3 prefetch window and the tallies all index into it. Navigation gives most of the value at none of that cost.
    - Because it does not filter, the folder containing the active photo is **highlighted in the accent tone and follows the cursor** — that "you are here" mark is what makes a read-only tree worth having.
    - **The whole section hides when the scanned folder has no subfolders.** A tree of one node says nothing the folder name above it does not.
  - **Active Photo panel.** Five fields describing whichever photo is currently active, updating live as the cursor moves:

    | Field | Source | Missing value |
    | :--- | :--- | :--- |
    | **Device** | EXIF camera make + model | omitted |
    | **Resolution** | pixel dimensions, plus megapixels | omitted |
    | **Captured** | the resolved capture date (§1.3), which already carries its source tier | omitted |
    | **Place** | reverse-geocoded place name from EXIF GPS — see §1.8's geocoding note | omitted |
    | **Folder** | the containing folder, relative to the scan root | omitted |
    | **Focal length** | EXIF | **shows `-`** |
    | **Shutter** | EXIF exposure time | **shows `-`** |
    | **Aperture** | EXIF f-number | **shows `-`** |

    **Absent fields are omitted, never shown blank or as a placeholder** — with the three exposure fields as a deliberate exception. The general rule exists because a PNG has no camera model and most files have no GPS at all, and a panel that renders `Device: —` for half a folder is worse than one that renders four fields instead of five. The exception, and why it is one, is set out in §1.8.1; it is intentional and should not be reconciled back into the general rule.

    This table is the single field list. The on-photo overlay (§1.8.1) renders the same properties off the same view-model rather than restating them.

  - **Change-folder control.** The folder name at the top of the panel is the natural place for it, since that is where the panel already states which folder is open. Activating it opens the native picker (§1.1.1); choosing a folder loads and resumes it through the same path launch uses. Cancelling changes nothing.

  - **Scan progress pill** — see §1.2.
  - **Finish Session is a disabled placeholder** carrying a "coming soon" tooltip. §4.1's modal and the entire batch copy/move path do not exist; a button that looked live would be a lie. The tooltip sits on a wrapping element because a disabled WinUI control receives no pointer input and would never show one.
  - **Not built from this line's original list:** nothing. Every item is present, though "file counts" is realised as the tally block plus the format breakdown rather than a single number.
- **Filmstrip:** virtualized horizontal scroller sized for 21:9 and 32:9. Active image scales to viewport height, neighbours flank left and right at reduced height.
- **State mark (Chromeless):** state reads as a **3px weight bar directly beneath the photo**, spanning its full rendered width — neutral grey when unrated, green when picked, red-brown when rejected. Stars render as a run of `★` in the caption row beside the filename. There are **no borders around photos anywhere in the app**.
- **Active indicator:** the active photo carries a **thin accent tick above it**, 2px tall and 18% of the photo's rendered width, centred. The active photo's filename also brightens to the accent tone while the flanking two stay muted. State and active-ness are therefore never confused — one reads below the photo, the other above it.
- **Window rule:** the top region shows a run of consecutive photos with the active one in the **centre** slot, except at the sequence boundaries: when the active photo is the **first** in the sequence the active marker sits on the **leftmost** slot, and when it is the **last** it sits on the **rightmost**. The window itself does not scroll past either end.
- **Variable slot count:** the number of photos on stage is **not fixed at three**. The stage expands outward from the active photo while the set is still *height*-bound — that is, while the shared height is clamped by the available height and the set therefore does not yet span the width. Three portrait photos on a wide stage leave most of the width unused, and that slack is spent on more photos rather than left black. Once width becomes the binding constraint the set already fills the stage and another photo would only shrink every photo, so expansion stops.
  - Counts stay **odd**, so "the active photo is the centre slot" is literally true; an even count has no centre.
  - Hard cap of **9**. This is not cosmetic: every staged photo holds a display-tier decode, and staged photos are pinned — never cancelled, never evicted (§3.3) — so the stage is the one part of the working set the cache ceiling cannot reclaim. The peak-working-set budget in §3.5 was failing outright when this cap was written (5.25 GB against 4 GB); §3.3's prefetch window and LRU cache have since brought it to 3.26 GB at a 3 GB ceiling, and lower again at 2 GB. The cap remains the right call regardless: an uncapped rule on an ultrawide full of extreme crops pins more decodes than any ceiling can evict around.
- **Stage spacing:** photos sit **5px apart** with **8px outer horizontal padding**, so the stage runs nearly the full window width. This is deliberate — the goal is maximum photo real estate. Vertical padding is *not* squeezed to match: it carries the accent tick above and the weight bar and caption below, which are the entire state read in this design.
  - The spacing is only meaningful because **slots size to their photo**. When each slot took a fixed share of the stage instead, a portrait photo sat in a landscape-sized cell and the visible space between photos became cell-width-minus-photo-width — hundreds of pixels — with the spacing setting having no say in it.
- **Equal-height rule:** every photo on stage is drawn at one shared height:

  `sharedHeight = min(availableHeight, (availableWidth − totalGapWidth) / sumOfVisibleAspects)`

  with each photo's width following its own aspect at that height. This solves the set's real total width (`height × Σaspects + gaps`) rather than assuming each photo needs an equal share. Nothing is ever cropped, and a portrait frame simply sits narrower beside a landscape one. Rotation (§1.11) feeds this rule its *post-rotation* aspect, so rotating one photo can resize its neighbours.
  - The rule agrees exactly with a per-photo-equal-share rule whenever the visible photos share an aspect, so the common all-landscape case is unchanged; it differs — in favour of larger photos — only when aspects are mixed.
- **Caption row height is fixed and identical in every slot.** The rotate buttons (§1.11) live in that row and are taller than its text; letting them size it made the active slot taller than its neighbours and, with the slots vertically centred, pushed that photo up by half the excess.

> **Superseded:** this section previously described concentric red/yellow/green state borders with an outer blue active ring, and a numeric star badge in the photo's bottom-right corner. That was the pre-Chromeless visual pass. The borders are gone, replaced by the weight bar and accent tick above.

- A small format chip (`ARW`, `JPG`, `PNG`) on each filmstrip item was specified here so a mixed folder is legible without the HUD. **The Chromeless direction drops it**, and the HUD that would replace it is v0.2 — so format is currently not visible anywhere in the UI. Open point, not a decision.

### 1.6 Rating Model

A single ordered ladder, not two independent axes. Stars are meaningful only on picked photos, so the two former axes collapse into one monotonic scale.

**The cull ladder — eight ordered states:**

| Index | State | Border | Star badge | Flag (storage) | Stars (storage) |
| :--- | :--- | :--- | :--- | :--- | :--- |
| 0 | Rejected | Red | none | `Rejected` (2) | 0 |
| 1 | Unrated | Yellow | none | `Unflagged` (0) | 0 |
| 2 | Picked | Green | none | `Picked` (1) | 0 |
| 3 | Picked, 1 star | Green | `1` | `Picked` (1) | 1 |
| 4 | Picked, 2 stars | Green | `2` | `Picked` (1) | 2 |
| 5 | Picked, 3 stars | Green | `3` | `Picked` (1) | 3 |
| 6 | Picked, 4 stars | Green | `4` | `Picked` (1) | 4 |
| 7 | Picked, 5 stars | Green | `5` | `Picked` (1) | 5 |

**Invariants (hard rules, asserted in code):**

- `Rejected` ⇒ `Stars == 0`. **Stars apply only to picked photos.**
- `Unflagged` ⇒ `Stars == 0`.
- `Stars >= 1` ⇒ `Flag == Picked`.
- Any (Flag, Stars) pair not in the table above is invalid and must never be representable in a persisted row.

**Transitions:**

- `Up` → ladder index + 1, **clamped at 7**. Pressing Up at 5 stars does nothing.
- `Down` → ladder index − 1, **clamped at 0**. Pressing Down at Rejected does nothing.
- Both are per-keypress single steps. There is no wrap-around.

**Other rules:**

- Both the top number row and the **numeric keypad** (`NumPad1` to `NumPad5`) set stars. NumLock state must be handled: with NumLock off the numpad emits navigation keycodes, so `NumPad2` arrives as `Down`, which collides with the ladder-down binding. Explicit v0.1 test case, it fails silently.
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

**Verification task — done, 2026-08-23.** A 96-file survey across two camera bodies (Sony `.ARW`, Canon `.CR2`) found 100% of RAW files carry an embedded JPEG preview at full sensor width — `.ARW` at 7008 x 4672, `.CR2` at 6000 x 4000, both matching their sensor's real image width (not a masked/bordered EXIF tag). This means RAW zoom is **Tier A for both formats tested**: no debayer decode is needed, the embedded preview is usable directly.

This is not a guarantee for every camera or RAW variant. Two caveats: only two bodies are represented in the survey, and a full-size preview's *dimensions* matching the sensor does not by itself confirm its JPEG compression quality survives 1:1 critical-focus inspection — that can only be confirmed once zoom mode (v0.2) is actually built and tested against it. `FullResIsCheap` in section 5.1 already encodes the fallback: any RAW file whose preview is below the 90% threshold still routes to Tier B automatically.

---

#### As built, 2026-08-24 — a reduced-scope zoom

A working zoom exists and is in daily use. **It is not the Tier A / Tier B design above**, and the difference matters enough to state plainly rather than let the section imply more than ships.

**What it does:**

- **`Space` toggles true fullscreen and photo zoom together**, as one gesture. The window switches to `AppWindowPresenterKind.FullScreen` (system chrome gone, taskbar auto-hides) and the active photo expands to fill the stage alone. `Esc` is an equivalent exit and is safe to press when not zoomed. The app's own title-bar strip collapses separately, because it is drawn by the app rather than the system.
- **The decode is sized to the photo's actual rendered box, not the raw viewport.** A letterboxed 3:2 photo on a 16:9 screen does not need viewport-width pixels; asking for them wastes decode and memory. Measured on this machine: a fullscreen zoom requests a 3440px long edge and holds about 30 MB of BGRA8.
- **The decode is re-requested whenever the stage element's size changes while zoomed.** This is not defensive coding — keying only on the photo was a real defect: entering zoom requested a decode sized to the still-windowed stage, the window then went fullscreen, the element grew, and nothing asked again. Measured, that shipped a 1424px decode into a 2158px box, a 1.5× upscale. The request is now keyed on `(photo, longEdge)`.
- **RAW and JPEG both go through the same zoom-tier path.** RAW is served by `RawPreviewDecoder`'s embedded-JPEG extraction — the same candidate walk as the display tier, asked for a larger edge, which reaches past the small preview to the full-sensor-width JPEG the container also carries. **This is not debayering.** Measured: `.ARW` yields 3440×2293 at the zoom tier.
- **The bottom filmstrip hides while zoomed**, so the photo owns the whole window.
- **On-photo rating indicators stay visible**, so the ladder can be driven without leaving zoom.
- The 512 MB dimension guard (§3.3) applies here, and a capped photo raises an on-photo `1:1 UNAVAILABLE` badge rather than looking mysteriously soft.
- **Loading indicator, lower-right corner.** Entering zoom shows the display-tier image immediately and swaps the larger zoom-tier decode in when it lands, so there is a window — short on JPEG, longer on RAW — where the photo on screen is not yet the one that was asked for. The indicator marks that window: visible while the zoom-tier decode is in flight, gone the instant it swaps in. Without it a soft frame is indistinguishable from a finished one that is simply soft, which is exactly the confusion that cost three rounds of investigation when the zoom decode was silently failing.
  - Lower-right, sharing that corner with the star badge and sitting opposite the §1.8 info overlay.
  - It must clear on **every** exit from the pending state, not just the success path: a cancelled decode (navigating away, exiting zoom, a resize superseding the request) and a failed one both have to take it down, or it becomes a permanent artefact on a photo that is no longer loading anything.

**What it is not — explicitly not implemented:**

| Designed above | Status |
| :--- | :--- |
| True pixel-level Tier A / Tier B decode beyond the embedded preview | **Not built.** There is one path: the embedded preview, at whatever size the container holds. A RAW whose preview fell below `FullResIsCheap` would have no full-resolution destination at all (§7) |
| Panning at 1:1 | **Not built.** The photo is fit to the stage; there is no pan offset to preserve, and the arrow keys still navigate and rate. Superseded in part by the scale zoom below, which introduces a pan offset for the first time — but by mouse, not by keyboard, and against the zoom-tier image rather than true 1:1 pixels |
| Metadata HUD in zoom (`I`) | **Not built** — §1.8 is unbuilt entirely |
| `A` / `D` navigation while zoomed | **Not built.** `A` and `S` remain rotation in both modes (§1.11); §2.2's `A`/`D` binding was never implemented |
| Zoom level and pan persisting across navigation | **Not applicable** while there is neither a zoom level nor a pan offset |

Because the zoom is fit-to-stage rather than 1:1, it answers "is this frame sharp enough and well composed" but **not** "is critical focus on the eye". The latter needs panning at true 1:1, and that remains the v0.2 work this section describes.

#### 1.7.1 Scale zoom and panning (specified, not yet built)

Fit-to-stage is the floor, not the ceiling. The mouse drives a scale factor on top of it, so a detail can be enlarged without leaving zoom mode.

**Scale, by mouse wheel:**

- **Wheel up** increases scale in **20% increments**, to a maximum of **300%**.
- **Wheel down** decreases scale in 20% increments, to a floor of **100%** — which is the existing fit-to-stage level the `Space` bar already produces. 100% is a floor, not a midpoint: there is no zooming out past the fit.
- **Scale anchors to the cursor, not to the image centre.** The image point under the pointer before the scale change is the same point under the pointer after it. Scrolling into a detail keeps that detail where it is; recentring on the image middle would push whatever the user was looking at off toward the edge, which is the opposite of the intent.

**Panning, by left-click-drag:**

- **Above 100%,** left-click-and-drag moves the visible portion of the image within the viewport.
- **Clamped to the image's own edges.** The image can never be dragged inward past its bounds, so no empty space appears beyond it. Clamping is **per axis**: at a given scale the image may overflow the viewport horizontally but not vertically, in which case it pans horizontally only and stays vertically centred.
- **At exactly 100% panning is a no-op.** The image already fits, so there is nothing to pan to. Dragging does nothing rather than rubber-banding or nudging.

**What scale does NOT do: trigger a decode.** Scaling is a pure render transform over the zoom-tier image already in memory. A wheel step must never start a new decode — twenty scroll steps would otherwise queue twenty decodes of the same photo, which is precisely the "space heater" failure §1.7 already warns about. The consequence is honest and worth stating: **above 100% the image is being enlarged past its decoded resolution and will soften.** True detail at scale needs the Tier B path, which remains unbuilt.

**Reset:** scale and pan reset to 100% and centred on **every entry to zoom** and on **every change of photo while zoomed**. Carrying a 300% scale onto the next photo would drop the user into a corner of a frame they have not seen yet, with no cue as to where in it they are.

**Overlays are unaffected by scale and pan.** The info overlay (§1.8.1, lower-left) and the loading indicator (§1.7, lower-right) are anchored to the viewport, not to the image, so they hold their corners at any scale or offset rather than sliding off with the photo.

**Zoom-percentage indicator.** The current scale reads as a percentage — `180%` — in the **lower-left**, beside where the info overlay sits.

- **Visible whenever scale is above 100%; hidden at exactly 100%.** At the fit scale there is no scale to report, and a permanent `100%` would be chrome that never says anything.
- **Independent of the `I` toggle.** This is deliberate and is the point of listing it separately: `I` governs *photo metadata* — facts about the file — while the zoom percentage is *view state*, a fact about where the app currently is. Those are different kinds of information and the user should not have to turn on a metadata overlay to find out how far they have zoomed. It shows whether `I` is on or off.
- **Updates live** as the wheel turns, in step with the scale itself.

### 1.8 Metadata HUD

**Full design (not yet built).** Toggled by `I`. Renders as a transparent overlay: `Filename`, `Format`, `Model`, `Lens`, `ISO`, `Shutter`, `Aperture`, `Focal Length`, `Timestamp` (with its source tier from 1.3), `Dimensions`, `File Size`, and the current display tier so you always know whether you are looking at real pixels.

- Fields absent from the file's metadata are omitted rather than shown blank. A PNG has no aperture; the HUD should not pretend otherwise.
- Metadata is read once during the scan and cached in the session database as JSON. The HUD never touches disk.

#### 1.8.1 Info overlay — the built subset

`I` toggles an on-photo overlay carrying **the same fields as the sidebar's Active Photo panel** (§1.5): device, resolution, capture date, place, folder, plus the three exposure fields below. It is a strict subset of the full HUD above, sharing the same source data — the two surfaces read the same properties off the same view-model, so they cannot drift apart and the field list is not written twice.

**Exposure triplet: focal length, shutter speed, aperture.** These are what a photographer checks when a frame looks soft or misjudged, so they sit together and read as a group.

> **Deliberate exception to the omit-if-missing rule, and it must stay one.**
>
> Everywhere else in this app an absent metadata field is **omitted entirely** — §1.5 says so, and the reasoning holds: a panel rendering `Device: —` for half a folder is worse than one rendering four rows.
>
> **Focal length, shutter speed and aperture are exempt. They always render their label, with `-` as the value when the file carries no figure.**
>
> The reason is that for these three, *absence is itself information the photographer wants*. A frame with no aperture recorded is a frame shot on an adapted or manual lens — that is a fact about the shot, and one that explains a soft result. Omitting the row would hide it, and worse, would make the overlay's height jump around as the cursor moves through a mixed folder, which makes the group hard to read at a glance precisely when it is being scanned quickly.
>
> This is not an oversight to be tidied up later into consistency with the other fields. If a future change makes these three omit-if-missing, it is a regression.

- **Lower-left corner of the active photo.** Deliberately not lower-right: that corner is taken by the star-rating badge and by the zoom loading indicator (§1.7), and stacking three things there would collide at the exact moment all three are most likely to be on screen at once.
- **Works in both normal stage view and while zoomed.** The same overlay, positioned against whatever the photo's rendered box currently is.
- **Toggle state does not persist across restart.** It is a glance, not a preference.
- Absent fields are omitted, per the rule above.

#### 1.8.2 Reverse geocoding — the first network-dependent feature in the app

Resolving a place name from GPS coordinates is the **first and currently only** thing FastCull does that touches the network. That deserves explicit constraints, because the prime directive (§0) does not stop applying just because a feature is convenient:

- **Opportunistic and best-effort. Never blocking.** A lookup must never delay navigation, decode, or the photo appearing. Nothing in the render path waits on it. The field simply fills in later if it fills in at all.
- **Fails silently to raw coordinates.** No network, DNS failure, timeout, rate limit, malformed response — every failure path falls back to displaying the raw `lat, long`, which is still genuinely useful information. No error dialog, no retry storm, no red text.
- **Times out quickly.** A slow lookup is a failed lookup.
- **Cached by rounded coordinate, not exact float.** A burst of forty frames from one spot must produce one lookup, not forty. Rounding is the cache key.
- **Offline is a supported state, not a degraded one.** The app must be fully usable with no network at all; the only difference is that Place reads as coordinates instead of a name.
- **No GPS is the common case.** Most files carry no GPS at all. Those omit the field entirely, per §1.5.

If reverse geocoding ever needs to become more than this — batch prefetching, a paid provider, a persistent on-disk cache — that is a decision to take explicitly, not to drift into.

### 1.9 Undo
- `Ctrl+Z` / `Ctrl+Y` over a command stack of at least 200 entries.
- Covers flag changes, star changes, and Recycle Bin deletes (restored via the shell API).
- Does not cover the Finish Session batch operation, which has its own confirmation and log.

### 1.10 Visual theme (OLED black)

FastCull renders on a true-black surface. Every pixel that is not photograph content is `#FF000000` — a fully-off pixel on an OLED panel. This includes the window background, the title bar, empty photo slots, filmstrip card backgrounds, gaps and padding. Near-blacks such as `#0A0A0A` do **not** satisfy this requirement; the value must be exactly `#FF000000`.

Consequences: the app forces dark theme regardless of the system setting, and uses no Mica, Acrylic or other system backdrop, since all of them tint with the desktop wallpaper and can never be fully black. Text and chrome use light foregrounds chosen for legibility against pure black.

Controls are held to the same rule in **every visual state**, not just at rest. The rotate buttons (§1.11) use a custom borderless template because the stock WinUI `Button` paints near-black greys on hover and press; theirs paints no background in any state and expresses hover and press purely as glyph brightness.

### 1.11 Rotation

A per-photo **quarter-turn count** — 0, 1, 2 or 3 turns clockwise — applied to the **selected** photo.

- `A` turns 90° **counter-clockwise**; `S` turns 90° **clockwise**. The keys run the way the photo does — `A` is left of `S` and turns the photo left. Both wrap: four turns in either direction return the photo to where it started.
- The turn is **animated**, using the same duration and easing as the navigation transition (§2.5). It is decoration on the same terms: the rotation state, the persisted value and the stage layout all change immediately, and nothing waits for the sweep.
- Two small buttons in the caption row, right-aligned, do the same thing. They render in the **active** slot — wherever that is, which at the first and last photo of the sequence is an end slot rather than the centre (§1.5). Exactly one slot shows them at a time, and they act on the active photo.
- **Rotation is a delta on top of whatever orientation the decode produced, never an absolute orientation of the final image.** A delta means the same thing regardless of what the decoder hands back, which is why it was chosen.

  **This decision paid for itself on 2026-08-24**, when EXIF orientation started being applied to RAW (§3.2). Every portrait RAW's decoded baseline turned a quarter turn that day. Because stored values are deltas on top of that baseline, existing user rotations stayed semantically correct across the change — "two more quarter turns than however this file naturally sits" is true before and after — and **no migration was needed**. An absolute orientation would have silently re-interpreted every value already in the database. The §7 risk row that tracked this is now closed.
- **It is a display transform and never a re-decode.** Re-decoding would spend a decode on a transform, blow the §3.5 keypress budget, and invalidate cache entries once §3.3 exists.
- Rotation **does not alter the cull state and does not move the cursor.** Rating and rotation are fully independent axes, and a write to one never disturbs the other in the database (§3.1).
- It applies to **both** the stage photo and its filmstrip thumbnail. A photo rotated on stage that stays sideways in the strip is a bug.
- A quarter turn inverts the photo's aspect, so the equal-height rule in §1.5 is fed the post-rotation aspect. Rotating one photo can therefore resize its two neighbours, because that rule sizes to the widest photo visible.
- Rotation **persists across sessions and app restarts**, exactly as ratings do (§3.1).

**Build status, 2026-08-24 — fully built as specified.** `A`/`S` rotate counter-clockwise/clockwise and wrap at four turns; the two caption-row buttons in the active slot do the same and claim their own tap so pressing one never also changes the selection; the turn animates on the same 110 ms eased-out timing as the navigation transition (§2.5) with state and layout changing immediately rather than waiting for the sweep; the value is written through `SessionStore` on the same background channel as ratings and survives a restart, which required a real schema migration for existing databases. It applies to both the stage photo and its filmstrip thumbnail, and feeds the equal-height rule its post-rotation aspect.

---

## 2. Control Matrix

### 2.1 Filmstrip Mode

**Revamped 2026-08-24 for one-handed operation.** The whole map is now reachable by the left hand resting on `WASD`, so the right hand is free for the mouse or for nothing at all. This replaced the previous scheme almost entirely; §2.1.1 lists what was removed and why that matters.

The organising idea is that the two axes a cull actually uses — **which photo** and **what rating** — sit on one four-key cluster:

| Key | Behaviour |
| :--- | :--- |
| `A` | **Previous photo** |
| `D` | **Next photo** |
| `W` | **Rate up** — one step up the cull ladder (§1.6), clamped at 5 stars |
| `S` | **Rate down** — one step down the ladder, clamped at Rejected |
| `Q` | Rotate the selected photo 90° **counter-clockwise** (§1.11) |
| `E` | Rotate the selected photo 90° **clockwise** (§1.11) |
| `R` | Jump to the **first** photo in the sequence |
| `T` | Jump to the **last** photo in the sequence |
| `F` | Toggle the on-photo info overlay (§1.8.1) |
| `G` | Open the folder picker (§1.1.1) — the same action as the sidebar's **CHANGE FOLDER** control |
| `Space` | Toggle zoom mode (§1.7) |
| `Delete` | Move the selected photo to the **Recycle Bin** (§2.1.2) |
| `1`–`5` / `NumPad1`–`NumPad5` | Set stars directly (implies `Picked`) |
| `Left` / `Right` | Previous / next photo — arrow-key equivalents of `A` / `D` |
| `Up` / `Down` | Rate up / down — arrow-key equivalents of `W` / `S` |
| `I` | Toggle the info overlay — synonym of `F`, retained |

**`W`/`S` and `Up`/`Down` are the same action, not two similar ones.** Both resolve to the identical command and call `CullState.Up()` / `CullState.Down()`. `W`/`S` is a same-hand duplicate of the arrows, added so the ladder can be driven without moving off `WASD`; it is not a second rating behaviour and must never diverge from the arrows. The same holds for `A`/`D` against `Left`/`Right`, and `F` against `I`.

**`Up`/`Down`/`W`/`S` are the only keys that *step* the ladder.** `1`–`5` set stars outright, jumping straight to their rung. Nothing else moves the rating.

**Rotation neither alters the cull state nor moves the cursor.** `Q` and `E` change only orientation; rating keys change only the ladder position; navigation keys change only the cursor. The three axes stay independent.

Bare `Ctrl` is **not** used as a toggle: it is a modifier, it fires as part of every accelerator, and its key-repeat behaviour is inconsistent.

#### 2.1.1 What the revamp removed, and the one real consequence

These were all live before 2026-08-24 and now do nothing at all. They are unmapped keys like any other — logged at debug level, otherwise ignored:

| Removed | Was | Replacement |
| :--- | :--- | :--- |
| `C`, `P` | Set `Picked` directly | **None** — see below |
| `Z`, `X` | Set `Rejected` directly | **None** — see below |
| `U` | Set `Unrated` directly | **None** — see below |
| `0` / `NumPad0` | Clear stars, keeping the flag | **None** |
| `A` (old) | Rotate counter-clockwise | `Q` |
| `S` (old) | Rotate clockwise | `E` |
| `Home` / `End` | First / last photo | `R` / `T` |
| `Esc` | Exit zoom | `Space`, which toggles |

**The consequence worth stating plainly: there is no longer any way to jump directly to a flag.** Reaching `Rejected` from a 5-star photo previously took one keypress (`Z`); it now takes seven presses of `S`, walking the ladder down rung by rung. The same applies to `Picked` and to clearing a rating.

That is a deliberate outcome of a map built around one hand and a single stepping axis, not an oversight. It is recorded here because it is the kind of trade that looks like a bug later: if rapid rejection turns out to matter more than the one-handed layout, the fix is to reintroduce a direct-set key rather than to assume `S` was meant to behave differently.

#### 2.1.2 Delete

`Delete` moves the selected photo to the **Recycle Bin** — a recoverable delete, never a permanent one.

- The cursor stays at the same position in the sequence, which is now the photo that followed the deleted one, so a run of unwanted frames can be cleared without moving the hand. At the end of the sequence it steps back to the new last photo.
- The remaining photos are renumbered, because position in the sequence is what §3.3's prefetch window and eviction are indexed by.
- **A file that cannot be deleted — locked, read-only, on a disconnected volume — leaves the sequence untouched.** The photo stays where it is rather than vanishing from the strip while surviving on disk.

**Undo does not cover this.** §1.9's undo stack is unbuilt, so within the app the deletion is final; recovery is through the Windows Recycle Bin. That gap is the reason this is a Recycle Bin move and not a permanent delete, and it is why the Recycle Bin's own restore is currently the only undo that exists.

**Companion grouping does not apply yet.** §1.4's RAW+JPEG pairing is unimplemented, so `Delete` removes exactly the selected file. On a card where a RAW and its JPEG are separate sequence entries, each must be deleted separately — the PRD's original "move file **group**" wording describes the intended behaviour once pairing exists, not what ships today.

### 2.2 Zoom Mode

**Designed:**

| Key | Behaviour |
| :--- | :--- |
| `Arrow keys` | Pan, with acceleration on hold |
| `A` / `D`, `PgUp` / `PgDn` | Previous / next, preserving zoom and pan |
| `1`-`5`, `NumPad1`-`NumPad5`, `Z`, `X`, `C` | Rating keys stay live |
| `Space` or `Esc` | Exit to filmstrip |
| `I` | Toggle HUD |

> Note: `Z`/`X`/`C` in this designed table were removed from the app entirely by the §2.1 revamp. The row is left as written because it is the *design*, not a description of the build — see §2.1.1.

**As built, 2026-08-24.** The reduced-scope zoom (§1.7) has **no separate key map**. `InputRouter` resolves keys identically in both modes — there is no two-state machine, because with no panning and no zoom-preserving navigation there is nothing for a second state to change. Every §2.1 binding is live while zoomed:

| Key | Behaviour while zoomed |
| :--- | :--- |
| `A` / `D`, `Left` / `Right` | Previous / next photo. The zoom stays on and re-decodes for the new photo |
| `W` / `S`, `Up` / `Down` | Rate up / down — the ladder stays live, as designed |
| `1`-`5`, `NumPad1`-`NumPad5` | Set stars — live |
| `Q` / `E` | Rotation, as in filmstrip mode |
| `F` or `I` | Toggle the info overlay (§1.8.1), positioned against the zoomed photo's box |
| `R` / `T` | Jump to first / last photo, staying zoomed |
| `G` | Open the folder picker — leaves zoom when a folder loads |
| `Delete` | Recycle-bin the selected photo (§2.1.2), staying zoomed on the next one |
| `Space` | Exit to filmstrip |
| ~~`Esc`~~ | **Removed** in the §2.1 revamp — `Space` toggles, so the second exit key was redundant |
| ~~`Arrow keys` pan~~, ~~`PgUp`/`PgDn`~~ | Not implemented |

The designed `A`/`D` row above wanted *zoom-preserving* navigation as a zoom-only binding. `A`/`D` now navigate in **both** modes, and zoom is in fact preserved across a photo change — so that row is satisfied, by a general binding rather than a mode-specific one.

**Mouse, in zoom mode (§1.7.1 — specified, not yet built).** Zoom mode is the only place in the app where the mouse does more than click a thumbnail:

| Input | Behaviour |
| :--- | :--- |
| `Wheel up` | Scale +20% per step, capped at 300%, anchored to the cursor |
| `Wheel down` | Scale −20% per step, floored at 100% (fit-to-stage) |
| `Left-drag` | Pan the image, clamped to its own edges. Above 100% only |
| `Left-drag` at 100% | No-op — the image already fits |

The zoom percentage (§1.7.1) appears in the lower-left while scale is above 100%. It is **not** on the `I` toggle: `I` shows metadata about the photo, the percentage shows where the view is, and one should not be hidden behind the other.

Note this does not extend §2.4's rule. Arrow keys remain owned by the stage and are still navigation and rating in zoom mode; the wheel and the drag are new inputs on a surface that previously ignored both, not a reassignment of anything.

Input routing is handled in one place at window level, per §2.4 — `MainWindow.RootGrid_PreviewKeyDown`, a tunnelling handler that claims every mapped key before any focused child can consume it. Unmapped keys log at debug level rather than being silently swallowed. What does not yet exist is the *two-state* part: the router is currently mode-agnostic.

### 2.3 Key Repeat
Holding `Right` must not queue 40 navigation events that replay after release. Navigation input is coalesced: the cursor moves at a fixed maximum rate (suggest 15 per second) and the decode pipeline targets only the settled position, not every intermediate index.

### 2.4 Keyboard ownership

Arrow keys are owned exclusively by the top stage region. They navigate and rate. They must **never** scroll the bottom filmstrip, move XY-focus, or be consumed by any child control. The bottom filmstrip is mouse-only: click, drag and wheel.

### 2.5 Navigation transition

Moving between photos animates rather than snapping, in both regions: the stage slides by one slot pitch, and the bottom filmstrip scrolls to re-centre the active thumbnail. Duration is short enough not to sit between the user and the next photo — currently **110 ms**, eased out — and is a single named value so it can be tuned in one place.

Three properties are behavioural contract, not styling:

1. **The state change is immediate; the animation is decorative.** The active index, the cull state and the stage layout all update on the keypress. Nothing — not a rating, not a decode, not the next navigation — ever waits for a transition to finish.
2. **Interruptible and coalescing.** A navigation arriving mid-animation *retargets* the in-flight one; it never enqueues another. Holding an arrow key fires navigations far faster than the animation completes, and the transition must converge on the settled position rather than fall behind a backlog.
3. **It never fights direct manipulation.** The filmstrip's pointer-drag path writes the scroll offset directly, so the re-centring animation does not run against a drag in progress and does not snap back on release.

The bottom filmstrip must keep the active thumbnail in view on **every** navigation, whatever its source — keyboard, a stage click, or a thumbnail click.

### 2.6 Manual rotation

Added outside the original specification. The model — quarter turns, wrapping, stored as a delta, display-transform-only, persisted — is §1.11; this section covers only how it is driven.

**Scope: filmstrip mode.** Zoom mode does not exist, so there is no second mode for these keys to mean something else in. See the collision warning at the end of this section before building one.

| Input | Action |
| :--- | :--- |
| `A` | Rotate the active photo 90° **counter-clockwise** |
| `S` | Rotate the active photo 90° **clockwise** |
| On-screen **↺** button | Rotate the active photo 90° counter-clockwise |
| On-screen **↻** button | Rotate the active photo 90° clockwise |

The keys run the way the photo does: `A` is left of `S` and turns the photo left.

**The buttons are exactly equivalent to the keys** — same two actions, same target, no separate code path. They render in the caption row of the **active** slot, wherever that is, which at the first and last photo of the sequence is an end slot rather than the centre (§1.5). Exactly one slot shows them at a time. Clicking one must not move the selection or alter the cull state, and must not take keyboard focus.

**The turn is animated**, sharing the navigation transition's easing profile (§2.5) so the two motions read as the same system: **110 ms, cubic ease-out**, driven from the same single named duration — retuning §2.5 retunes this. It is decoration on the same terms as §2.5's first contract: the rotation state, the persisted value and the stage layout all change immediately, and nothing waits for the sweep. Repeated turns retarget the in-flight sweep rather than queueing, so holding `A` or `S` turns continuously instead of falling behind.

> **On frame counts.** The durable specification is the duration and the easing curve, not a frame count. Sampling the *navigation slide* at ~5.5 ms intervals captured 16 distinct intermediate positions settling by ~133 ms of wall clock, and the rotation sweep behaves the same way — but both numbers are properties of the measuring rig and the display refresh rate, not targets. At 60 Hz a 110 ms animation is roughly seven displayed frames. Treat "16 frames / 130 ms" as an observation confirming the motion is smooth and fast, not as a requirement to hit.

**Known limitation:** on a 90° or 270° turn the photo's aspect changes, so its layout box takes the new shape immediately while the image sweeps between the two. A rotating rectangle passes through a bounding box larger than either endpoint, so the photo briefly overflows its own frame — measured at roughly 110 ms, crossing its caption row but not reaching the neighbouring photo. Accepted as-is; closing it would mean either clipping the frame (cutting the photo mid-sweep) or scaling down through the turn.

> **Collision to resolve before zoom mode is built.** §2.2 assigns `A` to *previous photo* in zoom mode. Input resolution is currently a single flat map with no mode state machine — the "explicit two-state machine" §2.2 describes was never implemented — so `A` resolves to rotate-left everywhere. When zoom mode lands, either §2.2's `A`/`D` navigation or these rotation bindings has to give, and the flat map has to become the two-state machine §2.2 already assumes.

---

## 3. Technical Specification

### 3.1 Persistence
File-backed SQLite at `%LOCALAPPDATA%\FastCull\sessions\{session-guid}.db`, `journal_mode=WAL`, `synchronous=NORMAL`.

- All writes go through a single background writer consuming a bounded channel. The UI never awaits a write.
- Writes are batched on a 250 ms timer or 32 pending operations, whichever comes first.
- On launch, if a session DB exists whose root folder still resolves, ~~offer to resume~~ **resume it** — see §1.1.1. The offer was dropped: a folder is an unfinished job, and prompting before the action that is almost always right is friction rather than safety.
- The session DB is deleted after a successful Finish Session.

**The last-opened folder is app-level state, and does not live in a session database.** Each session DB is scoped to one folder, so none of them can answer "which folder was open last" — that is a fact about the application, not about any one card.

It is stored as a small settings file at `%LOCALAPPDATA%\FastCull\settings.json`, beside the `sessions\` directory. Deliberately *not* `ApplicationData.Current.LocalSettings`, which is the obvious WinRT answer and does not work here: this app runs unpackaged, and `ApplicationData.Current` throws `InvalidOperationException` in that configuration. That is measured, not assumed — it is already in this project's crash log from the metadata work. A plain JSON file has no packaging requirement and can be inspected and deleted by hand, which matters for a setting whose failure mode is "opens the wrong folder".

The file is best-effort in both directions: an unreadable or malformed settings file is treated as "no last folder" and lands on §1.1.1's empty state, and a failure to write it costs the next launch its auto-resume and nothing else. Neither may take the app down or block a folder from opening.

In-memory SQLite was rejected: it does nothing for UI threading (that is what the background writer is for) and it loses a 45-minute session on any crash.

**Schema migration.** The schema is versioned with `PRAGMA user_version`; revision **1** added `photos.rotation`. On open, any column the current revision requires but the database lacks is added with `ALTER TABLE`, then the version is recorded. This is not optional bookkeeping: `CREATE TABLE IF NOT EXISTS` is a no-op against a database that already exists, so a column added only to that statement never reaches the session databases already on disk, and the first write to it fails with `no such column`. The presence check reads `PRAGMA table_info` rather than trusting `user_version` alone, because a freshly created database already has the column while its version is still 0.

**Independent fields.** A rating write and a rotation write for the same photo must not clobber one another. Queued writes carry only the fields they actually set, are merged when the writer collapses them by path, and are applied with `COALESCE` so an unset field keeps whatever the row already holds.

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
  rotation       INTEGER NOT NULL DEFAULT 0,   -- quarter turns clockwise, 0-3 (see 1.11)
  deleted        INTEGER NOT NULL DEFAULT 0,
  image_w        INTEGER,
  image_h        INTEGER,
  preview_w      INTEGER,                -- null when no embedded preview exists
  preview_h      INTEGER,
  thumb_blob     BLOB,                   -- generated for formats lacking an embedded thumb
  meta_json      TEXT
);
CREATE TABLE companions (photo_id INTEGER, path TEXT, kind TEXT);
-- Records the folder this session was scanned from, so a session DB can be matched back to
-- its root on relaunch. This is what makes the resume lookup above possible; the
-- offer-to-resume UI itself is still v0.4 (section 6), only the storage exists today.
CREATE TABLE session_meta (root_path TEXT NOT NULL);
CREATE INDEX idx_sort ON photos(sort_time, capture_subsec, path);
```

### 3.2 Decode Pipeline

Chain per image:
1. **Thumbnail (~160 px).** Extracted from embedded data where it exists (RAW, most JPEG, most HEIF). **Generated by downscaled decode where it does not** (PNG, WebP, BMP, most TIFF), then cached as a blob in the session DB so it is generated once per session and never again.
2. **Display tier.** Embedded preview for RAW; the file itself for JPEG and HEIF; a downscaled decode for large PNG and TIFF. This is the normal filmstrip display source.
3. **Full resolution.** Only for zoom Tier B, never speculative.

~~`IImageDecoder` resolves to `LibRawDecoder` or `WicDecoder` by format family at scan time. Callers never branch on file extension.~~

**As built — the decoder abstraction was never needed, and RAW works differently than planned.** There is no `IImageDecoder`, no `LibRawDecoder` and no `IRawDebayer`. See §5's architecture note. What exists:

- **`ThumbnailService`** — WIC via `Windows.Graphics.Imaging`, for JPEG/PNG and as the RAW fallback. One shared scaled-decode (`DecodeScaledFromStreamAsync`) serves every tier; the tier is just a requested long edge (160 thumbnail / 960 display / viewport-sized zoom).
- **`RawPreviewDecoder`** — RAW by **embedded-JPEG extraction**, not debayering. It reads a 16 MB scan window, locates embedded JPEG streams by their `FF D8 FF` / `FF D9` markers, sorts candidates smallest-first and decodes the smallest that satisfies the requested edge, then hands those bytes to the very same `ThumbnailService` decode. Vendor-agnostic by design: Sony and Canon store previews at different offsets under different maker-note tags, but both store ordinary JPEG. Costs about 30 ms against roughly 1000 ms for WIC's full debayer of the same file.
- Callers select between the two on `FormatFamily`, never on file extension — the original intent survives even though the abstraction did not.

**EXIF orientation is applied on both paths, since 2026-08-24.**

- **JPEG and friends:** WIC handles it, via `ExifOrientationMode.RespectExifOrientation`.
- **RAW:** it must be applied explicitly, and previously was not — **this was a real bug, and portrait RAW files displayed sideways.** The cause is structural rather than an oversight: a RAW's orientation lives in the *container's* TIFF IFD0, and `RawPreviewDecoder` slices an embedded JPEG stream out of that container. The slice leaves the orientation tag behind, so WIC was handed bytes that legitimately claimed to be upright. `ExifOrientation` now reads the tag from the container and passes it into the decode as an explicit override; when one is supplied the stream's own EXIF is deliberately **ignored** rather than combined, so a container whose preview happens to carry its own tag cannot have the rotation applied twice.
- Measured across the sample corpus: nine files tagged orientation 8 (six `.CR2`, three `.ARW`) decoded 960×640 landscape before the fix and 640×960 portrait after, with the other 92 files unchanged.
- The mapping covers all eight orientations. Note that WIC applies **flip before rotation**, which is what makes the two mirrored-diagonal values subtle — orientation 5 is a transpose and needs a *vertical* flip before its 90° turn; a horizontal one produces the transverse, which belongs to 7. Neither 5 nor 7 occurs in the sample corpus, so unit tests carry those cases rather than fixtures.

### 3.3 Prefetch and Cache

**Built 2026-08-23/24 and measured.** Everything in this section is implemented except the VRAM texture cache, which is explicitly marked below. The peak-working-set budget in §3.5 went from **5.25 GB (failing)** to **2.34 GB** against its 4 GB ceiling as a result — see `docs/benchmarks/2026-08-24-ceiling-2gb.md`.

- Sliding window: active index plus 5 ahead, 2 behind, on a bounded worker pool of `min(6, coreCount - 2)`. **Implemented** as `PrefetchWindow` and `DecodeGate` (a `SemaphoreSlim`); 6 workers on a 12-core machine.
- Direction-aware: the window reverses after three consecutive backwards moves. **Implemented, with one deliberate deviation:** reversal is **symmetric** — three consecutive moves in *either* direction set the orientation. Reverting on a single forward step made the window thrash on any jitter, and a user who steps back three and forward one is still going backwards.
- **Keep-set is the window UNION the pinned stage, not the window alone.** At nine slots the stage spans ±4, which is *wider* than the −2 lookbehind, so a window-only keep-set would cancel loads the stage had just started, on every navigation.
- LRU cache with a hard ceiling of **2 GB** system memory, evicting furthest-from-cursor first. Lowered from 3 GB on 2026-08-24: at 3 GB the cache filled and stayed filled, putting the measured peak working set at 3.26 GB against §3.5's 4 GB budget — passing, but with only 16% headroom. The window plus a nine-slot stage is roughly 17 items, so the ceiling governs how much the cache may hoard beyond what it needs, not how much it needs.
  **Implemented** as `PrefetchCoordinator`, evicting furthest-from-cursor first. Two rules the implementation had to add:
  - **Eviction never disposes.** It drops the last managed reference and lets the GC reclaim. Disposing a `SoftwareBitmap` that XAML may still be copying into a composition surface fail-fasts the process with `0xC000027B`, a bug this project has already paid for once.
  - **Eviction credits only what it actually frees.** `Evict()` keeps the bottom filmstrip's thumbnail on purpose, so a photo the cursor has passed holds a thumbnail and nothing else and frees *nothing*. Crediting it the full `ResidentBytes` anyway made those items — which sort furthest-from-cursor — absorb the entire sweep while the display tiers that should have been dropped survived. Measured at **133 useless evictions per navigation step** before the fix; 745 total across a 2,000-photo walk after. `ICacheableItem.EvictableBytes` now distinguishes what an item *holds* from what evicting it would *give back*.
- **VRAM texture cache:** decoded surfaces are uploaded to GPU textures and kept resident. At roughly 130 MB per 33 MP RGBA frame, 8 GB VRAM holds 40 to 50 images, comfortably covering the prefetch window plus zoom history. Eviction is by distance from cursor, with a VRAM budget defaulting to 60% of available. — **NOT IMPLEMENTED.** This is v0.4 scope (§6). Every memory figure in this PRD and in the benchmark files is therefore **system RAM only**; a `SoftwareBitmapSource` additionally copies pixels into a XAML composition surface that may live in GPU memory and is not this process's working set to sample.
- **Dimension guard:** any image whose decoded size would exceed 512 MB in memory (roughly 128 MP at RGBA) is capped at a downscaled decode, with a HUD notice that 1:1 is unavailable. Without this, one stitched panorama TIFF takes the app down. **Implemented** as `DimensionGuard`, applied at the two places a decode size is chosen — inside `DecodeScaledFromStreamAsync`, where the source dimensions are known, and at the top of the zoom request, where the viewport drives it. It solves for the largest long edge that fits the source's aspect rather than refusing the file. **One substitution:** there is no HUD in v0.1 (§1.8 is unbuilt), so the notice is an on-photo `1:1 UNAVAILABLE — IMAGE TOO LARGE` badge instead.
- Cache is cleared on folder change, not on session end.

**A tension worth naming:** the 2 GB ceiling and §3.5's 4 GB peak-working-set budget were set independently, and the ceiling is now half the budget. At 3 GB the measured peak was 3.26 GB — passing, but only because the ceiling happened to fit. At 2 GB it is 2.34 GB, with real headroom. The peak sits *above* the ceiling because the ceiling caps resident decoded pixels while the working set also carries roughly 460 MB of baseline process overhead plus transient decode buffers.

### 3.4 GPU Acceleration
Three stages, three different answers.

| Stage | GPU benefit | Decision |
| :--- | :--- | :--- |
| Render / scale / pan | Already GPU accelerated by the WinUI 3 compositor | Free, nothing to do |
| JPEG / PNG decode | Marginal; GPU decode exists but plumbing cost is high | CPU, multithreaded |
| RAW debayer | Substantial in principle (roughly 250-400 ms CPU vs under 50 ms GPU at 33 MP), but off the critical path — see below | **Deprioritized** |

Per section 1.7's updated finding, RAW zoom is Tier A for every file surveyed so far, which means debayer performance is no longer the critical path it was assumed to be. The GPU-debayer contingency for v0.5 is deprioritized on this basis.

**Open gap, not silently resolved:** no working debayer implementation currently exists in this codebase. `LibRawSharp` (the package this PRD originally named) was evaluated and found unusable — no native binary ships with it, and it exposes no preview-extraction API either. If a future RAW file is ever encountered whose embedded preview genuinely falls below the `FullResIsCheap` threshold, it will currently have **no full-resolution decode path at all** and would need to degrade to the upscaled-preview-with-decoding-indicator behaviour section 1.7 describes for Tier B, indefinitely, since there is nothing to decode with. `IRawDebayer` remains an unimplemented interface. If this gap needs closing, `Magick.NET-Q16-HDRI-x64` is the next candidate to spike, not LibRaw.

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
| Rotation keypress to photo turning | < 16 ms |
| Navigation keypress to new active state | < 16 ms |
| Pan at 1:1 | Sustained 60 fps minimum |
| Peak working set, 2,000 files | < 4 GB system RAM |

Rotation is held to the same bar as a rating keypress for the same reason: both are transforms of state already in memory, neither touches the decode pipeline. A rotation that needed a re-decode would fail this budget by an order of magnitude, which is why §1.11 forbids it.

**The navigation budget is measured to the state change, not to animation completion.** The active index, the stage window and the chrome must all be correct within 16 ms of the keypress; the transition in §2.5 then plays out over roughly 110 ms as decoration. Measuring to the end of the animation would be measuring the animation's duration, which is a design choice rather than a performance property — and would create pressure to shorten a transition that is deliberately visible. A navigation that *waited* for the previous animation would fail this budget, which is why §2.5 requires retargeting rather than queueing.

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

**As built, 2026-08-24.** Four projects in one solution, not one project with a `Benchmarks/` folder.

```text
Fastcull.sln
│
├── Fastcull.Core/              # headless logic. NO WinUI reference. UseWinUI=false.
│   │                           #   Exists because the WinUI project CANNOT be referenced from a
│   │                           #   test project - its MSIX targets refuse to build
│   │                           #   ProcessorArchitecture-neutral. Anything that needs testing
│   │                           #   lives here; that constraint shaped the whole layout.
│   ├── Input/
│   │   └── InputRouter.cs      # pure key -> command resolution, incl. the NumLock/numpad split
│   ├── Models/
│   │   ├── CullState.cs        # the 8-rung ladder (§1.6); invalid pairs throw
│   │   ├── Rotation.cs         # quarter-turn delta (§1.11)
│   │   ├── CullTally.cs        # sidebar session counts (§1.5)
│   │   ├── FolderTree.cs       # sidebar folder tree + flattening (§1.5)
│   │   └── FormatBreakdown.cs  # sidebar per-extension counts (§1.5)
│   ├── Services/
│   │   ├── DirectoryScanner.cs # streaming discovery via IAsyncEnumerable + Channel (§1.2)
│   │   ├── ThumbnailService.cs # WIC scaled decode, all tiers (§3.2)
│   │   ├── RawPreviewDecoder.cs# RAW via embedded-JPEG extraction (§3.2) - replaces LibRawDecoder
│   │   ├── ExifOrientation.cs  # container orientation -> WIC flip+rotation (§3.2)
│   │   ├── DecodeGate.cs       # bounded worker pool, min(6, cores-2) (§3.3)
│   │   ├── DimensionGuard.cs   # 512 MB decode ceiling (§3.3)
│   │   └── SessionStore.cs     # SQLite WAL, batched background writer, resume (§3.1)
│   └── ViewModels/             # view-model logic that needs no XAML
│       ├── FilmstripWindow.cs  # the stage window rule (§1.5)
│       ├── StageLayout.cs      # equal-height rule + slot count (§1.5)
│       ├── PrefetchWindow.cs   # +5/-2 sliding window, direction reversal (§3.3)
│       ├── PrefetchCoordinator.cs # LRU eviction at the ceiling (§3.3)
│       └── ICacheableItem.cs   # the seam that makes eviction testable at all
│
├── Fastcull/                   # the WinUI 3 app (Fastcull.csproj, at repo root)
│   ├── App.xaml(.cs)           # theme dictionaries, crash logging to a fixed temp path
│   ├── MainWindow.xaml(.cs)    # black title bar, window-level key routing, sidebar host
│   ├── Themes/
│   │   └── Nocturne.xaml       # the Chromeless palette: neutral + accent ramps, pick/reject,
│   │                           #   Inter font stack, spacing scale, borderless button template
│   ├── Converters/
│   │   ├── ChromelessConverters.cs  # CullStateToWeightBarBrush, ThumbnailMarkBrush,
│   │   │                            #   BoolToAccentBrush, BoolToCaptionBrush,
│   │   │                            #   BoolToVisibility, BoolToCollapsed,
│   │   │                            #   BoolToThumbnailOpacity
│   │   └── SlotConverters.cs        # ItemToVisibility
│   ├── ViewModels/
│   │   ├── MainViewModel.cs         # sequence, cursor, stage window, command execution
│   │   ├── FilmstripItemViewModel.cs# per-photo decode tiers, cull state, rotation
│   │   ├── SidebarViewModel.cs      # §1.5 panel: tallies, formats, folder tree, pin, scan pill
│   │   └── SidebarRowViewModels.cs  # folder + format row types
│   └── Views/
│       ├── FilmstripView.xaml(.cs)  # stage + bottom strip, both ItemsRepeater
│       └── SidebarView.xaml(.cs)    # the left panel
│
├── Fastcull.Tests/             # xunit, references Fastcull.Core only. 333 tests.
│   └── (one file per Core type: CullState, Rotation, InputRouter, DirectoryScanner,
│        RawPreviewDecoder, SessionStore, StageLayout, FilmstripWindow, DecodeGate,
│        Prefetch, DimensionGuard, ExifOrientation, CullTally, FolderTree, FormatBreakdown)
│
└── Fastcull.Benchmarks/        # separate console project, NOT a folder in the app
    ├── PerfHarness.cs          # asserts the §3.5 budgets against RAW / JPEG / mixed fixtures
    ├── WindowedCullSimulation.cs # replays the §3.3 architecture over 2,000 files
    ├── SyntheticCorpus.cs      # 2,000 NTFS hard links over the real sample files
    ├── FixtureSets.cs, BudgetResult.cs, MarkdownReport.cs, Program.cs
    └── (writes docs/benchmarks/*.md)
```

**Deliberate architecture changes from the original tree — these are decisions, not gaps:**

| Planned | Actual | Why |
| :--- | :--- | :--- |
| `IImageDecoder` + `LibRawDecoder` + `WicDecoder` | `ThumbnailService` (WIC) + `RawPreviewDecoder` | The abstraction earned nothing: there are two concrete decoders, callers pick on `FormatFamily`, and both funnel into one shared scaled-decode. An interface over two implementations that share their entire back half is ceremony |
| `IRawDebayer` (`CpuDebayer` now, `GpuDebayer` later) | **Nothing.** `RawPreviewDecoder` extracts the embedded JPEG | **The real, working, shipping RAW path.** §1.7's survey found 100% of surveyed RAW files carry a full-sensor-width embedded JPEG, making debayering unnecessary for every tier the app uses — at ~30 ms versus ~1000 ms. True debayering was never built. This is an upgrade path, not a hole (see `docs/BACKLOG.md`) |
| `PreviewCache.cs` (one file) | `DecodeGate` + `DimensionGuard` + `PrefetchWindow` + `PrefetchCoordinator` + `ICacheableItem` | Split by what needs WinUI and what does not. That split is what makes the cache logic testable at all — a single WinUI-side file would have been untestable |
| `FormatRegistry.cs` | The extension→family map inside `DirectoryScanner` | One consumer, so far |
| `Models/PhotoItem.cs` | `ScannedPhoto` (scan output) + `FilmstripItemViewModel` (UI state) | The single fat model in §5.1 was never built; scan facts and UI state have different lifetimes |
| `Benchmarks/` inside the app | `Fastcull.Benchmarks` console project | A benchmark harness inside an MSIX-packaged WinUI app cannot be run headlessly |
| `ZoomView.xaml`, `MetadataHud.xaml`, `FinishSessionViewModel.cs`, `UndoStack.cs`, `BatchProcessor.cs`, `XmpWriter.cs` | **Not built** | Zoom is a mode of `FilmstripView`, not its own view (§1.7). The rest are unbuilt features — §1.8, §1.9, §4 |

### 5.1 Model

> **Status, 2026-08-24: `PhotoItem` as written below was never built**, and the design below is
> retained as the target shape rather than a description of the code. What exists instead:
>
> - **`ScannedPhoto`** (`Fastcull.Core/Services/DirectoryScanner.cs`) — the scan's output, and a
>   strict subset: `FilePath`, `RelativePath`, `FileName`, `Family`, `FileBytes`, `SortTime`,
>   `SortTimeSource`, `CaptureSubsec`. It deliberately carries **no** rating, no companions and no
>   camera metadata.
> - **`CullState`** (`Fastcull.Core/Models/CullState.cs`) — replaces the loose `Flag` + `Stars`
>   pair with the single ordered ladder of §1.6. Invalid combinations throw from the constructor
>   rather than being silently normalised, which is how the invariants below are actually enforced.
> - **`Rotation`** (`Fastcull.Core/Models/Rotation.cs`) — §1.11's quarter-turn delta, which the
>   model below predates entirely.
> - **`FilmstripItemViewModel`** (WinUI project) — per-photo UI state: decode tiers, cull state,
>   rotation, pin and residency for §3.3.
>
> The split is deliberate: scan facts are immutable and cheap, UI state is mutable and expensive,
> and the metadata cache the fat model implies is not built (see `docs/BACKLOG.md`). `Companions`
> and every metadata field below are unimplemented — companion pairing is §1.4, still an open
> question, and the metadata fields belong to the unbuilt HUD (§1.8). `FullResIsCheap` exists only
> as the design below; nothing calls it, because the single embedded-preview path (§3.2) never
> branches on it yet.

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
- ~~`LibRawSharp` or `Magick.NET-Q16-HDRI-x64` (RAW only, spike before building on it)~~ — **neither is a dependency of the shipped code.** RAW decoding uses only WIC, via embedded-preview extraction (section 3.2). `LibRawSharp` was spiked and rejected: it ships no native binary (`DllNotFoundException: LibRawNative.dll`) and exposes no preview-extraction API. `Magick.NET-Q16-HDRI-x64` was never evaluated, because the embedded-preview path made it unnecessary. Either remains a future candidate only if section 3.4's debayer gap ever needs closing.
- `MetadataExtractor` (fast header parsing across RAW, JPEG, PNG, TIFF, HEIF without invoking a decoder)
- `CommunityToolkit.Mvvm`
- `System.Threading.Channels` (in-box)
- Test stack, test project only, not shipped: `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`

Licensing note: LibRaw is dual licensed LGPL 2.1 / CDDL 1.0 with a separate commercial option. Fine for personal use, needs revisiting if this is ever sold.

---

## 6. Delivery Phases

**v0.1 Walking skeleton.** Scan a folder, detect formats, group companions, resolve sort times, stream results into a filmstrip, display thumbnails and display-tier images, rate with `1`-`5` / `P` / `X`, persist to SQLite. No zoom, no HUD, no batch export. Ships with the perf harness and the first three budgets from 3.5 enforced.
*This phase proves the decode and prefetch pipeline, which is where every real risk lives. If the filmstrip is not snappy here, nothing later matters.*
*Scope note: implement JPEG and PNG first via WIC, then add the RAW path. WIC needs no external dependency, so slice 1 has no package risk.*
*Added scope: photo rotation (§1.11) was not part of v0.1 as originally written and has been added to it. This does not change the phase gate — v0.1 still requires the first three §3.5 budgets enforced, and the cache-hit budget has no cache to measure until §3.3 exists.*

*Gate status, measured 2026-08-24 (`docs/benchmarks/2026-08-24-ceiling-2gb.md`): **all three v0.1 gate budgets now pass.** First image 231 ms (< 1.5 s); full scan of 2,000 files 217 ms (< 8 s); next/previous cache hit 11.3 ms under a saturated decode pipeline and 10.1 µs for the decision alone (< 16 ms). §3.3 was built on 2026-08-23, which is what made the third one measurable for the first time — it had no cache to measure before. Peak working set, the other budget this phase's pipeline governs, went from 5.25 GB (failing) to comfortably inside 4 GB. **This note records the measurements only; whether the phase is done is a separate call and the description above is unchanged.***

**v0.2 Inspection.** 1:1 zoom with Tier A and Tier B paths, CPU debayer, dimension guard, metadata HUD, undo, Recycle Bin delete.

**v0.3 Output.** Finish Session modal, batch copy/move with collision handling, verification, cancel and logging.

**v0.4 Polish.** Session resume, pairing mode toggle, ultrawide layout tuning, key repeat coalescing, VRAM texture cache, full perf harness green.

**v0.5+ Optional.** GPU debayer if measurement shows RAW zoom is Tier B. XMP sidecar mode. Multi-body time offsets.

---

## 7. Risks

| Risk | Impact | Mitigation |
| :--- | :--- | :--- |
| ~~A7C II embedded preview is undersized~~ **Closed 2026-08-23** | Would have made every RAW zoom take the slow Tier B path | Checked, and thoroughly: 96 files across two bodies, 100% carry a full-sensor-width preview, so RAW zoom is Tier A (1.7). Residual risk is quality, not size, and is unmeasurable until zoom exists in v0.2 |
| No debayer exists if a Tier-B RAW file is ever encountered | A RAW file whose preview falls below the `FullResIsCheap` threshold has **no** full-resolution decode path at all, and would stay on the upscaled preview indefinitely | Accepted for now — the surveyed corpus has no such file. `FullResIsCheap` already routes correctly; only the destination is missing. Spike `Magick.NET-Q16-HDRI-x64` if one ever appears (3.4) |
| ~~RAW decoder NuGet package unmaintained or missing RAW delegate~~ **Closed 2026-08-23** | Blocked on the core format | Moot: no RAW library is a dependency. `RawPreviewDecoder` extracts the embedded JPEG using only in-box WIC (§3.2, §5.2) |
| WIC RAW codec absent on target machine | Cannot open RAW at all | ~~LibRaw primary for RAW~~ — **mitigation no longer accurate.** The shipped path does not need a WIC *RAW* codec at all: it slices an embedded JPEG out of the container and decodes that as ordinary JPEG. The residual risk is narrower — a RAW variant whose preview this scanner cannot locate |
| PNG and TIFF have no embedded thumbnail | Scan of a large PNG folder becomes a full decode storm | Generate once, cache as blob, downscaled decode only |
| Huge TIFF or stitched panorama | Out-of-memory crash | Dimension guard in 3.3, hard 512 MB decoded ceiling |
| `ItemsRepeater` horizontal virtualization with variable-width items | Filmstrip stutter, the one thing the app cannot afford | Fixed height, aspect-derived width, measured in v0.1 with 2,000 fixtures |
| ~~**EXIF orientation is applied for JPEG but not for RAW**~~ **Closed 2026-08-24** | A portrait JPEG displayed upright while a portrait RAW displayed sideways | Fixed. `ExifOrientation` reads the tag from the RAW *container* and passes it into the decode as an explicit override (§3.2). The earlier note that "the sample corpus is entirely landscape, orientation 1" was **wrong**: a full re-survey found nine portrait files tagged orientation 8 — six `.CR2`, three `.ARW` — all of which decoded landscape before the fix and portrait after |
| ~~Fixing RAW orientation later invalidates rotations already stored against the old baseline~~ **Closed 2026-08-24, no action needed** | A photo the user manually straightened would become double-corrected | The delta design in §1.11 absorbed it exactly as intended: baselines turned, deltas stayed correct, no migration and no extra column. The bounded set it worried about turned out to be empty in practice |
| NumLock ambiguity on numpad rating keys | Ratings silently do nothing | Explicit test case in v0.1 |
| Unbounded decode jobs on fast scrubbing | Thread pool starvation | Cancellation tokens, coalesced navigation, bounded worker pool |
| Scope creep past v0.3 | Never ships | Phase gates above |

---

## 8. Open Questions

1. ~~The five-minute exiftool check in section 1.7. Blocking.~~ **Resolved 2026-08-23** — answered by measurement rather than exiftool: 96 files, two bodies, 100% Tier A. See 1.7.
2. Confirm **Paired** as the default RAW+JPEG mode (section 1.4). Defaulted, not yet confirmed.