"""Apply gas migrations over populated prior-schema catalogs in a rolled-back fixture."""
import re


def run_checks(query, root, retained):
    def body(sql):
        return re.sub(r'(?m)^(BEGIN|COMMIT);\s*$', '', sql)

    tables = ('ref_data.locations', 'ref_data.commodities', 'market_data.curves',
              'market_data.contracts', 'market_data.observations', 'trading.books',
              'trading.portfolios', 'trading.legal_entities', 'trading.counterparties')
    state = 'jsonb_build_object(' + ','.join(
        f"'{table}',(SELECT coalesce(jsonb_agg(to_jsonb(r) ORDER BY to_jsonb(r)::text),'[]'::jsonb) FROM {table} r)"
        for table in tables) + ')'
    migrations = sorted((root / 'db/migrations').glob('014[2-6]_*.sql'))
    assert len(migrations) == 5, 'The gas upgrade must qualify its complete migration inventory'
    loaded = 'BEGIN;\n' + '\n'.join(body(sql) for sql in retained)
    upgraded = '\n'.join(body(path.read_text()) for path in migrations)
    query(loaded + f'\nCREATE TEMP TABLE gas_upgrade_before AS SELECT {state} AS value;\n' + upgraded + f"""
DO $check$ BEGIN
 IF (SELECT value FROM gas_upgrade_before) IS DISTINCT FROM ({state})
 THEN RAISE EXCEPTION 'Existing catalog or ownership rows changed during the gas upgrade';END IF;
 IF to_regclass('trading.gas_quantity_revisions') IS NULL
 THEN RAISE EXCEPTION 'The daily quantity migration was not installed';END IF;
END $check$;
ROLLBACK;
""")
    assert query("SELECT to_regclass('trading.gas_quantity_revisions') IS NULL;") == 't', \
        'The upgrade check must leave the prior schema available for normal installation'
    print('PASS gas upgrade: populated prior-schema reference, market and book rows survive all five migrations unchanged', flush=True)
