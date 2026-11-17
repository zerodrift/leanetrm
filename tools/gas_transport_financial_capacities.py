"""Financial authoring retains its exact instant when reading capacity terms."""
import json
from copy import deepcopy


def capacity_facts(f, rejected):
    context = f.capture()
    previous_at = f.at
    try:
        f.at = f.valuation_at
        rejected(lambda: f.read(f.entitlement, context=context), 'gas_transport_capacity_context')
    finally:
        f.at = previous_at

    def read(target=None, *, source=None, at=None):
        selected, instant = source or f.capture(), at or f.valuation_at
        identifier = 'NULL' if target is None else "'" + target + "'"
        return json.loads(f.call(f"SELECT security.use_market_context('{f.catalog}','{selected}');"
            f"SELECT security.use_trading_context('{f.catalog}','{instant}','{instant[:10]}');"
            f"SELECT security.gas_transport_financial_capacity_facts('{f.catalog}','{selected}','{instant}',{identifier});"))

    one = read(f.entitlement, source=context)
    assert one['asOf'].startswith(f.valuation_at.removesuffix('Z')) and one['contextId'] == context
    assert len(one['capacities']) == 1
    row = one['capacities'][0]
    operating = f.read(f.entitlement, context=context)
    assert row['entitlement'] == operating['entitlement'] and row['version'] == operating['version']
    assert row['hasFutureHeads'] is False
    assert any(item['entitlement']['id'] == f.entitlement for item in read()['capacities'])
    role = f.query("SELECT id FROM security.roles WHERE code='trader';")
    f.query(f"DELETE FROM security.role_permissions WHERE(role_id,permission)=('{role}','ReadGasOperations');")
    try:
        rejected(lambda: read(f.entitlement), 'gas_transport_financial_denied')
    finally:
        f.query(f"INSERT INTO security.role_permissions VALUES('{role}','ReadGasOperations');")

    # Use a separate entitlement so this read-only authoring control cannot
    # alter the financial agreement used by the main valuation checkpoints.
    book, definition, accepted = f.new_capacity('synthetic_financial_capacity_source')
    retained_book = f.book
    try:
        f.book = book
        facts = f.read(definition['entitlementId'])
        old_context = f.capture()
        replacement = deepcopy(definition)
        replacement.update(expectedStateVersion=facts['stateVersion'], expectedPreviousId=accepted['versionId'],
            evidence='Synthetic future financial capacity source control')
        f.at = '2020-12-20T23:59:59.999999Z'
        future = f.record(replacement, effective='2020-12-20T00:00:00Z')
        earlier = read(definition['entitlementId'], source=f.capture(day='2020-12-19'))['capacities'][0]
        assert earlier['version'] is None and earlier['hasFutureHeads'] is True
        old_cut = read(definition['entitlementId'], source=old_context)['capacities'][0]
        assert old_cut['version'] is None, 'A source-unknown latest head resurrected an obsolete capacity version.'
        later = read(definition['entitlementId'], source=f.capture(), at='2020-12-20T00:00:00Z')['capacities'][0]
        assert later['version']['id'] == future['versionId'] and later['hasFutureHeads'] is False
    finally:
        f.book, f.at = retained_book, previous_at
    print('PASS financial capacity authoring uses exact16 source, preserves typed operating terms, denies absent operating authority, and never resurrects a future or source-unknown latest head.', flush=True)
