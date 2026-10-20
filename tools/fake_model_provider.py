"""A loopback fake of the Messages API for the provider Test checks: synthetic recordings only, never a real provider.
It answers each request with the next scripted response and records every request it receives. A trap listener
stands for anything a request must never reach, such as a proxy named in the environment or a redirect target."""
import json
import socket
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

USAGE = {'input_tokens': 21, 'cache_creation_input_tokens': 0, 'cache_read_input_tokens': 0, 'output_tokens': 9,
         'output_tokens_details': {'thinking_tokens': 0}}


def event(name, data):
    return f'event: {name}\ndata: {json.dumps(data, separators=(",", ":"))}\n\n'


def recording(model, stop='end_turn', usage=None, text='ready'):
    """A synthetic Messages stream in the documented event flow, ending at message_stop."""
    final = USAGE if usage is None else usage
    start = {k: v for k, v in final.items() if k != 'output_tokens_details'} | {'output_tokens': 1, 'service_tier': 'standard', 'inference_geo': 'global'}
    blocks = '' if text is None else (event('content_block_start', {'type': 'content_block_start', 'index': 0, 'content_block': {'type': 'text', 'text': ''}})
                                      + event('content_block_delta', {'type': 'content_block_delta', 'index': 0, 'delta': {'type': 'text_delta', 'text': text}})
                                      + event('content_block_stop', {'type': 'content_block_stop', 'index': 0}))
    return (event('message_start', {'type': 'message_start', 'message': {'id': 'msg_synthetic_test', 'type': 'message', 'role': 'assistant', 'model': model,
                                                                         'content': [], 'stop_reason': None, 'stop_sequence': None, 'usage': start}})
            + blocks + event('message_delta', {'type': 'message_delta', 'delta': {'stop_reason': stop, 'stop_sequence': None}, 'usage': final})
            + event('message_stop', {'type': 'message_stop'}))


def stream(body, release=None, rest=''):
    """A 200 event stream: `body`, then, once `release` is set, `rest`."""
    def respond(handler):
        handler.send_response(200)
        handler.send_header('Content-Type', 'text/event-stream')
        handler.end_headers()
        try:
            handler.wfile.write(body.encode())
            handler.wfile.flush()
        except OSError:
            return
        if release is not None:
            release.wait(timeout=30)
            try:
                handler.wfile.write(rest.encode())
            except OSError:
                pass
    return respond


def status(code, body=None, headers=()):
    def respond(handler):
        payload = b'' if body is None else json.dumps(body).encode()
        handler.send_response(code)
        for name, value in headers:
            handler.send_header(name, value)
        handler.send_header('Content-Type', 'application/json')
        handler.send_header('Content-Length', str(len(payload)))
        handler.end_headers()
        handler.wfile.write(payload)
    return respond


class FakeProvider:
    def __init__(self):
        self.received = []
        self.script = []
        self.server = ThreadingHTTPServer(('127.0.0.1', 0), _Handler)
        self.server.daemon_threads = True
        self.server.fake = self
        threading.Thread(target=self.server.serve_forever, daemon=True).start()

    @property
    def url(self):
        return f'http://127.0.0.1:{self.server.server_port}'

    def respond(self, *responses):
        self.script.extend(responses)

    def close(self):
        self.server.shutdown()
        self.server.server_close()


class _Handler(BaseHTTPRequestHandler):
    # Each response ends its connection, so an event stream needs no length.
    protocol_version = 'HTTP/1.0'

    def log_message(self, *args):
        pass

    def do_POST(self):
        fake = self.server.fake
        body = self.rfile.read(int(self.headers.get('Content-Length', 0)))
        fake.received.append({'method': self.command, 'path': self.path, 'headers': [(k.lower(), v) for k, v in self.headers.items()], 'body': body})
        (fake.script.pop(0) if fake.script else status(500, {'type': 'error', 'error': {'type': 'api_error', 'message': 'Unscripted request'}}))(self)

    do_GET = do_PUT = do_DELETE = do_POST


class Trap:
    """Counts the connections that must never be made."""

    def __init__(self):
        self.socket = socket.socket()
        self.socket.bind(('127.0.0.1', 0))
        self.socket.listen(16)
        self.connections = 0
        threading.Thread(target=self._accept, daemon=True).start()

    @property
    def url(self):
        return f'http://127.0.0.1:{self.socket.getsockname()[1]}'

    def _accept(self):
        while True:
            try:
                connection, _ = self.socket.accept()
            except OSError:
                return
            self.connections += 1
            connection.close()

    def close(self):
        self.socket.close()
