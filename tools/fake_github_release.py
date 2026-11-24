#!/usr/bin/env python3
"""Local stand-in for GitHub's releases/latest + asset downloads, for testing Update now (1.2.8).

    python3 tools/fake_github_release.py <dir with release files> <tag> [port]

Serves:
  /api/latest      releases/latest JSON listing every file in <dir> (with GitHub-style sha256 "digest")
  /dl/<name>       302 redirect to /cdn/<name> (like github.com -> objects.githubusercontent.com)
  /cdn/<name>      the file
Point the app at it (test only):  TPO_UPDATE_URL=http://127.0.0.1:<port>/api/latest
FAKE_GH_SLOW=<seconds per 64 KB> slows downloads (to see progress / try Cancel).
Only loopback http is accepted by the app, and only while TPO_UPDATE_URL is set.
"""
import hashlib, json, os, sys, time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

root, tag = sys.argv[1], sys.argv[2]
port = int(sys.argv[3]) if len(sys.argv) > 3 else 8765
base = f"http://127.0.0.1:{port}"

def release():
    assets = []
    for n in sorted(os.listdir(root)):
        p = os.path.join(root, n)
        if not os.path.isfile(p):
            continue
        digest = "sha256:" + hashlib.sha256(open(p, "rb").read()).hexdigest()
        assets.append({"name": n, "size": os.path.getsize(p), "browser_download_url": f"{base}/dl/{n}", "digest": digest})
    return {"tag_name": tag, "html_url": f"https://github.com/devmaildumpster-ux/TarkovPriceOverlay/releases/tag/{tag}",
            "draft": False, "prerelease": False, "assets": assets}

class H(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path == "/api/latest":
            body = json.dumps(release()).encode()
            self.send_response(200); self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body))); self.end_headers(); self.wfile.write(body); return
        if self.path.startswith("/dl/"):
            self.send_response(302); self.send_header("Location", "/cdn/" + self.path[4:]); self.end_headers(); return
        if self.path.startswith("/cdn/"):
            name = os.path.basename(self.path[5:])
            p = os.path.join(root, name)
            if os.path.isfile(p):
                data = open(p, "rb").read()
                self.send_response(200); self.send_header("Content-Type", "application/octet-stream")
                self.send_header("Content-Length", str(len(data))); self.end_headers()
                slow = float(os.environ.get("FAKE_GH_SLOW", "0"))
                for i in range(0, len(data), 65536):
                    try:
                        self.wfile.write(data[i:i + 65536])
                    except (BrokenPipeError, ConnectionResetError):
                        return
                    if slow:
                        time.sleep(slow)
                return
        self.send_response(404); self.end_headers()

    def log_message(self, fmt, *args):
        sys.stderr.write("fake-github: " + (fmt % args) + "\n")

ThreadingHTTPServer(("127.0.0.1", port), H).serve_forever()
