"""Disposable PostgreSQL payment-block experiment; no API, UI or existing database.

PostgreSQL retains exact integer numerator/denominator arrays. The existing managed
checks executable exports synthetic inputs and evaluates the round-tripped blocks.
This is a physical-layout experiment, not a production migration or context resolver.
"""
import argparse
import csv
from datetime import datetime
import io
import json
import os
from pathlib import Path
import platform
import re
import shutil
import subprocess
import tempfile
import time

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / 'tests/OpenEtrm.MarketData.Checks'
SCHEMA = """
CREATE SCHEMA block_experiment;
CREATE FUNCTION block_experiment.integer_column(items numeric[]) RETURNS boolean
LANGUAGE sql IMMUTABLE STRICT AS $$
 SELECT cardinality(items)>0 AND NOT EXISTS
 (SELECT 1 FROM unnest(items) v WHERE v IS NULL OR v<>trunc(v)
   OR v::text IN ('NaN','Infinity','-Infinity'))
$$;
CREATE TABLE block_experiment.contexts (
 id text PRIMARY KEY, valuation_at timestamptz NOT NULL, known_at timestamptz NOT NULL
);
CREATE TABLE block_experiment.source_definitions (
 id text NOT NULL, version integer NOT NULL CHECK(version>0),
 kind text NOT NULL, unit text NOT NULL, grid jsonb NOT NULL,
 cell_count integer NOT NULL CHECK(cell_count>0), PRIMARY KEY(id,version),
 UNIQUE(id,version,cell_count), CHECK(jsonb_typeof(grid)='object'),
 CHECK(grid ?& ARRAY['start','stepSeconds','count','sampling']),
 CHECK(grid-ARRAY['start','stepSeconds','count','sampling']='{}'::jsonb),
 CHECK(jsonb_typeof(grid->'start')='string' AND jsonb_typeof(grid->'sampling')='string'
   AND jsonb_typeof(grid->'stepSeconds')='number' AND jsonb_typeof(grid->'count')='number'),
 CHECK((grid->>'count')::integer=cell_count),
 CHECK((grid->>'stepSeconds')::integer>0),
 CHECK(grid->>'sampling' IN ('uniform','scalar')),
 CHECK(grid->>'sampling'<>'scalar' OR cell_count=1),
 CHECK((grid->>'start')::timestamptz IS NOT NULL)
);
CREATE TABLE block_experiment.source_columns (
 context_id text NOT NULL REFERENCES block_experiment.contexts,
 source_id text NOT NULL, source_version integer NOT NULL, cell_count integer NOT NULL,
 numerators numeric[] NOT NULL, denominators numeric[] NOT NULL,
 PRIMARY KEY(context_id,source_id,source_version),
 FOREIGN KEY(source_id,source_version,cell_count)
   REFERENCES block_experiment.source_definitions(id,version,cell_count),
 CHECK(cardinality(numerators)=cell_count AND cardinality(denominators)=cell_count),
 CHECK(block_experiment.integer_column(numerators)),
 CHECK(block_experiment.integer_column(denominators) AND 0<ALL(denominators))
);
CREATE TABLE block_experiment.payment_blocks (
 id text PRIMARY KEY, side_id text NOT NULL, occurrence text NOT NULL,
 payment_at timestamptz NOT NULL, quantity_id text NOT NULL, quantity_version integer NOT NULL,
 price_id text NOT NULL, price_version integer NOT NULL, terms jsonb NOT NULL,
 UNIQUE(side_id,occurrence),
 FOREIGN KEY(quantity_id,quantity_version) REFERENCES block_experiment.source_definitions(id,version),
 FOREIGN KEY(price_id,price_version) REFERENCES block_experiment.source_definitions(id,version),
 CHECK(jsonb_typeof(terms)='object'),
 CHECK(terms ?& ARRAY['start','stepSeconds','count','selection','quantityDivisor',
                      'quantityScale','priceMultiplier','priceAdder','quoteMultiplier']),
 CHECK(terms-ARRAY['start','stepSeconds','count','selection','quantityDivisor',
                   'quantityScale','priceMultiplier','priceAdder','quoteMultiplier']='{}'::jsonb),
 CHECK(jsonb_typeof(terms->'start')='string' AND jsonb_typeof(terms->'selection')='array'
   AND jsonb_typeof(terms->'stepSeconds')='number' AND jsonb_typeof(terms->'count')='number'
   AND jsonb_typeof(terms->'quantityDivisor')='string' AND jsonb_typeof(terms->'quantityScale')='string'
   AND jsonb_typeof(terms->'priceMultiplier')='string' AND jsonb_typeof(terms->'priceAdder')='string'
   AND jsonb_typeof(terms->'quoteMultiplier')='string'),
 CHECK((terms->>'count')::integer>0 AND (terms->>'stepSeconds')::integer>0)
);
CREATE TABLE block_experiment.discount_factors (
 context_id text NOT NULL REFERENCES block_experiment.contexts,
 block_id text NOT NULL REFERENCES block_experiment.payment_blocks,
 factor text NOT NULL, PRIMARY KEY(context_id,block_id)
);
CREATE FUNCTION block_experiment.immutable() RETURNS trigger
LANGUAGE plpgsql AS $$BEGIN RAISE EXCEPTION 'block_experiment_immutable'; END$$;
DO $$DECLARE t text; BEGIN
 FOREACH t IN ARRAY ARRAY['contexts','source_definitions','source_columns','payment_blocks','discount_factors'] LOOP
  EXECUTE format('CREATE TRIGGER immutable BEFORE UPDATE OR DELETE ON block_experiment.%I '
                 'FOR EACH ROW EXECUTE FUNCTION block_experiment.immutable()',t);
 END LOOP;
END$$;
"""

INCOMING = {
    'contexts': """SELECT j->'context'->>'id', (j->'context'->>'valuationAt')::timestamptz,
        (j->'context'->>'knownAt')::timestamptz FROM input_document""",
    'source_definitions': """SELECT s->>'id',(s->>'version')::integer,s->>'kind',s->>'unit',
        jsonb_build_object('start',s->'start','stepSeconds',s->'stepSeconds',
        'count',s->'count','sampling',s->'sampling'),(s->>'count')::integer
        FROM input_document CROSS JOIN LATERAL jsonb_array_elements(j->'sources') s""",
    'source_columns': """SELECT j->'context'->>'id',s->>'id',(s->>'version')::integer,
        (s->>'count')::integer,ARRAY(SELECT x::numeric FROM jsonb_array_elements_text(s->'numerators') x),
        ARRAY(SELECT x::numeric FROM jsonb_array_elements_text(s->'denominators') x)
        FROM input_document CROSS JOIN LATERAL jsonb_array_elements(j->'sources') s""",
    'payment_blocks': """SELECT b->>'id',b->>'sideId',b->>'occurrence',(b->>'paymentAt')::timestamptz,
        b->>'quantityId',(b->>'quantityVersion')::integer,b->>'priceId',(b->>'priceVersion')::integer,
        b-ARRAY['id','sideId','occurrence','paymentAt','quantityId','quantityVersion',
                'priceId','priceVersion','discount']
        FROM input_document CROSS JOIN LATERAL jsonb_array_elements(j->'blocks') b""",
    'discount_factors': """SELECT j->'context'->>'id',b->>'id',b->>'discount'
        FROM input_document CROSS JOIN LATERAL jsonb_array_elements(j->'blocks') b""",
}
KEYS = {'contexts': 'id', 'source_definitions': 'id,version',
        'source_columns': 'context_id,source_id,source_version', 'payment_blocks': 'id',
        'discount_factors': 'context_id,block_id'}

LOAD = """
SELECT jsonb_build_object(
 'context',(SELECT jsonb_build_object('id',id,'valuationAt',valuation_at,'knownAt',known_at)
            FROM block_experiment.contexts),
 'sources',(SELECT jsonb_agg(d.grid || jsonb_build_object('id',d.id,'version',d.version,
       'kind',d.kind,'unit',d.unit,
       'numerators',ARRAY(SELECT n::text FROM unnest(c.numerators) n),
       'denominators',ARRAY(SELECT n::text FROM unnest(c.denominators) n)) ORDER BY d.id,d.version)
     FROM block_experiment.source_definitions d JOIN block_experiment.source_columns c
       ON(c.source_id,c.source_version)=(d.id,d.version)),
 'blocks',(SELECT jsonb_agg(b.terms || jsonb_build_object('id',b.id,'sideId',b.side_id,
       'occurrence',b.occurrence,'paymentAt',b.payment_at,'quantityId',b.quantity_id,
       'quantityVersion',b.quantity_version,'priceId',b.price_id,'priceVersion',b.price_version,
       'discount',f.factor) ORDER BY b.id)
     FROM block_experiment.payment_blocks b JOIN block_experiment.discount_factors f ON f.block_id=b.id)
);
"""


def write_sql(document):
    """Retry the same facts; a conflicting fact rolls back the entire write."""
    data = io.StringIO()
    csv.writer(data, lineterminator='\n').writerow([json.dumps(document, separators=(',', ':'))])
    sql = ['BEGIN; CREATE TEMP TABLE input_document(j jsonb) ON COMMIT DROP;',
           'COPY input_document FROM STDIN WITH(FORMAT csv);', data.getvalue().rstrip('\n'), '\\.']
    for table, select in INCOMING.items():
        sql += [f'CREATE TEMP TABLE incoming_{table} (LIKE block_experiment.{table} INCLUDING ALL) ON COMMIT DROP;',
                f'INSERT INTO incoming_{table} {select};',
                f'INSERT INTO block_experiment.{table} SELECT * FROM incoming_{table} ON CONFLICT DO NOTHING;',
                f"DO $$BEGIN IF EXISTS(SELECT 1 FROM incoming_{table} i JOIN block_experiment.{table} a "
                f"USING({KEYS[table]}) WHERE to_jsonb(i) IS DISTINCT FROM to_jsonb(a)) THEN "
                "RAISE EXCEPTION 'block_experiment_conflict'; END IF; END$$;"]
    return '\n'.join(sql + ['COMMIT;'])


def verify_roundtrip(original, loaded):
    """Check storage before comparing two evaluators of the loaded inputs."""
    def normalized(item, dates):
        result = dict(item)
        for field in dates:
            result[field] = datetime.fromisoformat(result[field].replace('Z', '+00:00'))
        return result

    if normalized(original['context'], ('valuationAt', 'knownAt')) != normalized(
            loaded['context'], ('valuationAt', 'knownAt')):
        raise RuntimeError('SQL changed the selected context')
    for name, keys, dates in [('sources', ('id', 'version'), ()), ('blocks', ('id',), ('paymentAt',))]:
        expected = {tuple(item[k] for k in keys): normalized(item, dates) for item in original[name]}
        actual = {tuple(item[k] for k in keys): normalized(item, dates) for item in loaded[name]}
        if len(expected) != len(original[name]) or len(actual) != len(loaded[name]) or expected != actual:
            raise RuntimeError(f'SQL changed {name} definitions, references or exact values')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pg-bin', type=Path, required=True)
    parser.add_argument('--dotnet', type=Path, required=True)
    parser.add_argument('--months', type=int, choices=(12, 120), default=12)
    parser.add_argument('--output', type=Path, help='Optional report outside the repository')
    parser.add_argument('--no-build', action='store_true')
    args = parser.parse_args()
    if args.output and args.output.resolve().is_relative_to(ROOT):
        parser.error('Keep generated reports outside the repository')
    bins = {name: args.pg_bin.resolve()/name for name in ('initdb', 'pg_ctl', 'psql')}
    for binary in [args.dotnet, *bins.values()]:
        if not binary.is_file():
            parser.error(f'Missing installed binary: {binary}')
    env = {k: v for k, v in os.environ.items() if not k.startswith(('PG', 'DOTNET_', 'OPENETRM_'))}
    env.update(LC_ALL='C', PGTZ='UTC', DOTNET_NOLOGO='1', DOTNET_CLI_TELEMETRY_OPTOUT='1')
    if not args.no_build:
        subprocess.run([str(args.dotnet), 'build', str(PROJECT), '-c', 'Release', '--no-restore',
                        '--disable-build-servers', '-m:1', '-v:q'], cwd=ROOT, env=env, check=True, timeout=180)
    dll = PROJECT/'bin/Release/net10.0/OpenEtrm.MarketData.Checks.dll'
    temporary = Path(tempfile.mkdtemp(prefix='oe-block-', dir='/tmp')).resolve()
    os.chmod(temporary, 0o700)
    data = temporary/'pg'
    started = False
    evaluation_memory = {'available': False, 'scope': 'Entire managed evaluation process, including both kernels and all scenarios'}

    def run(command, **kwargs):
        return subprocess.run([str(v) for v in command], cwd=ROOT, env=env, text=True,
                              capture_output=True, timeout=180, **kwargs)

    def required(command, **kwargs):
        result = run(command, **kwargs)
        if result.returncode:
            raise RuntimeError(result.stderr or result.stdout)
        return result.stdout.strip()

    def managed(*arguments):
        command = [args.dotnet, dll, *arguments]
        if arguments[0] == '--side-block-evaluate' and platform.system() == 'Darwin':
            # BSD time reports this process's OS high-water resident size in bytes.
            result = run(['/usr/bin/time', '-l', *command])
            if result.returncode:
                raise RuntimeError(result.stderr or result.stdout)
            maximum = re.search(r'^\s*(\d+)\s+maximum resident set size\s*$', result.stderr, re.MULTILINE)
            if not maximum:
                raise RuntimeError('The OS evaluator memory measurement was unavailable')
            evaluation_memory.update(available=True, peakResidentBytes=int(maximum[1]), source='BSD /usr/bin/time -l')
            return result.stdout.strip()
        return required(command)

    try:
        print('Checking exact managed payment-block behavior...', flush=True)
        print(managed('--side-block-checks'), flush=True)
        exported = temporary/'export.json'
        managed('--side-block-export', exported, str(args.months))
        document = json.loads(exported.read_text())
        required([bins['initdb'], '-D', data, '--auth-local=trust', '--auth-host=reject',
                  '--encoding=UTF8', '--locale=C', '--no-instructions', '--username=block_owner'])
        # A unique Unix socket directory and no TCP listeners exclude existing databases.
        started = True
        required([bins['pg_ctl'], '-D', data, '-w', '-t', '15', '-l', temporary/'pg.log',
                  '-o', f'-k {temporary} -h "" -p 55437', 'start'])
        psql = [bins['psql'], '-X', '-h', temporary, '-p', '55437', '-U', 'block_owner',
                '-d', 'postgres', '-qAt', '-v', 'ON_ERROR_STOP=1']

        def query(sql):
            return required(psql, input=sql)

        if Path(query('SHOW data_directory')).resolve() != data:
            raise RuntimeError('Refusing to use a database outside the owned cluster')
        query(SCHEMA)
        write = write_sql(document)
        before = time.perf_counter()
        query(write)
        store_seconds = time.perf_counter()-before
        # Retry and conflict recovery are exercised independently of pricing.
        query(write)
        altered = json.loads(json.dumps(document))
        altered['blocks'][0]['priceAdder'] = '999'
        conflict = run(psql, input=write_sql(altered))
        if conflict.returncode == 0 or 'block_experiment_conflict' not in conflict.stderr:
            raise RuntimeError('A conflicting retry was not refused')
        mutation = run(psql, input='UPDATE block_experiment.payment_blocks SET occurrence=occurrence;')
        if mutation.returncode == 0 or 'block_experiment_immutable' not in mutation.stderr:
            raise RuntimeError('Retained blocks were mutable')
        refusals = [
            ("""INSERT INTO block_experiment.source_definitions
                SELECT id||'/missing-grid-key',version,kind,unit,grid-'sampling',cell_count
                FROM block_experiment.source_definitions LIMIT 1;""", 'violates check constraint'),
            ("""INSERT INTO block_experiment.source_definitions
                SELECT id||'/null-grid-value',version,kind,unit,
                  jsonb_set(grid,'{sampling}','null'::jsonb),cell_count
                FROM block_experiment.source_definitions LIMIT 1;""", 'violates check constraint'),
            ("""INSERT INTO block_experiment.source_columns
                SELECT context_id,source_id||'/unknown',source_version,cell_count,numerators,denominators
                FROM block_experiment.source_columns LIMIT 1;""", 'violates foreign key constraint'),
            ("""INSERT INTO block_experiment.source_columns
                SELECT context_id||'/unknown',source_id,source_version,cell_count,numerators,denominators
                FROM block_experiment.source_columns LIMIT 1;""", 'violates foreign key constraint'),
            ("""INSERT INTO block_experiment.source_columns
                SELECT context_id,source_id,source_version+1,cell_count,numerators,
                  array_fill(0::numeric,ARRAY[cell_count])
                FROM block_experiment.source_columns LIMIT 1;""", 'violates check constraint'),
            ("""INSERT INTO block_experiment.source_columns
                SELECT context_id,source_id,source_version+1,cell_count,numerators[1:cell_count-1],denominators
                FROM block_experiment.source_columns WHERE cell_count>1 LIMIT 1;""", 'violates check constraint'),
        ]
        for sql, expected in refusals:
            rejected = run(psql, input=sql)
            if rejected.returncode == 0 or expected not in rejected.stderr:
                raise RuntimeError(f'Expected storage refusal ({expected}): {rejected.stderr}')
        # Disconnect/restart clears PostgreSQL shared buffers, not the OS file cache.
        required([bins['pg_ctl'], '-D', data, '-l', temporary/'pg.log',
                  '-m', 'fast', '-w', '-t', '15', 'restart'])
        before = time.perf_counter()
        loaded_text = query(LOAD)
        load_seconds = time.perf_counter()-before
        loaded = json.loads(loaded_text)
        verify_roundtrip(document, loaded)
        # Scenarios describe calculation requests, not stored schedule facts.
        loaded.update({key: document[key] for key in document if key not in loaded})
        roundtrip = temporary/'loaded.json'
        roundtrip.write_text(json.dumps(loaded))
        evaluation = temporary/'evaluation.json'
        print('Evaluating SQL-loaded columns and comparing the current graph...', flush=True)
        managed('--side-block-evaluate', roundtrip, evaluation)
        report = json.loads(evaluation.read_text())
        report['evaluationProcessMemory'] = evaluation_memory
        tables = json.loads(query("""SELECT json_agg(json_build_object('table',c.relname,
          'tableAndToastBytes',pg_table_size(c.oid),'indexBytes',pg_indexes_size(c.oid),
          'rows',(xpath('/row/count/text()',query_to_xml(format('SELECT count(*) FROM block_experiment.%I',c.relname),false,true,'')))[1]::text::bigint)
          ORDER BY c.relname) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
          WHERE n.nspname='block_experiment' AND c.relkind='r';"""))
        report['postgres'] = {'version': query('SHOW server_version'), 'storeSeconds': store_seconds,
            'loadAndJsonTransferSeconds': load_seconds, 'transferredBytes': len(loaded_text.encode()),
            'cacheState': 'new connection after PostgreSQL restart; OS cache not flushed', 'tables': tables,
            'idempotentRetry': True, 'conflictingRetryRefused': True, 'immutableBlocks': True,
            'invalidStorageInputsRefused': len(refusals), 'completeExactRoundtrip': True}
        report['host'] = {'os': platform.platform(), 'machine': platform.machine(),
                          'dotnet': required([args.dotnet, '--version'])}
        report['scope'] = ('Synthetic standalone storage/kernel experiment. No API, authorization, '
            'bitemporal resolver, production booking, settlement or end-to-end latency qualification.')
        text = json.dumps(report, indent=2)
        if args.output:
            with args.output.open('x') as output:
                output.write(text+'\n')
        print(text, flush=True)
    finally:
        if started:
            stopped = run([bins['pg_ctl'], '-D', data, '-m', 'fast', '-w', '-t', '15', 'stop'])
            if stopped.returncode and run([bins['pg_ctl'], '-D', data, 'status']).returncode == 0:
                raise RuntimeError(f'Owned PostgreSQL did not stop; retained {temporary}')
        shutil.rmtree(temporary)


if __name__ == '__main__':
    main()
