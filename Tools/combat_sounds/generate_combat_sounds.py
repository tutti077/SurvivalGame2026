"""Melee exchange sounds: a player hit (a fleshy thud), a hit on anything else, block, heavy block (clack + clunk), chamber (blades meet: a double clack), parry
(a perfectly timed block: a sword ting).

Mark 2026-11-16: the hit was "very squelchy, like a wet sponge"; blocks and parries should clack, a few block variations
so it is not repetitive, and one special higher clack (it plays on a chamber - Mark 2026-11-16: a parry is a perfectly
timed block, a chamber is two attacks meeting; the game has chambers, no timed parry yet). The first pass (clean sine partials ringing 85-200 ms)
"sound like a xylophone"; the second (Q 70-100 mode clusters + a 260-520 Hz knock) sounded "like they're from literal
drums". It must be "2 sticks of wood whacking against each other. CLACK CLACK": a hard rough contact plus a little
chatter through a LOW-Q wood body centred at 1.5-3 kHz, a broadband crack, high-passed - dry and over in under 0.1 s.
That third pass "nailed the clacking sound"; heavy block and chamber were still "too similar", so the heavy block gained
a wooden clunk under it and the chamber became a double clack; the parry is a "ting" - not a bell, two swords (Mark
2026-11-16).

Everything is synthesised, so the sounds are ours and re-tunable here. Writes 44.1 kHz 16-bit mono WAVs and the four .sound events to Assets/sounds/combat/
(PlayerCombat.CombatAudio.cs plays the events), plus an audition WAV with every variant in a row when --audition PATH
is given.

    python Tools/combat_sounds/generate_combat_sounds.py [--audition <path.wav>]
"""
import argparse
import json
import os

import numpy as np
from scipy.io import wavfile
from scipy.signal import butter, fftconvolve, sosfilt

SR = 44100
ROOT = os.path.normpath(os.path.join(os.path.dirname(__file__), "..", ".."))
OUT = os.path.join(ROOT, "Assets", "sounds", "combat")


def t_axis(seconds):
    return np.arange(int(SR * seconds)) / SR


def filtered_noise(rng, n, kind, cutoff, order=4):
    sos = butter(order, cutoff, btype=kind, fs=SR, output="sos")
    return sosfilt(sos, rng.standard_normal(n))


def finish(x, peak, fade_ms=12):
    fade = int(SR * fade_ms / 1000)
    x[-fade:] *= np.linspace(1, 0, fade)
    x[: int(SR * 0.0005)] *= np.linspace(0, 1, int(SR * 0.0005))
    return x / max(1e-9, np.max(np.abs(x))) * peak


def wood_body(rng, n, center, spread_oct, q_lo, q_hi, count=64, lo=650.0, hi=10000.0):
    """Impulse response of two hand-held wooden sticks: many modes spread log-uniform over [lo, hi], weighted by a broad
    bump around `center` (spread in octaves), each with a LOW Q (q_lo..q_hi). Low Q is the whole point: the previous
    passes used Q 70-100 modes plus a 260-520 Hz knock resonator, which rings as a pitched woodblock / tom - "literal
    drums" (Mark 2026-11-16). Here no mode rings longer than a few ms, so the ear hears a dry crack, not a note."""
    t = np.arange(n) / SR
    ir = np.zeros(n)
    for f in np.exp(rng.uniform(np.log(lo), np.log(hi), count)):
        g = np.exp(-0.5 * (np.log2(f / center) / spread_oct) ** 2) * rng.uniform(0.35, 1.0)
        tau = rng.uniform(q_lo, q_hi) / (np.pi * f)
        g /= np.sqrt(tau * 1000)  # equal energy per mode: a low mode's longer ring must not swamp the band
        ir += g * np.exp(-t / tau) * np.sin(2 * np.pi * f * t + rng.uniform(0, 2 * np.pi))
    return ir / max(1e-9, np.max(np.abs(ir)))


def contacts(rng, n, hits):
    """Force signal for a list of (time s, amplitude, width ms) contacts: rough wood, so each contact is a short
    noise-filled half-sine rather than a clean pulse."""
    x = np.zeros(n)
    for at, amp, width_ms in hits:
        i = int(SR * at)
        m = max(3, int(SR * width_ms / 1000))
        x[i:i + m] += amp * np.sin(np.linspace(0, np.pi, m)) * (0.55 + 0.45 * np.abs(rng.standard_normal(m)))
    return x


def stick_clack(rng, seconds, center, spread_oct=1.1, q=(6.0, 18.0), width_ms=0.5, rattles=2, rattle_amp=(0.18, 0.4),
                crack=0.5, thud=0.0, hp=600.0):
    """CLACK: two wooden sticks whacked together. A hard contact (plus a couple of small bounce contacts as the sticks
    chatter, 4-16 ms apart) through a low-Q wood body centred in the 1.5-3 kHz 'clack' band, a bright broadband crack
    on the contact itself, an optional dull thud for weight, and a high-pass so nothing booms like a drum skin."""
    n = int(SR * seconds)
    t = np.arange(n) / SR
    hits = [(0.0, 1.0, width_ms)]
    at, amp = 0.0, 1.0
    for _ in range(rattles):
        at += rng.uniform(0.004, 0.016)
        amp = rng.uniform(*rattle_amp) * (amp if amp < 1.0 else 1.0)
        hits.append((at, amp, width_ms * rng.uniform(0.6, 1.0)))
    force = contacts(rng, n, hits)
    x = fftconvolve(force, wood_body(rng, n, center, spread_oct, *q))[:n]
    x /= max(1e-9, np.max(np.abs(x)))
    # the crack: the contact's own broadband snap (this is the 'K' of CLACK)
    snap = fftconvolve(force, filtered_noise(rng, int(SR * 0.004), "highpass", 2500) * np.exp(-np.arange(int(SR * 0.004)) / (SR * 0.0007)))[:n]
    x += crack * snap / max(1e-9, np.max(np.abs(snap)))
    if thud > 0:
        th = filtered_noise(rng, n, "bandpass", [700, 1400], order=2) * np.exp(-t / 0.005)
        x += thud * th / max(1e-9, np.max(np.abs(th)))
    x = sosfilt(butter(4, hp, btype="highpass", fs=SR, output="sos"), x)
    # hand-held wood: everything is gone in well under 0.1 s
    return x * np.exp(-t / 0.035)


def block(rng, i):
    center = [1700, 1900, 2100, 2300, 2000][i]
    return stick_clack(rng, 0.14, center, rattles=[2, 1, 2, 3, 1][i], thud=0.12)


def wood_clunk(rng, n, lo=170.0, hi=650.0, count=26, q=(6.0, 14.0), width_ms=2.2, tau=0.026):
    """CLUNK: the dull, hollow knock of a heavy wooden mass landing - a DENSE low-mid cluster (no single mode leads, so
    there is no pitch to hear as a drum), struck by a soft wide contact, gone in ~0.1 s."""
    t = np.arange(n) / SR
    force = contacts(rng, n, [(0.0, 1.0, width_ms), (rng.uniform(0.006, 0.01), 0.3, width_ms * 0.7)])
    x = fftconvolve(force, wood_body(rng, n, np.sqrt(lo * hi), 1.0, *q, count=count, lo=lo, hi=hi))[:n]
    x = sosfilt(butter(2, 90, btype="highpass", fs=SR, output="sos"), x)
    return x * np.exp(-t / tau) / max(1e-9, np.max(np.abs(x)))


def block_heavy(rng, i):
    """A heavy blow on the guard: CLACK + CLUNK (Mark 2026-11-16: "maybe throw in a clunk noise for blocking heavy
    attacks"). The clack sits lower than a light block's (1.1-1.4 kHz) with more chatter; the clunk under it is the
    weight of the blow - the one blocked sound with real low-mid body, so it can never be mistaken for the chamber."""
    center = [1100, 1250, 1400][i]
    n = int(SR * 0.22)
    clack = stick_clack(rng, 0.22, center, spread_oct=1.3, q=(5.0, 15.0), width_ms=0.8, rattles=3,
                        rattle_amp=(0.25, 0.5), crack=0.4, thud=0.3, hp=480.0)
    clunk = wood_clunk(rng, n)
    lag = int(SR * rng.uniform(0.001, 0.003))
    clunk = np.concatenate([np.zeros(lag), clunk[:n - lag]])
    return clack / max(1e-9, np.max(np.abs(clack))) + 0.5 * clunk


def chamber():
    """The chamber - blades meet, the incoming swing is turned and the counter goes in: a quick DOUBLE clack, 'ka-TAK'.
    A lighter clack at 2.4 kHz, then ~70 ms later a harder, brighter one at 3.8 kHz with the strongest crack of any
    sound. The rhythm alone sets it apart from every single-hit block (Mark 2026-11-16: heavy block and chamber were
    "too similar"). Fixed seed and pitch: always the same recognisable sound."""
    rng = np.random.default_rng(9001)
    n = int(SR * 0.2)
    first = stick_clack(rng, 0.2, 2400, spread_oct=1.0, q=(7.0, 18.0), width_ms=0.4, rattles=1,
                        rattle_amp=(0.15, 0.2), crack=0.55, hp=650.0)
    second = stick_clack(rng, 0.2, 3800, spread_oct=0.9, q=(8.0, 20.0), width_ms=0.28, rattles=1,
                         rattle_amp=(0.12, 0.18), crack=0.85, hp=800.0)
    lag = int(SR * 0.07)
    x = 0.6 * first / max(1e-9, np.max(np.abs(first)))
    x[lag:] += (second / max(1e-9, np.max(np.abs(second))))[:n - lag]
    return x


def parry():
    """The parry - a perfectly timed block: two blades meeting, a sword 'TING'. The first pass rang a free steel bar
    (four clean partials, ~1 s) and sounded "like a bell" (Mark 2026-11-16). A blade is not a bell: a long thin plate
    has MANY close inharmonic modes high up (2-11 kHz), no single one leads, the high ones die fast and the whole ring is
    gone in about half a second; the edges also grind for a moment after the strike (the 'shing'). So: a very short
    hard metal contact through 46 inharmonic modes (bending series (n + 0.5)^2 jittered, plus random plate modes),
    energy weighted to 3-7 kHz, decays 40-380 ms falling with pitch; a 4-10 kHz grinding scrape for ~60 ms; a faint
    beat from detuned twins. Fixed seed and pitch."""
    rng = np.random.default_rng(4242)
    t = t_axis(0.7)
    n = len(t)
    freqs = [210.0 * (k + 0.5) ** 2 * rng.uniform(0.97, 1.03) for k in range(3, 8)]
    freqs = [f for f in freqs if f < 11000] + list(np.exp(rng.uniform(np.log(2200), np.log(11000), 40)))
    ring = np.zeros(n)
    for f in freqs:
        g = np.exp(-0.5 * (np.log2(f / 4600) / 0.9) ** 2) * rng.uniform(0.3, 1.0)
        decay = 0.38 * (2200 / f) ** 1.1 * rng.uniform(0.6, 1.2) + 0.04
        ph = rng.uniform(0, 2 * np.pi)
        ring += g * np.exp(-t / decay) * np.sin(2 * np.pi * f * t + ph)
        if rng.uniform() < 0.35:  # a detuned twin: the slow beat of a real blade
            ring += 0.5 * g * np.exp(-t / decay) * np.sin(2 * np.pi * f * rng.uniform(1.001, 1.004) * t + ph)
    ring /= max(1e-9, np.max(np.abs(ring)))
    # the strike: a sub-ms metal contact, bright and hard
    strike = filtered_noise(rng, n, "highpass", 3000) * np.exp(-t / 0.0006)
    # the shing: the edges grind past each other, a short bright scrape that swells and fades
    scrape = filtered_noise(rng, n, "bandpass", [4000, 10000], order=2)
    scrape *= (1 - np.exp(-t / 0.006)) * np.exp(-t / 0.03) * (0.7 + 0.3 * np.sin(2 * np.pi * 70 * t))
    x = (0.75 * ring + 0.45 * strike / max(1e-9, np.max(np.abs(strike)))
         + 0.35 * scrape / max(1e-9, np.max(np.abs(scrape))))
    return sosfilt(butter(4, 1200, btype="highpass", fs=SR, output="sos"), x)


def hit_other(i):
    """A blade landing on anything but another player (scavs, animals, anything with a DamageReceiver). It was built as
    the player-hit thud and "sounds like we're hitting corrugated metal" (Mark 2026-11-16) - the random low modes and the
    soft clipping make it clank - so it became the sound for everything that is not a player. A WIDE soft contact
    (3-5 ms) driving a dense, very low-Q cluster of modes at 55-300 Hz, a short 250-700 Hz band and a brief 900-3000 Hz
    smack, soft-clipped. Own seed per variant (the clack stream must stay untouched)."""
    rng = np.random.default_rng(5100 + i)
    t = t_axis(0.25)
    n = len(t)
    width = [3.5, 4.2, 3.0, 5.0][i]
    force = contacts(rng, n, [(0.0, 1.0, width)])
    body = fftconvolve(force, wood_body(rng, n, [120, 105, 140, 95][i], 1.0, 1.5, 4.0, count=30, lo=55.0, hi=300.0))[:n]
    body = sosfilt(butter(2, 45, btype="highpass", fs=SR, output="sos"), body) * np.exp(-t / 0.05)
    meat = filtered_noise(rng, n, "bandpass", [250, 700], order=2) * (1 - np.exp(-t / 0.002)) * np.exp(-t / 0.018)
    smack = filtered_noise(rng, n, "bandpass", [900, 3000], order=2) * np.exp(-t / 0.0035)
    x = (body / max(1e-9, np.max(np.abs(body)))
         + 0.75 * meat / max(1e-9, np.max(np.abs(meat)))
         + [0.3, 0.26, 0.34, 0.22][i] * smack / max(1e-9, np.max(np.abs(smack))))
    return np.tanh(1.4 * x / max(1e-9, np.max(np.abs(x))))


ENGINE_FLESH = os.path.join(os.environ.get("SBOX_ROOT", r"M:/SteamLibrary/steamapps/common/sbox"),
                            "core", "sounds", "Impacts", "Melee", "BluntWeapon")


def engine_flesh_body(k):
    """The first ~55-90 ms of the engine's own recorded melee flesh hit (core/sounds/Impacts/Melee/BluntWeapon/flesh-k):
    the real impact, a low uneven thump with its highs ~15 dB down. The recordings run 1.2-1.5 s and everything after the
    impact is a wet chattering tail - that tail is the "squelchy, like a wet sponge" Mark heard on 2026-11-16 - so it is cut:
    the clip ends where the low band has fallen 10 dB from its peak (55-90 ms), with a 15 ms fade, and a gentle 3.5 kHz
    low-pass calms the wet top."""
    sr, x = wavfile.read(os.path.join(ENGINE_FLESH, f"flesh-{k}.wav"))
    x = x.astype(float)
    if x.ndim > 1:
        x = x.mean(axis=1)
    if sr != SR:
        raise SystemExit(f"flesh-{k}.wav is {sr} Hz, expected {SR}")
    x /= max(1e-9, np.max(np.abs(x)))
    low = sosfilt(butter(2, 600, btype="lowpass", fs=SR, output="sos"), x)
    win = int(SR * 0.005)
    rms = np.array([np.sqrt(np.mean(low[a:a + win] ** 2)) for a in range(0, int(SR * 0.12), win)])
    peak = int(np.argmax(rms))
    cut = peak + 1
    while cut < len(rms) and rms[cut] > rms[peak] * 10 ** (-10 / 20):
        cut += 1
    end = int(np.clip(cut * win, SR * 0.055, SR * 0.09))
    fade = int(SR * 0.015)
    y = x[:end + fade].copy()
    y[end:] *= np.cos(np.linspace(0, np.pi / 2, len(y) - end)) ** 2
    y = sosfilt(butter(2, 3500, btype="lowpass", fs=SR, output="sos"), y)
    return y / max(1e-9, np.max(np.abs(y)))


def time_varying_formants(src, tracks):
    """Run `src` through parallel two-pole formant resonators whose (freq Hz, bandwidth Hz, gain) follow per-sample
    tracks: a list of (f[n], bw[n], g[n]) arrays."""
    n = len(src)
    out = np.zeros(n)
    for f, bw, g in tracks:
        y1 = y2 = 0.0
        r = np.exp(-np.pi * bw / SR)
        c1 = 2 * r * np.cos(2 * np.pi * f / SR)
        c2 = -r * r
        y = np.zeros(n)
        for k in range(n):
            v = (1 - r[k]) * src[k] + c1[k] * y1 + c2[k] * y2
            y2, y1 = y1, v
            y[k] = v
        out += g * y
    return out


def grunt(i):
    """The hit player's "ungh" (Mark 2026-11-16: "Maybe the player goes 'ungh'"). Formant speech synthesis, no recording:
    a glottal pulse train (Rosenberg pulses, differentiated for lip radiation) with jitter and shimmer, pitch jumping up
    on the blow then falling (~150 -> ~95 Hz, a man's pained grunt), a breathy burst at the onset and creak (vocal fry)
    as it dies; through four formants gliding from the vowel in "uh" (640 / 1190 / 2390 / 3300 Hz) into the nasal "ng"
    (F1 ~280 Hz, the upper formants sinking and fading). 0.24-0.34 s. Own seed per variant."""
    rng = np.random.default_rng(8100 + i)
    dur = [0.30, 0.26, 0.34, 0.28][i]
    n = int(SR * dur)
    t = np.arange(n) / SR
    u = t / dur
    f_hi, f_lo = [(158, 98), (172, 108), (146, 92), (164, 102)][i]
    f0 = f_lo + (f_hi - f_lo) * np.exp(-u / 0.45) * np.minimum(1, t / 0.015 + 0.85)
    # glottal pulses, one period at a time, with jitter / shimmer that grow into the creak at the end
    src = np.zeros(n)
    k = 0
    while k < n:
        uu = k / n
        creak = max(0.0, (uu - 0.62) / 0.38)
        T = SR / (f0[k] * (1 - 0.35 * creak)) * (1 + rng.normal(0, 0.012 + 0.08 * creak))
        T = max(20, int(T))
        tp, tn = int(0.42 * T), int(0.16 * T)
        pulse = np.zeros(T)
        pulse[:tp] = 0.5 * (1 - np.cos(np.pi * np.arange(tp) / tp))
        pulse[tp:tp + tn] = np.cos(0.5 * np.pi * np.arange(tn) / tn)
        amp = 1 + rng.normal(0, 0.05 + 0.25 * creak)
        m = min(T, n - k)
        src[k:k + m] += amp * pulse[:m]
        k += T
    src = np.diff(src, prepend=0.0)
    breath = filtered_noise(rng, n, "bandpass", [500, 4500], order=2)
    breath *= 0.35 * np.exp(-t / 0.03) + 0.06
    src = src / max(1e-9, np.max(np.abs(src))) + 0.5 * breath / max(1e-9, np.max(np.abs(breath)))
    # "uh" -> "ng": the glide starts ~40 % in
    g = np.clip((u - 0.4) / 0.35, 0, 1)
    g = g * g * (3 - 2 * g)
    lerp = lambda a, b: a + (b - a) * g
    tracks = [
        (lerp(640, 280), lerp(90, 70), 1.0),
        (lerp(1190, 1050), lerp(110, 250), lerp(0.7, 0.12)),
        (lerp(2390, 2300), lerp(150, 300), lerp(0.35, 0.05)),
        (np.full(n, 3300.0), np.full(n, 250.0), lerp(0.18, 0.02)),
    ]
    x = time_varying_formants(src, tracks)
    env = (1 - np.exp(-t / 0.008)) * np.clip(1.15 - u, 0, 1) ** 1.3
    x = x * env
    return x / max(1e-9, np.max(np.abs(x)))


def hit_player(i):
    """A player got hit on bare flesh: the engine's real flesh impact, tail cut (see engine_flesh_body), and the hit
    player's "ungh" a moment later (see grunt). History (Mark 2026-11-16): the engine's whole recording was "squelchy, like
    a wet sponge"; synthesised thuds read as a kick drum, "corrugated metal" and "a trashcan being hit" - flesh needs a
    real recording. Own seed per variant."""
    rng = np.random.default_rng(6100 + i)
    body = engine_flesh_body(i + 1)
    voice = grunt(i)
    at = int(SR * [0.045, 0.06, 0.05, 0.04][i])
    n = max(len(body), at + len(voice))
    x = np.zeros(n)
    x[:len(body)] += body
    x[at:at + len(voice)] += [0.55, 0.5, 0.6, 0.5][i] * voice * rng.uniform(0.95, 1.05)
    return x


def burn_old_hit_draws(rng):
    """The approved clacks (Mark 2026-11-16: "You nailed the clacking sound finally!") were generated from this stream
    after the old hit's draws; take the same draws so they stay byte-identical."""
    for _ in range(4):
        rng.uniform(size=3)
        rng.standard_normal(int(SR * 0.3))
        rng.standard_normal(int(SR * 0.3))


def write_wav(name, x):
    path = os.path.join(OUT, name + ".wav")
    wavfile.write(path, SR, (np.clip(x, -1, 1) * 32767).astype(np.int16))
    return path


def write_event(name, sounds, pitch, volume=(1.0, 1.0)):
    evt = {
        "UI": False,
        "Volume": f"{volume[0]:.2f},{volume[1]:.2f},0",
        "Pitch": f"{pitch[0]:.2f},{pitch[1]:.2f},0",
        "Decibels": 70,
        "SelectionMode": "Random",
        "Sounds": [f"sounds/combat/{s}.vsnd" for s in sounds],
        "Occlusion": True,
        "Reflections": True,
        "AirAbsorption": True,
        "Transmission": True,
        "OcclusionRadius": 64,
        "DistanceAttenuation": True,
        "ResourceVersion": 0,
        "__version": 0,
        "__references": [],
    }
    with open(os.path.join(OUT, name + ".sound"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(evt, f, indent=2)
        f.write("\n")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--audition", help="also write every variant in a row to this WAV")
    args = ap.parse_args()
    os.makedirs(OUT, exist_ok=True)
    rng = np.random.default_rng(20261116)
    burn_old_hit_draws(rng)

    sets = {
        "melee_hit_player": [(f"hit_player_{i + 1:02d}", finish(hit_player(i), 0.89)) for i in range(4)],
        "melee_hit_other": [(f"hit_other_{i + 1:02d}", finish(hit_other(i), 0.89)) for i in range(4)],
        "melee_block": [(f"block_{i + 1:02d}", finish(block(rng, i), 0.8)) for i in range(5)],
        "melee_block_heavy": [(f"block_heavy_{i + 1:02d}", finish(block_heavy(rng, i), 0.89)) for i in range(3)],
        "melee_chamber": [("chamber_01", finish(chamber(), 0.89))],
        "melee_parry": [("parry_01", finish(parry(), 0.85, fade_ms=60))],
    }
    # random pitch on top of the variants; the chamber stays put so it is always the same recognisable note
    pitch = {"melee_hit_player": (0.94, 1.06), "melee_hit_other": (0.94, 1.06), "melee_block": (0.97, 1.03), "melee_block_heavy": (0.96, 1.04),
             "melee_chamber": (1.0, 1.0), "melee_parry": (1.0, 1.0)}
    for evt, items in sets.items():
        for name, x in items:
            write_wav(name, x)
        write_event(evt, [n for n, _ in items], pitch[evt])
        print(evt, [n for n, _ in items])

    if args.audition:
        gap = np.zeros(int(SR * 0.45))
        parts = []
        for evt in ["melee_hit_player", "melee_hit_other", "melee_block", "melee_block_heavy",
                    "melee_chamber", "melee_parry"]:
            for _, x in sets[evt]:
                parts += [x, gap]
            parts.append(np.zeros(int(SR * 0.6)))
        wavfile.write(args.audition, SR, (np.clip(np.concatenate(parts), -1, 1) * 32767).astype(np.int16))
        print("audition", args.audition)


if __name__ == "__main__":
    main()
