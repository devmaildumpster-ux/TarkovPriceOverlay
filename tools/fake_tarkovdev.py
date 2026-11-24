# Fake json.tarkov.dev for the 1.2.5 outage checks: answers with the GraphQL outage body until <temp dir>/fake_up exists,
# then proxies to the real json.tarkov.dev (to watch the app recover).
import http.server, sys, datetime, os, tempfile, urllib.request
UP_FLAG = os.path.join(tempfile.gettempdir(), 'fake_up')
class H(http.server.BaseHTTPRequestHandler):
    def _r(self, body=True):
        up=os.path.exists(UP_FLAG)
        if up:
            req=urllib.request.Request('https://json.tarkov.dev'+self.path, method=self.command, headers={'User-Agent':'tpo-test','Accept-Encoding':'identity'})
            with urllib.request.urlopen(req, timeout=60) as r:
                b=r.read() if body else b''
                self.send_response(r.status); self.send_header('Content-Type','application/json')
                self.send_header('Content-Length', r.headers.get('Content-Length', str(len(b)))); self.end_headers()
                if body: self.wfile.write(b)
        else:
            b=b'{"errors":["GraphQL server unavailable. Try again later."]}'
            self.send_response(200); self.send_header('Content-Type','application/json'); self.send_header('Content-Length',str(len(b))); self.end_headers()
            if body: self.wfile.write(b)
        sys.stderr.write(f"{datetime.datetime.now():%H:%M:%S} {self.command} {self.path} {'proxied' if up else 'DOWN'}\n"); sys.stderr.flush()
    def do_GET(self): self._r()
    def do_HEAD(self): self._r(False)
    def log_message(self,*a): pass
http.server.ThreadingHTTPServer(('127.0.0.1',8765),H).serve_forever()
