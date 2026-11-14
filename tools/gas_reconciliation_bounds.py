"""Owned read-provider fault for complete related-resource admission."""


def check(query,catalog,stock,read):
    signature='security.read_gas_owned_reconciliation(uuid,uuid,jsonb,uuid,timestamptz)'
    projection='trading.gas_owned_reconciliation_source_rows(uuid,uuid,uuid,timestamptz,uuid[],uuid[],uuid[],uuid[],boolean)'
    if query(f"SELECT to_regprocedure('{projection}') IS NOT NULL;")=='t':signature=projection
    original=query(f"SELECT pg_get_functiondef('{signature}'::regprocedure);")
    table='trading.gas_owned_reservations'
    assert table in original
    # These typed synthetic rows fit the observed stock capacity. They replace
    # only the reader's provider; no financial row or integrity seal is changed.
    query("CREATE VIEW trading.synthetic_reconciliation_reservations AS SELECT "
        "p.catalog_id,md5('synthetic_scope_reservation_'||n::text)::uuid id,p.id lot_id,"
        "'synthetic_scope_reservation_'||n::text business_key,0.00000001::ref_data.exact_number quantity,"
        "p.actor_id,p.publication_order,p.effective_at FROM trading.gas_owned_positions p CROSS JOIN generate_series(1,16385)n "
        f"WHERE(p.catalog_id,p.id)=('{catalog}','{stock}');"
        "GRANT SELECT ON trading.synthetic_reconciliation_reservations TO openetrm_security_owner;")
    try:
        query(original.replace(table,'trading.synthetic_reconciliation_reservations'))
        try:read()
        except RuntimeError as error:
            assert 'gas_owned_scope' in str(error),str(error)
        else:raise AssertionError('The correction reader accepted 16,385 related resource facts instead of refusing the complete scope.')
    finally:
        query(original)
        query('DROP VIEW trading.synthetic_reconciliation_reservations;')
