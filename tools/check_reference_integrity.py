"""Direct storage checks for the exact reference item and activation links."""
import json
from uuid import uuid4


def run_checks(query, manifest, target_revision):
    catalog = manifest['referenceCatalogId']
    source = manifest['referenceRevisionId']
    actor = query("SELECT principal_id FROM security.identity_bindings WHERE subject='editor';")
    fields = {
        'currencies': 'currency_code', 'commodities': 'commodity_code',
        'locations': 'location_code', 'location_types': 'location_type_code',
        'units': 'unit_code', 'exchanges': 'exchange_code',
        'clearing_houses': 'clearing_house_code',
        'futures_products': 'futures_product_code', 'futures_series': 'futures_series_code',
    }
    # Populate only declared writable columns. The same invalid records must be
    # rejected with a foreign-key violation, including when generated selectors
    # replace the old independently writable table/code pair.
    columns = query("SELECT string_agg(quote_ident(attname),',' ORDER BY attnum) FROM pg_attribute WHERE attrelid='ref_data.display_edits'::regclass AND attnum>0 AND NOT attisdropped AND attgenerated='';")

    def rejected(sql, state='23503'):
        try:
            query('\\set VERBOSITY verbose\nBEGIN;' + sql + ';ROLLBACK;')
        except RuntimeError as error:
            assert state in str(error), str(error)
        else:
            raise AssertionError('Storage accepted an invalid exact reference relationship')

    def receipt(revision, source_revision, table, code, extra=None):
        values = {
            'catalog_id': catalog, 'revision_id': revision,
            'source_revision_id': source_revision, 'base_revision_id': source,
            'tenant_id': manifest['tenantId'], 'workspace_id': manifest['workspaceId'],
            'actor_id': actor, 'table_name': table, 'item_code': code,
            fields[table]: code, 'request': {}, 'recorded_at': '2020-12-17T00:00:00Z',
        }
        values.update(extra or {})
        encoded = json.dumps(values).replace("'", "''")
        return f"INSERT INTO ref_data.display_edits({columns}) SELECT {columns} FROM jsonb_populate_record(NULL::ref_data.display_edits,'{encoded}'::jsonb)"

    for table in fields:
        code = query(f"SELECT code FROM ref_data.{table} WHERE catalog_id='{catalog}' AND revision_id='{source}' ORDER BY code LIMIT 1;")
        assert code, 'The synthetic fixture must exercise '+table
        actual = query('BEGIN;' + receipt(source, source, table, code) + ' RETURNING table_name||\'|\'||item_code;ROLLBACK;')
        assert actual == table+'|'+code, 'Stored selectors must derive from the exact typed reference'
        empty = str(uuid4())
        revision = f"INSERT INTO ref_data.revisions(catalog_id,id,revision_key) VALUES('{catalog}','{empty}','integrity_{empty.replace('-', '')}');"
        # Existing source item, missing copied target item.
        rejected(revision + receipt(empty, source, table, code))
        # Existing target item, missing item in the claimed original source.
        rejected(revision + receipt(source, empty, table, code))

    commodity = query(f"SELECT code FROM ref_data.commodities WHERE catalog_id='{catalog}' AND revision_id='{source}' ORDER BY code LIMIT 1;")
    rejected(receipt(source, source, 'currencies', 'usd', {'commodity_code': commodity}), '23514')

    other_source = str(uuid4())
    rejected(f"""
      INSERT INTO ref_data.revisions(catalog_id,id,revision_key) VALUES('{catalog}','{other_source}','integrity_{other_source.replace('-', '')}');
      INSERT INTO presentation.reference_display_versions
       (tenant_id,workspace_id,catalog_id,source_revision_id,version,display_revision_id,operation_id,actor_id,request,response,recorded_at)
      VALUES('{manifest['tenantId']}','{manifest['workspaceId']}','{catalog}','{other_source}',1,
       '{target_revision}','{uuid4()}','{actor}','{{}}','{{}}','2020-12-17T00:00:00Z')
    """)
    print('PASS reference integrity: source and target items for nine types; activation exact source', flush=True)
