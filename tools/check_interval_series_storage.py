"""Audited interval series storage checks in the launcher's owned synthetic database."""
from datetime import datetime, timedelta, timezone
import hashlib
import json
from uuid import UUID, uuid4

TABLES = ('interval_series', 'interval_series_revisions', 'interval_series_values',
          'interval_series_requests', 'interval_series_knowledge_receipts')
HOURS = ['2027-01-01T05:00:00Z', '2027-01-01T06:00:00Z', '2027-01-01T07:00:00Z', '2027-01-01T08:00:00Z']


def run_checks(query, manifest):
    catalog, reference, ref_revision, tenant, workspace = [str(UUID(manifest[k])) for k in
        ('catalogId', 'referenceCatalogId', 'referenceRevisionId', 'tenantId', 'workspaceId')]
    passed = 0

    def check(name, condition):
        nonlocal passed
        assert condition, name
        passed += 1
        print('PASS interval series storage: ' + name, flush=True)

    def reject(sql, state):
        try:
            query('\\set VERBOSITY verbose\n' + sql)
        except RuntimeError as error:
            assert state in str(error), str(error)
        else:
            raise AssertionError('Storage accepted an invalid interval series operation')

    def issue(profile='viewer'):
        return query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','{profile}','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")

    def runtime(token, sql):
        return f"SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token}');{sql}"

    def committed(profile, sql):
        return 'BEGIN;' + runtime(issue(profile), sql) + 'COMMIT;'

    def call(profile, sql):
        return json.loads(query(committed(profile, sql)))

    def digest(text):
        return hashlib.sha256(text.encode()).hexdigest()

    def now():
        return query("SELECT to_char(clock_timestamp() AT TIME ZONE 'UTC','YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"');")

    def array(items, kind):
        return 'ARRAY[' + ','.join('NULL' if item is None else f"'{item}'" for item in items) + f']::{kind}[]'

    def define(request, code, kind='capacity_factor_forecast', specification='test_zero_rate'):
        return (f"SELECT market_data.define_interval_series('{request}','{digest(request)}','{catalog}','{reference}',"
                f"'{ref_revision}','{code}','Synthetic interval series','{kind}','{specification}','synthetic_eastern',60);")

    def revise(series, request, expected, reason, starts, ends, values, sha=None):
        return (f"SELECT market_data.append_interval_series_revision('{series}','{request}','{sha or digest(str(request))}',"
                f"{expected},'{reason}',{starts},{ends},{values});")

    def hours(first, last, values):
        return array(HOURS[first:last], 'timestamptz'), array(HOURS[first + 1:last + 1], 'timestamptz'), array(values, 'text')

    def bulk(fault=None):
        """1,000 hourly intervals valued 0.5; `fault` spoils row 500 alone."""
        shift = '-(i=500)::int' if fault == 'duplicate' else ''
        start = f"TIMESTAMPTZ '2028-01-01T00:00:00Z'+make_interval(hours=>i-1{shift})"
        minutes = 'CASE WHEN i=500 THEN 30 ELSE 60 END' if fault == 'short' else '60'
        spoiled = {'bound': '1.5', 'format': '5e-1'}.get(fault, '0.5')
        value = f"CASE WHEN i=500 THEN '{spoiled}' ELSE '0.5' END"
        rows = 'FROM generate_series(1,1000) i'
        return (f'ARRAY(SELECT {start} {rows})', f'ARRAY(SELECT {start}+make_interval(mins=>{minutes}) {rows})',
                f'ARRAY(SELECT {value} {rows})')

    def read(series, first, last, cutoff, operation='read_interval_series'):
        # Instants render in UTC whatever the caller's session zone.
        return call('viewer', f"SET LOCAL TimeZone='America/Chicago';SELECT market_data.{operation}('{series}','{first}','{last}','{cutoff}');")

    def revisions(series):
        return query(f"SELECT count(*) FROM market_data.interval_series_revisions WHERE catalog_id='{catalog}' AND series_id='{series}';")

    def utc(at):
        return at.replace('Z', '+00:00')

    request = str(uuid4())
    receipt = call('editor', define(request, 'synthetic_capacity'))
    series = str(UUID(receipt['seriesId']))
    metered = call('editor', define(str(uuid4()), 'synthetic_metered', 'metered_energy', 'test_power_quantity'))
    for kind, specification in [('multiplicative_price_shape', 'test_conversion_ratio'), ('metered_energy', 'test_conversion_ratio'),
                                ('additive_price_shape', 'test_zero_rate'), ('capacity_factor_forecast', 'absent_specification')]:
        reject(committed('editor', define(str(uuid4()), 'synthetic_other', kind, specification)), '23514: interval_series_definition')
    reject(committed('editor', define(str(uuid4()), 'synthetic_capacity')), 'P0001: interval_series_code')
    check('editor defines a capacity-factor series',
          receipt == {'requestId': request, 'seriesId': series, 'revision': 0, 'intervals': 0} and metered['revision'] == 0)

    reject(committed('viewer', define(str(uuid4()), 'synthetic_viewer')), '42501: interval_series_denied')
    reject(committed('viewer', revise(series, uuid4(), 0, 'Viewer import', *hours(0, 1, ['0.25']))), '42501: interval_series_denied')
    check('viewer cannot define or append', revisions(series) == '0')

    first, second = str(uuid4()), str(uuid4())
    initial = call('editor', revise(series, first, 0, 'Initial synthetic forecast', *hours(0, 3, ['0.25', '0.5', '0.75'])))
    between = now()
    correction = revise(series, second, 1, 'Synthetic correction', *hours(1, 3, ['0.55', None]))
    corrected = call('editor', correction)
    after = now()
    earlier, latest = read(series, HOURS[0], HOURS[3], between), read(series, HOURS[0], HOURS[3], after)
    shown = lambda result: [(row['start'], row['end'], row['value'], row['revision']) for row in result['rows']]
    check('revision 1 then a correction and a withdrawal',
          initial == {'requestId': first, 'seriesId': series, 'revision': 1, 'intervals': 3}
          and corrected == {'requestId': second, 'seriesId': series, 'revision': 2, 'intervals': 2}
          and earlier['head'] == 1 and shown(earlier) == [(utc(HOURS[i]), utc(HOURS[i + 1]), v, 1) for i, v in enumerate(['0.25', '0.5', '0.75'])]
          and latest['head'] == 2 and shown(latest) == [(utc(HOURS[0]), utc(HOURS[1]), '0.25', 1), (utc(HOURS[1]), utc(HOURS[2]), '0.55', 2)]
          and latest['series']['id'] == series and UUID(latest['contextId']) != UUID(earlier['contextId']))

    editor = query(f"""SELECT m.display_name FROM security.identity_bindings b JOIN security.workspace_memberships m ON m.principal_id=b.principal_id
      WHERE(b.issuer,b.subject)=('https://preview.invalid','editor') AND(m.tenant_id,m.workspace_id)=('{tenant}','{workspace}');""")
    changes = read(series, HOURS[0], HOURS[3], after, 'interval_series_changes')
    rows = {(row['revision'], row['start']): row for row in changes['rows']}
    changed, withdrawn, original = rows[(2, utc(HOURS[1]))], rows[(2, utc(HOURS[2]))], rows[(1, utc(HOURS[0]))]
    check('change log names actor, reason and previous value',
          [(row['revision'], row['start']) for row in changes['rows']] == [(2, utc(HOURS[1])), (2, utc(HOURS[2])), (1, utc(HOURS[0])), (1, utc(HOURS[1])), (1, utc(HOURS[2]))]
          and changed['previousValue'] == '0.5' and changed['value'] == '0.55' and changed['previousRevision'] == 1
          and changed['actor'] == editor and changed['reason'] == 'Synthetic correction' and changed['requestId'] == second
          and withdrawn['value'] is None and withdrawn['previousValue'] == '0.75'
          and original['previousRevision'] is None and original['previousValue'] is None and original['reason'] == 'Initial synthetic forecast')

    summary = lambda result: [(r['revision'], r['intervals'], r['intervalsInWindow']) for r in result['revisions']]
    narrow = read(series, HOURS[1], HOURS[2], after, 'interval_series_changes')
    newest = changes['revisions'][0]
    check('change log summarizes each revision',
          summary(changes) == [(2, 2, 2), (1, 3, 3)] and summary(narrow) == [(2, 2, 1), (1, 3, 1)]
          and (newest['actor'], newest['reason'], newest['requestId'], newest['knownFrom']) ==
              (editor, 'Synthetic correction', second, changed['knownFrom']))

    reject(committed('editor', revise(series, uuid4(), 1, 'Stale correction', *hours(0, 1, ['0.3']))), 'P0001: interval_series_revision_conflict')
    check('stale head is rejected and writes nothing', revisions(series) == '2')

    # A direct caller can bypass the managed grid; a stored interval, withdrawn or not, still holds its slot.
    single = lambda start, end: (array([start], 'timestamptz'), array([end], 'timestamptz'), array(['0.4'], 'text'))
    for start, end in [('2027-01-01T05:30:00Z', '2027-01-01T06:30:00Z'), ('2027-01-01T07:30:00Z', '2027-01-01T08:30:00Z')]:
        reject(committed('editor', revise(series, uuid4(), 2, 'Offset import', *single(start, end))), 'P0001: interval_series_values')
    adjacent = json.loads(query('BEGIN;' + runtime(issue('editor'), revise(series, uuid4(), 2, 'Adjacent import', *single(HOURS[3], '2027-01-01T09:00:00Z'))) + 'ROLLBACK;'))
    check('an offset revision cannot overlap stored intervals', adjacent['revision'] == 3 and revisions(series) == '2')

    replayed = call('editor', correction)
    unchanged = revisions(series) == '2'
    reject(committed('editor', revise(series, second, 1, 'Synthetic correction', *hours(1, 3, ['0.56', None]), digest('changed'))),
           'P0001: interval_series_conflict')
    reject(committed('editor', define(second, 'synthetic_reused')), 'P0001: interval_series_conflict')
    check('replay returns the original receipt',
          replayed == corrected and unchanged and call('editor', define(request, 'synthetic_capacity')) == receipt)

    control = json.loads(query('BEGIN;' + runtime(issue('editor'), revise(series, uuid4(), 2, 'Valid bulk control', *bulk())) + 'ROLLBACK;'))
    for fault in ('bound', 'format', 'short', 'duplicate'):
        reject(committed('editor', revise(series, uuid4(), 2, 'Bulk import', *bulk(fault))), 'P0001: interval_series_values')
    check('one bad row rejects the revision', control['revision'] == 3 and control['intervals'] == 1000 and revisions(series) == '2')

    origin = "TIMESTAMPTZ '2027-01-01T00:00:00Z'"
    widest, wider = [(datetime(2027, 1, 1, tzinfo=timezone.utc) + timedelta(hours=count)).strftime('%Y-%m-%dT%H:%M:%SZ') for count in (35136, 35137)]
    full = read(series, '2027-01-01T00:00:00Z', widest, after)
    for operation in ('read_interval_series', 'interval_series_changes'):
        for first_at, last_at, cutoff in [('2027-01-01T00:00:00Z', wider, f"'{after}'"), (HOURS[0], '2027-01-01T06:30:00Z', f"'{after}'"),
                                          (HOURS[1], HOURS[0], f"'{after}'"), (HOURS[0], HOURS[3], "clock_timestamp()+interval '1 minute'")]:
            reject(committed('viewer', f"SELECT market_data.{operation}('{series}','{first_at}','{last_at}',{cutoff});"), 'P0001: interval_series_window')
    crowded = call('editor', define(str(uuid4()), 'synthetic_crowded'))['seriesId']
    every = f"generate_series({origin},{origin}+make_interval(hours=>35135),interval '1 hour')"
    for expected in range(3):
        call('editor', revise(crowded, uuid4(), expected, 'Synthetic full rewrite', f'ARRAY(SELECT {every})',
                              f"ARRAY(SELECT t+interval '1 hour' FROM {every} t)", "ARRAY(SELECT '0.5' FROM generate_series(1,35136))"))
    current = now()
    crowded_head = read(crowded, HOURS[0], HOURS[3], current)['head']
    reject(committed('viewer', f"SELECT market_data.interval_series_changes('{crowded}','2027-01-01T00:00:00Z','{widest}','{current}');"),
           'P0001: interval_series_window')
    check('windows and cutoffs are bounded', len(full['rows']) == 2 and crowded_head == 3)

    certified = query(f"""SELECT count(*)=5 AND count(*)=count(*) FILTER(WHERE k.known_from=
      pg_xact_commit_timestamp(((k.publication_xid::text::numeric % 4294967296)::bigint::text)::xid))
      AND count(*)=(SELECT count(*) FROM market_data.interval_series_revisions WHERE catalog_id='{catalog}')
      FROM market_data.interval_series_knowledge_receipts k WHERE k.catalog_id='{catalog}';""")
    reject(f"UPDATE market_data.interval_series_knowledge_receipts SET known_from=known_from WHERE catalog_id='{catalog}';", '55000')
    check('known_from comes from commit time', certified == 't')

    for table in TABLES:
        for statement in (f"INSERT INTO market_data.{table} SELECT * FROM market_data.{table};",
                          f"UPDATE market_data.{table} SET catalog_id=catalog_id;", f"DELETE FROM market_data.{table};"):
            reject(committed('editor', statement), '42501')
        for statement in (f"UPDATE market_data.{table} SET catalog_id=catalog_id WHERE catalog_id='{catalog}';",
                          f"DELETE FROM market_data.{table} WHERE catalog_id='{catalog}';"):
            reject(statement, '55000')
    visible = query('BEGIN;' + runtime(issue(), f"SELECT count(*) FROM market_data.interval_series_values WHERE series_id='{series}';") + 'ROLLBACK;')
    unbound = query(f"SET SESSION AUTHORIZATION preview_runtime;SELECT count(*) FROM market_data.interval_series WHERE catalog_id='{catalog}';")
    check('runtime cannot insert, update or delete directly', visible == '5' and unbound == '0')
    return passed
