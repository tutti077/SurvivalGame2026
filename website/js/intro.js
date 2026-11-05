/* ==========================================================================
   IntroScene — the ~3 s opening trailer.
   Side-on tracking shot: two parallel tracks, a red train rushes in on the
   top track, crosses the diagonal switch to the bottom track and brakes
   into the exact pose of the Two Track Games logo; then the name slams in.
   Everything is drawn in banner units (2000 × 667). The rails, switch and
   letters are the #tt-banner paths from index.html (Path2D), so the last
   frame is the hero logo exactly.
   ========================================================================== */
(function () {
  "use strict";

  const clamp = (v, a, b) => (v < a ? a : v > b ? b : v);
  const lerp = (a, b, t) => a + (b - a) * t;
  const smooth = (a, b, x) => { const t = clamp((x - a) / (b - a), 0, 1); return t * t * (3 - 2 * t); };

  /* ---- logo geometry (banner units) ---- */
  const TOP = 197, BOT = 418, RAIL = 28, X0 = 84, X1 = 1916;
  const B = [783.5, TOP], C = [1002.5, BOT];          // switch centre line
  const CX = 1000, CY = 333.5;                         // banner centre
  const PATH = [[-6000, TOP], B, C, [7000, BOT]];
  const SEG = [];
  let total = 0;
  for (let i = 0; i < PATH.length - 1; i++) {
    const a = PATH[i], b = PATH[i + 1];
    const len = Math.hypot(b[0] - a[0], b[1] - a[1]);
    SEG.push({ a, b, len, s0: total, dx: (b[0] - a[0]) / len, dy: (b[1] - a[1]) / len });
    total += len;
  }
  const S_B = SEG[1].s0, S_C = SEG[2].s0;
  const S_FINAL = S_C + (1298 - C[0]);           // head stops at x = 1298 on the bottom track
  const TRAIN = S_FINAL - (S_B - (B[0] - 544));  // tail stops at x = 544 on the top track
  const S_START = S_FINAL - 5600;
  const DIAG = SEG[1];
  const WORDS = [{ id: "tt-two", cx: 435, from: -1 }, { id: "tt-track", cx: 1439, from: 1 }];
  const WORD_CY = 311.5, GAMES_CX = 998, GAMES_CY = 486.5;
  function shape(id) { const el = document.getElementById(id); return el ? new Path2D(el.getAttribute("d")) : new Path2D(); }

  /* ---- timeline (s) ---- */
  const T_IN = 0.12, T_STOP = 1.9, T_WORDS = 1.98, T_GAMES = 2.32, T_DONE = 3.15;

  function at(s) {
    for (let i = SEG.length - 1; i >= 0; i--) {
      const g = SEG[i];
      if (s >= g.s0 || i === 0) { const d = s - g.s0; return [g.a[0] + g.dx * d, g.a[1] + g.dy * d, g.dx, g.dy]; }
    }
  }
  function sub(s0, s1) {
    const pts = [at(s0)];
    for (let i = 1; i < SEG.length; i++) if (SEG[i].s0 > s0 && SEG[i].s0 < s1) pts.push(SEG[i].a);
    pts.push(at(s1));
    return pts;
  }
  function headS(t) {
    // full speed for 80% of the run, then a hard linear brake through the switch
    const u = clamp((t - T_IN) / (T_STOP - T_IN), 0, 1), k = 0.8;
    const d = u < k ? u : k + (u - k) - ((u - k) * (u - k)) / (2 * (1 - k));
    return S_START + (S_FINAL - S_START) * (d / (k + (1 - k) / 2));
  }

  function makeGrain() {
    const g = document.createElement("canvas");
    g.width = g.height = 192;
    const x = g.getContext("2d"), d = x.createImageData(192, 192);
    for (let i = 0; i < d.data.length; i += 4) { const n = (Math.random() * 255) | 0; d.data[i] = d.data[i + 1] = d.data[i + 2] = n; d.data[i + 3] = 255; }
    x.putImageData(d, 0, 0);
    return g;
  }

  class IntroScene {
    constructor(canvas, opts) {
      this.canvas = canvas;
      this.ctx = canvas.getContext("2d");
      this.onDone = (opts && opts.onDone) || null;
      this.t0 = null; this.last = 0; this.running = false; this.done = false;
      this.sparks = [];
      this.grain = makeGrain();
      this.prevS = headS(0);
      this.resize = this.resize.bind(this);
      this.frame = this.frame.bind(this);
      window.addEventListener("resize", this.resize);
      this.resize();
      this.rails = shape("tt-rails");
      this.switchShape = shape("tt-switch");
      this.words = WORDS.map((w) => shape(w.id));
      this.games = shape("tt-games");
    }

    resize() {
      const r = this.canvas.getBoundingClientRect();
      this.dpr = Math.min(window.devicePixelRatio || 1, 2);
      this.w = Math.max(1, r.width); this.h = Math.max(1, r.height);
      this.canvas.width = Math.round(this.w * this.dpr);
      this.canvas.height = Math.round(this.h * this.dpr);
      this.S0 = Math.min(this.w * 0.94, 1240) / 2000;   // must match .hero-logo width
    }

    start() { if (!this.running) { this.running = true; requestAnimationFrame(this.frame); } }
    destroy() { this.running = false; window.removeEventListener("resize", this.resize); }

    frame(now) {
      if (!this.running) return;
      requestAnimationFrame(this.frame);
      if (this.t0 === null) { this.t0 = now; this.last = now; }
      const t = (now - this.t0) / 1000;
      const dt = Math.min(0.05, (now - this.last) / 1000);
      this.last = now;
      this.step(t, dt);
      this.draw(t);
      if (!this.done && t >= T_DONE) { this.done = true; this.onDone && this.onDone(); }
    }

    step(t, dt) {
      const s = headS(t);
      this.speed = dt > 0 ? (s - this.prevS) / dt : 0;
      this.prevS = s;
      // sparks: through the switch and while braking
      const h = at(s);
      const inSwitch = s > S_B - 60 && s < S_C + 90;
      const braking = t > T_STOP - 0.45 && t < T_STOP;
      if ((inSwitch || braking) && this.speed > 40) {
        for (let i = 0; i < 7; i++) {
          const side = Math.random() < 0.5 ? -1 : 1;
          this.sparks.push({
            x: h[0] - h[2] * Math.random() * 30, y: h[1] + side * RAIL * 0.5,
            vx: -h[2] * (200 + Math.random() * 700) + (Math.random() - 0.5) * 300,
            vy: side * (120 + Math.random() * 420) - 100,
            life: 0.18 + Math.random() * 0.3, age: 0, hot: Math.random() < 0.5,
          });
        }
      }
      for (let i = this.sparks.length - 1; i >= 0; i--) {
        const p = this.sparks[i];
        p.age += dt; p.vy += 1400 * dt;
        p.x += p.vx * dt; p.y += p.vy * dt;
        if (p.age > p.life) this.sparks.splice(i, 1);
      }
    }

    camera(t, head) {
      const b = smooth(0.95, T_STOP + 0.05, t);
      const Z = lerp(2.5, 1, b * b * (3 - 2 * b));
      const fx = Math.max(-3500, head[0] + 190), fy = head[1];
      return { x: lerp(fx, CX, b), y: lerp(fy, CY, b), Z, roll: lerp(-0.06, 0, b), b };
    }

    draw(t) {
      const c = this.ctx, w = this.w, h = this.h;
      const s = headS(t), head = at(s);
      const cam = this.camera(t, head);
      const S = this.S0 * cam.Z;
      const impact = t - T_STOP;
      const speedK = clamp(this.speed / 4600, 0, 1);

      c.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
      c.fillStyle = "#000";
      c.fillRect(0, 0, w, h);

      // screen shake on impact
      let sx = 0, sy = 0;
      if (impact > 0 && impact < 0.4) { const a = 14 * (1 - impact / 0.4); sx = (Math.random() - 0.5) * a; sy = (Math.random() - 0.5) * a; }

      // world transform
      const toWorld = () => {
        c.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
        c.translate(w / 2 + sx, h / 2 + sy);
        c.rotate(cam.roll);
        c.scale(S, S);
        c.translate(-cam.x, -cam.y);
      };
      toWorld();

      // red atmosphere around the train
      const glowR = 1200;
      let g = c.createRadialGradient(head[0] - 200, head[1], 0, head[0] - 200, head[1], glowR);
      g.addColorStop(0, "rgba(253,15,45," + (0.16 + 0.1 * speedK) + ")");
      g.addColorStop(1, "rgba(253,15,45,0)");
      c.fillStyle = g;
      c.fillRect(head[0] - 200 - glowR, head[1] - glowR, glowR * 2, glowR * 2);

      const hw = (w / 2) / S * 1.25, hh = (h / 2) / S * 1.25;
      const xmin = cam.x - hw - 60, xmax = cam.x + hw + 60;
      const detail = clamp((cam.Z - 1.2) / 0.8, 0, 1);
      const outside = 1 - smooth(1.55, 2.05, t);
      const lit = impact > 0 ? 1 : 0.55 + 0.15 * cam.b;

      // tracks
      for (const y of [TOP, BOT]) {
        this.track(y, xmin, Math.min(xmax, X0), detail, outside, lit);
        this.track(y, Math.max(xmin, X1), xmax, detail, outside, lit);
        this.track(y, Math.max(xmin, X0), Math.min(xmax, X1), detail, 1, lit, true);
      }
      // inside the logo the solid rails are the banner's own rail shapes (with the cut either side of the switch)
      if (detail < 1) {
        const v = Math.round(lerp(120, 250, lit));
        c.globalAlpha = 1 - detail;
        c.fillStyle = "rgb(" + v + "," + v + "," + v + ")";
        c.fill(this.rails);
        c.globalAlpha = 1;
      }
      // the crossover itself (only while it reads as track)
      if (detail > 0) this.crossover(detail * outside);

      // headlight wash ahead of the train
      c.globalCompositeOperation = "lighter";
      const lx = head[0] + head[2] * 160, ly = head[1] + head[3] * 160;
      g = c.createRadialGradient(lx, ly, 0, lx, ly, 700);
      g.addColorStop(0, "rgba(255,255,255," + (0.22 * (1 - cam.b * 0.7)) + ")");
      g.addColorStop(1, "rgba(255,255,255,0)");
      c.fillStyle = g;
      c.fillRect(lx - 700, ly - 700, 1400, 1400);
      c.globalCompositeOperation = "source-over";

      // motion trail behind the tail
      const tailS = s - TRAIN;
      const tail = at(tailS);
      if (speedK > 0.02) {
        const len = 1200 * speedK;
        g = c.createLinearGradient(tail[0] - len, 0, tail[0], 0);
        g.addColorStop(0, "rgba(253,15,45,0)");
        g.addColorStop(1, "rgba(253,15,45," + 0.55 * speedK + ")");
        c.fillStyle = g;
        c.fillRect(tail[0] - len, TOP - RAIL / 2, len, RAIL);
      }

      // the train: carriages along the centre line while moving, the logo's exact switch shape once stopped
      c.save();
      c.shadowColor = "rgba(253,15,45,0.9)";
      c.shadowBlur = 26 + 30 * speedK;
      c.fillStyle = c.strokeStyle = "#fd0f2d";
      if (impact < 0) {
        const pts = sub(tailS, s);
        c.lineWidth = RAIL;
        c.lineJoin = "miter";
        c.lineCap = "butt";
        c.setLineDash([122, 7]);
        c.beginPath();
        c.moveTo(pts[0][0], pts[0][1]);
        for (let i = 1; i < pts.length; i++) c.lineTo(pts[i][0], pts[i][1]);
        c.stroke();
      } else {
        c.fill(this.switchShape);
      }
      c.restore();

      // headlight
      if (impact < 0.25) {
        const a = impact < 0 ? 1 : 1 - impact / 0.25;
        c.globalCompositeOperation = "lighter";
        g = c.createRadialGradient(head[0], head[1], 0, head[0], head[1], 120);
        g.addColorStop(0, "rgba(255,255,255," + 0.95 * a + ")");
        g.addColorStop(0.15, "rgba(255,255,255," + 0.4 * a + ")");
        g.addColorStop(1, "rgba(255,255,255,0)");
        c.fillStyle = g;
        c.fillRect(head[0] - 120, head[1] - 120, 240, 240);
        // anamorphic streak
        g = c.createLinearGradient(head[0] - 900, 0, head[0] + 900, 0);
        g.addColorStop(0, "rgba(253,15,45,0)");
        g.addColorStop(0.45, "rgba(255,60,50," + 0.25 * a + ")");
        g.addColorStop(0.5, "rgba(255,255,255," + 0.6 * a + ")");
        g.addColorStop(0.55, "rgba(255,60,50," + 0.25 * a + ")");
        g.addColorStop(1, "rgba(253,15,45,0)");
        c.fillStyle = g;
        c.fillRect(head[0] - 900, head[1] - 4, 1800, 8);
        c.globalCompositeOperation = "source-over";
      }

      // sparks
      c.globalCompositeOperation = "lighter";
      c.lineCap = "round";
      for (const p of this.sparks) {
        const a = 1 - p.age / p.life;
        c.strokeStyle = p.hot ? "rgba(255,255,255," + a + ")" : "rgba(255,50,40," + a + ")";
        c.lineWidth = 4;
        c.beginPath(); c.moveTo(p.x, p.y); c.lineTo(p.x - p.vx * 0.025, p.y - p.vy * 0.025); c.stroke();
      }
      c.globalCompositeOperation = "source-over";

      // wordmark
      if (t >= T_WORDS) this.drawWords(t);

      // ---- screen space ----
      c.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
      // speed streaks
      if (speedK > 0.25) {
        c.globalCompositeOperation = "lighter";
        for (let i = 0; i < 18; i++) {
          const y = Math.random() * h, x = Math.random() * w, l = w * (0.1 + Math.random() * 0.35) * speedK;
          c.fillStyle = Math.random() < 0.3 ? "rgba(253,15,45,0.25)" : "rgba(255,255,255,0.07)";
          c.fillRect(x, y, l, 1 + Math.random() * 1.5);
        }
        c.globalCompositeOperation = "source-over";
      }
      // vignette
      g = c.createRadialGradient(w / 2, h / 2, Math.min(w, h) * 0.3, w / 2, h / 2, Math.max(w, h) * 0.75);
      g.addColorStop(0, "rgba(0,0,0,0)");
      g.addColorStop(1, "rgba(0,0,0,0.8)");
      c.fillStyle = g;
      c.fillRect(0, 0, w, h);
      // grain
      c.globalAlpha = 0.07;
      c.globalCompositeOperation = "overlay";
      const ox = (Math.random() * 192) | 0, oy = (Math.random() * 192) | 0;
      c.translate(-ox, -oy);
      c.fillStyle = c.createPattern(this.grain, "repeat");
      c.fillRect(ox, oy, w, h);
      c.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
      c.globalCompositeOperation = "source-over";
      c.globalAlpha = 1;
      // impact flash
      if (impact > -0.05) {
        const a = impact < 0 ? (impact + 0.05) / 0.05 * 0.6 : 0.6 * Math.exp(-impact * 7);
        if (a > 0.004) { c.fillStyle = "rgba(255,255,255," + a + ")"; c.fillRect(0, 0, w, h); }
      }
      // letterbox
      const open = smooth(2.0, 2.65, t);
      const bar = Math.min(h * 0.12, Math.max(h * 0.06, (h - w / 2.39) / 2)) * (1 - open);
      if (bar > 0.5) { c.fillStyle = "#000"; c.fillRect(0, 0, w, bar); c.fillRect(0, h - bar, w, bar); }
      // fade up from black
      if (t < 0.3) { c.fillStyle = "rgba(0,0,0," + (1 - t / 0.3) + ")"; c.fillRect(0, 0, w, h); }
    }

    track(y, xa, xb, detail, alpha, lit, inLogo) {
      if (xb <= xa || alpha <= 0.002) return;
      const c = this.ctx;
      if (detail > 0) {
        c.globalAlpha = detail * alpha;
        c.fillStyle = "#2c2c2c";
        for (let x = Math.ceil(xa / 15) * 15; x < xb; x += 15) c.fillRect(x, y - 24, 6, 48);
        c.fillStyle = "#b9b9b9";
        c.fillRect(xa, y - 11, xb - xa, 4);
        c.fillRect(xa, y + 7, xb - xa, 4);
      }
      if (detail < 1 && !inLogo) {
        c.globalAlpha = (1 - detail) * alpha;
        const v = Math.round(lerp(120, 250, lit));
        c.fillStyle = "rgb(" + v + "," + v + "," + v + ")";
        c.fillRect(xa, y - RAIL / 2, xb - xa, RAIL);
      }
      c.globalAlpha = 1;
    }

    crossover(alpha) {
      const c = this.ctx;
      c.save();
      c.globalAlpha = alpha;
      c.translate(B[0], B[1]);
      c.rotate(Math.atan2(DIAG.dy, DIAG.dx));
      c.fillStyle = "#2c2c2c";
      for (let d = 0; d < DIAG.len; d += 15) c.fillRect(d, -24, 6, 48);
      c.fillStyle = "#b9b9b9";
      c.fillRect(0, -11, DIAG.len, 4);
      c.fillRect(0, 7, DIAG.len, 4);
      c.restore();
    }

    drawWords(t) {
      const c = this.ctx;
      c.fillStyle = "#fafafa";
      WORDS.forEach((wd, i) => {
        const k = clamp((t - T_WORDS - i * 0.06) / 0.3, 0, 1);
        if (k <= 0) return;
        const e = 1 - Math.pow(1 - k, 3), sc = lerp(1.7, 1, e);
        c.save();
        c.globalAlpha = clamp(k * 2.2, 0, 1);
        c.translate(wd.cx + wd.from * 210 * (1 - e), WORD_CY);
        c.scale(sc, sc);
        c.translate(-wd.cx, -WORD_CY);
        c.fill(this.words[i], "evenodd");
        c.restore();
      });
      const k = clamp((t - T_GAMES) / 0.5, 0, 1);
      if (k > 0) {
        const e = 1 - Math.pow(1 - k, 3);
        c.save();
        c.globalAlpha = e;
        c.translate(GAMES_CX, GAMES_CY);
        c.scale(lerp(1.45, 1, e), 1);
        c.translate(-GAMES_CX, -GAMES_CY);
        c.fill(this.games, "evenodd");
        c.restore();
      }
    }
  }

  window.IntroScene = IntroScene;
})();
