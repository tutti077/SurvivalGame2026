"""Shared material cells (see texture.Cell). Tones stay low-contrast, 3-4 steps, no hue shifts
(CLAUDE.md art style). Each item composes its own 16-cell texture from these."""
from .texture import Cell


def bark():
	return Cell((106, 78, 52), noise=0.10, tones=[(92, 66, 44), (106, 78, 52), (118, 88, 60), (98, 72, 48)],
				streak="x", streak_amt=0.18)


def bark_dark():
	return Cell((84, 60, 40), noise=0.10, tones=[(72, 52, 34), (84, 60, 40), (96, 70, 48)], streak="x", streak_amt=0.2)


def wood_end():
	return Cell((176, 142, 96), noise=0.06, rings=(132, 100, 64), ring_amt=0.8)


def wood_pale():
	return Cell((168, 132, 86), noise=0.07, tones=[(154, 120, 78), (168, 132, 86), (180, 144, 96)], streak="x",
				streak_amt=0.14)


def wood_handle():
	return Cell((128, 92, 58), noise=0.07, tones=[(116, 82, 50), (128, 92, 58), (139, 102, 66)], streak="x",
				streak_amt=0.14)


def stone():
	return Cell((128, 128, 126), noise=0.10, tones=[(112, 112, 110), (128, 128, 126), (142, 142, 140), (120, 121, 118)],
				speckle=(96, 96, 94), speckle_density=0.02)


def flint():
	return Cell((150, 152, 150), noise=0.07, tones=[(138, 140, 138), (150, 152, 150), (164, 166, 164)], streak="y",
				streak_amt=0.12)


def dirt():
	return Cell((96, 66, 38), noise=0.14, tones=[(84, 56, 32), (96, 66, 38), (110, 78, 46), (92, 62, 36)],
				speckle=(68, 46, 26), speckle_density=0.05)


def leather():
	return Cell((112, 72, 44), noise=0.08, tones=[(102, 64, 38), (112, 72, 44), (122, 80, 50)], blotch=(96, 60, 36),
				blotch_amt=0.6)


def leather_dark():
	return Cell((88, 52, 36), noise=0.08, tones=[(78, 46, 32), (88, 52, 36), (98, 60, 42)])


def leather_red():
	return Cell((112, 54, 44), noise=0.08, tones=[(100, 46, 38), (112, 54, 44), (124, 62, 50)], blotch=(96, 44, 36),
				blotch_amt=0.5)


def bone():
	return Cell((228, 220, 198), noise=0.05, tones=[(214, 206, 184), (228, 220, 198), (238, 232, 212)], streak="x",
				streak_amt=0.08)


def bone_dirty():
	return Cell((208, 196, 168), noise=0.06, tones=[(192, 180, 152), (208, 196, 168), (222, 212, 186)], speckle=(166, 150, 120),
				speckle_density=0.03)


def fat():
	return Cell((236, 230, 210), noise=0.05, tones=[(224, 218, 196), (236, 230, 210), (244, 240, 224)], blotch=(242, 226, 190),
				blotch_amt=0.5)


def meat_red():
	return Cell((196, 64, 36), noise=0.08, tones=[(176, 52, 30), (196, 64, 36), (214, 80, 48)], streak="x", streak_amt=0.18,
				stripes=(236, 214, 190), stripe_period=9, stripe_amt=0.55)


def meat_pink():
	return Cell((214, 96, 116), noise=0.08, tones=[(196, 84, 104), (214, 96, 116), (228, 112, 130)], streak="x",
				streak_amt=0.16, stripes=(240, 222, 214), stripe_period=10, stripe_amt=0.5)


def fur_orange():
	return Cell((214, 112, 36), noise=0.10, tones=[(196, 100, 30), (214, 112, 36), (228, 128, 48)], streak="y",
				streak_amt=0.18)


def fur_cream():
	return Cell((236, 226, 200), noise=0.06, tones=[(226, 214, 186), (236, 226, 200), (244, 236, 214)], streak="y",
				streak_amt=0.12)


def hide_brown():
	return Cell((118, 78, 48), noise=0.10, tones=[(104, 68, 42), (118, 78, 48), (132, 90, 56)], speckle=(150, 110, 70),
				speckle_density=0.03, blotch=(100, 64, 40), blotch_amt=0.5)


def feather_blue():
	return Cell((72, 112, 196), noise=0.08, tones=[(60, 98, 180), (72, 112, 196), (92, 132, 212)], streak="y",
				streak_amt=0.2)


def feather_white():
	return Cell((236, 238, 240), noise=0.04, streak="y", streak_amt=0.14)


def rope():
	return Cell((150, 118, 74), noise=0.08, tones=[(132, 102, 62), (150, 118, 74), (166, 132, 86)], streak="x",
				stripes=(104, 78, 46), stripe_period=6, stripe_amt=0.7)


def metal():
	return Cell((112, 114, 120), noise=0.06, tones=[(98, 100, 106), (112, 114, 120), (128, 130, 136)], streak="x",
				streak_amt=0.12)


def resin():
	return Cell((226, 86, 28), noise=0.05, tones=[(214, 76, 24), (226, 86, 28), (236, 100, 36)])


def leaf_green():
	return Cell((70, 132, 54), noise=0.10, tones=[(58, 116, 46), (70, 132, 54), (86, 150, 66)], streak="y", streak_amt=0.16)


def leaf_dark():
	return Cell((50, 104, 44), noise=0.10, tones=[(42, 90, 38), (50, 104, 44), (62, 118, 52)], streak="y", streak_amt=0.16)


def stem_green():
	return Cell((82, 138, 58), noise=0.08, streak="x", streak_amt=0.14)


def fire():
	return Cell((250, 160, 40), noise=0.12, tones=[(236, 92, 24), (250, 160, 40), (255, 214, 90), (244, 120, 32)])


def ember():
	return Cell((80, 44, 30), noise=0.12, tones=[(60, 34, 24), (80, 44, 30), (110, 56, 30)], speckle=(240, 120, 40),
				speckle_density=0.08)


def white():
	return Cell((236, 236, 232), noise=0.04)


def blue_dark():
	return Cell((44, 72, 168), noise=0.06)


def blue_berry():
	return Cell((54, 78, 190), noise=0.10, tones=[(40, 62, 168), (54, 78, 190), (72, 98, 206)], speckle=(130, 150, 220),
				speckle_density=0.02)


def honey():
	return Cell((230, 160, 36), noise=0.08, tones=[(214, 142, 28), (230, 160, 36), (242, 182, 52)])


def wheat():
	return Cell((222, 184, 90), noise=0.08, tones=[(206, 168, 78), (222, 184, 90), (236, 200, 108)], streak="y",
				streak_amt=0.2)


def sack():
	return Cell((176, 150, 110), noise=0.08, tones=[(162, 138, 100), (176, 150, 110), (190, 164, 122)], stripes=(150, 126, 90),
				stripe_period=4, stripe_amt=0.35, streak="x")


def seed_brown():
	return Cell((110, 74, 40), noise=0.10, tones=[(92, 60, 32), (110, 74, 40), (128, 88, 50)], streak="y", streak_amt=0.2)


def petal_white():
	return Cell((240, 240, 236), noise=0.04, streak="y", streak_amt=0.1)


def yellow():
	return Cell((236, 192, 40), noise=0.10, speckle=(212, 160, 30), speckle_density=0.1)


def skin_grey():
	return Cell((136, 118, 104), noise=0.08, tones=[(122, 106, 94), (136, 118, 104), (150, 132, 118)])


def skin_tan():
	return Cell((176, 134, 100), noise=0.08, tones=[(162, 122, 90), (176, 134, 100), (190, 148, 112)])


def fur_grey():
	return Cell((112, 110, 102), noise=0.10, tones=[(98, 96, 90), (112, 110, 102), (126, 124, 116)], streak="y",
				streak_amt=0.18)


def red_glow():
	return Cell((240, 40, 30), noise=0.05)


def black():
	return Cell((28, 26, 26), noise=0.06)


def cloth_red():
	return Cell((150, 42, 34), noise=0.08, tones=[(136, 36, 30), (150, 42, 34), (164, 50, 40)], speckle=(230, 220, 200),
				speckle_density=0.01)


def cloth_cream():
	return Cell((222, 206, 170), noise=0.06, tones=[(208, 192, 156), (222, 206, 170), (234, 220, 186)], streak="x",
				stripes=(196, 180, 146), stripe_period=5, stripe_amt=0.4)


def plaque():
	return Cell((122, 84, 52), noise=0.08, tones=[(108, 74, 46), (122, 84, 52), (136, 96, 60)], streak="y", streak_amt=0.16)


def teal():
	return Cell((70, 170, 150), noise=0.06)
