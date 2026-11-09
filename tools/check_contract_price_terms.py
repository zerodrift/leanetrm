#!/usr/bin/env python3
"""Qualify contract terms persistence in an isolated, disposable stock PostgreSQL cluster."""
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
    directory = Path(tempfile.mkdtemp(prefix='openetrm-contract-terms-', dir='/tmp')).resolve()
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
             shlex.join(['-k', str(directory), '-h', '', '-p', '55448', '-c', 'track_commit_timestamp=on']), 'start'])
        psql = [bins['psql'], '-X', '-h', directory, '-p', '55448', '-U', 'preview_owner', '-d', 'postgres',
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
        first = save(body)
        curve = str(uuid4())
        selected=json.loads(query(f"""SELECT row_to_json(x) FROM(SELECT c.code,c.revision_id FROM market_data.curves c
         WHERE c.catalog_id='{catalog}' AND c.quote_code='{row['quote_code']}' ORDER BY c.code LIMIT 1)x;"""))
        revision, source = selected['revision_id'],selected['code']
        # A real five-node authored graph; terms are normalized, never JSON relationships.
        runtime(f"""INSERT INTO market_data.custom_curves VALUES('{catalog}','{curve}','terms_check');
        INSERT INTO market_data.custom_curve_versions(catalog_id,curve_id,version,generation,state,name,output_node,engine_version,layout)
         VALUES('{catalog}','{curve}',1,1,'draft','Synthetic contract terms','out','custom_curve_recipe_v2','{{}}');
        INSERT INTO market_data.custom_curve_nodes VALUES
         ('{catalog}','{curve}',1,'source','source','{{}}'),
         ('{catalog}','{curve}',1,'select','select','{{"mode":"positions","first":"1","count":"1","binding":"context"}}'),
         ('{catalog}','{curve}',1,'dates','dates','{{"mode":"context"}}'),
         ('{catalog}','{curve}',1,'sample','sample','{{}}'),('{catalog}','{curve}',1,'out','output','{{}}');
        INSERT INTO market_data.custom_curve_node_sources VALUES('{catalog}','{curve}',1,'source','{catalog}','{revision}','{source}');
        INSERT INTO market_data.custom_curve_edges VALUES('{catalog}','{curve}',1,'select','source','source'),
         ('{catalog}','{curve}',1,'sample','selection','select'),('{catalog}','{curve}',1,'sample','dates','dates'),('{catalog}','{curve}',1,'out','values','sample');
        INSERT INTO market_data.custom_curve_price_mappings SELECT '{catalog}','{curve}',1,'source',catalog_id,revision_id,curve_code,contract_id,contract_id
         FROM market_data.curve_members WHERE(catalog_id,revision_id,curve_code)=('{catalog}','{revision}','{source}');
        INSERT INTO market_data.custom_curve_price_terms VALUES('{catalog}','{curve}',1,'source',0,NULL,'published_index_v1',
         '{body['key']['id']}',1,'{rcat}','{rrev}','{row['index_code']}','single_assessment_v1','require_final_v1',NULL,'same_delivery_period_v1');""")
        runtime(f"INSERT INTO market_data.custom_curve_requests VALUES('{catalog}',security.custom_curve_actor('{catalog}','WriteMarketDefinitions'),'{uuid4()}','contract_price_terms','{{}}','{{}}','{revision}','{curve}',1);")
        terms=json.loads(runtime(f"SELECT market_data.read_custom_curve_price_terms('{catalog}','{curve}',1,'source')::text;"))
        assert terms['default']['publication']==body['key']
        assert terms['default']['index']==ref(row['index_code'])
        assert terms['mappingRule']=='same_delivery_period_v1' and terms['mappings']
        assert all(m['target']==m['source'] for m in terms['mappings'])
        reject('23503',lambda:runtime(f"UPDATE market_data.custom_curve_price_mappings SET source_contract_id='{uuid4()}' WHERE curve_id='{curve}';"))
        runtime(f"DELETE FROM market_data.custom_curve_price_terms WHERE curve_id='{curve}';",'viewer')
        assert runtime(f"SELECT count(*) FROM market_data.custom_curve_price_terms WHERE curve_id='{curve}';")=='1'
        # Publishing freezes these same rows, including every source contract pair.
        query("INSERT INTO security.role_permissions SELECT id,'SealMarketDefinitions' FROM security.roles WHERE code='trader' ON CONFLICT DO NOTHING;")
        runtime(f"SELECT security.publish_custom_curve('{catalog}','{curve}',1,1);")
        reject('55000',lambda:runtime(f"DELETE FROM market_data.custom_curve_price_terms WHERE curve_id='{curve}';"))
        assert json.loads(runtime(f"SELECT market_data.read_custom_curve_price_terms('{catalog}','{curve}',1,'source')::text;"))==terms
        print('PASS contract terms persistence: runtime publication binding, exact reference and mapping round-trip, foreign-key refusal, authorization and published immutability',flush=True)
    finally:
        if started:
            result = subprocess.run([str(bins['pg_ctl']), '-D', str(data), '-m', 'immediate', '-w', '-t', '15', 'stop'],
                                    env=env, text=True, capture_output=True, timeout=25)
            if result.returncode:
                print(f'Owned cluster retained for inspection: {directory}')
                return
        shutil.rmtree(directory)


if __name__ == '__main__': main()
