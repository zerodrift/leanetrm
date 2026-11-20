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


if __name__ == '__main__':
    main(checks)
