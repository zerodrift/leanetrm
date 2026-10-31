"""Cookie/CSRF client for explicitly selected loopback synthetic previews."""
import collections
import http.cookiejar
import json
from urllib.error import HTTPError
from urllib.parse import urlsplit
from urllib.request import Request, build_opener, HTTPCookieProcessor


def initial_frame(workspace, effective_on):
    """Select one exact original fixture frame, excluding saved current variants."""
    initial_ids = set(workspace['initialFrameIds'])
    frames = [frame for frame in workspace['frames']
              if frame['id'] in initial_ids and frame['at'].startswith(effective_on + 'T')]
    if len(frames) != 1:
        raise AssertionError(f'The fixture requires one exact original input frame for {effective_on}; found {len(frames)}')
    return frames[0]


class PreviewClient:
    # The sessions clients open in each example's scope, which the preview caps (Program.cs): each new session cookie the
    # preview sets is one.
    sessions = collections.Counter()
    COOKIES = {'baseline': 'openetrm_preview', 'exchange': 'openetrm_exchange_preview'}

    def __init__(self, url, example='baseline'):
        address = urlsplit(url)
        if address.scheme != 'http' or address.hostname != '127.0.0.1' or not address.port or address.path not in ('', '/') or address.query or address.fragment or address.username or address.password:
            raise ValueError('An explicit loopback preview origin is required.')
        if example not in ('baseline','exchange'):raise ValueError('Choose a configured preview example.')
        self.api_prefix='/api/' if example=='baseline' else '/examples/exchange/api/'
        self.example, self.session = example, None
        self.url = url.rstrip('/')
        self.jar = http.cookiejar.CookieJar()
        self.opener = build_opener(HTTPCookieProcessor(self.jar))
        self.token = ''

    def call(self, path, body=None, expected=200, headers=None, timeout=20):
        """POST a body, serialized unless it is already bytes, or GET without one."""
        request_headers = {'Origin': self.url, **(headers or {})}
        if body is not None:
            request_headers.update({'Content-Type':'application/json','X-Preview-CSRF':self.token})
            request_headers.update(headers or {})
        data = body if body is None or isinstance(body, bytes) else json.dumps(body).encode()
        request = Request(self.url+self.api_prefix+path, data=data, headers=request_headers, method='GET' if body is None else 'POST')
        try:
            response = self.opener.open(request, timeout=timeout)
        except HTTPError as error:
            response = error
        value = response.read()
        session = next((c.value for c in self.jar if c.name == self.COOKIES[self.example]), None)
        if session is not None and session != self.session:
            self.session = session
            PreviewClient.sessions[self.example] += 1
        if response.code != expected:
            raise AssertionError(f'{path}: expected {expected}, got {response.code}: {value.decode()}')
        return json.loads(value) if value else None

    def stream(self, path, body, timeout=20):
        """POST and yield each NDJSON frame as parsed; closing the generator closes the response (a client abort)."""
        headers = {'Origin': self.url, 'Content-Type': 'application/json', 'X-Preview-CSRF': self.token}
        request = Request(self.url+self.api_prefix+path, data=json.dumps(body).encode(), headers=headers, method='POST')
        try:
            response = self.opener.open(request, timeout=timeout)
        except HTTPError as error:
            raise AssertionError(f'{path}: expected a stream, got {error.code}: {error.read().decode()}') from None
        with response:
            if response.headers.get_content_type() != 'application/x-ndjson':
                raise AssertionError(f'{path}: expected NDJSON, got {response.headers.get_content_type()}')
            for line in response:
                yield json.loads(line)

    def bootstrap(self):
        boot = self.call('bootstrap')
        self.token = boot['csrfToken']
        return boot
