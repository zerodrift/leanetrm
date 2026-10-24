"""Negotiated transport encoding preserves the exact authorized trading response."""
import gzip
import http.client
import json
import ssl
from urllib.parse import urlsplit
from urllib.request import HTTPCookieProcessor, Request
from preview_client import initial_frame


def _raw(client, origin, csrf, path, body=None, encoding=None, headers=None, tls=None):
    """Reuse the client's cookies; omit Accept-Encoding literally when requested."""
    target = path if path.startswith('/') else '/api/' + path
    payload = None if body is None else json.dumps(body).encode()
    selected = dict(headers or {})
    if payload is not None:
        selected = {'Origin': origin, 'Content-Type': 'application/json',
                    'X-Preview-CSRF': csrf, **selected}
    if encoding is not None:
        selected['Accept-Encoding'] = encoding
    request = Request(origin + target, data=payload, headers=selected)
    cookie_handler = next(h for h in client.opener.handlers if isinstance(h, HTTPCookieProcessor))
    cookie_handler.cookiejar.add_cookie_header(request)
    address = urlsplit(origin)
    connection = (http.client.HTTPSConnection(address.hostname, address.port, context=tls, timeout=20)
                  if address.scheme == 'https' else http.client.HTTPConnection(address.hostname, address.port, timeout=20))
    try:
        connection.putrequest('GET' if payload is None else 'POST', target, skip_accept_encoding=True)
        for name, value in request.header_items():
            connection.putheader(name, value)
        if payload is not None:
            connection.putheader('Content-Length', str(len(payload)))
        connection.endheaders(payload)
        response = connection.getresponse()
        return response.status, {k.lower(): v for k, v in response.getheaders()}, response.read()
    finally:
        connection.close()


def _checks(raw, frame, check):
    view = {'mode': 'trades', 'bookIds': [], 'portfolioIds': [], 'groupBy': [],
            'measures': [], 'pivotConvention': None}
    current = {'snapshotId': frame['id'], 'convention': 'trading_workspace_v1', 'knowledgeMode': 'current'}
    body = {'context': current, 'view': view, 'bookingCatalog': 'priced_contracts'}
    status, _, first = raw('trading/workspace', body, 'identity')
    assert status == 200
    body['context'] = {**current, 'knowledgeMode': 'retained',
                       'contextId': json.loads(first)['catalog']['context']['contextId']}
    status, plain_headers, plain = raw('trading/workspace', body, 'identity')
    assert status == 200 and 'content-encoding' not in plain_headers
    status, zipped_headers, zipped = raw('trading/workspace', body, 'gzip')
    check('trading JSON explicitly negotiates gzip', status == 200 and zipped_headers.get('content-encoding') == 'gzip')
    check('gzip preserves every byte of the retained authorized financial response', gzip.decompress(zipped) == plain)
    check('gzip materially reduces trading wire bytes', len(zipped) < len(plain) * 0.75)
    check('compressed trading JSON keeps no-store and encoding variation',
          zipped_headers.get('cache-control') == plain_headers.get('cache-control') == 'no-store'
          and 'accept-encoding' in {v.strip().lower() for v in zipped_headers.get('vary', '').split(',')}
          and zipped_headers.get('content-type', '').startswith('application/json'))
    directory = {**body, 'bookingCatalog': 'directory'}
    directory_status, _, directory_plain = raw('trading/workspace', directory, 'identity')
    status, headers, compressed_directory = raw('trading/workspace', directory, 'gzip')
    check('directory mode keeps the exact identity response under gzip',
          directory_status == status == 200 and headers.get('content-encoding') == 'gzip'
          and gzip.decompress(compressed_directory) == directory_plain)
    report = {'context': body['context'], 'view': view}
    report_status, _, report_plain = raw('trading/blotter', report, 'identity')
    status, headers, report_requested_gzip = raw('trading/blotter', report, 'gzip')
    check('unrelated authenticated POST responses remain uncompressed and unchanged',
          report_status == status == 200 and 'content-encoding' not in headers
          and report_requested_gzip == report_plain)
    for encoding in [None, 'identity', 'gzip;q=0', 'br', 'unsupported']:
        status, headers, value = raw('trading/workspace', body, encoding)
        check('uncompressed negotiation retains the legacy body: ' + str(encoding),
              status == 200 and 'content-encoding' not in headers and value == plain
              and headers.get('cache-control') == 'no-store')
    for path in ['bootstrap', 'access']:
        status, headers, value = raw(path, None, 'gzip')
        check('token and identity reads stay outside compression: ' + path,
              status == 200 and 'content-encoding' not in headers
              and 'no-store' in {v.strip().lower() for v in headers.get('cache-control', '').split(',')})
        if path == 'bootstrap':
            check('bootstrap still supplies its antiforgery contract without compression', bool(json.loads(value)['csrfToken']))
    for headers, expected in [({'X-Preview-CSRF': ''}, 400), ({'Origin': 'https://attacker.invalid'}, 403)]:
        status, response_headers, value = raw('trading/workspace', body, 'gzip', headers)
        check('gzip does not bypass origin or antiforgery admission',
              status == expected and 'content-encoding' not in response_headers)
    return body


def run_checks(c, check, query, manifest):
    c.call('profile', {'profile': 'trader'}); c.call('market/current', {})
    data = c.call('workspace')
    frame = initial_frame(data, '2020-12-18')
    def raw(path, body=None, encoding=None, headers=None):
        return _raw(c, c.url, c.token, path, body, encoding, headers)
    body = _checks(raw, frame, check)
    c.call('profile', {'profile': 'viewer'})
    status, headers, _ = raw('trading/workspace', body, 'gzip')
    check('gzip cannot confer current trading authority', status == 403 and 'content-encoding' not in headers)


def run_hosted_checks(client, origin, certificate, boot):
    """Call in the existing owned TLS/forwarded-header fixture before its empty reset."""
    tls = ssl.create_default_context(cafile=str(certificate))
    passed = 0
    def check(name, condition):
        nonlocal passed
        assert condition, name
        passed += 1
        print('PASS hosted compression: ' + name, flush=True)
    def raw(path, body=None, encoding=None, headers=None):
        return _raw(client, origin, client.csrf, path, body, encoding, headers, tls)
    frame = next(f for f in boot['frames'] if f['at'].startswith('2020-12-18') and not f.get('contextId'))
    _checks(raw, frame, check)
    print(f'{passed} hosted HTTPS trading compression checks passed.', flush=True)
