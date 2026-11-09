#!/usr/bin/env python3
"""Qualify publication persistence in an isolated, disposable stock PostgreSQL cluster."""
import argparse
from copy import deepcopy
import json
import os
from pathlib import Path
import shlex
import shutil
import subprocess
import tempfile
from uuid import uuid4

from preview_fixture import fixture, trading_fixture

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pg-bin', type=Path, required=True)
    args = parser.parse_args()
    if not __debug__:
        parser.error('Assertions must remain enabled')
    bins = {name: args.pg_bin.resolve() / name for name in ('initdb', 'pg_ctl', 'psql')}
    for binary in bins.values():
        if not binary.is_file(): parser.error(f'Missing installed binary: {binary}')
    directory = Path(tempfile.mkdtemp(prefix='openetrm-publications-', dir='/tmp')).resolve()
    directory.chmod(0o700)
    data = directory / 'data'
    env = {k: v for k, v in os.environ.items() if not k.startswith(('PG', 'OPENETRM_'))}
    env['PGTZ'] = 'UTC'
    started = False

    def run(command, **kwargs):
        result = subprocess.run([str(c) for c in command], cwd=ROOT, env=env, text=True,
                                capture_output=True, timeout=90, **kwargs)
        if result.returncode: raise RuntimeError(result.stderr)
        return result.stdout.strip()

    try:
        run([bins['initdb'], '-D', data, '--auth-local=trust', '--auth-host=reject', '--encoding=UTF8',
             '--locale=C', '--no-instructions', '--username=preview_owner'])
        started = True
        run([bins['pg_ctl'], '-D', data, '-w', '-t', '15', '-l', directory / 'postgres.log', '-o',
             shlex.join(['-k', str(directory), '-h', '', '-p', '55447', '-c', 'track_commit_timestamp=on']), 'start'])
        psql = [bins['psql'], '-X', '-h', directory, '-p', '55447', '-U', 'preview_owner', '-d', 'postgres',
                '-qAt', '-v', 'ON_ERROR_STOP=1', '-v', 'VERBOSITY=verbose']

        def query(sql): return run(psql, input=sql)

        assert Path(query('SHOW data_directory;')).resolve() == data
        for migration in sorted((ROOT / 'db/migrations').glob('[0-9][0-9][0-9][0-9]_*.sql')):
            query(migration.read_text())
        reference, market, security, manifest = fixture(ROOT)
        for sql in (reference, market, security, trading_fixture(manifest['catalogId'])): query(sql)
        catalog, rcat, rrev, tenant, workspace = [manifest[k] for k in
            ('catalogId', 'referenceCatalogId', 'referenceRevisionId', 'tenantId', 'workspaceId')]
        query("INSERT INTO security.role_permissions SELECT id,p FROM security.roles CROSS JOIN (VALUES('WriteMarketDefinitions'),('ImportObservations')) x(p) WHERE code='trader' ON CONFLICT DO NOTHING;")

        def runtime(sql, login='trader'):
            token = query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
                          f"'https://preview.invalid','{login}','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',120);")
            return query(f"BEGIN;SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token}');{sql}COMMIT;")

        def literal(value): return "'" + json.dumps(value).replace("'", "''") + "'::jsonb"
        def save(body, expected=0, request=None, login='trader'):
            request = request or str(uuid4())
            return json.loads(runtime(f"SELECT market_data.save_price_publication('{catalog}','{request}',{expected},{literal(body)})::text;", login))

        def reject(code, action):
            try: action()
            except RuntimeError as error:
                if code not in str(error): raise
            else: raise AssertionError(f'Expected {code}')

        refs = dict(catalogId=rcat, revisionId=rrev)
        def ref(code): return dict(revision=refs, code=code)
        row = json.loads(query(f"""SELECT row_to_json(x) FROM(SELECT i.code index_code,i.commodity_code,i.location_code,q.code quote_code,q.currency_code,q.quantity_code,trim_scale(q.multiplier)::text multiplier
            FROM ref_data.price_index_definitions i JOIN ref_data.quantity_specifications u ON(u.catalog_id,u.revision_id,u.commodity_code,u.unit_code)=(i.catalog_id,i.revision_id,i.commodity_code,i.quote_unit_code)
            JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.quantity_code,q.currency_code,q.multiplier)=(i.catalog_id,i.revision_id,u.code,i.currency_code,i.quote_multiplier)
            WHERE i.catalog_id='{rcat}' AND i.revision_id='{rrev}' AND q.kind='outright' AND i.code='test_price' ORDER BY i.code,q.code LIMIT 1)x;"""))
        body = dict(key=dict(catalogId=catalog, id=str(uuid4()), version=1), references=refs,
                    publisher=dict(key=dict(catalogId=catalog, id=str(uuid4()), version=1), name='Synthetic publisher'),
                    name='Synthetic gas report', commodities=[ref(row['commodity_code'])],
                    indices=[dict(key=ref(row['index_code']), commodity=ref(row['commodity_code']), location=ref(row['location_code']),
                                  quote=ref(row['quote_code']), unit=dict(currency=ref(row['currency_code']),quantity=ref(row['quantity_code']),multiplier=row['multiplier']),
                                  symbol='SYNTH_GAS', statistic='monthly_index')],
                    calendar=ref(query(f"SELECT code FROM ref_data.calendars WHERE(catalog_id,revision_id)=('{rcat}','{rrev}') ORDER BY code LIMIT 1;")),
                    clock=ref(query(f"SELECT code FROM ref_data.clock_rules WHERE(catalog_id,revision_id)=('{rcat}','{rrev}') ORDER BY code LIMIT 1;")),
                    implementation='gas_monthly_explicit_v1')
        before = runtime(f"SELECT security.capture_date_context('{catalog}','2020-12-15',NULL);")
        request = str(uuid4())
        first = save(body, request=request)
        assert first == body, (first, body)
        assert save(body, request=request) == first
        assert save(body) == first
        reject('publication_request_conflict', lambda: save(body | {'name':'Changed'}, request=request))
        reject('publication_denied', lambda: save(body, login='viewer'))
        invalid = deepcopy(body); invalid['indices'][0]['unit']['multiplier'] = '999'
        reject('publication_index', lambda: save(invalid))
        changed = deepcopy(body); changed['key']['version'] = 2; changed['name'] = 'Revised gas report'
        assert save(changed, 1) == changed
        stale = deepcopy(body); stale['name'] = 'Stale writer'
        reject('publication_revision_conflict', lambda: save(stale))
        assert sorted(json.loads(runtime(f"SELECT market_data.read_price_publications('{catalog}')::text;")), key=lambda p:p['key']['version']) == [first, changed]
        assert runtime(f"SELECT count(*) FROM market_data.price_publication_versions;") == '0'
        # Reuse the certified shared date context: newer definitions cannot appear in its read.
        historical = runtime(f"SELECT security.use_market_context('{catalog}','{before}');SELECT market_data.read_price_publications('{catalog}')::text;")
        assert json.loads(historical) == [], historical
        reject('publication_historical_write', lambda: runtime(f"SELECT security.use_market_context('{catalog}','{before}');SELECT market_data.save_price_publication('{catalog}','{uuid4()}',0,{literal(body)});"))
        count = query(f"SELECT count(*) FROM market_data.price_publication_versions WHERE publication_id='{body['key']['id']}';")
        assert count == '2', count
        occurrence_id, occurrence_request = str(uuid4()), str(uuid4())
        occurrence = dict(publication=changed['key'], assessmentFrom='2020-12-28T12:00:00Z', assessmentTo='2020-12-30T20:00:00Z',
                          assessmentDate='2021-01-01', periods=[dict(from_='2021-01-01', to='2021-02-01')], expectedReleaseAt='2021-01-01T20:00:00Z')
        occurrence['periods'] = [{'from': p['from_'], 'to':p['to']} for p in occurrence['periods']]
        def save_occurrence(selected, oid=occurrence_id, request=occurrence_request):
            return json.loads(runtime(f"SELECT market_data.save_price_publication_occurrence('{catalog}','{request}','{oid}',{literal(selected)})::text;"))
        saved_occurrence = save_occurrence(occurrence)
        assert save_occurrence(occurrence) == saved_occurrence
        assert save_occurrence(occurrence, str(uuid4()), str(uuid4())) == saved_occurrence
        reject('publication_occurrence_conflict', lambda: save_occurrence(occurrence | {'expectedReleaseAt':'2021-01-02T20:00:00Z'}, request=str(uuid4())))
        release_request = str(uuid4())
        release = dict(occurrenceId=occurrence_id,indexCode=row['index_code'],sourceCode='fixture',sourceEventId='synthetic-monthly-release',
                       sourceRevisionId='revision-a',publishedAt='2020-12-31T12:05:00Z',value='3.27',correctsReleaseId=None)
        revision = manifest['revisionId']
        def ingest(selected, request=None, login='trader'):
            return json.loads(runtime(f"SELECT market_data.ingest_price_publication_final('{catalog}','{revision}','{request or uuid4()}',{literal(selected)})::text;",login))
        first_release = ingest(release,release_request)
        assert len(first_release['observationIds']) == 1
        assert ingest(release,release_request) == first_release
        assert ingest(release) == first_release
        reject('publication_revision_conflict',lambda:ingest(release | {'value':'3.29'}))
        reject('publication_denied',lambda:ingest(release,login='viewer'))
        reject('publication_time',lambda:ingest(release | {'sourceRevisionId':'early','publishedAt':'2020-12-29T12:00:00Z'}))
        correction = release | dict(sourceRevisionId='revision-b',publishedAt='2021-01-02T12:05:00Z',value='3.28',correctsReleaseId=first_release['releaseId'])
        corrected = ingest(correction)
        assert corrected['correctsReleaseId'] == first_release['releaseId']
        observed = query(f"SELECT value::text||':'||(observed_at='2020-12-31T12:05:00Z'::timestamptz)::text||':'||(corrects_id='{first_release['observationIds'][0]}')::text FROM market_data.observations WHERE id='{corrected['observationIds'][0]}';")
        assert observed == '3.28:true:true', observed
        assert query(f"SELECT value::text FROM market_data.observations WHERE id='{first_release['observationIds'][0]}';") == '3.27'
        reject('publication_correction',lambda:ingest(correction | {'sourceRevisionId':'revision-c','value':'3.29'}))
        historical_occurrences=runtime(f"SELECT security.use_market_context('{catalog}','{before}');SELECT market_data.read_price_publication_occurrences('{catalog}')::text;")
        assert json.loads(historical_occurrences) == []
        assert json.loads(runtime(f"SELECT market_data.read_price_publication_releases('{catalog}','2021-01-01T23:59:59Z')::text;")) == [first_release]
        assert {r['releaseId'] for r in json.loads(runtime(f"SELECT market_data.read_price_publication_releases('{catalog}','2021-01-15T00:00:00Z')::text;"))} == {first_release['releaseId'], corrected['releaseId']}
        assert json.loads(runtime(f"SELECT security.use_market_context('{catalog}','{before}');SELECT market_data.read_price_publication_releases('{catalog}','2021-01-15T00:00:00Z')::text;")) == []
        assert query('SELECT count(*) FROM market_data.price_publication_releases;') == '2'
        print('PASS publication persistence: exact references, immutable versions, idempotency, stale-write refusal, current authorization and historical invisibility', flush=True)
        print('PASS publication ingestion: retained schedules, exact final actuals, retry/no-op, linked immutable corrections and both publication time coordinates', flush=True)
    finally:
        if started:
            result = subprocess.run([str(bins['pg_ctl']), '-D', str(data), '-m', 'immediate', '-w', '-t', '15', 'stop'],
                                    env=env, text=True, capture_output=True, timeout=25)
            if result.returncode:
                raise RuntimeError(f'Owned cluster could not stop and was retained for inspection: {directory}')
        shutil.rmtree(directory)


if __name__ == '__main__': main()
