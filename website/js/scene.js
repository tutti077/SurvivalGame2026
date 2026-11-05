/* ==========================================================================
   TerrainScene — hero backdrop: a slow flyover of procedurally generated
   terrain drawn as ridge lines fading into red haze. The camera rises as
   you scroll (scene.crane = 0..1).
   Palette: black / greys / white / red only.
   ========================================================================== */
(function () {
  "use strict";

  const lerp = (a, b, t) => a + (b - a) * t;
  const FAR = 160, NEAR = 3, DZ = 2.2, COLS = 96, FOG = 70;

  // small value-noise
  function hash(x, y) {
    let h = (x * 374761393 + y * 668265263) | 0;
    h = (h ^ (h >>> 13)) * 1274126177;
    return ((h ^ (h >>> 16)) >>> 0) / 4294967295;
  }
  function vnoise(x, y) {
    const xi = Math.floor(x), yi = Math.floor(y), xf = x - xi, yf = y - yi;
    const u = xf * xf * (3 - 2 * xf), v = yf * yf * (3 - 2 * yf);
    const a = hash(xi, yi), b = hash(xi + 1, yi), c = hash(xi, yi + 1), d = hash(xi + 1, yi + 1);
    return lerp(lerp(a, b, u), lerp(c, d, u), v);
  }
  function height(x, z) {
    let n = 0, amp = 1, f = 0.035, sum = 0;
    for (let o = 0; o < 4; o++) { n += vnoise(x * f, z * f) * amp; sum += amp; amp *= 0.5; f *= 2.1; }
    n /= sum;
    const valley = Math.min(1, Math.abs(x) / 26);          // a flight path through a valley
    return Math.pow(n, 1.6) * 34 * (0.12 + 0.88 * valley * valley);
  }

  class TerrainScene {
    constructor(canvas) {
      this.canvas = canvas;
      this.ctx = canvas.getContext("2d");
      this.crane = 0;
      this.camZ = 0;
      this.last = null;
      this.running = false;
      this.visible = true;
      this.resize = this.resize.bind(this);
      this.frame = this.frame.bind(this);
      window.addEventListener("resize", this.resize);
      this.resize();
    }

    resize() {
      const r = this.canvas.getBoundingClientRect();
      this.dpr = Math.min(window.devicePixelRatio || 1, 1.5);
      this.w = Math.max(1, r.width);
      this.h = Math.max(1, r.height);
      this.canvas.width = Math.round(this.w * this.dpr);
      this.canvas.height = Math.round(this.h * this.dpr);
      this.f = Math.max(this.h * 0.9, this.w * 0.5);
    }

    start() { if (!this.running) { this.running = true; requestAnimationFrame(this.frame); } }
    stop() { this.running = false; }

    frame(now) {
      if (!this.running) return;
      requestAnimationFrame(this.frame);
      if (this.last === null) this.last = now;
      const dt = Math.min(0.05, (now - this.last) / 1000);
      this.last = now;
      if (!this.visible) return;
      this.camZ += dt * 4;
      this.draw();
    }

    draw() {
      const c = this.ctx, w = this.w, h = this.h;
      c.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
      const camY = lerp(7, 60, this.crane);
      const hy = h * lerp(0.42, 0.2, this.crane);
      const cx = w / 2, f = this.f;

      let g = c.createLinearGradient(0, 0, 0, h);
      g.addColorStop(0, "#030303");
      g.addColorStop(0.45, "#0c0c0c");
      g.addColorStop(1, "#050505");
      c.fillStyle = g;
      c.fillRect(0, 0, w, h);
      g = c.createRadialGradient(cx, hy, 0, cx, hy, w * 0.65);
      g.addColorStop(0, "rgba(253,15,45,0.26)");
      g.addColorStop(0.4, "rgba(253,15,45,0.06)");
      g.addColorStop(1, "rgba(253,15,45,0)");
      c.fillStyle = g;
      c.fillRect(0, 0, w, h);

      // ridge lines, far to near; each fills below itself to hide the rows behind
      const z0 = Math.ceil((this.camZ + NEAR) / DZ) * DZ;
      const ys = new Float32Array(COLS + 1), xs = new Float32Array(COLS + 1);
      for (let zw = Math.floor((this.camZ + FAR) / DZ) * DZ; zw >= z0; zw -= DZ) {
        const z = zw - this.camZ;
        const span = (z * w) / (2 * f) * 1.08 + 2;
        for (let i = 0; i <= COLS; i++) {
          const x = -span + (2 * span * i) / COLS;
          xs[i] = cx + (x * f) / z;
          ys[i] = hy - ((height(x, zw) - camY) * f) / z;
        }
        c.beginPath();
        c.moveTo(xs[0], h + 2);
        for (let i = 0; i <= COLS; i++) c.lineTo(xs[i], ys[i]);
        c.lineTo(xs[COLS], h + 2);
        c.closePath();
        c.fillStyle = "#070707";
        c.fill();
        const fog = Math.exp(-z / FOG);
        c.strokeStyle = "rgba(235,235,235," + (0.1 + 0.6 * fog).toFixed(3) + ")";
        c.lineWidth = 1;
        c.beginPath();
        for (let i = 0; i <= COLS; i++) {
          if (i) c.lineTo(xs[i], ys[i]); else c.moveTo(xs[i], ys[i]);
        }
        c.stroke();
      }

      g = c.createRadialGradient(w / 2, h / 2, Math.min(w, h) * 0.25, w / 2, h / 2, Math.max(w, h) * 0.75);
      g.addColorStop(0, "rgba(0,0,0,0)");
      g.addColorStop(1, "rgba(0,0,0,0.85)");
      c.fillStyle = g;
      c.fillRect(0, 0, w, h);
    }
  }

  window.TerrainScene = TerrainScene;
})();
