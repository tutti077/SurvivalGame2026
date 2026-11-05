/* ==========================================================================
   Two Track Games — shared page behaviour
   - opening trailer (home only, once per browser session)
   - nav, page wipe between pages, scroll rail
   - scroll engine: [data-pin] sections get --p (0..1) while stuck
   - reveals, split headings, word-fill statement, marquee, horizontal gallery
   ========================================================================== */
(function () {
  "use strict";

  const root = document.documentElement;
  const reduced = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  const clamp = (v, a, b) => (v < a ? a : v > b ? b : v);
  const $ = (s, el) => (el || document).querySelector(s);
  const $$ = (s, el) => Array.from((el || document).querySelectorAll(s));
  const store = {
    get(k) { try { return sessionStorage.getItem(k); } catch (e) { return null; } },
    set(k, v) { try { sessionStorage.setItem(k, v); } catch (e) { /* private mode */ } },
  };

  /* ---------------- year, socials, email from SITE config ---------------- */
  function applyConfig() {
    const cfg = window.SITE || { socials: {} };
    $$("[data-year]").forEach((el) => (el.textContent = new Date().getFullYear()));
    $$("[data-email]").forEach((el) => {
      if (!cfg.email) { el.closest("[data-email-wrap]")?.remove(); return; }
      el.textContent = cfg.email;
      if (el.tagName === "A") el.href = "mailto:" + cfg.email;
    });
    $$("[data-social]").forEach((a) => {
      const url = (cfg.socials || {})[a.getAttribute("data-social")];
      if (!url) { a.remove(); return; }
      a.href = url; a.target = "_blank"; a.rel = "noopener";
    });
    const names = { discord: "Discord", x: "X / Twitter", youtube: "YouTube", steam: "Steam", bluesky: "Bluesky", instagram: "Instagram", tiktok: "TikTok" };
    $$("[data-socials]").forEach((ul) => {
      const entries = Object.entries(cfg.socials || {}).filter(([, url]) => url);
      ul.innerHTML = "";
      if (!entries.length) {
        if (ul.hasAttribute("data-socials-empty")) ul.innerHTML = '<li class="none">' + ul.getAttribute("data-socials-empty") + "</li>";
        else ul.closest("[data-socials-wrap]")?.remove();
        return;
      }
      for (const [k, url] of entries) {
        const li = document.createElement("li");
        const a = document.createElement("a");
        a.href = url; a.target = "_blank"; a.rel = "noopener"; a.textContent = names[k] || k;
        li.appendChild(a); ul.appendChild(li);
      }
    });
  }

  /* ---------------- nav ---------------- */
  function initNav() {
    const nav = $(".nav");
    if (!nav) return;
    const toggle = $(".nav-toggle", nav);
    toggle && toggle.addEventListener("click", () => {
      const open = root.classList.toggle("nav-open");
      toggle.setAttribute("aria-expanded", open ? "true" : "false");
    });
    $$(".nav-links a", nav).forEach((a) => a.addEventListener("click", () => root.classList.remove("nav-open")));
  }

  /* ---------------- page wipe ---------------- */
  function initWipe() {
    const wipe = $(".wipe");
    if (!wipe || reduced) return;
    if (store.get("tt-wipe")) {
      store.set("tt-wipe", "");
      wipe.classList.add("uncover-start");
      requestAnimationFrame(() => requestAnimationFrame(() => {
        wipe.classList.remove("uncover-start");
        wipe.classList.add("uncover");
      }));
    }
    document.addEventListener("click", (e) => {
      const a = e.target.closest("a");
      if (!a || e.defaultPrevented || e.metaKey || e.ctrlKey || e.shiftKey || e.button !== 0) return;
      const href = a.getAttribute("href");
      if (!href || href.startsWith("#") || href.startsWith("mailto:") || a.target === "_blank" || /^https?:/i.test(href)) return;
      e.preventDefault();
      store.set("tt-wipe", "1");
      wipe.classList.remove("uncover");
      void wipe.offsetWidth;
      wipe.classList.add("cover");
      setTimeout(() => (window.location.href = href), 520);
    });
    // back/forward cache: don't come back to a red screen
    window.addEventListener("pageshow", (e) => { if (e.persisted) wipe.className = "wipe"; });
  }

  /* ---------------- trailer ---------------- */
  function initIntro(onReady) {
    const intro = $("#intro");
    if (!intro || root.classList.contains("skip-intro") || !window.IntroScene) {
      intro && intro.remove();
      onReady();
      return;
    }
    root.classList.add("intro-playing");
    store.set("tt-intro", "1");
    if ("scrollRestoration" in history) history.scrollRestoration = "manual";
    window.scrollTo(0, 0);
    let finished = false, scene = null;
    function finish() {
      if (finished) return;
      finished = true;
      intro.classList.add("is-out");
      root.classList.remove("intro-playing");
      onReady();
      setTimeout(() => { scene && scene.destroy(); intro.remove(); }, 950);
    }
    $(".intro-skip", intro).addEventListener("click", finish);
    window.addEventListener("keydown", function k(e) {
      if (e.key === "Escape" || e.key === " " || e.key === "Enter") { finish(); window.removeEventListener("keydown", k); }
    });
    scene = new window.IntroScene($("canvas", intro), { onDone: finish });
    scene.start();
  }

  function replayIntro() {
    try { sessionStorage.removeItem("tt-intro"); } catch (e) { /* ignore */ }
    window.scrollTo(0, 0);
    window.location.reload();
  }

  /* ---------------- hero backdrop scene ---------------- */
  let heroScene = null;
  function initHeroScene() {
    const c = $(".hero canvas");
    if (!c || !window.TerrainScene) return;
    heroScene = new window.TerrainScene(c);
    heroScene.start();
    if ("IntersectionObserver" in window) {
      new IntersectionObserver((es) => es.forEach((e) => (heroScene.visible = e.isIntersecting))).observe($(".hero"));
    }
  }

  /* ---------------- split headings into lines / chars ---------------- */
  function initSplits() {
    $$("[data-split-lines]").forEach((el) => {
      const lines = el.innerHTML.split(/<br\s*\/?>/i);
      el.innerHTML = lines.map((l, i) => '<span class="split-line"><span style="--i:' + i + '">' + l.trim() + "</span></span>").join("");
    });
    // per-character scatter for the page hero titles
    $$("[data-scatter] .row").forEach((row) => {
      const text = row.textContent;
      row.innerHTML = "";
      for (const ch of text) {
        const s = document.createElement("span");
        s.className = "ch";
        s.textContent = ch === " " ? " " : ch;
        s.style.setProperty("--dy", String(Math.round((Math.random() - 0.3) * -600)));
        s.style.setProperty("--rot", String(Math.round((Math.random() - 0.5) * 70)));
        row.appendChild(s);
      }
    });
    // word-fill statement
    $$("[data-words]").forEach((p) => {
      const hot = (p.getAttribute("data-hot") || "").toLowerCase().split(",").map((s) => s.trim()).filter(Boolean);
      const words = p.textContent.trim().split(/\s+/);
      p.innerHTML = words.map((w) => {
        const clean = w.toLowerCase().replace(/[^a-z0-9]/g, "");
        return '<span class="w' + (hot.includes(clean) ? " hot" : "") + '">' + w + "</span>";
      }).join(" ");
    });
  }

  /* ---------------- reveals ---------------- */
  function initReveals() {
    const els = $$("[data-reveal], [data-split-lines], .station");
    if (!("IntersectionObserver" in window) || reduced) { els.forEach((e) => e.classList.add("is-in")); return; }
    // A fully clipped element (clip / wipe reveals start at zero visible area) never reports as
    // intersecting, so those are watched through their parent instead.
    const watch = new Map();
    els.forEach((el) => {
      const clipped = /^(clip|wipe)$/.test(el.getAttribute("data-reveal") || "");
      const target = clipped && el.parentElement ? el.parentElement : el;
      if (!watch.has(target)) watch.set(target, []);
      watch.get(target).push(el);
    });
    const io = new IntersectionObserver((entries) => {
      entries.forEach((e) => {
        if (!e.isIntersecting) return;
        (watch.get(e.target) || []).forEach((el) => el.classList.add("is-in"));
        io.unobserve(e.target);
      });
    }, { rootMargin: "0px 0px -12% 0px", threshold: 0.05 });
    watch.forEach((_, target) => io.observe(target));
  }

  /* ---------------- marquee (speeds up with scroll velocity) ---------------- */
  const marquees = [];
  function initMarquees() {
    $$(".marquee").forEach((m) => {
      const track = $(".marquee-track", m);
      const first = track.innerHTML;
      track.innerHTML = first + first + first + first;
      marquees.push({ track, x: 0, dir: m.classList.contains("alt") ? 1 : -1, width: 0, el: m });
    });
    const measure = () => marquees.forEach((q) => (q.width = q.track.scrollWidth / 4));
    measure();
    window.addEventListener("resize", measure);
    if (document.fonts && document.fonts.ready) document.fonts.ready.then(measure);
  }

  /* ---------------- horizontal gallery sizing ---------------- */
  function sizeGalleries() {
    $$("[data-hscroll]").forEach((sec) => {
      const track = $(".hgallery-track", sec);
      if (!track || reduced) return;
      const dist = Math.max(0, track.scrollWidth - window.innerWidth);
      sec.style.setProperty("--hdist", dist + "px");
      sec.style.height = window.innerHeight + dist + "px";
    });
  }

  /* ---------------- scroll engine ---------------- */
  let pins = [], words = [], lines = [], nav = null, lastY = window.scrollY, vel = 0, ticking = false;

  function measureScroll() {
    const vh = window.innerHeight;
    const y = window.scrollY;
    const docH = document.documentElement.scrollHeight;
    root.style.setProperty("--page-p", (clamp(y / Math.max(1, docH - vh), 0, 1)).toFixed(4));
    nav && nav.classList.toggle("is-scrolled", y > 30);

    for (const s of pins) {
      const r = s.getBoundingClientRect();
      const total = Math.max(1, r.height - vh);
      const p = clamp(-r.top / total, 0, 1);
      s.style.setProperty("--p", p.toFixed(4));
      if (s === heroPin && heroScene) heroScene.crane = p;
      const cnt = s.querySelector("[data-fold-count]");
      if (cnt) cnt.textContent = "0" + clamp(Math.ceil(p * 3.9 / 1.1 + 0.01), 1, 3);
    }
    for (const p of words) {
      const r = p.getBoundingClientRect();
      const prog = clamp((vh * 0.85 - r.top) / (r.height + vh * 0.35), 0, 1);
      const spans = p._spans || (p._spans = $$(".w", p));
      const lit = Math.round(prog * spans.length);
      for (let i = 0; i < spans.length; i++) spans[i].classList.toggle("on", i < lit);
    }
    for (const l of lines) {
      const r = l.getBoundingClientRect();
      const v = clamp((vh * 0.55 - r.top) / r.height, 0, 1);
      l.style.setProperty("--v", v.toFixed(4));
    }
  }

  let heroPin = null;
  function initScroll() {
    pins = $$("[data-pin]");
    heroPin = $(".hero");
    words = $$("[data-words]");
    lines = $$(".line-track");
    nav = $(".nav");
    const onScroll = () => {
      if (!ticking) { ticking = true; requestAnimationFrame(() => { ticking = false; measureScroll(); }); }
    };
    window.addEventListener("scroll", onScroll, { passive: true });
    window.addEventListener("resize", () => { sizeGalleries(); measureScroll(); });
    sizeGalleries();
    measureScroll();

    // continuous loop for marquees (velocity-coupled)
    let last = performance.now();
    function loop(now) {
      const dt = Math.min(0.05, (now - last) / 1000);
      last = now;
      const y = window.scrollY;
      const dy = y - lastY;
      lastY = y;
      vel = vel * 0.9 + (dy / Math.max(dt, 0.001)) * 0.1;
      const boost = clamp(Math.abs(vel) / 6, 0, 600);
      for (const q of marquees) {
        if (!q.width) continue;
        const dir = q.dir * (vel < -5 ? -1 : 1);
        q.x += dir * (70 + boost) * dt;
        if (q.x <= -q.width) q.x += q.width;
        if (q.x > 0) q.x -= q.width;
        q.track.style.transform = "translate3d(" + q.x.toFixed(1) + "px,0,0)";
      }
      if (!reduced) requestAnimationFrame(loop);
    }
    if (marquees.length) requestAnimationFrame(loop);
  }

  /* ---------------- boot ---------------- */
  function ready() {
    root.classList.add("ready");
  }

  document.addEventListener("DOMContentLoaded", () => {
    applyConfig();
    initNav();
    initWipe();
    initSplits();
    initMarquees();
    initScroll();
    initReveals();
    initHeroScene();
    $$("[data-replay-intro]").forEach((b) => b.addEventListener("click", replayIntro));
    initIntro(ready);
    if (document.fonts && document.fonts.ready) document.fonts.ready.then(() => { sizeGalleries(); measureScroll(); });
    window.addEventListener("load", () => { sizeGalleries(); measureScroll(); });
  });
})();
