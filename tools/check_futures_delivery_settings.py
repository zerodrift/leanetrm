"""Bounded account discovery with exact retained delivery settings."""
import json
import re
from uuid import uuid4

from check_futures_physical_delivery import fixture, checks as physical_checks
from check_reference_editing import main


SIGNATURE = 'settlement_data.read_futures_delivery_settings(uuid,uuid,timestamptz)'


def configured(query, manifest, setup):
    catalog = manifest['catalogId']

    def call(sql):
        token = query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
                      f"'{manifest['identityIssuer']}','trader','human_interactive','{manifest['tenantId']}',"
                      f"'{manifest['workspaceId']}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}COMMIT;")

    def capture():
        return call(f"SELECT security.resolve_operation_context('{catalog}','2026-10-16',NULL);")

    def settings(context):
        result = json.loads(call(f"SELECT security.use_market_context('{catalog}','{context}');"
                                 f"SELECT security.use_trading_context('{catalog}','{setup['at']}');"
                                 f"SELECT settlement_data.read_futures_delivery_settings('{catalog}','{context}','{setup['at']}');"))
        for profile in result['profiles']:
            del profile['authorityUntil']
        return result

    retained = capture()
    original = settings(retained)
    assert len(original['accounts']) == 1 and len(original['profiles']) == 2
    base = original['accounts'][0]
    fields = dict(bookId='book_id', legalEntityId='legal_entity_id', legalEntityRevision='legal_entity_revision',
                  counterpartyId='counterparty_id', counterpartyRevision='counterparty_revision',
                  referenceCatalogId='reference_catalog_id', referenceRevisionId='reference_revision_id',
                  definitionRevisionId='definition_revision_id', markCurve='mark_curve_code', markContractId='mark_contract_id',
                  quantity='quantity_code', currency='currency_code', location='location_code',
                  titlePolicy='title_policy', lossPolicy='loss_policy')
    terms = {key: base[column] for key, column in fields.items()}
    visible = {base['id']}
    for index in range(6):
        identity = str(uuid4())
        recorded_at = setup['at'] if index < 5 else '2026-10-16T18:00:00Z'
        body = dict(terms, code=f'synthetic_delivery_settings_{index}', kind='owned_inventory',
                    effectiveAt='2026-10-01T00:00:00Z' if index < 5 else recorded_at)
        encoded = json.dumps(body).replace("'", "''")
        call(f"SELECT security.record_gas_owned('{catalog}','{uuid4()}','account','{identity}',"
             f"'{encoded}'::jsonb,'{recorded_at}','{capture()}',NULL,NULL,false);")
        if index < 5:
            visible.add(identity)
    current = capture()

    # EXPLAIN on the outer PL/pgSQL call hides nested work. Explain the actual
    # account statement, parameterized like its cached plan, under its owner.
    body = query(f"SELECT prosrc FROM pg_proc WHERE oid='{SIGNATURE}'::regprocedure;")
    start = body.index('SELECT coalesce(jsonb_agg(to_jsonb(v)')
    statement = body[start:body.index(';', start)].replace('INTO accounts', '')
    for name, parameter in [('catalog', '$1'), ('prefix', '$2'), ('cut', '$3'), ('as_of', '$4')]:
        statement = re.sub(r'\b' + name + r'\b', lambda _: parameter, statement)
    from native_read_transport import captured_input
    source=captured_input(query,current)
    context = dict(prefix=source['trading_prefix'],cut=source['known_at'])

    def work(node):
        visited = (node['Actual Rows'] + node.get('Rows Removed by Filter', 0)
                   + node.get('Rows Removed by Join Filter', 0)) * node['Actual Loops']
        return visited + sum(work(child) for child in node.get('Plans', ()))

    for mode in ('force_generic_plan', 'force_custom_plan'):
        execute = f"EXECUTE settings_accounts('{catalog}',{context['prefix']},'{context['cut']}','{setup['at']}');"
        output = query(f"BEGIN;SET LOCAL ROLE openetrm_security_owner;SET LOCAL plan_cache_mode={mode};"
                       f"PREPARE settings_accounts(uuid,integer,timestamptz,timestamptz) AS {statement};"
                       f"EXPLAIN(ANALYZE,TIMING OFF,FORMAT JSON) {execute}SELECT 'SETTINGS_RESULT';{execute}ROLLBACK;")
        plan, payload = output.split('\nSETTINGS_RESULT\n')
        selected = json.loads(payload)
        assert len(selected) == 6 and {row['id'] for row in selected} == visible, 'The measured query must select all six eligible accounts exactly once'
        visited = round(work(json.loads(plan)[0]['Plan']))
        assert visited < 100_000, f'Delivery settings expanded six visible accounts into {visited} tuple visits ({mode})'
        print(f'PASS delivery settings bounded account discovery: six accounts, {visited} tuple visits ({mode})', flush=True)

    # Independent payload oracle: stored account rows and separate exact-key
    # lookups, without calling the reader or repeating its multi-table join.
    rows = json.loads(query(f"SELECT jsonb_agg(to_jsonb(a) ORDER BY code,id) FROM trading.gas_owned_accounts a WHERE catalog_id='{catalog}';"))
    expected = []
    for row in rows:
        if row['id'] not in visible:
            continue
        metadata = json.loads(query(f"SELECT jsonb_build_object("
            f"'book_name',(SELECT name FROM trading.books WHERE(catalog_id,id)=('{catalog}','{row['book_id']}')),"
            f"'legal_entity_name',(SELECT name FROM trading.legal_entities WHERE(catalog_id,id,revision)=('{catalog}','{row['legal_entity_id']}',{row['legal_entity_revision']})),"
            f"'mark_expiry_at',(SELECT expiry_at FROM market_data.contracts WHERE(catalog_id,revision_id,id)=('{catalog}','{row['definition_revision_id']}','{row['mark_contract_id']}')),"
            "'quote_multiplier',(SELECT multiplier FROM ref_data.quote_specifications WHERE(catalog_id,revision_id,code)="
            f"(SELECT reference_catalog_id,reference_revision_id,quote_code FROM market_data.curves WHERE(catalog_id,revision_id,code)=('{catalog}','{row['definition_revision_id']}','{row['mark_curve_code']}'))));"))
        expected.append(dict(row, **metadata))
    actual = settings(current)
    assert actual['accounts'] == expected, 'Exact account metadata or business-effective selection changed'
    assert actual['profiles'] == original['profiles'], 'Discovery must retain both profile revisions'
    old = settings(retained)
    assert old['accounts'] == original['accounts'] and old['profiles'] == original['profiles'], 'Later backdated accounts leaked into retained knowledge'
    print('PASS delivery settings preserve exact account metadata, both profile revisions, business time and retained knowledge', flush=True)

    scopes = query(f"SELECT jsonb_agg(to_jsonb(s)) FROM security.reference_catalog_scopes s WHERE catalog_id='{manifest['referenceCatalogId']}';")
    query(f"DELETE FROM security.reference_catalog_scopes WHERE catalog_id='{manifest['referenceCatalogId']}';")
    try:
        try:
            settings(current)
        except RuntimeError as error:
            assert 'denied' in str(error), str(error)
        else:
            raise AssertionError('Delivery settings disclosed accounts after reference access was revoked')
    finally:
        query("INSERT INTO security.reference_catalog_scopes SELECT * FROM jsonb_populate_recordset("
              "NULL::security.reference_catalog_scopes,'" + scopes.replace("'", "''") + "'::jsonb);")
    print('PASS delivery settings refuse revoked reference authority', flush=True)


def checks(query, manifest):
    physical_checks(query, manifest, configuration_checks=configured)


def upgrade_checks(query, migration):
    if migration.name == '0313_futures_delivery_settings_probes.sql':
        from assistant_context import check_guard
        from check_fact_anchor_upgrade import run_checks
        check_guard(query, migration.read_text(), '0313', 'delivery_settings_upgrade',
            [(f'REVOKE EXECUTE ON FUNCTION {SIGNATURE} FROM openetrm_security_owner;', SIGNATURE)])
        run_checks(query, migration, number='0313', guard='delivery_settings_upgrade', signatures=(SIGNATURE,))


if __name__ == '__main__':
    main(checks=checks, fixture_factory=fixture, before_migration=upgrade_checks)
