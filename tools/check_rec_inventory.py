"""Independent synthetic checks for evidenced certificate accounts and serial ranges."""
import json
from pathlib import Path
from uuid import uuid4

ROOT = Path(__file__).resolve().parents[1]
MIGRATION = '0406_inventory_role_grants.sql'


def run_checks(query, manifest, psql=None, environment=None):
    catalog = manifest['catalogId']
    passed = 0

    def check(label, condition=True):
        nonlocal passed
        assert condition, label
        passed += 1
        print('PASS REC inventory: ' + label, flush=True)

    def runtime(sql, actor='operator'):
        token = query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
            f"'https://preview.invalid','{actor}','human_interactive','{manifest['tenantId']}',"
            f"'{manifest['workspaceId']}',NULL,'preview_runtime',120);")
        return ('\\set VERBOSITY verbose\nBEGIN;SET LOCAL SESSION AUTHORIZATION preview_runtime;'
                f"SELECT security.use_context('{token}');" + sql + 'COMMIT;')

    def literal(value):
        return "'" + json.dumps(value).replace("'", "''") + "'::jsonb"

    def reject(sql, code, actor='operator'):
        try:
            query(runtime(sql, actor))
        except RuntimeError as error:
            assert code in str(error), str(error)
        else:
            raise AssertionError('Accepted invalid certificate operation: ' + code)

    account = dict(accountId=str(uuid4()), bookId='d1000000-0000-0000-0000-000000000001',
        legalEntityId='d4000000-0000-0000-0000-000000000001', legalEntityRevision=1,
        referenceCatalogId=manifest['referenceCatalogId'], referenceRevisionId=manifest['referenceRevisionId'],
        registryCode='fixture', registryAccountReference='SYNTHETIC-OWNED-001')
    operation = str(uuid4())
    def configure(body=account, request=operation):
        return f"SELECT security.configure_rec_account('{catalog}','{request}',{literal(body)},'2020-12-15T12:00:00Z');"

    def holders(permission):
        return set(query("SELECT r.code FROM security.role_permissions p JOIN security.roles r ON r.id=p.role_id "
                         f"WHERE p.permission='{permission}';").split())

    agent_only = {'agent_reader', 'swap_settlement_operator', 'gas_lifecycle_operator'}
    trade_readers = holders('ReadTrades') - agent_only
    check('every human role that reads trades reads REC inventory, and no other role does',
          holders('ReadRecInventory') == trade_readers)
    check('every human role that reads trades reads owned gas, and the agent-only gas role is the only other reader',
          holders('ReadGasOwnedHoldings') == trade_readers | {'gas_lifecycle_operator'})
    check('Facility operations records and Contracts administration sets up REC inventory, and no other role does either',
          holders('RecordRecInventory') == {'facility_operator'} and holders('ConfigureRecInventory') == {'contracts_officer'})
    check('the two gas roles and the agent-only gas role record owned gas, and Gas service setup alone sets it up',
          holders('RecordGasOwnedHoldings') == {'gas_operator', 'gas_service_admin', 'gas_lifecycle_operator'}
          and holders('ConfigureGasOwnedHoldings') == {'gas_service_admin'})
    check('only the two gas roles can be on the customer side of custody, and no role that books reads gas operations',
          holders('ReadGasOperations') & holders('ReadGasOwnedHoldings') == {'gas_operator', 'gas_service_admin'}
          and not holders('BookTrade') & holders('ReadGasOperations'))
    check('neither gas role can bind an automatic action policy, which needs RunJobs',
          not holders('RunJobs') & {'gas_operator', 'gas_service_admin'})
    check('grants made by hand before the migration are kept, and the migration installs the rest',
          query("SELECT count(*) FROM security.role_permissions p JOIN security.roles r ON r.id=p.role_id WHERE(r.code,p.permission) IN("
                "('credit_officer','ReadRecInventory'),('credit_officer','ReadGasOwnedHoldings'),"
                "('trader','ReadRecInventory'),('trader','ReadGasOwnedHoldings'));") == '4')
    migration = (ROOT / 'db/migrations' / MIGRATION).read_text()
    grants = "SELECT(SELECT count(*) FROM security.role_permissions)::text||' '||(SELECT count(*) FROM security.role_grants)::text;"
    installed = query(grants)
    query(migration)
    check('applying the grants again changes no role and gives no person a role', query(grants) == installed)
    body = migration[migration.index('BEGIN;') + len('BEGIN;'):migration.rindex('COMMIT;')]
    try:
        query("BEGIN;UPDATE security.roles SET code='synthetic_renamed_role' WHERE code='credit_officer';" + body + 'ROLLBACK;')
    except RuntimeError as error:
        assert 'inventory_role_grants' in str(error) and 'credit_officer' in str(error), str(error)
    else:
        raise AssertionError('The grants accepted an installation without one of their roles')
    check('an installation without one of the named roles is told which and receives no part of the grants',
          query(grants) == installed)
    for actor in ('trader', 'risk', 'viewer', 'editor', 'approver', 'admin'):
        reject(configure(), '42501', actor)
    check('a person without the setup role cannot set up a certificate account')
    from preview_fixture import trading_fixture
    query(trading_fixture(catalog))
    first = json.loads(query(runtime(configure())))
    check('account pins an existing book, exact entity and registry source', first['accountId'] == account['accountId'] and first['changed'])
    check('exact command replay returns its first receipt', json.loads(query(runtime(configure()))) == first)
    duplicate = json.loads(query(runtime(configure(request=str(uuid4())))))
    check('semantic account retry creates no second account', not duplicate['changed'] and duplicate['accountId'] == account['accountId'])
    different = dict(account, registryAccountReference='SYNTHETIC-OWNED-002')
    reject(configure(different), 'rec_request_conflict')
    check('a request identity cannot change its account command')
    reject('SELECT * FROM trading.rec_inventory_accounts;', '42501')
    check('raw inventory tables are inaccessible to runtime')
    reject(configure([], str(uuid4())), 'rec_account_input')
    reject(configure(dict(account, registryAccountReference=123), str(uuid4())), 'rec_account_input')
    check('account transport refuses nonobjects and implicit scalar conversion')
    lot = str(uuid4())
    receipt = dict(kind='receipt', lotId=lot, productCode='synthetic_rec', registryLotReference='SYNTHETIC-LOT-001',
        vintageFrom='2019-01-01', vintageTo='2020-01-01', **{'from': '0', 'to': '100'},
        receiptKind='issuance', evidenceSource='fixture', evidenceReference='SYNTHETIC-ISSUANCE-001')

    def record(action, expected, business=None, request=None):
        return (f"SELECT security.record_rec_inventory('{catalog}','{request or uuid4()}','{account['accountId']}',"
            f"'{business or uuid4()}',{expected},'2020-12-15T12:00:00Z',{literal(action)});")

    issuance = json.loads(query(runtime(record(receipt, 0))))
    check('evidenced issuance appends one immutable fact', issuance['generation'] == 1 and issuance['changed'])
    same_lot = json.loads(query(runtime(record(dict(receipt,lotId=str(uuid4())),1))))
    check('a new candidate lot UUID cannot duplicate the same registry lot and issued fact', not same_lot['changed'] and same_lot['factId']==issuance['factId'] and same_lot['generation']==1)
    reject(record(dict(receipt, evidenceReference=None), 1), 'rec_input')
    reject(record(dict(receipt, evidenceSource='absent'), 1), 'rec_input')
    check('missing or unresolved evidence refuses even a semantic fact retry')
    retirement = dict(kind='retire', lotId=lot, **{'from': '60', 'to': '100'}, beneficiaryKind='legal_entity',
        beneficiaryId=account['legalEntityId'], beneficiaryRevision=1, purposeCode='synthetic_voluntary',
        evidenceSource='fixture', evidenceReference='SYNTHETIC-RETIREMENT-001')
    retired = json.loads(query(runtime(record(retirement, 1).replace("T12:00:00Z", "T13:00:00Z"))))
    check('an explicit retirement appends a separate fact', retired['generation'] == 2)
    reject(record(dict(retirement, **{'from': '50', 'to': '70'}), 2), 'rec_unavailable')
    check('retired certificate overlap cannot be spent again')
    other = dict(account, accountId=str(uuid4()), registryAccountReference='SYNTHETIC-OWNED-002')
    query(runtime(configure(other, str(uuid4()))))
    duplicate_holding = record(dict(receipt, **{'to': '50'}), 0).replace(account['accountId'], other['accountId'])
    reject(duplicate_holding, 'rec_overlap')
    check('another owned account cannot issue the same registry serials')
    context = query(runtime(f"SELECT security.resolve_operation_context('{catalog}','2020-12-15',NULL);"))
    selected = (f"SELECT security.use_market_context('{catalog}','{context}');"
                f"SELECT security.use_trading_context('{catalog}','2020-12-15T23:59:59.999999Z');")
    held = json.loads(query(runtime(selected + f"SELECT security.read_rec_inventory('{catalog}','{account['accountId']}','{context}','2020-12-15T23:59:59.999999Z');")))
    check('certified read retains two facts and sixty available certificates', len(held['facts']) == 2 and held['lotBalances'][0]['availableCount'] == '60')
    directory = json.loads(query(runtime(selected + f"SELECT security.rec_inventory_directory('{catalog}','{context}','2020-12-15T23:59:59.999999Z');")))
    check('directory offers exact eligible products and configured owned accounts', len(directory['products']) == 1 and len(directory['accounts']) == 2 and directory['registries'][0]['code'] == 'fixture')
    read_held = selected + f"SELECT security.read_rec_inventory('{catalog}','{account['accountId']}','{context}','2020-12-15T23:59:59.999999Z');"
    read_directory = selected + f"SELECT security.rec_inventory_directory('{catalog}','{context}','2020-12-15T23:59:59.999999Z');"
    for actor in ('trader', 'risk'):
        assert json.loads(query(runtime(read_held, actor)))['lotBalances'][0]['availableCount'] == '60', actor
        assert len(json.loads(query(runtime(read_directory, actor)))['accounts']) == 2, actor
    check('installed trade readers read the certified inventory and the directory')
    for actor in ('viewer', 'editor', 'approver', 'admin'):
        reject(read_held, '42501', actor)
        reject(read_directory, '42501', actor)
    check('roles that read no trades read no inventory')
    reject(record(receipt, 2), '42501', 'trader')
    reject(configure(request=str(uuid4())), '42501', 'trader')
    check('a trade reader records and sets up nothing')
    status = json.loads(query(runtime(f"SELECT security.rec_inventory_status('{catalog}','{operation}');")))
    check('status returns the original actor scoped account receipt', status == first)
    destination = dict(destinationId=str(uuid4()), counterpartyId='d5000000-0000-0000-0000-000000000001', counterpartyRevision=1,
        referenceCatalogId=manifest['referenceCatalogId'], referenceRevisionId=manifest['referenceRevisionId'], registryCode='fixture', registryAccountReference='SYNTHETIC-DESTINATION-001')
    destination_request = str(uuid4())
    destination_call = f"SELECT security.configure_rec_destination('{catalog}','{destination_request}',{literal(destination)},'2020-12-15T12:00:00Z');"
    configured = json.loads(query(runtime(destination_call)))
    check('external delivery destination pins an exact counterparty and registry', configured['destinationId'] == destination['destinationId'] and configured['changed'])
    check('destination request replay returns its original receipt', json.loads(query(runtime(destination_call))) == configured)
    reject(record(dict(kind='allocate', lotId=lot, **{'from':'0','to':'1'}, deliveryId=str(uuid4())),2), 'rec_eligibility')
    reject(record(dict(kind='release', lotId=lot, **{'from':'0','to':'1'}, allocationId=str(uuid4()), allocationRevision=1),2), 'rec_allocation')
    check('allocation and release cannot fabricate missing booked targets or prior ownership')
    intraday = (f"SELECT security.use_market_context('{catalog}','{context}');"
        f"SELECT security.use_trading_context('{catalog}','2020-12-15T12:30:00Z');")
    before_retirement = json.loads(query(runtime(intraday + f"SELECT security.read_rec_inventory('{catalog}','{account['accountId']}','{context}','2020-12-15T12:30:00Z');")))
    check('effective-time read does not spend certificates before the evidenced retirement', before_retirement['lotBalances'][0]['availableCount']=='100')
    corrected = dict(retirement, **{'from':'70'}, evidenceReference='SYNTHETIC-RETIREMENT-CORRECTION')
    correction = json.loads(query(runtime(record(corrected,2,business=retired['factId']).replace("T12:00:00Z", "T13:00:00Z"))))
    check('changed retirement value appends a revision under the original business identity', correction['factId']==retired['factId'] and correction['revision']==2 and correction['generation']==3)
    old_view = json.loads(query(runtime(selected + f"SELECT security.read_rec_inventory('{catalog}','{account['accountId']}','{context}','2020-12-15T23:59:59.999999Z');")))
    check('certified old knowledge retains the original forty-certificate retirement', len(old_view['facts'])==2 and old_view['lotBalances'][0]['availableCount']=='60')
    current_context = query(runtime(f"SELECT security.resolve_operation_context('{catalog}','2020-12-15',NULL);"))
    current = (f"SELECT security.use_market_context('{catalog}','{current_context}');"
        f"SELECT security.use_trading_context('{catalog}','2020-12-15T23:59:59.999999Z');")
    current_read = f"SELECT security.read_rec_inventory('{catalog}','{account['accountId']}','{current_context}','2020-12-15T23:59:59.999999Z');"
    amended = json.loads(query(runtime(current+current_read)))
    check('current knowledge uses the thirty-certificate correction with complete retained lineage', len(amended['facts'])==3 and amended['lotBalances'][0]['availableCount']=='70')
    reasserted = json.loads(query(runtime(record(retirement,3,business=retired['factId']).replace("T12:00:00Z", "T13:00:00Z"))))
    check('reasserting an old fact returns it without restoring its value', not reasserted['changed'] and reasserted['revision']==1 and json.loads(query(runtime(current+current_read)))['lotBalances'][0]['availableCount']=='70')
    query("DELETE FROM security.role_permissions WHERE permission='RecordRecInventory' AND role_id IN(SELECT id FROM security.roles WHERE code='facility_operator');")
    no_record = json.loads(query(runtime(configure(request=str(uuid4())))))
    reject(record(receipt,2),'42501')
    check('configuration capability is independent and never grants evidence recording', not no_record['changed'])
    query("INSERT INTO security.role_permissions SELECT id,'RecordRecInventory' FROM security.roles WHERE code='facility_operator';")
    query("DELETE FROM security.role_permissions WHERE permission='ConfigureRecInventory' AND role_id IN(SELECT id FROM security.roles WHERE code='contracts_officer');")
    reject(configure(request=str(uuid4())), '42501')
    no_setup = json.loads(query(runtime(record(retirement,3,business=retired['factId']).replace("T12:00:00Z", "T13:00:00Z"))))
    query("INSERT INTO security.role_permissions SELECT id,'ConfigureRecInventory' FROM security.roles WHERE code='contracts_officer';")
    check('evidence recording is independent and never grants configuration', not no_setup['changed'] and no_setup['revision'] == 1)
    from concurrent.futures import ThreadPoolExecutor
    raced_lot = str(uuid4()); raced_business = str(uuid4()); raced_request = str(uuid4())
    raced_receipt = dict(receipt, lotId=raced_lot, registryLotReference='SYNTHETIC-CONCURRENT-LOT', **{'from':'0','to':'10'})
    raced_sql = record(raced_receipt,3,business=raced_business,request=raced_request)
    attempts = [runtime(raced_sql),runtime(raced_sql)]
    before_race = int(query(f"SELECT count(*) FROM trading.rec_inventory_facts WHERE catalog_id='{catalog}';"))
    with ThreadPoolExecutor(max_workers=2) as workers:
        outcomes = [json.loads(value) for value in workers.map(query, attempts)]
    check('concurrent exact requests return one first outcome across native sessions', outcomes[0] == outcomes[1] and outcomes[0]['generation'] == 4)
    check('concurrent receipt retry appends only one business fact', int(query(f"SELECT count(*) FROM trading.rec_inventory_facts WHERE catalog_id='{catalog}';")) == before_race+1)
    return passed


def before_migration(query, migration):
    if migration.name == MIGRATION:
        # An installation may already have granted some of these by hand; the migration keeps those grants. Only reads
        # of a role the missing-role case below names are granted here, so no later step runs on a hand grant alone.
        query("INSERT INTO security.role_permissions SELECT r.id,g.permission FROM security.roles r JOIN(VALUES"
              "('credit_officer','ReadRecInventory'),('credit_officer','ReadGasOwnedHoldings'))g(code,permission) ON g.code=r.code;")


if __name__ == '__main__':
    from check_reference_editing import main
    from preview_fixture import fixture
    from rec_fixture import extend
    main(checks=run_checks, fixture_factory=lambda root: extend(*fixture(root)), before_migration=before_migration)
