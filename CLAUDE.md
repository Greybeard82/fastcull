# FastCull

Read `docs/PRD.md` before any task. It is the source of truth.
`docs/BACKLOG.md` tracks what is real but deliberately unbuilt.

## Non-negotiable constraints

- Prime directive: the app must feel instant. See PRD section 0.
- The UI thread does no I/O, no decoding, no DB writes, no metadata parsing. Ever.
- Every decode job takes a `CancellationToken`.
- **Eviction and teardown never call `Dispose()` on a `SoftwareBitmap` that has been handed to
  `SoftwareBitmapSource.SetBitmapAsync`.** XAML copies those pixels into a composition surface on
  its own worker thread, and destroying one mid-copy fail-fasts the process with `0xC000027B`.
  Dropping the last managed reference is the safe equivalent. This project has already paid for
  that bug once.
- PRD 1.10 is absolute: every non-photograph pixel is exactly `#FF000000`, in every visual state,
  hover and press included.
- Target framework is .NET 9.0, WinUI 3 (Windows App SDK), x64.

## What is built (2026-08-24)

Implemented: folder scan and sort, the 8-rung cull ladder, SQLite persistence with resume,
the Chromeless stage with variable 3–9 slots, the bottom filmstrip, manual rotation (A/S,
persisted), the left sidebar (auto-hide/pin, live tallies, format breakdown, folder tree),
PRD 3.3's prefetch window + LRU cache + dimension guard, and a **reduced-scope zoom**
(fullscreen fit-to-stage; not the 1:1 Tier A/B inspection PRD 1.7 specifies).

**Not implemented — do not assume these exist:** metadata HUD (PRD 1.8), undo (PRD 1.9),
batch export / Finish Session (PRD 4), true RAW debayering, panning at 1:1, streaming the
scan into the UI (PRD 1.2 — the scanner streams, the consumer does not).

Ask before starting any of those; several are explicitly later phases in PRD 6.

## Architecture facts worth knowing before you edit

- `Fastcull.Core` holds everything testable and references **no** WinUI. The WinUI project cannot
  be referenced from a test project, so logic that needs tests goes in Core. This is why
  `ICacheableItem` exists.
- RAW is decoded by **embedded-JPEG extraction** (`RawPreviewDecoder`), not by debayering.
  There is no LibRaw and no `IImageDecoder` abstraction. See PRD 3.2 and PRD 5.
- `ItemsRepeater` does **not** propagate `DataContext` into its templates. Bind the item to `Tag`
  via `{x:Bind}` and read that in handlers.
- A WinUI panel with no `Background` is not hit-testable — `Transparent` is load-bearing wherever
  pointer events matter.

## Working rules

- One task at a time. Build must pass (`dotnet build -p:Platform=x64`) before moving to the next.
  The `-p:Platform=x64` is required, not optional: a bare `dotnet build` defaults Platform to
  AnyCPU and fails with "Packaged .NET applications with an app host exe cannot be
  ProcessorArchitecture neutral".
- After each task: confirm the build succeeds, then commit with a clear message.
- Stage commits **by explicit path**. Never `git add -A`.
- Do not add NuGet packages not listed in PRD section 5.2 without asking first.
- Do not refactor files outside the current task's scope.
- Verify, don't assume. A green test run is not evidence the app builds or runs — the Core and
  test projects do not depend on the WinUI project, so both can pass while the app is broken.
