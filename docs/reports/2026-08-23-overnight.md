# Overnight run report — 2026-08-23

Work order: `docs/tasks/2026-08-23-v01-overnight.md`
Run: unattended, overnight. Everything below was executed and verified in this session
unless explicitly marked otherwise.

**Headline: all nine tasks are done, and the crash is fixed with hard evidence — the exact
failing call was identified from the dump and the fix went from 6/6 crashes to 0/12.**

---

## Task-by-task status

| Task | Status | Commit |
| :--- | :--- | :--- |
| A — Amend the PRD | **Done** | `c43ffec` |
| B — The crash | **Done, root-caused and fixed** | `b8c6eec` |
| C — Window-level keyboard routing | **Done** | `1d357e9` |
| D — The rating ladder | **Done** | `a93ef47` |
| E — The three-slot window | **Done** | `a93ef47` |
| F — OLED black theme | **Done** | `caf35c2` |
| G — Test project | **Done** — 134 tests, 0 failed, 0 skipped | `2e53dde` |
| H — SQLite persistence | **Done** | `74edf75` |
| I — Final verification and report | **Done** | this file |

Nothing was blocked or skipped.

D and E are in one commit. They rewrite the same members of `MainViewModel` and the same
block of `FilmstripView.xaml`; splitting them would have produced an intermediate commit
that does not build, which the ground rules forbid. Every other task is its own commit.

---

## The crash — solved

**Exit code `0xC000027B` / `STATUS_STOWED_EXCEPTION`, root cause found, fix verified.**

WinDbg installed via `winget install Microsoft.WinDbg` (the Store build — note it ships
`cdb.exe` inside the MSIX at
`C:\Program Files\WindowsApps\Microsoft.WinDbg_1.2606.22001.0_x64__8wekyb3d8bbwe\amd64\cdb.exe`,
*not* under `Windows Kits\10\Debuggers\x64`, which contains only redistributable DLLs).

`.exr -1` / `.ecxr` / `k` against `C:\dumps\Fastcull.exe_260823_034244.dmp` gave the native
stack, which is the whole answer:

```
KERNELBASE!RaiseFailFastException+0x188
combase!RoFailFastWithErrorContextInternal2+0x4a9
Microsoft_UI_Xaml!FailFastWithStowedExceptions+0x61
Microsoft_UI_Xaml!AsyncCopyToSurfaceTask::CopyOperation+0x1d2198   <-- the failing call
Microsoft_UI_Xaml!AsyncCopyToSurfaceTask::Execute+0x92
Microsoft_UI_Xaml!AsyncImageFactory::WorkCallback+0x17
Microsoft_UI_Xaml!CWinWorkItem::WorkCallback+0x1b
ntdll!TppWorkpExecuteCallback+0x4d0
ntdll!TppWorkerThread+0x801
```

cdb also reported, for the faulting thread `0x91d8`:
`ClrmaThread::Initialize FAILED managed thread not found`. The faulting thread is one of
XAML's own native image workers — not a managed thread at all. That is why no managed
exception existed anywhere in the dump and why `FirstChanceException` never saw it.

**Mechanism.** Assigning a `SoftwareBitmapSource` to an `Image` starts an *asynchronous*
copy of the pixels into a composition surface, which XAML runs on that worker. The enqueued
callback in `ApplyDecodeResultAsync` did `finally { bitmap.Dispose(); }` — destroying the
`SoftwareBitmap` the instant `setImage` returned, racing the copy. When the copy lost the
race it got a failing HRESULT, which was stowed and fail-fasted the process.

**Fix.** The bitmap's lifetime belongs to the `SoftwareBitmapSource` once `SetBitmapAsync`
succeeds; it is reclaimed by the GC. Disposal is kept only on the path where
`SetBitmapAsync` itself threw and the source never took ownership. (PRD 3.3's LRU cache
will take over lifetime management later.)

**Evidence for the fix, not just the diagnosis.** A scripted sequence — click the third top
slot, click the third thumbnail, then rapid-click thumbnails 0–6 — reproduced the crash
**6 out of 6 runs** before the change and **0 out of 12 runs** after it.

I could not read the HRESULT *value* itself. `!analyze -v` did not decode the stowed
exception, and `!pde.dse` needs the PDE extension, which is not bundled with WinDbg and
which I did not install. **The specific HRESULT is unverified.** It did not turn out to
matter: the native stack named the failing call unambiguously, and the fix is confirmed by
the 6/6 → 0/12 result.

`RPC_E_WRONG_THREAD` stays refuted, as the work order said. Instrumentation reconfirmed
`SoftwareBitmapSource` construction and `SetBitmapAsync` run on the UI thread (tid=2, STA).

### ProcDump

I used the **`-w` (wait) form**, not `-i`. **No `procdump -u` is required** — nothing
machine-wide was registered. Verified after the run: no procdump process is alive, and
`HKLM\...\Windows Error Reporting\LocalDumps\Fastcull.exe` does not exist.

---

## Test count

**134 tests, 0 failed, 0 skipped**, running headless via `dotnet test`.

The work order preferred referencing the WinUI project from the test project. That does not
work — building `Fastcull.csproj` as a test dependency fails in the MSIX packaging targets
with *"Packaged .NET applications with an app host exe cannot be ProcessorArchitecture
neutral"*. **I took the documented fallback** and extracted the pure types into a new
`Fastcull.Core` project (no WinUI, no MSIX): `InputRouter`, `CullState`, `FilmstripWindow`,
`DirectoryScanner`, and later `SessionStore`. Namespaces are unchanged, so nothing else
moved. `Fastcull` and `Fastcull.Tests` both reference Core.

Coverage: every row of every `InputRouter` table including all three NumLock collisions; the
full eight-rung ladder with both clamps and every invariant; every row of the `FilmstripWindow`
table plus degenerate counts and out-of-range indices; `DirectoryScanner` smoke tests over
the real corpus; and nine `SessionStore` tests.

---

## What I could **not** verify

**Keyboard input — the big one.** Keyboard injection does not reach the app in this sandbox.
I confirmed this is an environment limit and not an app bug: with the window foreground and
`FilmstripView` holding focus (confirmed via `FocusManager.GetFocusedElement`), *no* key
reached the handler — not `A`, `P`, `Space`, or the arrows — while mouse injection worked
fine in the same run.

So the entire keyboard path is verified **only** by unit tests plus code inspection:

- `InputRouter.Resolve` is exhaustively unit-tested. I have high confidence in the resolution
  logic itself.
- **The XAML wiring is untested.** That `PreviewKeyDown` on `MainWindow`'s root Grid actually
  fires, tunnels ahead of the `ScrollViewer`, and that `e.Handled = true` really stops the
  filmstrip scrolling — none of that is verified. It is the thin layer the work order
  anticipated would stay untested, but please treat it as unproven.
- I drove the `AppCommand` handlers directly via a temporary hook to prove the *command*
  path renders correctly. That hook has been removed (verified by grep before the final
  commit).

**The HRESULT value**, as described above.

**Packaged vs unpackaged.** I test unpackaged (`-p:WindowsPackageType=None`); you run
packaged. The title-bar work (`ExtendsContentIntoTitleBar`, `AppWindow.TitleBar` colours) is
the most likely place packaged behaviour could differ.

---

## Verified visually (screenshots taken and inspected)

- **OLED black:** 12 of 12 sampled chrome regions read exactly `(0,0,0)` — title bar at three
  points, window background, both inter-slot gaps, letterboxing inside a slot, an empty CR2
  slot, filmstrip band above and below the cards, an inter-card gap, bottom-left corner.
  One earlier sample read `(79,79,79)`; probing showed it had landed on the anti-aliased
  filename text of a partially-visible thumbnail — content, not chrome. Re-sampled against
  genuinely empty chrome it is black. Recording that so the 12/12 is not mistaken for a
  first-try result.
- **Three-slot window, all three boundary cases:** first photo → active marker on the **left**
  slot; a mid-sequence photo → **centre** slot showing photos 2/3/4; last photo → **right**
  slot showing the final three.
- **All eight ladder states**, driven through the real `AppCommand` dispatch: red / yellow /
  green state borders, badges `1`–`5` bottom-right on dark chips, and the blue active ring
  drawn concentrically *outside* the state border (clearly visible on the rejected item,
  where a red inner ring sits inside a blue outer ring).
- **Persistence end-to-end:** seeded eight ladder states, closed the app to flush, **removed
  the seeding hook**, rebuilt, and relaunched — all eight states came back identically from
  SQLite. The `.db` gained `-wal`/`-shm` sidecars while running and checkpointed cleanly on
  close, confirming WAL is live.
- Clicking a top slot or a bottom thumbnail changes the active photo.

---

## Things I changed that you did not ask for

1. **`Fastcull.Core` project.** Forced by the test-project constraint; it is the fallback the
   work order sanctioned. Also required adding glob exclusions to `Fastcull.csproj`, since
   both sibling projects sit under its directory and would otherwise be swept into its
   compile.
2. **Deleted `Converters/BoolToAccentBrushConverter.cs`.** The concentric-ring design needs
   blue-or-transparent rather than accent-or-card-stroke. This also removed the unguarded
   `ResourceDictionary` lookup an earlier round suspected of causing the crash.
3. **`session_meta` table.** The PRD 3.1 schema has nowhere to record which folder a session
   belongs to, which H.3 requires in order to match a session to a scan root on relaunch.
   The `photos` and `companions` tables are verbatim from the PRD. **The PRD should be
   updated to include `session_meta`.**
4. **`Fastcull.Tests` and `Fastcull.Core` added to `Fastcull.sln`.**
5. **`MainWindow.Closed` handler** to flush pending writes on exit — implied by H.2's "a
   rating made 100 ms before close must not be lost", but not spelled out.
6. **Persistence failures degrade to an in-memory session** rather than propagating. A locked
   or corrupt session DB must never stop the app opening.

---

## Open questions I resolved by judgement — please correct me

1. **`AsPicked()` from rungs 3–7 is a no-op that keeps stars.** PRD 2.1 says "no-op if
   already 3–7", which I read as preserving the star count rather than dropping to plain
   Picked. Tested that way.
2. **`SetStars(0)` keeps the current flag even when Rejected.** PRD 2.1 says "keep the
   current flag" without carving out Rejected, so `0` on a rejected photo leaves it rejected.
3. **Grey `PageUp`/`PageDown` are unmapped in filmstrip mode.** PRD 2.1 does not bind them;
   only the *non-extended* `PageDown` (numpad 3) means anything. PRD 2.2 binds them in zoom
   mode, which is out of scope tonight.
4. **State-border hues are the Windows-standard ones** the work order suggested
   (`#FFE81123` / `#FFFFB900` / `#FF10893E`). They read well on pure black; I did not adjust
   them. The active ring is `#FF0078D4`.
5. **Filename/format text moved to the top-left of each slot.** It was previously centred
   over the photo, which collided with the image. Not asked for; say the word and I will
   revert it.
6. **Slot clicks use a `Tag`-based handler**, and thumbnail clicks resolve through
   `ItemsRepeater.GetElementIndex`, because `ItemsRepeater` does not propagate `DataContext`
   to realized elements under `x:Bind` (that was the latent bug fixed in `e430adc`).

---

## Manual checklist for the morning

The keyboard is the part I could not test. Please run through this:

**Navigation**
- [ ] `Left` / `Right` move between photos, one step per press.
- [ ] `Home` / `End` jump to the first / last photo.
- [ ] Arrows **never** scroll the bottom filmstrip (PRD 2.4) — the single most likely thing
      to be wrong, since the wiring is untested.
- [ ] Arrows still work after clicking a bottom thumbnail (focus previously moved into the
      `ScrollViewer` and swallowed them — that is the bug Task C set out to fix).
- [ ] Holding an arrow does not crash or corrupt state (repeat coalescing is v0.4, so
      expect it to feel fast, not smooth).

**The ladder — `Up` / `Down`**
- [ ] From Unrated: `Up` → Picked → 1 → 2 → 3 → 4 → 5 stars.
- [ ] `Up` at 5 stars does nothing (clamp).
- [ ] `Down` walks back: 5 → 4 → 3 → 2 → 1 → Picked → Unrated → Rejected.
- [ ] `Down` at Rejected does nothing (clamp).
- [ ] Border colour tracks: red Rejected, yellow Unrated, green Picked.
- [ ] Star badge appears only at ≥ 1 star, bottom-right.
- [ ] Rating never moves the cursor; navigating never changes a rating.

**Direct keys**
- [ ] `1`–`5` set stars directly and imply Picked (green border appears).
- [ ] `0` clears stars and **keeps** the current flag.
- [ ] `P` picks; from 1–5 stars it is a no-op and keeps the stars.
- [ ] `X` rejects and clears stars.
- [ ] `U` unflags and clears stars.

**The NumLock case — PRD 1.6's silent-failure test**
- [ ] With **NumLock OFF**, numpad `2` sets 2 stars (it must *not* step the ladder down).
- [ ] With **NumLock OFF**, numpad `4` sets 4 stars (it must *not* navigate left).
- [ ] With **NumLock OFF**, numpad `1` sets 1 star (it must *not* jump to the last photo).
- [ ] With **NumLock ON**, numpad `1`–`5` still set stars.
- [ ] The real arrow keys and `Home`/`End` still navigate in both NumLock states.

**Persistence**
- [ ] Rate several photos, close the app, reopen it — the ratings come back.
- [ ] Rate a photo and close within ~100 ms — that rating survives too.

**General**
- [ ] Rapid clicking around the filmstrip no longer closes the app silently. (Fixed and
      verified 0/12 here, but you have hit it in ways my scripted repro may not cover.)
- [ ] Chrome is pure black on your OLED panel, title bar included.

---

## Out of scope tonight, as instructed

Zoom, metadata HUD, undo/redo, Recycle Bin delete, Finish Session, session-resume prompt,
key-repeat coalescing, LibRaw/RAW decode, the perf harness, RAW+JPEG pairing. **RAW files
stay blank in the filmstrip — that is expected and correct.**

---

## Housekeeping

- `docs/tasks/2026-08-23-v01-overnight.md` is still **untracked**. It is your file, so I left
  it alone rather than committing it on your behalf.
- Every commit was staged by explicit path. The CRLF/LF churn in `.gitignore`, `Fastcull.sln`,
  `Properties/*`, `app.manifest` and `Package.appxmanifest` is untouched and uncommitted.
  `MainWindow.xaml`, `MainWindow.xaml.cs`, `Fastcull.csproj` and `docs/PRD.md` were edited for
  real reasons and committed with real diffs.
- Build warnings: 24 occurrences, all `MVVMTK0045` — the pre-existing CommunityToolkit
  advisory about field-backed `[ObservableProperty]` versus partial properties. **No new
  warning category was introduced**, but the count grew because Tasks D and E added
  observable properties. The partial-property form the analyzer suggests does not generate
  correctly in this WinUI build pipeline (its source generator emits nothing, so every
  property fails to compile), which is why the field form is still in use.
