# Ninja sprite

Original chibi ninja generated with the built-in imagegen tool for this project.
No external character or third-party sprite pack was used.

Production prompt: 8 columns x 3 rows, consistent navy chibi ninja with teal scarf,
idle breathing/blink loop, working hand-seal loop, one-shot celebration; transparent
background, no text or detached effects, readable at small desktop-pet size.
The transparency request produced an opaque background; a subsequent imagegen edit
preserved the poses and replaced only the background with solid #FF00FF for
deterministic chroma-key extraction and resizing to 128px cells.

`ninja.json` defines the cell geometry, row, frame count, duration and looping.
PNG and JSON are embedded at build time, so the executable remains self-contained.
The main ninja and small clones share the same atlas.

Calm revision: the built-in imagegen tool edited the atlas using the existing ninja
as the identity reference. Heads face forward, feet and scarves stay planted;
idle frames vary only through a brief blink and very small chest movement, while
working frames vary only through small hand/finger movements. The full edit prompt
is saved in `calm-prompt.txt`. Chroma-key extraction uses `tools/prepare-ninja.ps1`.

Rendering anchors each loop to frame zero outside the JSON `motionRegions` so tiny
generated alignment differences cannot shake the head or feet. Idle holds still
for 4000ms, then plays eight 80ms frames; working uses 200ms frames, clones 400ms.
Success remains a full-body one-shot. Preview GIFs come from actual renderer output.
