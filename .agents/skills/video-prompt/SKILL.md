---
name: video-prompt
description: Use when writing or revising a prompt for a Kingdom of Bad Ideas cutscene or transition clip on Hyper3D Video (Rodin), when a generated clip ignores its prompt, adds something unexpected or morphs, or when Sam corrects how one of these prompts is written.
---

# Video prompts

The generator takes a start frame, an end frame and a prompt. It reads the frames first, and it
drops the end of a long prompt without saying so.

## The file

Prompts live beside their clips in `art-pipeline/cutscenes/kingdom-intro/newest-vid/`, named
`<from>-to-<to>-transition-prompt.md`, in this order: title with the length in seconds, date,
`Start frame:` and `End frame:` with where each came from, `## Prompt`, then the prompt. Only the
text under `## Prompt` goes to the generator.

- **Start frame:** the real last frame of the previous clip. ffmpeg is not on the Bash PATH; it
  is under `$LOCALAPPDATA/Microsoft/WinGet/Packages/Gyan.FFmpeg_*/ffmpeg-*/bin/`.
  `ffmpeg -sseof -0.2 -i clip.mp4 -update 1 clip-last-frame.png`
- **End frame:** the plate cropped and scaled to exactly the start frame's size. Look at the crop.

## The prompt, in this order

1. `**Visual world:**`, `**Weather:**` and `**Camera path:**`. The path gives one direction, one
   constant pace, and the second the camera stops.
2. `**Action:**` as timed beats, with the payoff last and the camera still for it.
3. `The only figures in this shot are ...`
4. `Elements exist and come in and out of view via the edges of the frame only.`

| Element | The beat must say |
|---|---|
| In the start frame | Stays on its spot, which edge it leaves by, and that it never returns |
| Not in the start frame | A full description matching the end frame, `already` standing or holding its pose, which way it faces, `stays exactly this ... in every frame`, `never walks` |

## Before handing it over

Count it, and quote the output. Stay under 2,000.

```bash
python -c "import sys;t=open(sys.argv[1],encoding='utf-8').read().replace('\r\n','\n').split('## Prompt\n',1)[1].strip();print(len(t),'chars, limit 2049, spare',2049-len(t))" <file>
```

When a clip ignores its ending, run the count before rewording anything.

## Gotchas

**Add to this list whenever a clip fails or Sam corrects how a prompt is written:** one dated
line, in this same commit or the next. **Proven** means a clip got better with the fix.
**Suspected** means the cause is a guess; move it to Proven, or delete it, when a clip settles it.

### Proven (2026-11-02)

- **The limit is about 2,049 characters.** Everything after it is ignored with no error, which
  costs the payoff beat and the closing lines. A wrong ending can be a line the model never saw.
- **The image outranks the text.** 50+ generations of "one-legged" failed because the end frame's
  stump read as a raised thigh. Repainting the frame fixed it. Change the frame, not the wording.
- **Camera direction follows the subject.** Moving right means exits on the left and arrivals on
  the right. Arrivals come in the end frame's left-to-right order.
- **The model invents anything that is only named.** "The dog" became a different dog that morphed
  into the real one at the last second.
- **Anything with no exit stays or comes back,** and merges with what else is in the end frame.
- **"Motionless" comes out frozen.** Floating things need visible motion and a visible cause: the
  still hair read as fake until an electric field held it and it bobbed.
- **Distance is fixed, so a shorter beat is a faster camera,** and that shows as motion blur. Bring
  the next arrival in while the last one is still in frame, so there is no empty stretch.
- **Long travel eats the payoff.** Over a wall took most of 12s and left about 2s of gate. A close
  foreground element sweeping across the frame gets there in 1.5s and leaves 6s for the scene.
- **A character entering alone can arrive in profile.** Have them already in place when the
  scene is revealed, and name the angle: `three-quarter ... exactly as in the end frame`.

### Suspected (2026-11-02)

- **Name an action's start, or it can run backwards.** A scoop aimed at a cone came off the cone
  instead. That prompt was also cut off mid-beat, so this one may be the limit.
- **Naming something to remove may invite it.** An electric trail appeared after a prompt said "no
  static remains". Say what leaves, once, and stop.
- **Fast moves stream flame hair** like a comet. Say it stays close around her head.
- **A face in the end frame wins.** Written dismay may fade into a calm end frame.
- **Every generation is a new roll.** A good run is not a saved state; reuse its seed if the tool
  offers one.
