"""Map pin for the player's corpse bag (Assets/ui/map/pin_corpse_bag.png, 64 px like the other pins): a burlap sack
tied with a black string, drawn on a 32 px grid and doubled so the pixels match the other pins.

Run:  python Tools/map_pins/draw_corpse_bag_pin.py
"""
import os
from PIL import Image

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
OUT = os.path.join(ROOT, "Assets", "ui", "map", "pin_corpse_bag.png")

# . clear, o outline, a tan, b light tan, c shade, k string
ROWS = [
	"................................",
	"................................",
	"..........oo......oo............",
	".........oabo....obao...........",
	".........oaabo..obaao...........",
	"..........oaaboobaaco...........",
	"...........oaaaaaaco............",
	"............oaaaaco.............",
	"...........okkkkkkko............",
	"...........okkkkkkkko...........",
	"............oaaaacokko..........",
	"..........ooabaaacookko.........",
	"........ooabbaaaaaccookko.......",
	".......oabbbaaaaaaaacco.oko.....",
	"......oabbbaaaaaaaaaacco.oko....",
	".....oabbbaaaaaaaaaaaacco.o.....",
	".....oabbaaaaaaaaaaaaaaco.......",
	"....oabbaaaaaaaaaaaaaaacco......",
	"....oabbaaaaaaaaaaaaaaacco......",
	"....oabaaaaaaaaaaaaaaaacco......",
	"....oabaaaaaaaaaaaaaaaacco......",
	"....oaaaaaaaaaaaaaaaaaacco......",
	"....oaaaaaaaaaaaaaaaaaccco......",
	"....oaaaaaaaaaaaaaaaaaccco......",
	".....oaaaaaaaaaaaaaaaaccco......",
	".....oacaaaaaaaaaaaaacccco......",
	"......oaccaaaaaaaaaccccco.......",
	".......ooccccccccccccccoo.......",
	".........oooooooooooooo.........",
	"................................",
	"................................",
	"................................",
]
COLOURS = {
	".": (0, 0, 0, 0),
	"o": (38, 28, 20, 255),
	"a": (176, 142, 92, 255),
	"b": (206, 176, 122, 255),
	"c": (134, 104, 66, 255),
	"k": (22, 20, 22, 255),
}


def main():
	img = Image.new("RGBA", (32, 32), (0, 0, 0, 0))
	for y, row in enumerate(ROWS):
		for x, ch in enumerate(row[:32]):
			img.putpixel((x, y), COLOURS[ch])
	img = img.resize((64, 64), Image.NEAREST)
	img.save(OUT)
	print("wrote", OUT)


if __name__ == "__main__":
	main()
