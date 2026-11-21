"""Exercise legal admission and document authority in a disposable gas desk."""
import base64
import json
from uuid import uuid4
from check_booked_side_programs import runtime_query
from check_reference_editing import main
from preview_fixture import blank_agreement_pdf, trading_fixture, trade_booking


def checks(query, manifest):
    catalog = manifest['catalogId']
    query(trading_fixture(catalog, bilateral_products=[]))
    party = trade_booking()
    effective = '2020-12-01T00:00:00Z'
    run = lambda sql, who='trader': runtime_query(query, manifest, sql, profile=who)
    def status():
        return json.loads(run(f"SELECT desk.eligibility('{catalog}','{party['legalEntityId']}','{party['counterpartyId']}','physical_gas','{effective}');"))
    def save(body, who, request=None):
        value = json.dumps(body).replace("'", "''")
        return json.loads(run(f"SELECT desk.record('{catalog}','{request or uuid4()}','{value}'::jsonb);", who))
    def refused(work, code):
        try:
            work()
        except RuntimeError as error:
            assert code in str(error), str(error)
        else:
            raise AssertionError('Expected refusal: ' + code)
    assert status()['reason'] == 'agreement_required'
    print('PASS a bare party directory does not confer bilateral coverage', flush=True)
    for role, cap in [('market_data_editor','ReadTrades'),('market_data_approver','ReadTrades'),('market_data_editor','ManageAgreements'),('market_data_approver','ManageCounterpartyCredit'),('market_data_editor','ReadAgreementDocuments')]:
        query(f"INSERT INTO security.role_permissions SELECT id,'{cap}' FROM security.roles WHERE code='{role}' ON CONFLICT DO NOTHING;")
    document = base64.b64encode(blank_agreement_pdf()).decode()
    agreement = dict(**party,kind='agreement',code='gas_master',name='Synthetic bilateral master',agreementType='gas_master',
        products=['physical_gas'],effectiveAt=effective,validTo=None,status='executed',procedure='oral',provisions='Synthetic delivery provisions',
        filename='executed.pdf',pdf=document,expectedRevision=0)
    refused(lambda: save(agreement,'trader'), 'desk_denied')
    request = uuid4()
    first = save(agreement,'editor',request)
    assert first == save(agreement,'editor',request) == save(agreement,'editor')
    assert status()['reason'] == 'credit_required'
    fetch = f"SELECT encode(content,'hex') FROM desk.document('{catalog}','{first['id']}');"
    refused(lambda: run(fetch), 'desk_denied')
    assert bytes.fromhex(run(fetch, 'editor')) == blank_agreement_pdf()
    credit = dict(**party,kind='credit',product='physical_gas',effectiveAt=effective,validTo='2099-01-01',status='approved',reason='Approved synthetic desk',expectedRevision=0)
    refused(lambda: save(credit,'editor'), 'desk_denied')
    approval = save(credit,'approver')
    assert status()['eligible'] and status()['agreementId'] == first['id'] and status()['clearanceId'] == approval['id']
    assert not json.loads(run(f"SELECT desk.eligibility('{catalog}','{party['legalEntityId']}','{party['counterpartyId']}','gas_storage','{effective}');"))['eligible']
    hold = dict(credit,status='hold',expectedRevision=1,effectiveAt='2021-01-01T00:00:00Z',reason='Review required')
    save(hold,'approver')
    assert status()['reason'] == 'credit_hold', 'Backdating a proposed execution must not bypass a current hold'
    assert first == save(agreement,'editor',request)
    second = save(dict(agreement, code='second_master'), 'editor')
    ambiguous = status()
    assert ambiguous['reason'] == 'agreement_ambiguous'
    assert {row['id'] for row in ambiguous['choices']} == {first['id'], second['id']}
    operation = uuid4()
    choose = lambda agreement_id: run(f"SELECT desk.choose('{catalog}','{operation}','{agreement_id}');")
    assert json.loads(choose(second['id'])) == json.loads(choose(second['id'])) == {'agreementId': second['id']}
    refused(lambda: choose(first['id']), 'desk_request_conflict')
    print('PASS filed document access, explicit agreement selection, product scope, retries and credit holds', flush=True)
    # Independent row facts: a revision carries its predecessor's commercial terms only when nothing outside the
    # internal booking fields differs. An unknown or absent term is a commercial change.
    booked = {'revision': 1, 'previous_revision': None, 'event_id': 'a', 'event_kind': 'booked', 'book_id': 'book-a', 'binding_id': 'binding-1',
        'instrument_id': 'instrument-1', 'quantity': 100, 'memo': '', 'reason': 'Initial booking', 'operation_id': 'first',
        'request': {'valuation': '2020-12-01T00:00:00+00:00', 'body': {'memo': '', 'scheduled': {'first': {'basis': 'period_total', 'power': {'startHour': 7}}}}},
        'actor_id': 'trader', 'publication_order': 4, 'effective_at': '2020-12-01T00:00:00+00:00', 'trade_date': '2020-12-01',
        'legal_entity_id': 'entity', 'legal_entity_revision': 1, 'counterparty_id': 'party', 'counterparty_revision': 1}
    moved = {**booked, 'revision': 2, 'previous_revision': 1, 'event_id': 'b', 'event_kind': 'amended', 'book_id': 'book-b', 'memo': 'Desk note',
        'reason': 'Synthetic book correction', 'operation_id': 'second', 'actor_id': 'another trader',
        'request': {'valuation': '2020-12-02T00:00:00+00:00', 'body': {'memo': 'Desk note', 'scheduled': booked['request']['body']['scheduled']}},
        'publication_order': 9, 'effective_at': '2020-12-02T00:00:00+00:00'}
    def commercial(before, after):
        answer = query(f"SELECT desk.commercial_change('{json.dumps(before)}'::jsonb,'{json.dumps(after)}'::jsonb);")
        assert answer in ('t', 'f'), answer
        return answer == 't'
    assert not commercial(booked, moved)
    for term, value in [('instrument_id', 'instrument-2'), ('binding_id', 'binding-2'), ('quantity', 101), ('direction', -1), ('trade_date', '2020-12-02'),
                        ('legal_entity_id', 'another entity'), ('counterparty_revision', 2), ('boundary_at', '2021-01-01T00:00:00+00:00')]:
        assert commercial(booked, {**moved, term: value}), term
    # The same resolved instrument can be stated with other delivery hours or another quantity basis; the stated terms are commercial.
    restated = {**moved, 'request': {'body': {'memo': 'Desk note', 'scheduled': {'first': {'basis': 'period_total', 'power': {'startHour': 8}}}}}}
    assert commercial(booked, restated), 'Restated scheduled terms'
    without_instrument = lambda row: {key: value for key, value in row.items() if key != 'instrument_id'}
    assert commercial(without_instrument(booked), without_instrument(moved)), 'Terms held outside the revision row cannot be proven unchanged'
    print('PASS only a book or memo change carries the earlier revision\'s commercial terms', flush=True)
    # Three admitted revisions of one trade: the second carries the first's commercial terms, the third changes them.
    trade, book = str(uuid4()), 'd1000000-0000-0000-0000-000000000001'
    query(f"""BEGIN;
INSERT INTO trading.publications SELECT '{catalog}',max(publication_order)+1,pg_current_xact_id() FROM trading.publications WHERE catalog_id='{catalog}';
INSERT INTO trading.trades SELECT '{catalog}','{trade}','{book}','{uuid4()}',(SELECT m.principal_id FROM security.workspace_memberships m
  JOIN security.principals h ON h.id=m.principal_id WHERE(m.tenant_id,m.workspace_id)=('{manifest['tenantId']}','{manifest['workspaceId']}') AND h.kind='human' LIMIT 1),
 '{{}}',max(publication_order),'{effective}' FROM trading.publications WHERE catalog_id='{catalog}';
INSERT INTO desk.admissions SELECT '{catalog}','{trade}',v.revision,'{book}','physical_gas','{first['id']}','{approval['id']}','{manifest['revisionId']}',
 '{effective}','{effective}',(SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}'),v.carried
 FROM (VALUES(1,NULL::int),(2,1),(3,NULL))v(revision,carried);
COMMIT;""")
    for cap in ('ConfirmBilateralTrades', 'ReadTradeParties'):
        query(f"INSERT INTO security.role_permissions SELECT id,'{cap}' FROM security.roles WHERE code='market_data_editor' ON CONFLICT DO NOTHING;")
    confirmation = lambda revision: dict(kind='confirmation', tradeId=trade, tradeRevision=revision, effectiveAt='2020-12-02T00:00:00Z',
        reference='CP-2020-1', evidence='Synthetic matched confirmation', status='matched', expectedRevision=0)
    refused(lambda: save(confirmation(2), 'editor'), 'desk_confirmation')
    save(confirmation(1), 'editor')
    rows = [row for row in json.loads(run(f"SELECT desk.directory('{catalog}');", 'editor'))['trades'] if row['trade_id'] == trade]
    assert [row['revision'] for row in rows] == [3, 2, 1], 'Revisions admitted at one execution instant list newest first'
    listed = {row['revision']: row for row in rows}
    assert [listed[n]['carried_from'] for n in (1, 2, 3)] == [None, 1, None], listed
    assert [[c['status'] for c in listed[n]['confirmations']] for n in (1, 2, 3)] == [['matched'], ['matched'], []], listed
    assert [query(f"SELECT desk.confirmed_revision('{catalog}','{trade}',{n});") for n in (1, 2, 3, 4)] == ['1', '1', '3', '4']
    # Attribution reads the latest admitted revision through the legacy union view; an unknown profile is its only refusal here.
    refused(lambda: run(f"SELECT desk.attribute('{catalog}','{trade}','{uuid4()}');"), 'desk_facility')
    print('PASS a carried revision shows and accepts confirmations only through the revision whose terms it carries', flush=True)


if __name__ == '__main__':
    main(checks)
