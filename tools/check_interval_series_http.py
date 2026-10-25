"""Audited interval series HTTP checks against the launcher's owned synthetic preview."""
from http.client import HTTPResponse
from datetime import datetime
import hashlib
import json
import re
import socket
from urllib.parse import urlsplit
from urllib.request import HTTPCookieProcessor
from uuid import UUID, uuid4

from preview_client import PreviewClient
from preview_fixture import installed_tzdb

# A clock pinned to a tzdb release other than the installed one: the state of every series on an
# IANA clock after the host's time-zone data is updated.
STALE_CLOCK, STALE_TZDB = 'synthetic_stale_eastern', '2000a'


def prepare_market(market, manifest):
    """Add the stale clock to the synthetic reference revision before the fixture seals it."""
    if installed_tzdb() == STALE_TZDB:
        raise ValueError('The stale clock must declare a tzdb release other than the installed one')
    reference, revision = [str(UUID(manifest[k])) for k in ('referenceCatalogId', 'referenceRevisionId')]
    marker = 'UPDATE ref_data.revisions SET'
    if market.count(marker) != 1:
        raise ValueError('The synthetic fixture must have exactly one reference sealing statement')
    rule = (f"INSERT INTO ref_data.clock_rules (catalog_id,revision_id,code,kind,iana_zone,tzdb_version) "
            f"VALUES ('{reference}','{revision}','{STALE_CLOCK}','iana','America/New_York','{STALE_TZDB}');")
    return market.replace(marker, rule + '\n' + marker, 1)


def declared(client, path, body):
    """(status, code, message) of the server's answer to a POST that declares the body's size with Expect: 100-continue.

    The body is never sent, so an early refusal cannot race the upload; a server that asks for it
    (100 Continue) has accepted the size, and the read times out."""
    address = urlsplit(client.url)
    jar = next(h.cookiejar for h in client.opener.handlers if isinstance(h, HTTPCookieProcessor))
    head = (f'POST {client.api_prefix}{path} HTTP/1.1\r\nHost: {address.netloc}\r\nOrigin: {client.url}\r\n'
            f'Cookie: {"; ".join(f"{c.name}={c.value}" for c in jar)}\r\nX-Preview-CSRF: {client.token}\r\n'
            f'Content-Type: application/json\r\nContent-Length: {len(json.dumps(body))}\r\nExpect: 100-continue\r\n\r\n')
    with socket.create_connection((address.hostname, address.port), timeout=10) as connection:
        connection.sendall(head.encode())
        response = HTTPResponse(connection)
        response.begin()
        answer = response.read()
    refusal = json.loads(answer) if answer else {}
    return response.status, refusal.get('code'), refusal.get('message')


def chunked(client, path, size):
    """(status, code, message) of the answer to a POST whose body, over `size` bytes, is sent chunked with no
    declared length, so only reading it can find it too large."""
    address = urlsplit(client.url)
    jar = next(h.cookiejar for h in client.opener.handlers if isinstance(h, HTTPCookieProcessor))
    head = (f'POST {client.api_prefix}{path} HTTP/1.1\r\nHost: {address.netloc}\r\nOrigin: {client.url}\r\n'
            f'Cookie: {"; ".join(f"{c.name}={c.value}" for c in jar)}\r\nX-Preview-CSRF: {client.token}\r\n'
            f'Content-Type: application/json\r\nTransfer-Encoding: chunked\r\n\r\n')
    body = b'{"value":"' + b'1' * size + b'"}'
    with socket.create_connection((address.hostname, address.port), timeout=10) as connection:
        connection.sendall(head.encode())
        try:
            for start in range(0, len(body), 16384):
                piece = body[start:start + 16384]
                connection.sendall(f'{len(piece):x}\r\n'.encode() + piece + b'\r\n')
            connection.sendall(b'0\r\n\r\n')
        except OSError:
            pass  # The server may answer and close before the whole body is sent.
        response = HTTPResponse(connection)
        response.begin()
        answer = response.read()
    refusal = json.loads(answer) if answer else {}
    return response.status, refusal.get('code'), refusal.get('message')


def run_checks(url, query, manifest):
    tenant, workspace = manifest['tenantId'], manifest['workspaceId']
    passed = 0

    def check(name, condition):
        nonlocal passed
        assert condition, name
        passed += 1
        print('PASS interval series http: ' + name, flush=True)

    def refused(path, body, status, code):
        return client.call(path, body, expected=status)['code'] == code

    def revise(expected, start, values, reason='Synthetic HTTP import'):
        return {'requestId': str(uuid4()), 'seriesId': series, 'expectedRevision': expected,
                'reason': reason, 'segments': [{'start': start, 'values': values}]}

    def window(start, end):
        # Each read pins a fresh certified cutoff, as the screen does on every catalog load.
        return {'seriesId': series, 'from': start, 'to': end, 'knownAt': client.call('interval-series')['knownAt']}

    def utc(at):
        return at.replace('Z', '+00:00')

    anonymous = PreviewClient(url)
    anonymous.bootstrap()
    anonymous.call('interval-series', expected=403)
    client = PreviewClient(url)
    client.bootstrap()
    client.call('profile', {'profile': 'viewer'})
    viewed = client.call('interval-series')
    definition = {'requestId': str(uuid4()), 'code': 'synthetic_http_capacity', 'name': 'Synthetic HTTP capacity',
                  'kind': 'capacity_factor_forecast', 'specification': 'test_zero_rate', 'clock': 'synthetic_eastern',
                  'intervalMinutes': 60}
    client.call('interval-series/define', definition, expected=403)
    check('anonymous and viewer writes are refused', viewed['canDefine'] is False and viewed['canRevise'] is False)

    client.call('profile', {'profile': 'editor'})
    catalog = client.call('interval-series')
    def parsed_at(value):
        return datetime.fromisoformat(re.sub(r'(\.\d{6})\d+', r'\1', value).replace('Z', '+00:00'))

    # Latest browsing is bounded and honors withdrawals and the caller's exact cutoff.
    context = client.call('date-schedules/context', {'effectiveOn': '2027-01-01', 'knownAt': None,
                                                   'mode': 'end_of_day', 'timeZone': 'America/Chicago'})
    check('same-day default never exceeds certified knowledge', parsed_at(context['context']['knownAt']) == parsed_at(context['certifiedThrough']))
    choices = catalog['choices']
    hours = ['2027-01-01T05:00:00Z', '2027-01-01T06:00:00Z', '2027-01-01T07:00:00Z', '2027-01-01T08:00:00Z']
    # test_zero_rate is a value specification, so it cannot shape an additive (quote) price.
    invalid = [({'code': None}, 'interval_series_code'), ({'name': ' Padded name'}, 'interval_series_definition'),
               ({'intervalMinutes': 45}, 'interval_series_resolution'), ({'kind': 'additive_price_shape'}, 'interval_series_definition')]
    refusals = [refused('interval-series/define', {**definition, 'requestId': str(uuid4()), **change}, 422, code) for change, code in invalid]
    defined = client.call('interval-series/define', definition)
    series = defined['seriesId']
    refusals += [refused('interval-series/define', {**definition, 'requestId': str(uuid4())}, 422, 'interval_series_code'),
                 refused('interval-series/revise', revise(0, hours[0], ['0.25'], ' '), 422, 'interval_series_reason'),
                 refused('interval-series/revise', revise(0, hours[0], ['0.25'], 'x' + ' ' * 600), 422, 'interval_series_reason')]
    # A missing expected revision is refused while binding, never read as revision 0.
    unstated = revise(0, hours[0], ['0.25'])
    del unstated['expectedRevision']
    if client.call('interval-series/revise', unstated, expected=400)['code'] != 'request_format':
        raise AssertionError('A missing request member must be refused as request_format')
    check('a series without values has no latest window', client.call('interval-series/latest', {'seriesId': series, 'knownAt': catalog['knownAt']})['window'] is None)
    check('read requests refuse unknown coordinates', refused('interval-series/values', {**window(hours[0], hours[3]), 'effectiveOn': '2027-01-01'}, 400, 'request_format'))
    first = revise(0, hours[0], ['0.25', '0.5', '0.75'], 'Initial synthetic forecast')
    receipt = client.call('interval-series/revise', first)
    values = client.call('interval-series/values', window(hours[0], hours[3]))
    latest = client.call('interval-series/latest', {'seriesId': series, 'knownAt': client.call('interval-series')['knownAt']})
    check('latest window ends at the newest available record', parsed_at(latest['window']['to']) == parsed_at(hours[3]))
    # A read renders instants with +00:00; sending its knownAt back reads the same values.
    reread = client.call('interval-series/values', {**window(hours[0], hours[3]), 'knownAt': values['knownAt']})
    check('editor defines and imports an hourly series',
          all(refusals) and catalog['canDefine'] is True and catalog['canRevise'] is True
          and 'synthetic_eastern' in choices['clocks'] and choices['resolutions'] == [5, 15, 30, 60]
          and 'test_zero_rate' in choices['specifications']['capacity_factor_forecast']
          and 'test_conversion_ratio' not in choices['specifications']['capacity_factor_forecast']
          and 'test_power_outright' in choices['specifications']['additive_price_shape']
          and 'test_power_quantity' in choices['specifications']['metered_energy']
          and defined == {'requestId': definition['requestId'], 'seriesId': series, 'revision': 0, 'intervals': 0}
          and receipt == {'requestId': first['requestId'], 'seriesId': series, 'revision': 1, 'intervals': 3}
          and values['head'] == 1 and values['series']['code'] == 'synthetic_http_capacity'
          and values['knownAt'].endswith('+00:00') and reread['rows'] == values['rows'] and reread['knownAt'] == values['knownAt']
          and [(row['start'], row['end'], row['value'], row['revision']) for row in values['rows']] ==
              [(utc(hours[i]), utc(hours[i + 1]), value, 1) for i, value in enumerate(['0.25', '0.5', '0.75'])])

    resent = client.call('interval-series/revise', first)
    listed = next(s for s in client.call('interval-series')['series'] if s['id'] == series)
    check('lost response is retried safely',
          resent == receipt and listed['head'] == 1
          and {k: listed[k] for k in ('code', 'kind', 'specification', 'clock', 'intervalMinutes')} ==
              {'code': 'synthetic_http_capacity', 'kind': 'capacity_factor_forecast', 'specification': 'test_zero_rate',
               'clock': 'synthetic_eastern', 'intervalMinutes': 60})

    changed = {**first, 'segments': [{'start': hours[0], 'values': ['0.25', '0.5', '0.7']}]}
    check('changed payload under the same request id conflicts',
          refused('interval-series/revise', changed, 409, 'interval_series_conflict'))

    check('stale expected revision conflicts',
          refused('interval-series/revise', revise(0, hours[0], ['0.3']), 409, 'interval_series_revision_conflict'))

    fallback = client.call('interval-series/revise', revise(1, '2026-11-01T04:00:00Z', ['0.5'] * 25, 'Synthetic fall-back day'))
    day = client.call('interval-series/values', window('2026-11-01T04:00:00Z', '2026-11-02T05:00:00Z'))
    starts = [row['start'] for row in day['rows']]
    check('fall-back day stores both repeated hours',
          fallback['revision'] == 2 and fallback['intervals'] == 25 and len(starts) == 25 == len(set(starts))
          and {'2026-11-01T05:00:00+00:00', '2026-11-01T06:00:00+00:00'} <= set(starts))

    check('local and misaligned starts are rejected',
          all(refused('interval-series/revise', revise(2, start, ['0.5']), 422, 'interval_series_instant')
              for start in ('2027-01-01T06:00:00+01:00', '2027-01-01T06:00:00', '2027-01-01T05:00:00.Z', '2027-01-01T05:00:00-00:00'))
          and refused('interval-series/revise', revise(2, '2027-01-01T05:30:00Z', ['0.5']), 422, 'interval_series_grid')
          and refused('interval-series/values', {**window(hours[0], hours[3]), 'from': '2027-01-01T06:00:00+01:00'},
                      422, 'interval_series_instant'))

    # Storage refuses a From or To that cuts through a stored interval; a boundary in a gap cuts nothing.
    gap = client.call('interval-series/values', {**window(hours[0], hours[3]), 'from': '2027-01-01T04:30:00Z'})
    check('a read window that cuts through a stored interval is rejected',
          all(refused(path, {**window(hours[0], hours[3]), 'from': '2027-01-01T05:30:00Z'}, 422, 'interval_series_grid')
              for path in ('interval-series/values', 'interval-series/changes'))
          and refused('interval-series/values', {**window(hours[0], hours[3]), 'to': '2027-01-01T07:59:00Z'}, 422, 'interval_series_grid')
          and [row['start'] for row in gap['rows']] == [utc(hour) for hour in hours[:3]])

    past = client.call('interval-series')['knownAt']
    client.call('market/context', {'effectiveOn': '2020-12-18', 'knownAt': past, 'mode': 'at', 'selection': 'latest_correction'})
    historical = client.call('interval-series', expected=422)['code'] == 'interval_series_historical' and all(
        refused(path, body, 422, 'interval_series_historical') for path, body in [
            ('interval-series/define', {**definition, 'requestId': str(uuid4()), 'code': 'synthetic_http_history'}),
            ('interval-series/revise', revise(2, hours[0], ['0.3'])),
            ('interval-series/values', {'seriesId': series, 'from': hours[0], 'to': hours[3], 'knownAt': past}),
            ('interval-series/changes', {'seriesId': series, 'from': hours[0], 'to': hours[3], 'knownAt': past})])
    client.call('market/current', {})
    check('historical session refuses series operations', historical and client.call('interval-series')['knownAt'] > past)

    huge = revise(2, hours[0], ['0.5'] * (9 * 1024 * 1024 // 7))
    corrections = {'snapshotId': str(uuid4()), 'observationId': str(uuid4()), 'value': '1' * (33 * 1024)}
    check('body limits are route-specific',
          declared(client, 'interval-series/revise', huge)[:2] == (413, 'request_too_large')
          and '8192 KiB' in declared(client, 'interval-series/revise', huge)[2]
          and declared(client, 'corrections', corrections)[:2] == (413, 'request_too_large')
          and '32 KiB' in declared(client, 'corrections', corrections)[2]
          and chunked(client, 'corrections', 40 * 1024)[:2] == (413, 'request_too_large')
          and refused('interval-series/revise', revise(2, hours[0], [None] * 200001), 422, 'interval_series_size'))

    editor = query(f"""SELECT m.display_name FROM security.identity_bindings b JOIN security.workspace_memberships m ON m.principal_id=b.principal_id
      WHERE(b.issuer,b.subject)=('https://preview.invalid','editor') AND(m.tenant_id,m.workspace_id)=('{tenant}','{workspace}');""")
    corrected = client.call('interval-series/revise', revise(2, hours[1], ['0.40'], 'Synthetic HTTP correction'))
    log = client.call('interval-series/changes', window(hours[0], hours[3]))
    latest = next(row for row in log['rows'] if row['revision'] == 3)
    check('change log returns actor and reason',
          corrected['revision'] == 3 and (latest['start'], latest['previousValue'], latest['value']) == (utc(hours[1]), '0.5', '0.4')
          and latest['actor'] == editor and latest['reason'] == 'Synthetic HTTP correction'
          and log['revisions'][0]['revision'] == 3 and log['revisions'][0]['actor'] == editor)

    # A series on the stale clock with a revision saved through storage, as it was before the update: reads
    # never load the clock, while a revision expands onto it and is refused.
    stale = client.call('interval-series/define', {**definition, 'requestId': str(uuid4()), 'code': 'synthetic_http_stale_clock',
                                                   'clock': STALE_CLOCK})['seriesId']
    token = query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','editor',"
                  f"'human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
    request, reason = str(uuid4()), 'Synthetic revision saved under the earlier release'
    query(f"BEGIN;SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token}');"
          f"SELECT market_data.append_interval_series_revision('{stale}','{request}','{hashlib.sha256(request.encode()).hexdigest()}',0,"
          f"'{reason}',ARRAY['{hours[0]}']::timestamptz[],ARRAY['{hours[1]}']::timestamptz[],ARRAY['0.5']);COMMIT;")
    stale_window = {**window(hours[0], hours[3]), 'seriesId': stale}
    stored, logged = client.call('interval-series/values', stale_window), client.call('interval-series/changes', stale_window)
    check('latest browsing survives a market clock tzdb update', client.call('interval-series/latest', {'seriesId': stale, 'knownAt': stale_window['knownAt']})['window'] is not None)
    check('reads stay available after a tzdata update while revisions are refused',
          [(row['start'], row['value'], row['revision']) for row in stored['rows']] == [(utc(hours[0]), '0.5', 1)]
          and [(row['start'], row['value'], row['reason']) for row in logged['rows']] == [(utc(hours[0]), '0.5', reason)]
          and refused('interval-series/revise', {**revise(1, hours[1], ['0.6']), 'seriesId': stale}, 422, 'clock_tzdb'))
    latest_series = client.call('interval-series/define', {**definition, 'requestId': str(uuid4()),
        'code': 'synthetic_latest_browsing'})['seriesId']
    client.call('interval-series/revise', {**revise(0, hours[0], ['0.1', '0.2', '0.3']), 'seriesId': latest_series})
    earlier = client.call('interval-series')['knownAt']
    client.call('interval-series/revise', {**revise(1, hours[2], [None]), 'seriesId': latest_series})
    current = client.call('interval-series')['knownAt']
    endpoint = lambda cutoff: client.call('interval-series/latest', {'seriesId': latest_series, 'knownAt': cutoff})['window']['to']
    check('latest browsing respects historical knowledge and withdrawals',
          parsed_at(endpoint(earlier)) == parsed_at(hours[3]) and parsed_at(endpoint(current)) == parsed_at(hours[2]))
    check('same-day default requires an explicit display time zone', refused('date-schedules/context',
          {'effectiveOn': '2027-01-01', 'knownAt': None, 'mode': 'end_of_day'}, 422, 'time_zone'))
    return passed
