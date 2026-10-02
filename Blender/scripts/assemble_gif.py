"""Assemble frame_NNN.png in a folder into <folder>/felling.gif (system python, needs Pillow).

    python Blender/scripts/assemble_gif.py Blender/blenderprojects/elm_felling [fps] [size]
"""
import glob, os, sys
from PIL import Image

folder = sys.argv[1]
fps = int(sys.argv[2]) if len(sys.argv) > 2 else 12
size = int(sys.argv[3]) if len(sys.argv) > 3 else 480
frames = sorted(glob.glob(os.path.join(folder, "frame_*.png")))
imgs = [Image.open(f).convert("RGB").resize((size, size), Image.LANCZOS).quantize(colors=128, method=Image.Quantize.MEDIANCUT) for f in frames]
out = os.path.join(folder, "felling.gif")
imgs[0].save(out, save_all=True, append_images=imgs[1:], duration=int(1000 / fps), loop=0, optimize=True)
print("GIF", len(imgs), "frames", os.path.getsize(out) // 1024, "KB")
