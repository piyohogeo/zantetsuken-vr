"""Loopback-only synthetic receiver for the isolated Android statistics recovery APK."""
import argparse
import hashlib
import json
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

parser = argparse.ArgumentParser()
parser.add_argument('--record', required=True)
args = parser.parse_args()
lock = threading.Lock()
counts = {}
secondary = None

class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_GET(self):
        global secondary
        if self.path != '/enable-recovery':
            self.send_error(404)
            return
        if secondary is None:
            secondary = ThreadingHTTPServer(('127.0.0.1', 18766), Handler)
            threading.Thread(target=secondary.serve_forever, daemon=True).start()
        self.respond(200)

    def do_POST(self):
        data = self.rfile.read(int(self.headers.get('Content-Length', '0')))
        if len(data) > 16384 or self.headers.get('X-Telemetry-Token') != 'local-check-only':
            self.send_error(400)
            return
        with lock:
            number = counts[self.path] = counts.get(self.path, 0) + 1
            with open(args.record, 'a', encoding='utf-8') as stream:
                stream.write(json.dumps({'case': self.path, 'request': number, 'bytes': len(data), 'sha256': hashlib.sha256(data).hexdigest()}) + '\n')
        if self.path == '/timeout' and number == 1:
            time.sleep(4)
        if self.path == '/withdraw':
            time.sleep(8)
        self.respond(503 if self.path == '/unavailable' and number == 1 else 201)

    def respond(self, status):
        body = b'{"ok":true}' if status in (200, 201) else b'{"error":"synthetic-unavailable"}'
        try:
            self.send_response(status)
            self.send_header('Content-Type', 'application/json')
            self.send_header('Content-Length', str(len(body)))
            self.end_headers()
            self.wfile.write(body)
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
            pass

print('Synthetic receiver listening only on 127.0.0.1:18765', flush=True)
ThreadingHTTPServer(('127.0.0.1', 18765), Handler).serve_forever()
