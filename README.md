# S1Utility

A desktop companion app for the Roland AIRA Compact S-1 Tweak Synthesizer.

S1Utility connects to a S-1 over MIDI and provides a visual parameter editor, a read-only Patch Inspector for browsing `.PRM` patch backups, and hardware-calibrated heuristic visualizations of the synth's ADSR envelope, filter response and oscillator waveform.

The design principle: **While using heuristics, the app tries to stay honest about what it knows.** When the app can reflect the synth's exact state, it does. When it can't, it surfaces uncertainty rather than displaying wrong or misleading values.

---

## Disclaimer

This is an unofficial third-party tool. This project is not affiliated with, endorsed by, or sponsored by Roland Corporation. Major part of the code is generated with Claude Code, but the app is a product of continuous iteration and refinement.

---

## Features

- **Visual parameter editor**. Knobs, LEDs, sliders, and waveform displays that mirror the S-1's current state over MIDI CC.
- **Patch Mirror**. When a `.PRM` folder is configured, the editor stays in sync with the S-1 in real time: it loads the corresponding patch when patterns change on the device and preserves unsaved edits per slot.
- **Sync-state tracking**. Every parameter shows whether the editor has confirmed its value from the synth (via incoming CC, Send All, or PRM load). Unsynced controls go grey with a `?` label.
- **Parameter visualization**. Filter frequency-response curve, animated ADSR envelope visualizer, draw-wave bars with signed value labels, chop pattern grid.
- **Patch Inspector**. Read-only view of all `.PRM` file data, including parameters that have no MIDI CC equivalent and can be only set from the hardware (sequencer data, draw and chop patterns, etc.).
- **Undo / Redo** for parameter edits, with per-CC coalescing for knob drags.
- **Hardware-verified encodings**. CC mappings, ADSR timing constants, oscillater waveforms, and other measured values were established by testing against real hardware.
- **Keyboard shortcuts**. `Ctrl+M` toggle Connect/Disconnect, `Ctrl+I` Init Patch, `Ctrl+R` Restore Patch, `Ctrl+S` Save Preset, `Ctrl+Z`/`Ctrl+Y` Undo/Redo, `Tab` cycle tabs, arrow keys for patch grid navigation.

---

## Screenshots

_TODO: add screenshots of the Realtime Editor tab and the Patch Inspector tab._

---

## Requirements

- Windows 10 or 11
- .NET 10 SDK (pinned via `global.json`)
- A Roland S-1 connected over USB MIDI

---

## Build and run

```sh
git clone https://github.com/denzlobin/S1Utility.git
cd S1Utility
dotnet build
dotnet run --project S1Utility/S1Utility.csproj
```

Run the tests:

```sh
dotnet test
```

---

## Project status

Beta version; Targets Windows for now, portability to macOS/Linux is on the roadmap (the underlying MIDI library, [DryWetMidi.Multimedia](https://github.com/melanchall/drywetmidi), is cross-platform from v7+; the Win32-specific window aspect-ratio policy would need a sibling implementation).

---

## Known limitations

- **Windows-only** at the moment. The aspect-ratio policy uses Win32 APIs (`WM_SIZING`, `WS_MAXIMIZEBOX`); contributing a sibling implementation for Mac/Linux behind the existing `IWindowSizePolicy` interface would unlock cross-platform builds.
- **Tab 2 (Patch Inspector) is read-only.** The app does not write `.PRM` files — most PRM-only parameters have no MIDI CC path, and writing risks corrupting your patch backups.
- **No sysex.** The S-1 does not use sysex for parameter control.
- **CC13 (VCO Mod Depth)** display scaling is unverified — the editor knob may not match the S-1's display. (CC90 Delay Time was hardware-verified in May 2026 and now uses a piecewise-linear mapping anchored to seven on-device data points.)

---

## How `.PRM` backup works

The S-1 can export its 64-pattern bank as a folder of `.PRM` files via its USB Disk Mode. Configure that folder in Settings, and the Patch Inspector and Patch Mirror features become available.

_TODO: link the official Roland S-1 export procedure once a stable reference page is identified._

---

## Acknowledgments

- [Avalonia](https://avaloniaui.net/) for the cross-platform UI framework.
- [Melanchall.DryWetMidi](https://github.com/melanchall/drywetmidi) for MIDI I/O.

---

## License

This project is licensed under the GNU General Public License v3.0 or later. See [LICENSE](LICENSE) for the full text.

---

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Hardware-verified values must not be changed without re-verifying against a real S-1; that section is mandatory reading before touching parameter mappings.
