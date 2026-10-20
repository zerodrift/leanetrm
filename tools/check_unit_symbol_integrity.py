"""Exact symbol bounds shared by retained units and managed amount labels."""
import json
from pathlib import Path
from uuid import uuid4


def run_checks(query, manifest):
    catalog, source = manifest['referenceCatalogId'], manifest['referenceRevisionId']

    def unit(symbol):
        revision = str(uuid4())
        escaped = symbol.replace("'", "''")
        return revision, f"""
          INSERT INTO ref_data.revisions(catalog_id,id,revision_key) VALUES('{catalog}','{revision}','symbol_{revision.replace('-', '')}');
          INSERT INTO ref_data.dimensions SELECT(jsonb_populate_record(NULL::ref_data.dimensions,to_jsonb(d)||jsonb_build_object('revision_id','{revision}'))).* FROM ref_data.dimensions d WHERE catalog_id='{catalog}' AND revision_id='{source}';
          INSERT INTO ref_data.units(catalog_id,revision_id,code,symbol,dimension_code)
          SELECT catalog_id,'{revision}','symbol_check','{escaped}',dimension_code FROM ref_data.units WHERE catalog_id='{catalog}' AND revision_id='{source}' ORDER BY code LIMIT 1;
        """

    def rejected(sql):
        try:
            query('\\set VERBOSITY verbose\n' + sql)
        except RuntimeError as error:
            assert '23514' in str(error), str(error)
        else:
            raise AssertionError('Retained units accepted a symbol unsupported by amount labels')

    for symbol in ['x' * 81, '\U0001f4a1' * 81, '\x01', 'x\x85', '\u00a0', '\u2003', '\u202f', '\u3000']:
        _, sql = unit(symbol)
        rejected('BEGIN;' + sql + 'ROLLBACK;')
    for symbol in ['x' * 80, '\U0001f4a1' * 80, ' x ', '\u2003x\u00a0']:
        revision, sql = unit(symbol)
        assert json.loads(query('BEGIN;' + sql + f"SELECT to_json(symbol)::text FROM ref_data.units WHERE catalog_id='{catalog}' AND revision_id='{revision}';ROLLBACK;")) == symbol, 'The exact symbol was changed during storage'

    migration = Path(__file__).resolve().parents[1] / 'db/migrations/0049_unit_symbol_bounds.sql'
    if migration.is_file():
        upgrade = migration.read_text()
        descriptor = query("SELECT pg_get_functiondef('security.reference_display_editor_fields(text)'::regprocedure);")
        prior_storage = 'ALTER TABLE ref_data.units DROP CONSTRAINT units_amount_symbol_check;DROP FUNCTION ref_data.unit_symbol_supported(text);'
        for symbol in ['x' * 81, '\u2003', 'x\x85']:
            _, sql = unit(symbol)
            rejected('BEGIN;' + prior_storage + sql + upgrade)
            assert query("SELECT convalidated FROM pg_constraint WHERE conrelid='ref_data.units'::regclass AND conname='units_amount_symbol_check';") == 't'
            assert query("SELECT pg_get_functiondef('security.reference_display_editor_fields(text)'::regprocedure);") == descriptor
        revision, sql = unit('\U0001f4a1' * 80)
        # Recreate the prior unconstrained storage around a valid retained row,
        # then apply the actual migration and compare every retained field.
        query('BEGIN;' + prior_storage + sql + 'COMMIT;')
        original = query(f"SELECT row_to_json(u)::text FROM ref_data.units u WHERE catalog_id='{catalog}' AND revision_id='{revision}';")
        query(upgrade)
        assert query(f"SELECT row_to_json(u)::text FROM ref_data.units u WHERE catalog_id='{catalog}' AND revision_id='{revision}';") == original
        assert query("SELECT convalidated FROM pg_constraint WHERE conrelid='ref_data.units'::regclass AND conname='units_amount_symbol_check';") == 't'
    print('PASS unit symbols: 80 Unicode scalars, controls/whitespace rejected, exact retained values and invalid upgrade rollback', flush=True)
