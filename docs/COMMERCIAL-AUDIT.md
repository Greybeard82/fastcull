# Commercial viability audit

**Date:** 2026-08-25
**Scope:** licensing and external-dependency review for selling FastCull as a commercial product.
**Status:** documentation only — no code was changed to produce this.

## How to read this

Findings are separated into three groups, because they are not the same kind of problem:

| Group | Meaning |
| :--- | :--- |
| **Blockers** | Shipping the product for money in its current state would breach a licence or a service policy. Must be fixed before any sale. |
| **Decisions** | Not a violation, but a choice that has to be made deliberately, with cost or capability consequences. |
| **Non-issues** | Checked and settled. Recorded so the same question is not reopened later. |

### What was verified, and how

- **Package inventory** is taken from `publish/FastCull-x64/Fastcull.deps.json` — the actual transitive closure of what ships, not the `PackageReference` lists. Those differ, and the difference matters (see XmpCore).
- **Nominatim's usage policy** was fetched from the live OSMF page during this audit rather than recalled.
- **Licence identifiers** for individual packages are from general knowledge of those projects **except** where a fetch is noted. They are marked with a confidence column, and anything below "high" needs confirming against the package's own `LICENSE` file before a sale. I have not read all twenty licence texts; saying otherwise would be false confidence.

---

# 1. Blockers

These would be violations. Two are cheap to fix, one is a design change.

> **Status update, 2026-08-25 — all three are now fixed, and D1 is decided.** Recorded here so this
> document does not go on describing a state that no longer exists. The findings below are left as
> written, each with what was done. See PRD §1.8.2 for the resulting contract.
>
> | | Was | Now |
> | :--- | :--- | :--- |
> | B1 | No attribution anywhere | `© OpenStreetMap contributors` in the sidebar Active Photo panel and the on-photo overlay, shown only when a resolved name is displayed |
> | B2 | `private const string Endpoint` | `geocodingEndpoint` in `settings.json`, defaulting to the public instance |
> | B3 | UA claimed `personal use` | `FastCull/0.1 (photo culling tool; +https://github.com/Greybeard82/fastcull)` |
> | D1 | On by default | **Off** by default, `geocodingEnabled` to turn it on |
>
> The remaining open items are unchanged: **D2** (Windows App SDK terms) and **D3** (XmpCore's
> Adobe licence) still want a professional read, and **D4** (template icons) is still a branding
> decision.

## B1 — No OpenStreetMap attribution anywhere in the product

The app displays place names derived from OSM data (§1.8.2's info overlay and the sidebar's Active Photo panel). OSM data is licensed **ODbL 1.0**, and Nominatim's usage policy requires: *"Clearly display attribution as suitable for your medium."*

A search of every `.xaml` and `.cs` file found **no attribution string anywhere in the UI** — no "© OpenStreetMap contributors", no credit in an about box (there is no about box).

- **Severity:** genuine licence breach for a distributed product.
- **Fix cost:** very low. A credit line wherever the place name is shown, or in an about/credits surface.
- **Note:** this obligation survives switching to most other providers — commercial geocoders built on OSM carry the same requirement, and non-OSM providers impose their own.

## B2 — The endpoint is hard-coded, which the policy explicitly forbids

Nominatim's policy states that applications *"must make sure that they can switch the service at our request at any time (in particular, switching should be possible without requiring a software update)."*

`NominatimPlaceResolver` has:

```csharp
private const string Endpoint = "https://nominatim.openstreetmap.org/reverse";
```

A `const` compiled into the assembly. If OSMF asked FastCull to stop using the public instance, every installed copy would keep calling it until users installed a new build. That is precisely the situation the clause exists to prevent, and a shipped desktop app is the worst case for it — there is no server-side switch to throw.

- **Severity:** direct non-compliance with a stated requirement of the service being used.
- **Fix cost:** low. The endpoint needs to be configurable at runtime — settings file, remote config, or both — before the app is distributed at scale.

## B3 — The product identifies itself to the service as "personal use"

```csharp
_http.DefaultRequestHeaders.Add("User-Agent", "FastCull/0.1 (photo culling tool; personal use)");
```

The policy requires a User-Agent that identifies the application. FastCull sends one, which is correct as far as it goes — but the string asserts **personal use**. Selling the product makes that statement false, and it is made to the operator of a free service whose access decisions depend on it.

The policy also asks for a way to be contacted; the current string offers none (no URL, no email).

- **Severity:** misrepresentation to a service operator. Not a copyright violation, but not something to ship knowingly.
- **Fix cost:** trivial — one string, plus a contact URL.

---

# 2. Decisions needed

## D1 — Whether to keep the public Nominatim instance at all

**This is the substantive question, and the answer is less clear-cut than expected.**

What the policy actually says, verified against the live page during this audit:

| Point | Text |
| :--- | :--- |
| Commercial use | **Not broadly prohibited.** The restriction is narrower: *"Applications and services whose primary function is related to geocoding must run their own service"* — naming package-tracking apps and API resellers. |
| Rate limit | *"1 request per second"* absolute maximum; 4/minute for bulk. |
| Distribution | Bulk-geocoding restrictions include *"no distributed scripts"* and require results to be *"cached on your side"*. |
| Alternatives | The policy points at *"commercial third-party providers"* and at self-hosting. |

**The original PRD note is only half right.** It says Nominatim was chosen for having *"clear terms for low-volume personal use"* — and you were right to suspect that is not the same as commercial permission. But the policy does **not** ban commercial use outright, and FastCull is not a geocoding product: geocoding is one optional metadata field in a photo-culling tool. On the "primary function" test, FastCull passes.

What genuinely does not sit well:

1. **Aggregate load.** The 1 req/s limit is enforced correctly *per running client* (`MinimumInterval`, plus the rounded-coordinate cache). But a sold product means N installations, and the policy's *"no distributed scripts"* language and general fair-use framing are aimed at exactly that shape. One user is fine; ten thousand users each politely making 1 req/s is not what a free community service is for.
2. **B2 above** must be fixed regardless of which way this decision goes.

### Options

| Option | Cost | Consequence |
| :--- | :--- | :--- |
| **Keep public Nominatim**, fix B1–B3 | Lowest | Compliant on paper for a low-volume product; exposed to being cut off as the user base grows, and leaning on a donated service for a paid product is a reputational judgement as much as a legal one |
| **Self-host Nominatim** | High — a planet-wide instance is a serious server (hundreds of GB, ongoing ops) | Full control, no third-party terms, no per-request cost. Disproportionate for one optional metadata field |
| **Commercial provider** (keyed) | Per-request cost, plus either shipping a key or asking users for one | Clear commercial terms; reintroduces exactly the problem Nominatim was chosen to avoid — a secret in the binary, or a setup step before a field works |
| **User-supplied key, feature off by default** | Low | Legally clean, zero cost, but the feature is invisible to most users |
| **Remove geocoding** | Low | Removes the whole question, and the app's only network call. GPS coordinates would still display via the existing offline `GeoFormat` fallback |

### Recommendation

**Make the endpoint and User-Agent configurable (B2/B3), add attribution (B1), and ship with geocoding defaulting to OFF and a one-line explanation.**

Reasoning: the feature is genuinely marginal — it fills one optional field, and the app already degrades gracefully to raw coordinates when it is unavailable, by design. Defaulting off means a sold product places no aggregate load on a donated service by default, which resolves the only real objection. Users who want it can turn it on; a future commercial provider can be dropped in without a code change once B2 is done, and if the feature ever justifies a per-request bill, the plumbing is already there.

The alternative worth considering if geocoding turns out to matter more than expected: a keyed commercial provider with the key user-supplied, entered once in settings.

## D2 — Windows App SDK and friends are proprietary, not open source

Fourteen of the twenty shipped packages are Microsoft's, under **Microsoft Software Licence Terms**, not an OSI licence. Redistributing the runtime inside a self-contained app is the documented purpose of `WindowsAppSDKSelfContained`, so this is very likely fine — but "very likely" is doing real work in that sentence, and I have not read the terms.

**Needs a lawyer's look before sale**, specifically on redistribution rights for the self-contained runtime in a *paid* product.

Also worth noting: **WebView2, the AI/ML packages, Widgets and Search all ship but are unused.** They arrive transitively via `Microsoft.WindowsAppSDK` and account for a large share of the 278 shipped DLLs. They are dead weight, and every one is additional licence surface for functionality the product does not have. Worth investigating whether the dependency can be trimmed.

## D3 — XmpCore is under a custom Adobe licence

`XmpCore 6.1.10.1` ships (`XmpCore.dll` is in the build) as a transitive dependency of `MetadataExtractor`. **It is not in PRD §5.2's dependency list** — it arrived without being chosen.

nuget.org publishes it under the **"Adobe XMP Library License"**, pointing at Adobe's EULA for the XMP Library rather than an SPDX identifier. That is not a permissive OSI licence by name, and I could not resolve it to one.

- **Action:** read the linked Adobe EULA before sale. The Adobe XMP Toolkit has historically been BSD-like in substance, so this may well be fine — but a custom EULA behind a link is exactly the case where guessing is inappropriate.
- **Escape hatch if it is a problem:** XmpCore is only reachable through MetadataExtractor's XMP support. If FastCull does not need XMP parsing, check whether the dependency can be dropped.

## D4 — Application icons are Microsoft's template placeholders

`Assets/` contains the seven stock PNGs generated by the WinUI project template (`Square150x150Logo`, `StoreLogo`, `SplashScreen`, etc.), untouched since 2026-08-22.

Not a licence violation — template output is yours to use — but shipping a paid product wearing the default template logo is a branding decision that has already been made by accident. Store submission would also expect real artwork.

---

# 3. Non-issues — checked and settled

## N1 — LibRaw: definitively not present. The PRD's warning is stale.

The PRD §5.2 note — *"LibRaw is dual licensed LGPL 2.1 / CDDL 1.0 … needs revisiting if this is ever sold"* — **no longer applies to anything.** Four independent checks:

1. **No `PackageReference`** to LibRawSharp, Magick.NET, or any RAW library in any of the four `.csproj` files.
2. **Not in the shipped closure.** All 20 packages in `Fastcull.deps.json` are listed below; none is a RAW decoder.
3. **No native binary.** Of 278 DLLs in the portable build, none matches `libraw`, `dcraw`, `magick` or `exiv`.
4. **The code does not use one.** `RawPreviewDecoder` imports only `Windows.Graphics.Imaging` and `System.IO`: it scans the container for embedded JPEG markers and hands the slice to WIC. There is no debayer.

**Conclusion: there is no LGPL, CDDL or GPL code anywhere in the shipped product.** The licensing concern is void, and PRD §5.2's note should be struck so it stops raising a settled question. *(Recommend as a follow-up edit — not made here, since this task was documentation-only for the audit itself.)*

The only residual link is a documentation one: PRD's header still describes the stack as *"WIC + LibRaw dual decoder"*, which has been inaccurate since the embedded-preview work.

## N2 — No fonts are redistributed

`Themes/Nocturne.xaml` declares `Inter, Segoe UI Variable Display, Segoe UI` and the icon glyphs use `Segoe Fluent Icons, Segoe MDL2 Assets`. These are **font-family names, resolved at runtime** — a search found **no `.ttf`, `.otf` or `.woff` file anywhere in the repository**.

- Nothing is redistributed, so there is no font licensing exposure at all.
- Inter is not installed on a stock Windows machine, so in practice the app renders in Segoe UI Variable — which ships with Windows and is licensed for use on it.
- **Two things to avoid later:** never bundle the Segoe fonts (redistribution is not permitted outside Windows), and if Inter is ever bundled, SIL OFL 1.1 permits it but attaches conditions (cannot be sold on its own; reserved-name rules if modified).

## N3 — Only one outbound network call exists

A search of every `.cs` and `.xaml` file for `http(s)://` found exactly one non-schema URL: the Nominatim endpoint. There is **no telemetry, no analytics, no crash reporting, no update check, no licence server**.

Good for the audit (nothing else to review) and worth knowing deliberately: a commercial product usually *wants* some of those, and each would be a new privacy and terms question.

## N4 — SQLite is public domain

`e_sqlite3.dll` ships. SQLite itself is public domain; the wrapper packages are Apache-2.0. No obligations.

---

# 4. Full shipped dependency inventory

Twenty packages, from the deps file of the portable build. Confidence reflects how well I can stand behind the licence identifier **without having read each licence text**.

| Package | Version | Licence | Confidence | Note |
| :--- | :--- | :--- | :--- | :--- |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | High | .NET Foundation |
| MetadataExtractor | 2.9.3 | Apache-2.0 | Medium-high | Verify against the package's LICENSE |
| **XmpCore** | 6.1.10.1 | **Adobe XMP Library License** | **Verified via nuget.org** | **See D3** — custom EULA, transitive, undeclared in PRD §5.2 |
| Microsoft.Data.Sqlite | 9.0.0 | MIT | High | |
| Microsoft.Data.Sqlite.Core | 9.0.0 | MIT | High | |
| SQLitePCLRaw.bundle_e_sqlite3 | 2.1.10 | Apache-2.0 | Medium-high | Bundles SQLite (public domain) |
| SQLitePCLRaw.core | 2.1.10 | Apache-2.0 | Medium-high | |
| SQLitePCLRaw.lib.e_sqlite3 | 2.1.10 | Apache-2.0 | Medium-high | |
| SQLitePCLRaw.provider.e_sqlite3 | 2.1.10 | Apache-2.0 | Medium-high | |
| System.Numerics.Tensors | 9.0.0 | MIT | High | Unused by FastCull; transitive |
| Microsoft.WindowsAppSDK | 2.4.0 | Microsoft Software Licence Terms | Medium | **Proprietary — see D2** |
| Microsoft.WindowsAppSDK.Foundation | 2.3.9 | Microsoft | Medium | |
| Microsoft.WindowsAppSDK.WinUI | 2.3.6 | Microsoft | Medium | |
| Microsoft.WindowsAppSDK.InteractiveExperiences | 2.1.6 | Microsoft | Medium | |
| Microsoft.WindowsAppSDK.AI | 2.4.4 | Microsoft | Medium | Unused |
| Microsoft.WindowsAppSDK.ML | 2.1.74 | Microsoft | Medium | Unused |
| Microsoft.WindowsAppSDK.Search | 2.4.4 | Microsoft | Medium | Unused |
| Microsoft.WindowsAppSDK.Widgets | 2.0.5 | Microsoft | Medium | Unused |
| Microsoft.Windows.AI.MachineLearning | 2.1.74 | Microsoft | Medium | Unused |
| Microsoft.Web.WebView2 | 1.0.3719.77 | Microsoft (WebView2 SDK terms) | Medium | Unused; ships `WebView2Loader.dll` |

**Build-time only, not redistributed:** `Microsoft.Windows.SDK.BuildTools` 10.0.28000.2526 — absent from the shipped closure, as expected.

**Test-only, not redistributed:** `xunit` 2.9.2 (Apache-2.0), `xunit.runner.visualstudio` 2.8.2 (Apache-2.0), `Microsoft.NET.Test.Sdk` 17.11.1 (MIT). Test dependencies do not ship, so they carry no distribution obligation.

---

# 5. Summary

**Nothing found here prevents selling the product**, but three things must be fixed first and two need a professional read.

| | Item | Effort |
| :--- | :--- | :--- |
| **Fix before sale** | B1 — OSM attribution | Very low |
| | B2 — configurable endpoint | Low |
| | B3 — honest User-Agent + contact | Trivial |
| **Decide** | D1 — geocoding provider *(recommend: configurable + default off)* | Low |
| | D4 — replace template icons | Low |
| **Have a lawyer read** | D2 — Windows App SDK redistribution in a paid product | — |
| | D3 — Adobe XMP Library License | — |
| **Settled, no action** | N1 LibRaw · N2 fonts · N3 network · N4 SQLite | — |

**The headline result on the original question: the LibRaw concern is dead.** There is no copyleft code in the shipped product. The licensing risk that actually exists is somewhere the PRD never flagged — a transitive Adobe EULA nobody chose, and a geocoding service whose terms are about *load and switchability* rather than commerce.

## Explicit limits of this audit

- I have **not read the licence text** of every package. Identifiers for the Medium-confidence rows are from general knowledge of those projects and should be confirmed against each package's own `LICENSE` before money changes hands.
- I am **not a lawyer**, and D2 and D3 in particular are judgement calls about proprietary licence terms that warrant a professional opinion.
- The Nominatim policy was read on **2026-08-25**. It can change; re-check before release.
- This covers **licensing and external dependencies only**. It is not a review of consumer law, warranty, refund obligations, export control, GDPR/privacy (relevant if telemetry is ever added), or trademark clearance on the name "FastCull" — which has not been searched at all.
