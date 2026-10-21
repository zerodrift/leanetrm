"""Cookie/CSRF client for explicitly selected loopback synthetic previews."""
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
    def __init__(self, url, example='baseline'):
        address = urlsplit(url)
        if address.scheme != 'http' or address.hostname != '127.0.0.1' or not address.port or address.path not in ('', '/') or address.query or address.fragment or address.username or address.password:
            raise ValueError('An explicit loopback preview origin is required.')
        if example not in ('baseline','exchange'):raise ValueError('Choose a configured preview example.')
        self.api_prefix='/api/' if example=='baseline' else '/examples/exchange/api/'
        self.url = url.rstrip('/')
        self.opener = build_opener(HTTPCookieProcessor(http.cookiejar.CookieJar()))
        self.token = ''

    def call(self, path, body=None, expected=200, headers=None):
        """POST a body, serialized unless it is already bytes, or GET without one."""
        request_headers = {'Origin': self.url, **(headers or {})}
        if body is not None:
            request_headers.update({'Content-Type':'application/json','X-Preview-CSRF':self.token})
            request_headers.update(headers or {})
        data = body if body is None or isinstance(body, bytes) else json.dumps(body).encode()
        request = Request(self.url+self.api_prefix+path, data=data, headers=request_headers, method='GET' if body is None else 'POST')
        try:
            response = self.opener.open(request, timeout=20)
        except HTTPError as error:
            response = error
        value = response.read()
        if response.code != expected:
            raise AssertionError(f'{path}: expected {expected}, got {response.code}: {value.decode()}')
        return json.loads(value) if value else None

    def bootstrap(self):
        boot = self.call('bootstrap')
        self.token = boot['csrfToken']
        return boot
