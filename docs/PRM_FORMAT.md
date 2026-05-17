# S-1 PRM File Format

Reverse-engineered notes on the Roland S-1's `.PRM` patch dump format.
Roland has not published this format; everything here was either confirmed against
hardware captures or inferred from inspecting many real backup patches. Items that
have *not* been hardware-verified are flagged inline so readers know what they're
trusting.

S1Utility reads PRM files; it does not write them. The mapping is published here
both as project documentation and because the verified encodings are the most
valuable thing in the repository — they are what makes the editor agree with the
device.

## File shape

A `.PRM` file is a plain text dump, one entry per line, structured as
`KEY = VALUE`.

```
LFO_RATE = 64
VCF_CUTOFF = 255
STEP_NOTE 1 = NOTE1=60 VELO1=100 LENG1=24
STEP_MOTION 11 = M1=64 PB=-32768
; comment lines (semicolon or #) are ignored
```

- Whitespace around `=` is permitted and trimmed.
- Keys are case-insensitive on read (the parser upper-cases everything).
- Lines starting with `;` or `#` are comments and ignored.
- Lines without `=` are silently skipped (intentionally — the parser is INI-like
  and lenient about non-standard headers / blank lines).
- The parser enforces a 1 MB file size cap and an 8 KB line length cap. Anything
  larger is treated as not-a-PRM and rejected with `InvalidDataException`.
- If a file parses but yields zero recognizable entries, it is rejected.

Real backup patches are typically 10–30 KB.

## Parameter categories

Three categories of entry:

1. **CC-mapped parameters** — appear as both a PRM key and a MIDI CC. Editing
   either side and saving propagates the change; the editor sends the CC at
   load time so the synth follows.
2. **PRM-only parameters** — exist only in the file (FX advanced settings,
   D-Motion routing, riser, chop step patterns, sequencer data). Edited in the
   PRM Inspector tab but not transmitted over MIDI.
3. **Step sequencer / step motion** — special multi-value lines covered below.

## CC-mapped parameters

| PRM key                  | CC  | PRM range | CC range | Notes |
|--------------------------|-----|-----------|----------|-------|
| `LFO_RATE`               | 3   | 0–255     | 0–127    | Lin scale |
| `LFO_WAVE_FORM`          | 12  | 0–127     | 0–127    | Direct |
| `LFO_MOD_DEPTH`          | 17  | 0–255     | 0–127    | Lin scale |
| `LFO_MODE`               | 79  | 0–127     | 0–127    | Direct (Normal / Fast) |
| `LFO_KEY_TRIG`           | 105 | 0–127     | 0–127    | Off / On |
| `LFO_SYNC`               | 106 | 0–127     | 0–127    | Off / On |
| `PORTAMENTO_TIME`        | 5   | 0–255     | 0–127    | Lin scale |
| `PAN`                    | 10  | 0–127     | 0–127    | Direct |
| `PORTAMENTO_MODE`        | 31  | 0–127     | 0–127    | Off / Auto / On |
| `PORTAMENTO`             | 65  | 0–1       | 0–127    | Flag (see below) |
| `KBD_TRANSPOSE`          | 77  | 0–127     | 0–127    | Direct |
| `ASSIGN_MODE`            | 80  | 0–127     | 0–127    | Mono / Unison / Poly / Chord |
| `CHORD_VOICE2_SW`        | 81  | 0–1       | 0–127    | Flag |
| `CHORD_VOICE3_SW`        | 82  | 0–1       | 0–127    | Flag |
| `CHORD_VOICE4_SW`        | 83  | 0–1       | 0–127    | Flag |
| `CHORD_VOICE2_KEY_SHIFT` | 85  | −64..63   | 0–127    | Signed semitones (see below) |
| `CHORD_VOICE3_KEY_SHIFT` | 86  | −64..63   | 0–127    | Signed semitones |
| `CHORD_VOICE4_KEY_SHIFT` | 87  | −64..63   | 0–127    | Signed semitones |
| `CHORUS`                 | 93  | 0–127     | 0–127    | Direct |
| `VCO_MOD_DEPTH`          | 13  | 0–255     | 0–127    | Lin scale |
| `VCO_RANGE`              | 14  | 0–127     | 0–127    | 64' / 32' / 16' / 8' / 4' / 2' |
| `VCO_PULSE_WIDTH`        | 15  | 0–255     | 0–127    | Square PW |
| `VCO_PWM_SOURCE`         | 16  | 0–127     | 0–127    | Envelope / Manual / LFO |
| `VCO_BEND_SENS`          | 18  | 0–127     | 0–127    | Direct |
| `VCO_PWM_LEVEL`          | 19  | 0–255     | 0–127    | Square oscillator level |
| `VCO_SAW_LEVEL`          | 20  | 0–255     | 0–127    | Saw level |
| `VCO_SUB_LEVEL`          | 21  | 0–255     | 0–127    | Sub level |
| `VCO_SUB_TYPE`           | 22  | 0–127     | 0–127    | −2 Oct Asym / −2 Oct / −1 Oct |
| `VCO_NOISE_LEVEL`        | 23  | 0–255     | 0–127    | Noise level |
| `FINE_TUNE`              | 76  | 0–255     | 0–127    | Centre PRM 128 → CC 64 |
| `NOISE_MODE`             | 78  | 0–127     | 0–127    | Pink / White |
| `OSC_DRAW_MULT`          | 102 | 7–255     | 3–127    | Anchored — see below |
| `OSC_CHOP_OVERTONE`      | 103 | 0–255     | 0–127    | Display scale: see below |
| `OSC_CHOP_COMB`          | 104 | 7–255     | 3–127    | Anchored — see below |
| `OSC_DRAW_SW`            | 107 | 0–127     | 0–127    | Off / Step / Slope |
| `VCF_ENV_DEPTH`          | 24  | 0–255     | 0–127    | Lin scale |
| `VCF_MOD_DEPTH`          | 25  | 0–255     | 0–127    | Lin scale |
| `VCF_KEY_FOLLOW`         | 26  | 0–255     | 0–127    | Lin scale |
| `VCF_BEND_SENS`          | 27  | 0–127     | 0–127    | Direct |
| `VCF_RESONANCE`          | 71  | 0–255     | 0–127    | Lin scale |
| `VCF_CUTOFF`             | 74  | 0–255     | 0–127    | Init PRM=255 → CC 127 |
| `VCA_ENV_MODE`           | 28  | 0–127     | 0–127    | Direct |
| `ENV_TRG_MODE`           | 29  | 0–127     | 0–127    | Direct |
| `ENV_SUSTAIN`            | 30  | 0–255     | 0–127    | Lin scale |
| `ENV_RELEASE`            | 72  | 0–255     | 0–127    | Lin scale |
| `ENV_ATTACK`             | 73  | 0–255     | 0–127    | Lin scale |
| `ENV_DECAY`              | 75  | 0–255     | 0–127    | Lin scale |
| `REVERB_TIME`            | 89  | 0–255     | 0–127    | Lin scale |
| `DELAY_TIME`             | 90  | 0–255     | 0–127    | Lin scale |
| `REVERB_LEVEL`           | 91  | 0–255     | 0–127    | Lin scale |
| `DELAY_LEVEL`            | 92  | 0–255     | 0–127    | Lin scale |

`MOD_WHEEL` and `EXPRESSION` may appear in the file but are not in the editor's
map — they are runtime performance state, not patch state, and the editor does
not send them when applying a PRM.

## Scaling rules

All rules are linear over the source range, rounded to the nearest integer.

### Direct (0–127 ↔ 0–127)

1:1, no scaling. Used for parameters that are natively 7-bit on the device.

### 0–255 → 0–127

```
cc  = round(prm * 127 / 255)
prm = round(cc * 255 / 127)
```

This is the largest category — most knobs on the front panel.

### Flag (0–1 → 0/127)

Used for momentary switches (`PORTAMENTO`, `CHORD_VOICE2_SW`, etc.).
- PRM `0` → CC `0`
- PRM `>0` → CC `127` (clamp + round path: any non-zero PRM becomes 1 internally,
  which scales to 127)

### Signed semitones (−64..63 → 0–127)

The three chord voice key-shift parameters. Stored as signed integers in PRM,
biased by +64 to fit MIDI's unsigned 7-bit range:

```
cc  = prm + 64
prm = cc - 64
```

Range capped on read so a malformed file can't push values outside −64..63.

### Anchored range (`OSC_DRAW_MULT`, `OSC_CHOP_COMB`)

Both use `PrmMin = 7`, `PrmMax = 255`, `CcMin = 3` — meaning the PRM value
range starts at 7 and the matching CC range starts at 3. Hardware init is
display "1.0" = CC 3 = PRM 7. **Hardware-verified**: all 64 backup patches
have minimum PRM = 7 for these two parameters; the previous assumption of
`PrmMin = 0` was wrong.

```
norm = (prm - 7) / (255 - 7)        # 0.0 .. 1.0
cc   = round(3 + norm * (127 - 3))  # 3 .. 127
```

The CC-side display formula (in `CcDisplay.cs`) converts CC 3..127 to the
device's displayed "x1.0 .. x32.0" in 0.5 steps:

```
display = round((1.0 + (cc - 3) * 31.0 / 124.0) * 2) / 2.0
```

### `OSC_CHOP_OVERTONE` display

CC 0–127 maps to a 0–200 display value with the formula

```
display = min(200, round(cc * 255 / 127))
```

Note the denominator is **255**, not 200 — this is intentional and was
hardware-verified after iteration. Don't "simplify" it to `*200/127`; that
breaks the round-trip at the top end.

## Hardware-special-case: `OSC_CHOP_OVERTONE` gating

`OSC_CHOP_OVERTONE` (CC103) only affects the audible signal when **at least
one of the four chop grid masks** (`OSC_CHOP_PWM`, `OSC_CHOP_SAW`,
`OSC_CHOP_SUB`, `OSC_CHOP_NOISE`) differs from `0xFFFF`. All-bits-on is the
synth's "no-chop" idle state; flipping any bit off engages chopping, which
is what makes overtone audible.

`OSC_CHOP_TYPE` does **not** gate audibility. All 16 PTN1-* factory patches
ship with type=0; some have altered grids, and raising overtone on those
patches is audibly active. If `OSC_CHOP_TYPE` has any sonic effect at all,
it's something else — possibly a chop algorithm selector — not the gate.

### Editor consequence

The editor's `ApplyInitPatch` always sends `CC103 = 0` after loading the
init PRM, regardless of the stored value (which is 100). The reason is
*not* that the hardware would otherwise play overtone — under the gate
above, the init's all-0xFFFF grid prevents that. The reason is sonic
predictability: there is no MIDI CC for the chop grid, so the editor
cannot reset the device's grid state when the user clicks "Init Settings".
If a previously-loaded pattern altered the grid, sending the init's
stored overtone=100 would bleed audibly into what's supposed to be a
blank starting point. Forcing CC103=0 guarantees init is sonically blank
for chop regardless of the device's current grid.

`ApplyPrmData` (used for factory patches and pattern sync) sends CC103
as-is — the stored value is what the user expects to hear on those patches.

The OSC preview's "Chop output is not represented" warning checks
`overtone > 0 AND chop grid altered` before firing.

> ⚠ **History.** Two prior incorrect models were ruled out. First, the
> hypothesis was `OSC_CHOP_TYPE = 0` as sole gate with raw MIDI bypassing
> it — falsified when init with type=0 still produced no audible overtone.
> Second was `type ≠ 0 AND grid altered` — falsified by PTN1_01 (type=0,
> altered grid) where raising overtone is audibly active. The grid-only
> gate is the simplest model consistent with all observed behaviour.

## PRM-only parameters

These appear in the file and are surfaced in the Patch Inspector tab but never
sent over MIDI. The full list is defined in `PrmFileManager` and includes:

- **Sequencer header**: `LENG`, `TEMPO`, `TRANSPOSE`, `SHUFFLE`, `MOTION_CC1..8`
- **Riser**: `RISER_RESO`, `RISER_LEVEL`, `RISER_TIME`, ...
- **D-Motion routing**: `DM_ASSIGN_X`, `DM_ASSIGN_Y`, `DM_ASSIGN_TAP`,
  `DM_ASSIGN_FF` (each maps a hardware control to one of 9 destinations:
  Off, Modulation, Frequency, Resonance, Pitch Bend, Pan, Expression,
  Delay Level, Reverb Level)
- **Reverb/Delay advanced**: `REVERB_PRE_DELAY` (ms), `REVERB_TONE_LOW_CUT`,
  `REVERB_TONE_HIGH_CUT`, `DELAY_TONE_LOW_CUT`, `DELAY_TONE_HIGH_CUT`,
  `DELAY_TEMPO`, `DELAY_FEEDBACK`
- **Chop patterns**: `OSC_CHOP_PWM`, `OSC_CHOP_SAW`, `OSC_CHOP_SUB`,
  `OSC_CHOP_NOISE` — each a 16-bit integer, bit `N-1` = step N on/off
  (4 waveforms × 16 steps)
- **Draw waveform**: encoded across multiple PRM keys; rendered in the Draw
  Inspector card

The low-cut and high-cut options for delay and reverb tone use fixed string
tables defined in `PrmFileManager.s_lowCutOpts` / `s_highCutOpts` (frequency
labels in Hz / kHz).

## Step sequencer encoding

### `STEP_NOTE N` (N = 1..64)

```
STEP_NOTE 1 = NOTE1=60 VELO1=100 LENG1=24 NOTE2=-1 VELO2=0 LENG2=0 ...
```

Each step holds up to 4 notes (chord steps). Fields per slot `x` (1..4):

- `NOTEx` — MIDI note number, **−1 means slot inactive**
- `VELOx` — 0–127 velocity
- `LENGx` — duration in 1/96 ticks (so 24 = 1/16, 48 = 1/8, 96 = 1/4)

Slots without `NOTEx` set or with `NOTEx = -1` are inactive. Parsed by
`SequencerStep.ParseFrom`.

### `STEP_MOTION BS` (B = bar 1..8, S = step 1..8)

The two-digit suffix encodes bar and step:

```
STEP_MOTION 11 → bar 1, step 1 → linear index 0
STEP_MOTION 88 → bar 8, step 8 → linear index 63
```

Value content:

- `M1..M8` — motion lane values for motion CCs 1..8, **−1 means inactive**
- `PB` — pitch bend, **−32768 means inactive**

Parsed by `SequencerStep.ParseMotionFrom`.

> ⚠ **Inferred, not hardware-verified.** All 64 backup patches examined so far
> have fully inactive motion data (`M*` = −1, `PB` = −32768). The motion
> encoding has *not* been exercised against a real patch with active motion.
> If you have such a patch, please verify and update this section.

## Chop pattern encoding

Each of the four chop waveform keys (`OSC_CHOP_PWM`, `_SAW`, `_SUB`, `_NOISE`)
stores a 16-bit integer where bit `(N-1)` represents step `N`:

```
OSC_CHOP_SAW = 21845    # 0101010101010101 → steps 1,3,5,7,9,11,13,15 ON
```

Decoded by `ChopPattern.LoadFromPrm`.

## Validation status

| Encoding                              | Status                              |
|---------------------------------------|-------------------------------------|
| All CC-mapped parameters in the table | Verified against device round-trip  |
| `OSC_DRAW_MULT` / `OSC_CHOP_COMB` anchors | Hardware-verified (64-patch backup audit) |
| `OSC_CHOP_OVERTONE` display formula   | Hardware-verified after iteration   |
| Chop overtone grid-only gating        | Hardware re-tested 2026-05-17 by ear on PTN1_01 |
| ADSR curve calibration                | Hardware-calibrated (see `osc_waveform_visualizer.md`) |
| Filter cutoff curve                   | Hardware-verified (spike at xC)     |
| OSC waveform synthesis model          | Hardware-calibrated against captures in `E:\S-1 Shapes\` |
| `STEP_NOTE` encoding                  | Verified against backup patches     |
| `STEP_MOTION` encoding                | **Inferred** — no active-motion patches available |
| `OSC_CHOP_*` bit-per-step encoding    | Verified against backup patches     |

## A note on the writer

The codebase has a `ToPrm()` method on both `PrmParameterInfo` and
`PrmParameter`, and a reverse lookup `PrmCcMap.ByCC`. These look like writer
infrastructure but **the project does not write PRM files**. They exist for
display formatting: `CcDisplay.KnobValue` calls `Info.ToPrm(cc)` to render
the PRM-equivalent number next to a knob's CC value in the Patch Inspector.
A real PRM writer would need to round-trip additional state (sequencer steps,
chop patterns, draw waveform, motion, the full FX advanced section) that the
editor only reads and surfaces, not edits.

If a writer is ever implemented, the encodings in this document are the
authoritative reference for the CC-mapped portion.
