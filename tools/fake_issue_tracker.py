"""A loopback fake of the issue tracker's API for the issue report checks: synthetic issues only, never a real tracker.
It issues one fixed installation token, lists the issues it holds by creation order and page, and answers a create
request as its current behaviour says, so a check can make the answer arrive, be refused or be lost after the issue
exists. It does not filter a listing by time."""
import json
import threading
from datetime import datetime, timezone
from urllib.parse import parse_qs, urlsplit
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

TOKEN = 'synthetic-installation-token'


class FakeIssueTracker:
    """behaviour: create | refuse (422) | unavailable (503, nothing created) | lose (created, connection closed unanswered)."""

    def __init__(self, repository):
        self.repository, self.behaviour, self.token_status, self.issues, self.requests = repository, 'create', 201, [], []
        fake = self

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass

            def answer(self, status, value):
                payload = json.dumps(value).encode()
                self.send_response(status)
                self.send_header('Content-Type', 'application/json')
                self.send_header('Content-Length', str(len(payload)))
                self.end_headers()
                self.wfile.write(payload)

            def do_GET(self):
                fake.requests.append('GET '+self.path)
                if self.headers.get('Authorization') != 'Bearer '+TOKEN or not self.path.startswith(f'/repos/{fake.repository}/issues?'):
                    return self.answer(404, {'message': 'Not Found'})
                asked = {name: values[0] for name, values in parse_qs(urlsplit(self.path).query).items()}
                size, page = int(asked['per_page']), int(asked['page'])
                ordered = sorted(fake.issues, key=lambda issue: issue['number'], reverse=asked['direction'] == 'desc')
                self.answer(200, ordered[(page-1)*size:page*size])

            def do_POST(self):
                body = self.rfile.read(int(self.headers.get('Content-Length', '0')))
                fake.requests.append('POST '+self.path)
                if self.path.endswith('/access_tokens'):
                    return self.answer(fake.token_status, {'token': TOKEN, 'expires_at': '2040-01-01T00:00:00Z'} if fake.token_status == 201 else {'message': 'Bad credentials'})
                if self.headers.get('Authorization') != 'Bearer '+TOKEN or self.path != f'/repos/{fake.repository}/issues':
                    return self.answer(404, {'message': 'Not Found'})
                if fake.behaviour == 'refuse':
                    return self.answer(422, {'message': 'Validation Failed'})
                if fake.behaviour == 'unavailable':
                    return self.answer(503, {'message': 'Service Unavailable'})
                sent = json.loads(body)
                number = len(fake.issues)+1
                issue = {'number': number, 'html_url': f'https://github.com/{fake.repository}/issues/{number}', 'title': sent['title'], 'body': sent['body'],
                         'created_at': datetime.now(timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ')}
                fake.issues.append(issue)
                if fake.behaviour == 'lose':
                    self.close_connection = True
                    return self.connection.close()
                self.answer(201, issue)

        self.server = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
        self.url = f'http://127.0.0.1:{self.server.server_address[1]}'
        threading.Thread(target=self.server.serve_forever, daemon=True).start()

    def close(self):
        self.server.shutdown()
        self.server.server_close()
