"""Pack renderer-produced QA frames into an animated documentation preview.

Run test.ps1 first. Requires Pillow in the development environment only.
This does not modify the production sprite atlas.
"""
from pathlib import Path
from PIL import Image

root = Path(__file__).resolve().parents[1]
for state, count, durations in [('working', 16, [200]*16), ('idle', 8, [4080]+[80]*7)]:
    frames = []
    for i in range(count):
        with Image.open(root / 'bin' / 'ninja-tests' / f'{state}-{i}.png') as source:
            background = Image.new('RGBA', source.size, '#eef2f5')
            background.alpha_composite(source.convert('RGBA'))
            frames.append(background.convert('RGB'))
    frames[0].save(root / 'docs' / 'assets' / f'ninja-{state}.gif', save_all=True,
                   append_images=frames[1:], duration=durations, loop=0, disposal=2)
    print(f'{state} preview saved: {count} frames, {sum(durations)} ms loop.')
