# Contributing to S1Utility

Thanks for considering a contribution. This document captures the conventions and gotchas that aren't obvious from the code.

---

## Getting started

### Build

```sh
dotnet build
```

### Run

```sh
dotnet run --project S1Utility/S1Utility.csproj
```

### Test

```sh
dotnet test
```

The test project (`S1Utility.Core.Tests`) covers `S1Utility.Core` only — the UI and MIDI I/O are intentionally not tested. Fixtures are inlined in test files rather than loaded from external `.PRM` files (the `BackupPatches/` folder is not in the repo).

---

## Project layout

- `S1Utility.Core/` - pure C# domain model: parameters, parsing, presets. No Avalonia or MIDI I/O dependencies. Fully unit-tested.
- `S1Utility/` - Avalonia desktop UI. Hosts the MIDI transport and the editor view.
- `S1Utility.Core.Tests/` - xUnit tests for the Core project.

Within `S1Utility/`:

- `MainWindow.axaml` + `MainWindow.*.cs` partials - the editor shell, split across `Builders`, `Settings`, `Sequencer`, `DirtyTracking`, `UndoRedo`, and the main partial.
- `Panels/` - section-card builders (Effects, Voice, Sequencer card, OSC Draw/Chop cards).
- `Widgets/` - shared factories (knobs, LEDs, dashboards).
- `Palette.cs` - XAML brush lookup helper.
- `WindowsAspectRatioPolicy.cs` - Win32-specific window resize behaviour, isolated behind `IWindowSizePolicy`.

---

## Hard constraints: do not change without hardware re-verification

These values were established through direct hardware testing. They aren't first-principles derivable. Changing them without re-verifying against a real S-1 will silently break the editor's accuracy.

### CC encodings

| Parameter | Rule                                                                                                              |
| --- |-------------------------------------------------------------------------------------------------------------------|
| CC103 Chop Overtone display | `Math.Min(200, (int)Math.Round(val * 255.0 / 127))` - denominator is **255**, not 200                             |
| CC102 / CC104 PRM min | Both use `P(cc, 7, 255, 3)` - PRM min is **7**, not 0                                                             |
| LFO sync (CC106 = 1 mode) | CC 0–30 (31 values, 0-indexed); divisor = 30                                                                      |
| Delay sync (CC89) | CC 0–15 (16 values, 0-indexed); divisor = 15                                                                      |
| CC76 OSC Fine Tune | Display: `(val - 64).ToString()`                                                                                  |
| CC77 Transpose | Display: `FormatSemitone(val - 64)`, range −12..+12 semitones (CC 52..76)                                         |
| CC64 HOLD | Excluded from bulk send - must never override hardware pedal state                                                |
| Draw wave encoding | 16-bit unsigned int per value, lo byte first, hi byte second; each byte `v > 127 ? v - 256 : v`; scale −100..+100 |

### ADSR timing constants (measured from hardware recordings)

`ModEnvTime(int cc, double maxSecs)` uses a square-law taper: `0.001 + (cc / 127)² * maxSecs`.

| Segment | Max seconds |
| --- | --- |
| Attack | 3.570 |
| Decay | 15.000 |
| Release | 19.500 |

Do not "round" these to nicer values - they reflect measured hardware behaviour.

### Filter curve shape

- The resonance spike peak is at `xC` exactly.
- The rolloff bezier (segment ③) starts at `(xC, peakY)` - there is no return-to-passY segment between the spike and the rolloff.
- The rolloff is convex: `CP1` at `(xC + kSlope * 0.68, passY)`, `CP2` at `(xC + kSlope * 0.90, flatBot - 6)`.
- Passband droops with resonance: `passY = flatTop + rN * (flatBot - flatTop) * 0.18`.

### CC103 / Chop grid interaction (ApplyInitPatch only)

When `ApplyInitPatch()` runs and the init PRM has non-default Chop Grid data, `CC103` must be explicitly sent as 0. Factory init patches have it at 100 because Chop Overtone has no audible effect if the pattern's Chop Grid is unmodified. But since the user might trigger an Init patch on a slot with modified grid, forcing CC103 to zero will give them authentically sound init patch for the slot. **Do not apply this correction in `ApplyPrmData()`**, it would break Pattern Sync for patches where Chop is active.

---

## Architectural rules

- **Tab 2 (Patch Inspector) is read-only.** It displays PRM data; it does not edit it. Two reasons: most PRM-only parameters have no MIDI CC path, and writing PRM files risks silently corrupting the user's patch backups.
- **The app never writes `.PRM` files.** No exceptions.
- **No sysex.** The S-1 does not use sysex for parameter control.
- **CC64 (HOLD) is excluded from bulk send.** Send All / Init Patch / preset load must never override the hardware sustain-pedal state.
- **`InitPatch.prm` is an embedded resource.** It must never be sourced from the user's backup folder.

---

## The honesty principle

When the app can reflect the synth's exact state, it does. When it can't, it surfaces the uncertainty rather than displaying a confident-but-wrong value. Examples:

- Parameters that haven't been confirmed from the synth show as grey with a `?` value label. The footer surfaces an "N UNSYNCED" chip.
- The ADSR envelope animation hides its warning glyph only when the model is trustworthy in the current parameter state.
- The Patch Inspector displays a blocking banner when the current slot's PRM file is missing or malformed.
- When the user clicks Connect with a non-S-1 device selected, a confirmation dialog fires before any CC traffic is sent.

When you add a feature that involves a heuristic or an approximation, follow this pattern: show a warning overlay rather than silently shipping a guess.

---

## File encoding policy

The repo's `.editorconfig` enforces:

- `.cs` / `.axaml` / `.csproj` - UTF-8 with BOM
- `.json` / `.md` - plain UTF-8 (no BOM)
- CRLF line endings
- Trim trailing whitespace

---

## Pull requests

- Branch off `main`.
- Build clean (`dotnet build` - 0 warnings) and pass tests (`dotnet test`).
- For UI changes, verify in a running window before claiming the change is complete. Type checks and tests verify code correctness, not feature correctness.
- For changes that touch hardware-verified values, document the hardware test in the PR description: which S-1 serial / firmware, what you measured, and how to reproduce.
- Keep commits focused; avoid bundling unrelated changes.

---

## Cross-platform contributions

Most of the app is platform-agnostic. The Windows-specific bits are isolated:

- `IWindowSizePolicy` is implemented by `WindowsAspectRatioPolicy` (Win32 `WM_SIZING` hook + `WS_MAXIMIZEBOX` strip). A macOS or Linux contributor would add a sibling class and select it in the `Opened` handler in `MainWindow.axaml.cs`.
- `Melanchall.DryWetMidi.Multimedia` is officially cross-platform from v7+, so the MIDI side should work on macOS / Linux. It just hasn't been smoke-tested there yet.
