# Animation & Timing: design (2026-11-07)

## Why

Tefa (2026-11-07, morning): *"a tool that lets me go through any of the player body's animations - like finger movement,
leg movement and foot angle during pushing an enemy away, and adding or removing frames and is something that lets me tune
the weapons timings, like you did with the bullet cover yesterday. the goal is to have a tool like the poser tools, that i
can use to tune hand movement on the wolfsbane, when the game's own animation is playing, to be able to fine tune that
original hand movement and placement frame by frame"*. First target: *"the left hand rotating the drum, right now it clips
through the drum a lot ... it sits through the drum"* (a placement fault, the whole hand too deep, for the whole turn).

Tefa opted out of the design questions (*"build the best you can with info you got, and lets fine tune the tool later"*),
so every "call" below is the builder's, said out loud here.

## What it is

A desktop window beside the game (hung in the headset by Window View, like Hand Poser) with three boxes, plus two in-game
halves:

1. **PLAYING NOW** (any RE Engine game with REFramework): the animations playing on the player right now, one line per
   motion layer (where it plays, the bank and motion numbers, frame / last frame, speed). **FREEZE** stops them all on the
   frame they are at; **◀ −1 / +1 ▶** step every frozen layer one frame; a **slow motion** slider (5 % to 100 %) plays them
   slowly; **PLAY** gives them back. While frozen (or not), **NUDGE** rows turn any joint by a delta (X / Y / Z degrees)
   and **KEEP ON THIS FRAME** saves that delta as a key on the frame of the layer it belongs to; keys are played back by
   the in-game script whenever that animation plays again, interpolated between keyed frames, flat before the first and
   after the last. This is the legs / feet / "push an enemy" case.
2. **OUR CLIPS** (Visceral RE8 only; hidden elsewhere): the finger movements the mod plays on the docked hands
   (`clips/<gun>_<event>.csv`, cut from the game's own takes). Pick one for the gun in hand, **PLAY** it on the hand,
   or **HOLD** it on a frame and step. Per frame: **NUDGE** rows for the clip's joints, and six **HAND** sliders that move
   and turn the whole hand on the gun (forward / up / right in cm, tilt / turn / roll in degrees, the gun's own axes).
   **KEEP ON THIS FRAME** keys them; between keys they interpolate; one key = the whole clip. **ADD FRAME** repeats the
   current frame once more (a hold), **REMOVE FRAME** drops it, **SLOWER / FASTER** retime a frame range. The source CSV is
   never changed: the edits are a list of operations in `<gun>_<event>.edit.json` the plugin applies when it loads the
   clip, so they are undoable and committable (the CSV is the game's data and never leaves the PC).
3. **TIMINGS** (Visceral RE8 only): the mod's named seconds and sizes that were constants (the cover's swing, the thumb
   lift, the hammer press, the drum's turn, the glides, the hand-over, the case's fall...), one slider each with the
   default marked and **RESET**; saved to `visceral_re8/timings.json`, which the plugin re-reads when it changes, so the
   bullet-cover kind of tuning needs no rebuild.

## The two in-game halves (calls)

- **`reframework\autorun\anim_timing.lua`** (any RE game; Lua, like Hand Poser: a tuning tool, not gameplay). Finds the
  player the Hand Poser way (plus Village's four body meshes through the player updater's mesh controller, the finger
  recorder's route), lists every `via.motion.Motion`'s layers (bank, motion, frame, end frame, speed), freezes with
  `set_Speed(0)` written every frame at UpdateMotion (pre) and steps with `set_Frame`, plays slow with `set_Speed(x)`,
  gives speeds back on PLAY. Applies joint nudges at LateUpdateBehavior and PrepareRendering (the Hand Poser / EMV timing)
  as a quaternion delta on `LocalRotation` (Euler YXZ, the engine's order as measured 2026-11-05), live from
  `command.json` and saved from `anim_timing\nudges\<game>\<src>_<bank>_<motion>.json` (keys by frame). Files:
  `anim_timing\state.json` (written 8 times a second while anything is held, once a second otherwise) and
  `anim_timing\command.json` (seq, freeze, step, speed, set_frame, nudge_live, want_read). ⚠️ Untested in a real game
  until the home PC runs it; `set_Frame` is known to be undone by some state machines (RE2 layer 3, 2026-08).
- **Visceral RE8's plugin** (C++, `shellclip.cpp`, `dockpose.cpp`, new `timings.cpp`): clips load with their
  `.edit.json` (ops: `remove i`, `hold i n`, `speed from to x`; nudges keyed by SOURCE frame index, so they survive the
  ops), re-load when either file changes; `scrub.json` {seq, gun, event, hands, frame, hold} holds a clip's edited frame
  on the hand (no blend-out) or plays it once; `clips_state.json` (twice a second) lists the gun's clips, each clip's
  edited frames and what plays now; the per-key HAND six numbers come out as `shellclip::hand_nudge()` and dockpose
  (the last writer on the IK target) adds them on top of whoever placed the hand; timings: a name -> value map with
  the settings.h defaults, `timings_known.json` written at start for the window, `timings.json` read on change.
- **The window** `AnimTiming.exe` (C# WinForms, .NET Framework 4, `csc.exe` from Windows; Skin.cs, Json.cs, Nudge.cs
  shared with Hand Poser): the three boxes, the nudge rows, undo through the ops list, the controller buttons stepping
  ticked sliders (X −1, Y −5, A +1, B +5, held grips repeat) through Window View's shared block as Hand Poser does.
  Autosave is the files themselves: every change goes straight to the game's data folder.

## Not now

Editing the game's own animation frames (adding / removing frames of a game clip: only its speed can change here);
other engines; nudges on the game's animations played by the plugin (the Lua plays them, with or without the window);
a visual timeline strip (the frame list is a number and two buttons for now, Tefa tunes the look later).

## Testing

- Plugin: compiles on the dev PC (VS 2022 Build Tools); the ops and nudge maths checked by the window's `--selftest`
  (the same maths in C#) against hand-made cases.
- Window: `--selftest` (ops, interpolation, json, timings); `test\fake_game.py` stands in for both in-game halves
  (writes state.json + clips_state.json + timings_known.json, echoes command.json, scrub.json, timings.json).
- In the game: the home PC (Village): freeze a reload, step, nudge a finger and see it stay; hold the Wolfsbane's drum
  turn on a frame and move the HAND sliders until the hand sits on the drum; one key; wear it.
