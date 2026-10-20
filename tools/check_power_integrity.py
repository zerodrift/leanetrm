"""Qualify delivery catalog relationships and upgrades in a fresh stock database."""
import argparse
import json
import os
from pathlib import Path
import shlex
import shutil
import subprocess
import tempfile
from uuid import uuid4

ROOT = Path(__file__).resolve().parents[1]


def fixture(query, manifest, *, batch_other=False, source_other=False, attempt_other=False, price_other=False):
    catalog = manifest['catalogId']
    rc, rr = manifest['referenceCatalogId'], manifest['referenceRevisionId']
    tenant, workspace = manifest['tenantId'], manifest['workspaceId']
    other, actor, source, job, attempt = [str(uuid4()) for _ in range(5)]
    batch_catalog = other if batch_other else catalog
    source_catalog = other if source_other else catalog
    expanded = query("SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='power_data' AND table_name='attempts' AND column_name='catalog_id');") == 't'
    attempt_catalog = other if attempt_other else catalog
    price_catalog = other if price_other else batch_catalog
    attempt_column, attempt_value = (',catalog_id', f",'{attempt_catalog}'") if expanded else ('', '')
    price_column, price_value = (',catalog_id', f",'{price_catalog}'") if expanded else ('', '')
    sql = f"""
      INSERT INTO market_data.catalogs VALUES('{other}','synthetic_integrity_{other.replace('-', '')}');
      INSERT INTO security.principals VALUES('{actor}','service',true);
      INSERT INTO security.workspace_memberships VALUES('{tenant}','{workspace}','{actor}','integrity_{actor[:8]}','Synthetic integrity collector',true,1);
      INSERT INTO power_data.collectors VALUES('integrity_{actor[:8]}','{actor}','{tenant}','{workspace}','{catalog}');
      INSERT INTO power_data.jobs VALUES('{job}',gen_random_uuid(),'Synthetic integrity job','integrity_{actor[:8]}','{catalog}','PJM',true,false,'0 8 * * *','UTC',-1,-1);
      INSERT INTO power_data.attempts(id,job_id,generation,worker_pid,security_generation,from_date,through_date,started_at,state,finished_at,error_code{attempt_column})
      VALUES('{attempt}','{job}',gen_random_uuid(),pg_backend_pid(),1,'2021-01-04','2021-01-04',clock_timestamp(),'succeeded',clock_timestamp(),NULL{attempt_value});
      INSERT INTO power_data.sources
       (id,catalog_id,name,provider,dataset,market,point,minutes,reference_catalog_id,reference_revision_id,location_code,clock_code,currency_code,quantity_code)
      SELECT '{source}','{source_catalog}','Synthetic integrity source','PJM','da_hrl_lmps','DA','synthetic_{source}',60,'{rc}','{rr}',
        (SELECT code FROM ref_data.locations WHERE catalog_id='{rc}' AND revision_id='{rr}' ORDER BY code LIMIT 1),
        (SELECT code FROM ref_data.clock_rules WHERE catalog_id='{rc}' AND revision_id='{rr}' ORDER BY code LIMIT 1),
        (SELECT code FROM ref_data.currencies WHERE catalog_id='{rc}' AND revision_id='{rr}' ORDER BY code LIMIT 1),
        (SELECT code FROM ref_data.quantity_specifications WHERE catalog_id='{rc}' AND revision_id='{rr}' ORDER BY code LIMIT 1);
      INSERT INTO power_data.batches(id,catalog_id,publication_xid) VALUES('{attempt}','{batch_catalog}',pg_current_xact_id());
      INSERT INTO power_data.prices(batch_id,source_id,starts_at,ends_at,price,provider_revision{price_column})
      VALUES('{attempt}','{source}','2021-01-04 00:00Z','2021-01-04 01:00Z',123.5,1{price_value});
    """
    return sql, attempt, source


def rejected(query, sql, state):
    try:
        query(sql)
    except RuntimeError as error:
        assert state in str(error), str(error)
    else:
        raise AssertionError('Storage accepted a mismatched delivery catalog relationship')


def run_checks(query, manifest):
    migration = ROOT / 'db/migrations/0048_delivery_catalog_integrity.sql'
    if migration.is_file():
        upgrade = migration.read_text()
        previous = query("SELECT md5(pg_get_functiondef('power_data.finish_attempt(uuid,jsonb,text)'::regprocedure));")
        for fault in ('batch', 'source'):
            sql, _, _ = fixture(query, manifest, batch_other=fault == 'batch', source_other=fault == 'source')
            rejected(query, 'BEGIN;' + sql + upgrade, 'delivery_catalog_integrity')
            assert query("SELECT count(*) FROM information_schema.columns WHERE table_schema='power_data' AND table_name IN('attempts','prices') AND column_name='catalog_id';") == '0'
            assert query("SELECT md5(pg_get_functiondef('power_data.finish_attempt(uuid,jsonb,text)'::regprocedure));") == previous
            assert query("SELECT tgenabled FROM pg_trigger WHERE tgrelid='power_data.prices'::regclass AND tgname='immutable_fact';") == 'O'
        # Fail after the new-column backfill when an unexpected worker body
        # cannot be upgraded. DDL, rows and the restored immutable trigger must
        # all roll back together, including that deliberately altered body.
        sql, _, _ = fixture(query, manifest)
        drifted = query("SELECT replace(pg_get_functiondef('power_data.finish_attempt(uuid,jsonb,text)'::regprocedure),'INSERT INTO power_data.prices VALUES','INSERT INTO power_data.prices  VALUES');")
        rejected(query, 'BEGIN;' + sql + drifted + ';' + upgrade, 'unexpected worker insert contract')
        assert query("SELECT count(*) FROM information_schema.columns WHERE table_schema='power_data' AND table_name IN('attempts','prices') AND column_name='catalog_id';") == '0'
        assert query("SELECT md5(pg_get_functiondef('power_data.finish_attempt(uuid,jsonb,text)'::regprocedure));") == previous
        assert query("SELECT tgenabled FROM pg_trigger WHERE tgrelid='power_data.prices'::regclass AND tgname='immutable_fact';") == 'O'
        sql, attempt, _ = fixture(query, manifest)
        query('BEGIN;' + sql + 'COMMIT;')
        retained = query(f"SELECT to_jsonb(p)::text FROM power_data.prices p WHERE batch_id='{attempt}';")
        query(upgrade)
        assert json.loads(query(f"SELECT (to_jsonb(p)-'catalog_id')::text FROM power_data.prices p WHERE batch_id='{attempt}';")) == json.loads(retained)
        assert query(f"SELECT p.catalog_id=a.catalog_id AND a.catalog_id=b.catalog_id FROM power_data.prices p JOIN power_data.batches b ON b.id=p.batch_id JOIN power_data.attempts a ON a.id=b.id WHERE p.batch_id='{attempt}';") == 't'
        assert query("SELECT tgenabled FROM pg_trigger WHERE tgrelid='power_data.prices'::regclass AND tgname='immutable_fact';") == 'O'
        rejected(query, f"BEGIN;UPDATE power_data.prices SET price=1 WHERE batch_id='{attempt}';ROLLBACK;", '55000')
        print('PASS delivery upgrade: retained values, exact parent backfill and atomic refusal of inconsistent old catalogs', flush=True)
    for fault in ('attempt', 'batch', 'source', 'price'):
        sql, _, _ = fixture(query, manifest, batch_other=fault == 'batch', source_other=fault == 'source',
                            attempt_other=fault == 'attempt', price_other=fault == 'price')
        rejected(query, 'BEGIN;' + sql + 'ROLLBACK;', '23503')
    sql, attempt, _ = fixture(query, manifest)
    assert query('BEGIN;' + sql + f"SELECT count(*) FROM power_data.prices WHERE batch_id='{attempt}';ROLLBACK;") == '1'
    print('PASS delivery integrity: valid insertion and cross-catalog batch/source rejection with exact foreign keys', flush=True)


def main():
    if not __debug__ or os.environ.get('PYTHONOPTIMIZE') not in (None, '', '0'):
        raise ValueError('Python assertions must remain enabled for verification.')
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pg-bin', type=Path, required=True)
    args = parser.parse_args()
    binaries = args.pg_bin.resolve()
    for name in ('initdb', 'pg_ctl', 'psql'):
        if not (binaries / name).is_file():
            parser.error('Missing installed PostgreSQL binary: ' + str(binaries / name))
    directory = Path(tempfile.mkdtemp(prefix='oetrm-delivery-', dir='/tmp')).resolve()
    directory.chmod(0o700)
    environment = {k: v for k, v in os.environ.items() if not k.startswith('PG')}
    environment.update(PGHOST=str(directory), PGPORT='55439', PGDATABASE='postgres', PGUSER='preview_owner', PGTZ='UTC')
    safe_to_remove = True

    def run(command, **options):
        result = subprocess.run([str(v) for v in command], cwd=ROOT, env=environment, capture_output=True, text=True, timeout=30, **options)
        if result.returncode:
            raise RuntimeError(result.stderr)
        return result.stdout.strip()

    def query(sql):
        return run([binaries / 'psql', '-X', '-qAt', '-v', 'ON_ERROR_STOP=1', '-v', 'VERBOSITY=verbose'], input=sql)

    try:
        run([binaries / 'initdb', '-D', directory / 'data', '--auth-local=trust', '--auth-host=reject', '--locale=C', '--encoding=UTF8', '--username=preview_owner'])
        try:
            safe_to_remove = False
            run([binaries / 'pg_ctl', '-D', directory / 'data', '-l', directory / 'postgres.log', '-w', '-o', shlex.join(['-k', str(directory), '-h', '', '-p', '55439', '-c', 'track_commit_timestamp=on']), 'start'])
            for migration in sorted((ROOT / 'db/migrations').glob('*.sql')):
                if int(migration.name[:4]) <= 47:
                    query(migration.read_text())
            from preview_fixture import fixture as standard_fixture
            reference, market, security, manifest = standard_fixture(ROOT)
            # This upgrade starts at 0047; index-fixing definitions arrive in
            # 0050 and are unrelated to the delivery catalog relationships.
            market = '\n'.join(line for line in market.splitlines()
                               if not line.startswith('INSERT INTO market_data.index_fixing_observations '))
            for sql in (reference, market, security):
                query(sql)
            run_checks(query, manifest)
        finally:
            run([binaries / 'pg_ctl', '-D', directory / 'data', '-w', '-m', 'fast', 'stop'])
            safe_to_remove = True
    finally:
        if safe_to_remove:
            shutil.rmtree(directory)


if __name__ == '__main__':
    main()
