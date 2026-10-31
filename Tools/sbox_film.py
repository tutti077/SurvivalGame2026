"""Film the running s&box editor: run C# setup scripts, then grab a burst of camera screenshots over one
MCP connection (sbox-cli --connect) and stitch them into a contact sheet and a GIF.

Usage (from the repo root, editor open on the project):
  python Tools/sbox_film.py <shot.json>

shot.json:
  {
    "out": "C:/.../film/right_slash",        # folder for frame_NNN.png, sheet.png, film.gif
    "steps": [                                # run in order; each is one of:
      {"csharp": "C:/.../setup.cs"},          #   run a C# body (run_csharp); its return text is printed
      {"console": "equip club_wood"},         #   console command
      {"write": {"C:/.../args.txt": "1 0.1"}}, #   write small files (arguments for the next csharp step)
      {"play": true},                         #   start play mode if it is not running
      {"wait": 1.5},                          #   real seconds
      {"film": {"camera": "<guid or @last>",  #   burst: frames, real-seconds interval, size, label
                "frames": 16, "interval": 0.15, "width": 640, "height": 480, "label": "R slash"}}
    ]
  }
"@last" uses the last guid-looking line a csharp step returned (setup scripts return their camera id).
Frames from every film step are numbered on, so one shot can film several actions in a row.
"""
import base64
import json
import os
import re
import subprocess
import sys
import threading
import time

CLI = os.environ.get("SBOX_CLI", r"M:/SteamLibrary/steamapps/common/sbox/bin/managed/sbox-cli.exe")
GUID = re.compile(r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")


class Mcp:
    def __init__(self):
        self.p = subprocess.Popen([CLI, "--connect"], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                  stderr=subprocess.DEVNULL, text=True, encoding="utf-8", bufsize=1)
        self.n = 0
        self.lock = threading.Lock()
        self.request("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                                    "clientInfo": {"name": "sbox_film", "version": "1"}})
        self.notify("notifications/initialized", {})

    def notify(self, method, params):
        self.p.stdin.write(json.dumps({"jsonrpc": "2.0", "method": method, "params": params}) + "\n")
        self.p.stdin.flush()

    def request(self, method, params):
        with self.lock:
            self.n += 1
            rid = self.n
            self.p.stdin.write(json.dumps({"jsonrpc": "2.0", "id": rid, "method": method, "params": params}) + "\n")
            self.p.stdin.flush()
            while True:
                line = self.p.stdout.readline()
                if not line:
                    raise RuntimeError("sbox-cli closed the connection")
                msg = json.loads(line)
                if msg.get("id") == rid:
                    if "error" in msg:
                        raise RuntimeError(str(msg["error"]))
                    return msg["result"]

    def tool(self, name, args=None):
        """Call a registry tool through the call_tool entry point; returns the content blocks."""
        res = self.request("tools/call", {"name": "call_tool", "arguments": {"name": name, "arguments": args or {}}})
        if res.get("isError"):
            raise RuntimeError(f"{name}: " + " ".join(c.get("text", "") for c in res.get("content", [])))
        return res.get("content", [])

    def text(self, name, args=None):
        return "\n".join(c.get("text", "") for c in self.tool(name, args) if c.get("type") == "text")

    def close(self):
        try:
            self.p.stdin.close()
            self.p.wait(timeout=5)
        except Exception:  # noqa: BLE001
            self.p.kill()


def sheet(frames, labels, out_path, cols=6):
    from PIL import Image, ImageDraw
    imgs = [Image.open(f).convert("RGB") for f in frames]
    w, h = imgs[0].size
    tw, th = w // 2, h // 2
    rows = (len(imgs) + cols - 1) // cols
    canvas = Image.new("RGB", (tw * cols, th * rows), (20, 20, 20))
    d = ImageDraw.Draw(canvas)
    for i, (im, lab) in enumerate(zip(imgs, labels)):
        x, y = (i % cols) * tw, (i // cols) * th
        canvas.paste(im.resize((tw, th)), (x, y))
        d.rectangle([x, y, x + tw - 1, y + 16], fill=(0, 0, 0))
        d.text((x + 4, y + 2), lab, fill=(255, 255, 0))
    canvas.save(out_path)


def gif(frames, out_path, ms):
    from PIL import Image
    imgs = [Image.open(f).convert("RGB") for f in frames]
    imgs = [im.resize((im.width // 2, im.height // 2)).quantize(colors=128) for im in imgs]
    imgs[0].save(out_path, save_all=True, append_images=imgs[1:], duration=ms, loop=0, optimize=True)


def main(spec_path):
    with open(spec_path, encoding="utf-8") as f:
        spec = json.load(f)
    out = spec["out"]
    os.makedirs(out, exist_ok=True)
    for old in os.listdir(out):
        if old.startswith("frame_"):
            os.remove(os.path.join(out, old))
    mcp = Mcp()
    last_guid = None
    frames, labels = [], []
    try:
        for step in spec["steps"]:
            if "csharp" in step:
                txt = mcp.text("run_csharp", {"path": step["csharp"]})
                print("csharp:", txt.strip()[:400])
                g = GUID.findall(txt)
                if g:
                    last_guid = g[-1]
            elif "write" in step:
                for path, content in step["write"].items():
                    os.makedirs(os.path.dirname(path), exist_ok=True)
                    with open(path, "w", encoding="utf-8") as fo:
                        fo.write(content)
            elif "console" in step:
                mcp.text("instance_console_command", {"command": step["console"]})
            elif "stop" in step:
                st = json.loads(mcp.text("editor_status"))
                if st.get("IsPlaying"):
                    mcp.text("play_stop")
                    time.sleep(2)
            elif "play" in step:
                st = json.loads(mcp.text("editor_status"))
                if not st.get("IsPlaying"):
                    mcp.text("play_start")
                    time.sleep(step.get("settle", 8))
            elif "wait" in step:
                time.sleep(step["wait"])
            elif "film" in step:
                fs = step["film"]
                cam = last_guid if fs.get("camera", "@last") == "@last" else fs["camera"]
                args = {"width": fs.get("width", 640), "height": fs.get("height", 480), "includeUi": False}
                if cam:
                    args["camera"] = cam
                t0 = time.time()
                for i in range(fs.get("frames", 12)):
                    target = t0 + i * fs.get("interval", 0.15)
                    delay = target - time.time()
                    if delay > 0:
                        time.sleep(delay)
                    blocks = mcp.tool("camera_screenshot", args)
                    img = next((b for b in blocks if b.get("type") == "image"), None)
                    if img is None:
                        print("no image in screenshot result", blocks[:1])
                        continue
                    path = os.path.join(out, f"frame_{len(frames):03d}.png")
                    with open(path, "wb") as fo:
                        fo.write(base64.b64decode(img["data"]))
                    frames.append(path)
                    labels.append(f"{fs.get('label', '')} {time.time() - t0:0.2f}s")
            else:
                print("unknown step", step)
    finally:
        mcp.close()
    if frames:
        sheet(frames, labels, os.path.join(out, "sheet.png"), cols=spec.get("cols", 6))
        gif(frames, os.path.join(out, "film.gif"), spec.get("gif_ms", 120))
        print("frames", len(frames), "->", out)


if __name__ == "__main__":
    main(sys.argv[1])
