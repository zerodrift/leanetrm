"""Audited interval series HTTP checks against the launcher's owned synthetic preview."""
from http.client import HTTPResponse
import json
import socket
from urllib.parse import urlsplit
from urllib.request import HTTPCookieProcessor
from uuid import uuid4

from preview_client import PreviewClient


def declared(client, path, body):
    """(status, code) of the server's answer to a POST that declares the body's size with Expect: 100-continue.

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
    return response.status, json.loads(answer)['code'] if answer else None


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
    choices = catalog['choices']
    hours = ['2027-01-01T05:00:00Z', '2027-01-01T06:00:00Z', '2027-01-01T07:00:00Z', '2027-01-01T08:00:00Z']
    # test_zero_rate is a value specification, so it cannot shape an additive (quote) price.
    invalid = [({'code': None}, 'interval_series_code'), ({'name': ' Padded name'}, 'interval_series_definition'),
               ({'intervalMinutes': 45}, 'interval_series_resolution'), ({'kind': 'additive_price_shape'}, 'interval_series_definition')]
    refusals = [refused('interval-series/define', {**definition, 'requestId': str(uuid4()), **change}, 422, code) for change, code in invalid]
    defined = client.call('interval-series/define', definition)
    series = defined['seriesId']
    refusals += [refused('interval-series/define', {**definition, 'requestId': str(uuid4())}, 422, 'interval_series_code'),
                 refused('interval-series/revise', revise(0, hours[0], ['0.25'], ' '), 422, 'interval_series_reason')]
    # A missing expected revision is refused while binding, never read as revision 0.
    unstated = revise(0, hours[0], ['0.25'])
    del unstated['expectedRevision']
    client.call('interval-series/revise', unstated, expected=400)
    first = revise(0, hours[0], ['0.25', '0.5', '0.75'], 'Initial synthetic forecast')
    receipt = client.call('interval-series/revise', first)
    values = client.call('interval-series/values', window(hours[0], hours[3]))
    check('editor defines and imports an hourly series',
          all(refusals) and catalog['canDefine'] is True and catalog['canRevise'] is True
          and 'synthetic_eastern' in choices['clocks'] and 'test_zero_rate' in choices['specifications']['capacity_factor_forecast']
          and 'test_power_outright' in choices['specifications']['additive_price_shape']
          and 'test_power_quantity' in choices['specifications']['metered_energy']
          and defined == {'requestId': definition['requestId'], 'seriesId': series, 'revision': 0, 'intervals': 0}
          and receipt == {'requestId': first['requestId'], 'seriesId': series, 'revision': 1, 'intervals': 3}
          and values['head'] == 1 and values['series']['code'] == 'synthetic_http_capacity'
          and [(row['start'], row['end'], row['value'], row['revision']) for row in values['rows']] ==
              [(utc(hours[i]), utc(hours[i + 1]), value, 1) for i, value in enumerate(['0.25', '0.5', '0.75'])])

    listed = next(s for s in client.call('interval-series')['series'] if s['id'] == series)
    check('lost response is retried safely',
          client.call('interval-series/revise', first) == receipt and listed['head'] == 1
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
              for start in ('2027-01-01T06:00:00+01:00', '2027-01-01T06:00:00'))
          and refused('interval-series/revise', revise(2, '2027-01-01T05:30:00Z', ['0.5']), 422, 'interval_series_grid')
          and refused('interval-series/values', {**window(hours[0], hours[3]), 'from': '2027-01-01T06:00:00+01:00'},
                      422, 'interval_series_instant'))

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
          declared(client, 'interval-series/revise', huge) == (413, 'request_size')
          and declared(client, 'corrections', corrections) == (413, 'request_size')
          and refused('interval-series/revise', revise(2, hours[0], [None] * 750001), 422, 'interval_series_size'))

    editor = query(f"""SELECT m.display_name FROM security.identity_bindings b JOIN security.workspace_memberships m ON m.principal_id=b.principal_id
      WHERE(b.issuer,b.subject)=('https://preview.invalid','editor') AND(m.tenant_id,m.workspace_id)=('{tenant}','{workspace}');""")
    corrected = client.call('interval-series/revise', revise(2, hours[1], ['0.40'], 'Synthetic HTTP correction'))
    log = client.call('interval-series/changes', window(hours[0], hours[3]))
    latest = next(row for row in log['rows'] if row['revision'] == 3)
    check('change log returns actor and reason',
          corrected['revision'] == 3 and (latest['start'], latest['previousValue'], latest['value']) == (utc(hours[1]), '0.5', '0.4')
          and latest['actor'] == editor and latest['reason'] == 'Synthetic HTTP correction'
          and log['revisions'][0]['revision'] == 3 and log['revisions'][0]['actor'] == editor)
    return passed
