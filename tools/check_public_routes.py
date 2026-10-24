#!/usr/bin/env python3
"""Qualify the public gateway template with owned nginx and a synthetic upstream."""
import argparse
import http.client
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import re
import shutil
import socket
import ssl
import subprocess
import tempfile
import threading
import time
from urllib.parse import urlsplit

from owned_process import stop_owned_group

ROOT = Path(__file__).resolve().parents[1]
VARIABLES = {'PUBLIC_HOST', 'CERTIFICATE_CHAIN', 'CERTIFICATE_KEY', 'UPSTREAM_ADDRESS',
             'UPSTREAM_PORT', 'LANDING_ROOT', 'LANDING_HEADERS'}


def render_template(template, values):
    """Replace only the seven operator placeholders; preserve nginx variables."""
    if set(values) != VARIABLES:
        raise ValueError('Supply every explicit gateway placeholder.')
    for name, value in values.items():
        if name == 'UPSTREAM_PORT':
            valid = str(value).isdigit() and 1 <= int(value) <= 65535
        elif name in {'PUBLIC_HOST', 'UPSTREAM_ADDRESS'}:
            valid = bool(re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9.-]*', str(value)))
        else:
            valid = bool(re.fullmatch(r'/[A-Za-z0-9_./-]+', str(value))) and '..' not in str(value).split('/')
        if not valid:
            raise ValueError('Unsupported gateway placeholder: ' + name)
    def replace(match):
        if match[1] not in VARIABLES:
            raise ValueError('Unknown gateway placeholder: ' + match[1])
        return str(values[match[1]])
    return re.sub(r'\$\{([A-Z_]+)\}', replace, template)


def available_port():
    with socket.socket() as listener:
        listener.bind(('127.0.0.1', 0))
        return listener.getsockname()[1]


class SyntheticApplication(BaseHTTPRequestHandler):
    def log_message(self, *_args):
        pass

    def do_GET(self):
        self.respond()

    do_HEAD = do_GET

    def do_POST(self):
        self.respond()

    def respond(self):
        body = self.rfile.read(int(self.headers.get('Content-Length', '0')))
        self.server.requests.append((self.command, self.path, dict(self.headers)))
        path = urlsplit(self.path).path
        if re.fullmatch(r'/api/assistant/conversations/[0-9a-f-]{36}/events', path) and self.headers.get('Cookie') == 'synthetic=accepted':
            # A page-assistant event stream: its first frame, then its last only once the client has read the first.
            self.send_response(200)
            self.send_header('Content-Type', 'text/event-stream; charset=utf-8')
            self.end_headers()
            self.wfile.write(b': open\n\n'); self.wfile.flush()
            self.server.first_read.wait(timeout=10)
            self.wfile.write(b': ping\n\n'); self.wfile.flush()
            return
        location, cookie = None, None
        if path == '/auth/login':
            status, content = 302, b''
            location = '/' if body == b'valid=1' else '/auth?failed=1'
            if body == b'valid=1':
                cookie = 'synthetic=accepted; Path=/; Secure; HttpOnly; SameSite=Strict'
        elif path == '/auth/claim':
            status, content, location = 302, b'', '/'
        elif path == '/auth':
            status, content = 200, b'SYNTHETIC SIGN IN'
        elif self.headers.get('Cookie') == 'synthetic=accepted':
            status, content = 200, b'SYNTHETIC PRIVATE APPLICATION'
            if path == '/api/redirect':
                status, content, location = 302, b'', '/'
        elif path.startswith('/api/'):
            status, content = 401, b'{"code":"sign_in_required"}'
        else:
            status, content, location = 302, b'', '/auth'
        self.send_response(status)
        self.send_header('Content-Type', 'text/plain')
        self.send_header('Content-Length', str(len(content)))
        self.send_header('X-Powered-By', 'synthetic-upstream')
        if location:
            self.send_header('Location', location)
        if cookie:
            self.send_header('Set-Cookie', cookie)
        self.end_headers()
        if self.command != 'HEAD':
            self.wfile.write(content)


def run_checks(nginx):
    if not __debug__:
        raise RuntimeError('Assertion-enabled Python is required; do not use -O for routing verification.')
    temporary = Path(tempfile.mkdtemp(prefix='openetrm-public-routes-')).resolve()
    upstream = ThreadingHTTPServer(('127.0.0.1', 0), SyntheticApplication)
    upstream.daemon_threads = True
    upstream.requests = []
    thread = threading.Thread(target=upstream.serve_forever, daemon=True)
    thread.start()
    process, stopped = None, False
    try:
        site = temporary / 'site'
        site.mkdir()
        (site / 'index.html').write_text('SYNTHETIC PUBLIC LANDING')
        (site / 'site.css').write_text('/* SYNTHETIC PUBLIC STYLE */')
        (site / 'favicon.svg').write_text('<svg xmlns="http://www.w3.org/2000/svg"/>')
        shutil.copyfile(ROOT / 'site/headers.inc', site / 'headers.inc')
        certificate, key = temporary / 'certificate.pem', temporary / 'key.pem'
        subprocess.run(['openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes',
                        '-keyout', str(key), '-out', str(certificate), '-days', '1',
                        '-subj', '/CN=public.example.test'], check=True,
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=15)
        http_port, https_port = available_port(), available_port()
        values = {'PUBLIC_HOST': 'public.example.test', 'CERTIFICATE_CHAIN': certificate,
                  'CERTIFICATE_KEY': key, 'UPSTREAM_ADDRESS': '127.0.0.1',
                  'UPSTREAM_PORT': upstream.server_port, 'LANDING_ROOT': site,
                  'LANDING_HEADERS': site / 'headers.inc'}
        servers = render_template((ROOT / 'deploy/nginx.conf.template').read_text(), values)
        servers = servers.replace('listen 80;', f'listen 127.0.0.1:{http_port};')
        servers = servers.replace('listen 443 ssl;', f'listen 127.0.0.1:{https_port} ssl;')
        (temporary / 'nginx.conf').write_text(
            f'pid {temporary}/nginx.pid; error_log {temporary}/error.log;\n'
            'events { worker_connections 64; }\nhttp { access_log off; '
            f'client_body_temp_path {temporary}/body; proxy_temp_path {temporary}/proxy;\n'
            'types { text/html html; text/css css; image/svg+xml svg; }\n' + servers + '\n}\n')
        command = [str(nginx), '-p', str(temporary) + '/', '-c', 'nginx.conf']
        subprocess.run([*command, '-t'], check=True, stdout=subprocess.DEVNULL,
                       stderr=subprocess.PIPE, timeout=10)
        process = subprocess.Popen([*command, '-g', 'daemon off;'],
                                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                                   start_new_session=True)
        for _ in range(100):
            if process.poll() is not None:
                raise RuntimeError('Owned nginx exited before readiness.')
            try:
                with socket.create_connection(('127.0.0.1', https_port), timeout=.1):
                    break
            except OSError:
                time.sleep(.05)
        else:
            raise RuntimeError('Owned nginx did not become ready.')

        def request(path, method='GET', body=None, headers=None, secure=True):
            connection = (http.client.HTTPSConnection('127.0.0.1', https_port,
                          context=ssl._create_unverified_context(), timeout=5) if secure
                          else http.client.HTTPConnection('127.0.0.1', http_port, timeout=5))
            try:
                connection.request(method, path, body, {'Host': values['PUBLIC_HOST'], **(headers or {})})
                response = connection.getresponse()
                return response.status, dict(response.getheaders()), response.read()
            finally:
                connection.close()

        checked = 0
        def expect(label, path, status, content=None, location=None, answer=None, **kwargs):
            nonlocal checked
            actual, headers, body = request(path, **kwargs)
            assert actual == status, f'{label}: expected HTTP {status}, got {actual}'
            if content is not None:
                assert content in body, label + ': unexpected content'
            if answer is not None:
                assert body == answer, label + ': unexpected answer'
            if location is not None:
                assert headers.get('Location') == location, label + ': unexpected redirect'
            assert 'X-Powered-By' not in headers, label + ': upstream implementation header exposed'
            if kwargs.get('secure', True) and not path.startswith('/_landing/'):
                assert 'no-store' in headers.get('Cache-Control', ''), label + ': private or homepage response must not be cached'
            checked += 1
            return headers

        expect('public homepage', '/', 200, b'SYNTHETIC PUBLIC LANDING')
        for cookie in ['synthetic=forged', 'synthetic=accepted']:
            headers = expect('public homepage ignores application cookies', '/', 200,
                             b'SYNTHETIC PUBLIC LANDING', headers={'Cookie': cookie})
            assert 'Set-Cookie' not in headers, 'The public page must not create application sessions.'
        for path, kind in [('/', 'text/html'), ('/_landing/site.css', 'text/css'),
                           ('/_landing/favicon.svg', 'image/svg+xml')]:
            headers = expect('public HEAD ' + path, path, 200, method='HEAD')
            assert headers['Content-Type'].split(';')[0] == kind
            assert "default-src 'none'" in headers.get('Content-Security-Policy', '')
            assert headers.get('X-Content-Type-Options') == 'nosniff'
            if path != '/':
                assert headers.get('Cache-Control') == 'public, max-age=300'
            expect('public method restriction ' + path, path, 403, method='POST', body='synthetic')
        expect('public stylesheet', '/_landing/site.css', 200, b'SYNTHETIC PUBLIC STYLE')
        expect('public icon', '/_landing/favicon.svg', 200, b'<svg')
        expect('unknown public asset', '/_landing/private.json', 404)
        stylesheet = site / 'site.css'
        content = stylesheet.read_bytes()
        stylesheet.unlink()
        try:
            expect('missing public file has no upstream fallback', '/_landing/site.css', 404)
        finally:
            stylesheet.write_bytes(content)
        assert not upstream.requests, 'Public files and rejected public paths must never reach the application.'
        expect('HTTP canonical origin', '/app?synthetic=1', 308,
               location='https://public.example.test/app?synthetic=1', secure=False)
        query = 'x=a%2Fb&x=c+z'
        expect('application slash canonicalization', '/app/?' + query, 308,
               location=f'https://public.example.test:{https_port}/app?' + query)
        expect('private application unauthenticated', '/app?' + query, 302, location='/auth')
        assert upstream.requests[-1][1] == '/?' + query, 'Application query must reach its native root unchanged.'
        expect('private API', '/api/private', 401, b'sign_in_required')
        expect('private application asset', '/app.js', 302, location='/auth')
        expect('sign-in form', '/auth', 200, b'SYNTHETIC SIGN IN')
        headers = expect('successful login', '/auth/login', 302, location='/app', method='POST', body='valid=1')
        assert headers.get('Set-Cookie', '').startswith('synthetic=accepted;')
        expect('failed login', '/auth/login', 302, location='/auth?failed=1', method='POST', body='valid=0')
        expect('other auth redirect', '/auth/claim', 302, location='/', method='POST', body='synthetic')
        private = {'Cookie': 'synthetic=accepted'}
        untrusted = {'X-Forwarded-For': '192.0.2.99', 'X-Forwarded-Proto': 'http',
                     'X-Forwarded-Host': 'untrusted.example.test', 'Forwarded': 'host=untrusted.example.test'}
        def forwarded_as(label, target):
            path, forwarded = upstream.requests[-1][1], upstream.requests[-1][2]
            assert path == target, label + ': unexpected upstream URI'
            assert forwarded.get('Host') == 'public.example.test', label + ': the upstream Host must be the public host'
            assert forwarded.get('X-Forwarded-Proto') == 'https', label + ': forwarding headers must be the gateway\'s'
            assert forwarded.get('X-Forwarded-For') == '127.0.0.1', label + ': forwarding headers must be the gateway\'s'
            assert 'Forwarded' not in forwarded and 'X-Forwarded-Host' not in forwarded, label + ': untrusted forwarding headers must be cleared'
        expect('authenticated application', '/app?synthetic=1', 200, b'SYNTHETIC PRIVATE APPLICATION', headers={**private, **untrusted})
        forwarded_as('authenticated application', '/?synthetic=1')
        expect('authenticated application asset', '/app.js', 200, b'SYNTHETIC PRIVATE APPLICATION', headers=private)
        expect('application redirect is unchanged', '/api/redirect', 302, location='/', headers=private)
        expect('nested application path is unchanged', '/app/other?synthetic=1', 200,
               b'SYNTHETIC PRIVATE APPLICATION', headers=private)
        assert upstream.requests[-1][1] == '/app/other?synthetic=1'
        # The gateway keeps the API's body limits and refuses a larger API body as the API does, so a page can
        # tell that nothing was written.
        too_large = b'{"code":"request_too_large","message":"The request body exceeds its %d KiB limit."}'
        for path, limit in [('/api', 32), ('/api/private', 32), ('/api/private.html', 32), ('/api/interval-series/revise', 8192)]:
            expect(f'API body at its {limit} KiB limit to {path}', path, 200, b'SYNTHETIC PRIVATE APPLICATION', method='POST',
                   body=b'x' * (limit * 1024), headers={**private, **untrusted})
            forwarded_as(f'API body to {path}', path)
            assert upstream.requests[-1][2].get('Content-Length') == str(limit * 1024), 'The whole API body must reach the application.'
            reached = len(upstream.requests)
            headers = expect(f'API body over its {limit} KiB limit to {path}', path, 413, method='POST',
                             body=b'x' * (limit * 1024 + 1), headers=private, answer=too_large % limit)
            assert headers.get('Content-Type') == 'application/json; charset=utf-8', path + ': the refusal must be the API answer'
            assert len(upstream.requests) == reached, 'An API body over its limit must not reach the application.'
        for path in ['/auth/login', '/auth/claim', '/app']:
            reached = len(upstream.requests)
            headers = expect(f'body over 32 KiB to {path}', path, 413, method='POST', body=b'x' * (32 * 1024 + 1))
            assert len(upstream.requests) == reached, 'A body over 32 KiB must not reach the application.'
            assert not headers.get('Content-Type', '').startswith('application/json'), 'Only API routes answer as the API does.'
        # The page assistant's event stream passes through as it is written (docs/ai-assistant.md §11): its first frame
        # arrives while the application still holds the response open, waiting for that frame to be read.
        upstream.first_read = threading.Event()
        connection = http.client.HTTPSConnection('127.0.0.1', https_port, context=ssl._create_unverified_context(), timeout=5)
        try:
            connection.request('GET', '/api/assistant/conversations/00000000-0000-4000-8000-000000000001/events?after=0',
                               headers={'Host': values['PUBLIC_HOST'], 'Accept': 'text/event-stream', **private})
            response = connection.getresponse()
            first = response.readline() + response.readline()
            upstream.first_read.set()
            rest = response.read()
        finally:
            connection.close()
        assert response.status == 200 and first == b': open\n\n' and rest == b': ping\n\n', 'An event stream must pass through unbuffered.'
        checked += 1
        print(f'PASS {checked} owned public gateway route checks; application authentication is synthetic.')
    finally:
        try:
            if process is not None:
                stop_owned_group(process)
            stopped = True
        finally:
            upstream.shutdown()
            upstream.server_close()
            thread.join(timeout=5)
            if stopped:
                shutil.rmtree(temporary)
            else:
                print('Owned nginx cleanup unconfirmed; retaining its private fixture directory.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--nginx', type=Path, required=True, help='Installed nginx executable with HTTP/2 and TLS support')
    args = parser.parse_args()
    if not args.nginx.is_file():
        parser.error('Supply an installed nginx executable explicitly.')
    run_checks(args.nginx.resolve())


if __name__ == '__main__':
    main()
