"""playtest_report.py - turn a playtest logger file into the tables Mark reads after a session.

    python Tools/playtest_report.py <file.jsonl | folder>

With a folder it reads the newest .jsonl in it (files are named <host steam name>_<date_time>_v<build>.jsonl). The logger writes to the s&box data folder:
<sbox>/data/local/survivalgamebasics#local/2Tgames/playtest/ (FileSystem.Data). Plain Python, no deps.

Sections: session; per-player timeline (first craft / build / kill / death, deaths and causes, damage
taken by source); weapon vs entity (hits, kills, median time-to-kill, hits per kill, killer HP% / ST%
at the kill, damage taken per kill); blocks; stamina (empties per 10 min, how long empty, what the
player was doing when it emptied, mean stamina per flag); grapple (attaches per 10 min, spam share
= re-fires under 1 s after the last attach, long swings >= 4 s, speed and distance medians, rejects);
sessions (join, leave, overtime past a `playtest_mark stop` marker, leaves right after a death); movement
(roam radius from the first position, path length, mean distance to the nearest other player); client
performance (average and 1 % low fps, menu share, resolution per player).
"""
from __future__ import annotations

import json
import math
import os
import statistics
import sys
from collections import Counter, defaultdict


def load(path: str) -> list[dict]:
    if os.path.isdir(path):
        files = sorted(
            (os.path.join(path, f) for f in os.listdir(path) if f.endswith(".jsonl")),
            key=os.path.getmtime,
        )
        if not files:
            sys.exit(f"no .jsonl in {path}")
        path = files[-1]
    events = []
    with open(path, encoding="utf-8") as fh:
        for n, line in enumerate(fh, 1):
            line = line.strip()
            if not line:
                continue
            try:
                events.append(json.loads(line))
            except json.JSONDecodeError as e:
                print(f"  (skipped line {n}: {e})")
    print(f"file: {path}\nevents: {len(events)}\n")
    return events


def med(xs):
    xs = [x for x in xs if x is not None and x >= 0]
    return statistics.median(xs) if xs else float("nan")


def fmt(x, nd=1):
    if x != x:  # nan
        return "-"
    return f"{x:.{nd}f}"


def mmss(t):
    if t is None:
        return "-"
    return f"{int(t // 60):d}:{int(t % 60):02d}"


def table(headers, rows):
    rows = [[str(c) for c in r] for r in rows]
    widths = [max(len(h), *(len(r[i]) for r in rows)) if rows else len(h) for i, h in enumerate(headers)]
    print("  " + "  ".join(h.ljust(w) for h, w in zip(headers, widths)))
    for r in rows:
        print("  " + "  ".join(c.ljust(w) for c, w in zip(r, widths)))
    print()


def section(title):
    print(f"== {title} ==")


def report(events: list[dict]) -> None:
    by = defaultdict(list)
    for e in events:
        by[e.get("ev")].append(e)

    end_t = max((e["t"] for e in events), default=0.0)
    minutes = max(end_t / 60.0, 1e-6)

    # ---- session
    section("session")
    for s in by["session"]:
        print(f"  scene={s.get('scene')} build={s.get('build')} started={s.get('startedAt')} host={s.get('host')} networked={s.get('networked')}")
    print(f"  length {mmss(end_t)}  players: {', '.join(p['player'] for p in by['player_join'])}")
    for m in by["mark"]:
        print(f"  mark @ {mmss(m['t'])}: {m.get('text')}")
    print()

    players = [p["player"] for p in by["player_join"]]

    # ---- per-player timeline
    section("per player")
    rows = []
    for p in players:
        def first(ev, key="player"):
            ts = [e["t"] for e in by[ev] if e.get(key) == p]
            return mmss(min(ts)) if ts else "-"

        deaths = [e for e in by["player_death"] if e["player"] == p]
        causes = Counter(e.get("cause") for e in deaths)
        kills = [e for e in by["kill"] if e.get("killer") == p]
        taken = defaultdict(float)
        for e in by["player_damaged"]:
            if e["player"] == p:
                taken[e.get("src")] += e.get("dmg", 0.0)
        top_taken = ", ".join(f"{k} {v:.0f}" for k, v in sorted(taken.items(), key=lambda kv: -kv[1])[:3])
        rows.append([
            p,
            first("gain"), first("craft"), first("build"), first("kill", "killer"), first("player_death"),
            len(kills), len(deaths),
            ", ".join(f"{k}x{v}" for k, v in causes.most_common(3)) or "-",
            top_taken or "-",
        ])
    table(["player", "1st gain", "1st craft", "1st build", "1st kill", "1st death", "kills", "deaths", "death causes", "dmg taken by"], rows)

    # ---- weapon vs entity
    section("weapon vs entity (player kills)")
    agg = defaultdict(lambda: {"hits": 0, "dmg": 0.0, "kills": 0, "ttk": [], "hpk": [], "hp": [], "st": [], "taken": [], "heavy": 0})
    for e in by["hit"]:
        if e.get("atkKind") != "player":
            continue
        k = (e.get("weapon"), e.get("tgt"))
        a = agg[k]
        a["hits"] += 1
        a["dmg"] += e.get("dmg", 0.0)
        if e.get("kind") == "heavy":
            a["heavy"] += 1
    for e in by["kill"]:
        if e.get("killerKind") != "player":
            continue
        k = (e.get("weapon"), e.get("victim"))
        a = agg[k]
        a["kills"] += 1
        a["ttk"].append(e.get("ttkS", -1))
        a["hpk"].append(e.get("hits", 0))
        if e.get("atkHpMax"):
            a["hp"].append(100.0 * e["atkHp"] / e["atkHpMax"])
        if e.get("atkStMax"):
            a["st"].append(100.0 * e["atkSt"] / e["atkStMax"])
        a["taken"].append(e.get("taken", 0.0))
    rows = []
    for (weapon, tgt), a in sorted(agg.items(), key=lambda kv: (-kv[1]["kills"], -kv[1]["hits"])):
        rows.append([
            weapon, tgt, a["hits"], f"{100.0 * a['heavy'] / a['hits']:.0f}%" if a["hits"] else "-",
            fmt(a["dmg"] / a["hits"]) if a["hits"] else "-",
            a["kills"], fmt(med(a["ttk"])), fmt(med(a["hpk"]), 0),
            fmt(med(a["hp"]), 0) + "%" if a["hp"] else "-", fmt(med(a["st"]), 0) + "%" if a["st"] else "-",
            fmt(med(a["taken"])),
        ])
    table(["weapon", "entity", "hits", "heavy", "dmg/hit", "kills", "med TTK s", "hits/kill", "HP at kill", "ST at kill", "dmg taken/kill"], rows)

    # ---- damage kinds (what actually hurt entities)
    kinds = Counter(e.get("kind") for e in by["hit"] if e.get("atkKind") == "player")
    if kinds:
        print("  player damage by kind: " + ", ".join(f"{k} {v}" for k, v in kinds.most_common()) + "\n")

    # ---- entities hurting players
    section("entities vs players")
    agg2 = defaultdict(lambda: {"hits": 0, "dmg": 0.0, "kills": 0})
    for e in by["player_damaged"]:
        a = agg2[(e.get("src"), e.get("kind"))]
        a["hits"] += 1
        a["dmg"] += e.get("dmg", 0.0)
    for e in by["player_death"]:
        for (src, kind), a in agg2.items():
            if src == e.get("cause"):
                a["kills"] += 1
                break
    table(["source", "kind", "hits on players", "dmg/hit", "player deaths"],
          [[s, k, a["hits"], fmt(a["dmg"] / a["hits"]) if a["hits"] else "-", a["kills"]]
           for (s, k), a in sorted(agg2.items(), key=lambda kv: -kv[1]["dmg"])])

    # ---- blocks
    if by["block"]:
        section("blocks")
        c = Counter((e.get("def"), e.get("outcome"), "parry" if e.get("parry") else "block") for e in by["block"])
        table(["defender", "outcome", "type", "count"], [[d, o, t, n] for (d, o, t), n in c.most_common()])

    # ---- stamina
    section("stamina")
    rows = []
    for p in players:
        empties = [e for e in by["stamina_empty"] if e["player"] == p]
        recov = [e.get("emptyS", 0.0) for e in by["stamina_recovered"] if e["player"] == p]
        flags = Counter()
        for e in empties:
            for f in (e.get("flags") or "idle").split(","):
                flags[f] += 1
        samples = [e for e in by["vitals"] if e["player"] == p and e.get("stMax")]
        mean_st = statistics.mean(100.0 * e["st"] / e["stMax"] for e in samples) if samples else float("nan")
        low = sum(1 for e in samples if e["st"] / e["stMax"] < 0.25)
        rows.append([
            p, len(empties), fmt(len(empties) / minutes * 10.0), fmt(med(recov)),
            ", ".join(f"{k} {v}" for k, v in flags.most_common(3)) or "-",
            fmt(mean_st, 0) + "%", f"{100.0 * low / len(samples):.0f}%" if samples else "-",
        ])
    table(["player", "empties", "per 10 min", "med s empty", "doing what when empty", "mean ST", "time under 25%"], rows)

    # ---- health
    section("health")
    rows = []
    for p in players:
        samples = [e for e in by["vitals"] if e["player"] == p and e.get("hpMax")]
        mean_hp = statistics.mean(100.0 * e["hp"] / e["hpMax"] for e in samples) if samples else float("nan")
        low = sum(1 for e in samples if e["hp"] / e["hpMax"] < 0.3)
        dmg = sum(e.get("dmg", 0.0) for e in by["player_damaged"] if e["player"] == p)
        rows.append([p, fmt(mean_hp, 0) + "%", f"{100.0 * low / len(samples):.0f}%" if samples else "-", fmt(dmg, 0), fmt(dmg / minutes)])
    table(["player", "mean HP", "time under 30%", "dmg taken", "dmg/min"], rows)

    # ---- grapple
    section("grapple")
    rows = []
    for p in players:
        att = [e for e in by["grapple_attach"] if e["player"] == p]
        det = [e for e in by["grapple_detach"] if e["player"] == p]
        rej = Counter(e.get("why") for e in by["grapple_reject"] if e["player"] == p)
        if not att and not rej:
            continue
        spam = sum(1 for e in att if 0 <= e.get("sinceAttachS", -1) < 1.0)
        durs = [e.get("durS", 0.0) for e in det]
        long_ = sum(1 for d in durs if d >= 4.0)
        kinds = Counter(e.get("kind") for e in att)
        rows.append([
            p, len(att), fmt(len(att) / minutes * 10.0),
            f"{100.0 * spam / len(att):.0f}%" if att else "-",
            fmt(med(durs)), f"{100.0 * long_ / len(durs):.0f}%" if durs else "-",
            fmt(med([e.get("maxMps", 0.0) for e in det])), fmt(max((e.get("maxMps", 0.0) for e in det), default=float("nan"))),
            fmt(med([e.get("distM", 0.0) for e in det])), fmt(med([e.get("riseM", 0.0) for e in det])),
            fmt(med([e.get("lenM", 0.0) for e in att])),
            ", ".join(f"{k} {v}" for k, v in kinds.most_common()),
            ", ".join(f"{k} {v}" for k, v in rej.most_common()) or "-",
        ])
    table(["player", "attaches", "per 10 min", "re-fire <1s", "med swing s", "swings >=4s", "med top m/s", "best m/s", "med dist m", "med rise m", "med rope m", "kinds", "rejects"], rows)
    reasons = Counter(e.get("reason") for e in by["grapple_detach"])
    if reasons:
        print("  detach reasons: " + ", ".join(f"{k} {v}" for k, v in reasons.most_common()) + "\n")

    # ---- sessions: join / leave / overtime
    section("sessions")
    stop_marks = [m["t"] for m in by["mark"] if "stop" in (m.get("text") or "").lower()]
    stop_t = min(stop_marks) if stop_marks else None
    rows = []
    for p in players:
        joins = [e["t"] for e in by["player_join"] if e["player"] == p]
        leaves = [e for e in by["player_leave"] if e["player"] == p]
        left_t = leaves[-1]["t"] if leaves else None
        rage = sum(1 for e in leaves if 0 <= e.get("sinceDeathS", -1) <= 120)
        played = (left_t if left_t is not None else end_t) - (min(joins) if joins else 0.0)
        overtime = "-" if stop_t is None else (f"+{mmss(max(0.0, (left_t if left_t is not None else end_t) - stop_t))}")
        rows.append([p, mmss(min(joins)) if joins else "-", mmss(left_t) if left_t is not None else "still in", mmss(played), overtime, rage])
    table(["player", "joined", "left", "played", "past stop mark", "left <2 min after death"], rows)
    if stop_t is None:
        print("  (no 'stop' marker: run playtest_mark \"stop\" at the agreed end so overtime can be measured)\n")

    # ---- movement
    section("movement")
    rows = []
    pos_by_player = defaultdict(list)
    for e in by["vitals"]:
        if "x" in e:
            pos_by_player[e["player"]].append((e["t"], e["x"], e["y"], e["z"]))
    for p in players:
        pts = pos_by_player.get(p) or []
        if len(pts) < 2:
            continue
        x0, y0 = pts[0][1], pts[0][2]
        roam = max(math.hypot(x - x0, y - y0) for _, x, y, _ in pts)
        path = sum(math.hypot(b[1] - a[1], b[2] - a[2]) for a, b in zip(pts, pts[1:]))
        still = sum(1 for a, b in zip(pts, pts[1:]) if math.hypot(b[1] - a[1], b[2] - a[2]) < 0.5)
        nearest = []
        for t, x, y, _ in pts:
            best = None
            for q, qpts in pos_by_player.items():
                if q == p:
                    continue
                for qt, qx, qy, _ in qpts:
                    if abs(qt - t) <= 1.1:
                        d = math.hypot(qx - x, qy - y)
                        best = d if best is None or d < best else best
                        break
            if best is not None:
                nearest.append(best)
        rows.append([p, fmt(roam, 0), fmt(path, 0), f"{100.0 * still / (len(pts) - 1):.0f}%", fmt(med(nearest), 0) if nearest else "-",
                     f"{100.0 * sum(1 for d in nearest if d <= 30) / len(nearest):.0f}%" if nearest else "-"])
    table(["player", "roam radius m", "path m", "time standing still", "med dist to nearest m", "time within 30 m of someone"], rows)

    # ---- client performance
    if by["client_perf"]:
        section("client performance")
        rows = []
        for p in sorted({e.get("player") for e in by["client_perf"]}):
            reps = [e for e in by["client_perf"] if e.get("player") == p]
            win = sum(e.get("windowS", 0.0) for e in reps)
            menu = sum(e.get("menuS", 0.0) for e in reps)
            rows.append([p, len(reps), fmt(med([e.get("avgFps") for e in reps]), 0), fmt(min(e.get("lowFps", 0.0) for e in reps), 0),
                         fmt(med([e.get("lowFps") for e in reps]), 0), f"{100.0 * menu / win:.0f}%" if win else "-",
                         Counter(e.get("res") for e in reps).most_common(1)[0][0]])
        table(["player", "reports", "med avg fps", "worst 1% low", "med 1% low", "time in menus", "resolution"], rows)

    # ---- economy
    section("crafts / builds / gains")
    crafts = Counter(e.get("recipe") for e in by["craft"])
    builds = Counter(e.get("piece") for e in by["build"])
    gains = Counter()
    for e in by["gain"]:
        if e.get("src") != "craft":
            gains[e.get("item")] += e.get("amount", 0)
    print("  crafts: " + (", ".join(f"{k} x{v}" for k, v in crafts.most_common(12)) or "-"))
    print("  builds: " + (", ".join(f"{k} x{v}" for k, v in builds.most_common(12)) or "-"))
    print("  picked up: " + (", ".join(f"{k} {v}" for k, v in gains.most_common(12)) or "-"))
    print()


if __name__ == "__main__":
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    report(load(sys.argv[1]))
