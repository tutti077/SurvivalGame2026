/* Contact form — validates, then opens the visitor's mail app pre-filled
   (no server needed; works on any static host). */
(function () {
  "use strict";
  document.addEventListener("DOMContentLoaded", () => {
    const form = document.querySelector(".form");
    if (!form) return;
    const box = form.closest(".contact-main");
    const emailOk = (v) => /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(v);

    function check(field) {
      const input = field.querySelector("input, textarea");
      const v = input.value.trim();
      const bad = !v || (input.type === "email" && !emailOk(v));
      field.classList.toggle("invalid", bad);
      return !bad;
    }

    form.querySelectorAll(".field").forEach((f) => {
      const input = f.querySelector("input, textarea");
      input.addEventListener("blur", () => { if (input.value) check(f); });
      input.addEventListener("input", () => f.classList.contains("invalid") && check(f));
    });

    form.addEventListener("submit", (e) => {
      e.preventDefault();
      const fields = Array.from(form.querySelectorAll(".field"));
      const ok = fields.map(check).every(Boolean);
      if (!ok) { fields.find((f) => f.classList.contains("invalid")).querySelector("input, textarea").focus(); return; }
      const to = (window.SITE && window.SITE.email) || "";
      const data = new FormData(form);
      const topic = data.get("topic") || "General";
      const subject = "[" + topic + "] Message from " + data.get("name");
      const body = data.get("message") + "\n\n— " + data.get("name") + " (" + data.get("email") + ")";
      window.location.href = "mailto:" + encodeURIComponent(to).replace("%40", "@") +
        "?subject=" + encodeURIComponent(subject) + "&body=" + encodeURIComponent(body);
      box.classList.add("sent");
    });

    const reset = box.querySelector("[data-reset]");
    reset && reset.addEventListener("click", () => { form.reset(); box.classList.remove("sent"); });
  });
})();
