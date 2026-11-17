"""Install Transport capacity over retained title, custody, fee and storage facts."""
import json
from pathlib import Path
from uuid import uuid4

from check_fact_anchor_upgrade import run_checks as guard_checks
from check_reference_editing import main
from gas_park_custody_fixture import CustodyFixture, fixture
from gas_balance_fee_fixture import BalanceFeeService
from gas_park_loan_fixture import literal


def run():
    prepared = {}
    root = Path(__file__).resolve().parents[1]
    def before(query, migration):
        if migration.name != '0352_gas_transport_capacity.sql':
            return
        guard_checks(query,migration,number='0352',guard='gas_transport_capacity_upgrade',signatures=('assistant.is_prompt_page(text)',))
        reference, market, access, manifest = fixture(root)
        for sql in [reference,market,access]: query(sql)
        f = CustodyFixture(query,manifest)
        owned, book = f.account('synthetic_retained_transport_customer')
        policy = f.bind(owned,'gas.owned.receive_title')
        title = f.evidence(owned,'synthetic_retained_customer_title','title_receipt','1000','2020-12-18T08:00:00Z')
        lot = f.native(owned,'receive_title',dict(evidenceId=title,quantity='1000',titleAt='2020-12-18T08:00:00Z',availableAt='2020-12-18T08:00:00Z',
            originPolicy='opening_owned_stock_v1',carrying='nominal_mark_v1'),policy=policy)
        f.bind(owned,'gas.owned.reserve');f.bind(owned,'gas.owned.release')
        accept_policy, return_policy = f.custody_policy('accept'),f.custody_policy('return')
        f.at,f.day='2020-12-19T16:00:00Z','2020-12-19'
        booking = f.booking(book,'customer',owned)
        booked = f.book(booking)
        agreement = f.custody_evidence(booked['tradeId'],'synthetic_upgrade_custody_agreement','custody_agreement','720','2020-12-19T08:00:00Z')
        f.at,f.day='2020-12-20T16:00:00Z','2020-12-20'
        receipt = f.custody_evidence(booked['tradeId'],'synthetic_upgrade_custody_receipt','custody_receipt','720','2020-12-20T10:00:00Z')
        acceptance = dict(expectedVersion=f.read(booked['tradeId'])['stateVersion'],agreementEvidenceId=agreement['id'],receiptEvidenceId=receipt['id'],
            effectiveAt='2020-12-20T10:00:00Z',allocations=[dict(lotId=lot['id'],quantity='720')])
        acceptance_operation, acceptance_context = str(uuid4()),f.capture()
        accepted=f.record(booked['tradeId'],'accept',acceptance,accept_policy,operation=acceptance_operation,context=acceptance_context)
        custody_old=f.capture()
        returned=f.custody_evidence(booked['tradeId'],'synthetic_upgrade_custody_return','custody_return','270','2020-12-20T12:00:00Z')
        state=f.read(booked['tradeId'])
        f.record(booked['tradeId'],'return',dict(expectedVersion=state['stateVersion'],businessKey='synthetic_upgrade_partial_return',evidenceId=returned['id'],
            quantity='270',effectiveAt='2020-12-20T12:00:00Z',allocations=[dict(reservationId=state['reservations'][0]['reservationId'],quantity='270')]),return_policy)
        timed=BalanceFeeService(f,'synthetic_retained_transport_fee');timed.advance()
        timed_old=f.capture();timed.returned('270','2020-12-20T11:00:00Z','synthetic_upgrade_fee_partial')
        selection=timed.selection('2020-12-20T11:00:00Z','synthetic_upgrade_determined_fee')
        determination_operation,determination_context=str(uuid4()),f.capture()
        determined=timed.command('fee_determine',selection,operation=determination_operation,context=determination_context)
        timed.command('fee_payment',dict(expectedVersion=timed.read()['stateVersion'],feeId=determined['feeId'],businessKey='synthetic_upgrade_fee_bank',expectedRevision=0,
            paidAt='2020-12-20T11:30:00Z',amount='-1',evidence='Synthetic retained bank contribution'))
        query("INSERT INTO security.role_permissions SELECT id,p FROM security.roles CROSS JOIN unnest(ARRAY['ReadFacilityQuantities','RecordFacilityQuantities','ConfigureFacilityLimits'])p WHERE code='trader' ON CONFLICT DO NOTHING;")
        facility,port=str(uuid4()),str(uuid4())
        def storage_record(body,witness=None,effective=None,operation=None):
            return json.loads(f.call(f"SELECT security.record_storage_facility('{f.catalog}','{operation or uuid4()}',{literal(body)},'{effective or f.at}',{literal(witness)},'{f.capture()}')::text;"))
        def storage_read(context=None):
            return json.loads(f.call(f"SELECT security.use_market_context('{f.catalog}','{context or f.capture()}');SELECT security.use_trading_context('{f.catalog}','{f.at}');"
                f"SELECT security.read_storage_facility('{f.catalog}','{facility}')::text;"))
        storage_definition=dict(kind='define',facilityId=facility,expectedStateVersion=0,expectedPreviousId=None,
            referenceCatalogId=manifest['referenceCatalogId'],referenceRevisionId=manifest['referenceRevisionId'],facilityCode='synthetic_gas_point_a',
            commodity='test_gas',quantity='test_gas_quantity',clock='test_utc',minimum='0',maximum='1000',implementation='storage_total_physical_step_ratchets_v1',
            inventoryBasis='total_physical_inventory_v1',rateBasis='native_quantity_per_elapsed_hour_v1',simultaneousFlow='separate_injection_withdrawal_v1',lossTreatment='no_loss_v1',
            ports=[dict(portId=port,pipeline='test_pipeline_a',zone='synthetic_gas_zone_a',point='synthetic_gas_point_a',product='test_gas',direction='inject',contractDirection=1)],
            ratchets=[dict(lower='0',upper='1000',injectionPerHour='100',withdrawalPerHour='100')],evidence='Synthetic retained physical storage definition')
        storage_version=storage_record(storage_definition,effective='2020-12-20T00:00:00Z')
        storage_old=f.capture()
        opening=storage_record(dict(kind='opening',facilityId=facility,expectedStateVersion=storage_read()['stateVersion'],expectedPreviousId=None,
            businessKey='synthetic_retained_opening',observedAt='2020-12-20T00:00:00Z',quantity='400',evidence='Synthetic dated opening'))
        storage_plan=dict(kind='plan',facilityId=facility,expectedStateVersion=storage_read()['stateVersion'],expectedPreviousId=None,planId=str(uuid4()),
            versionId=storage_version['versionId'],openingRevisionId=opening['revisionId'],**{'from':'2020-12-20T00:00:00Z'},through='2020-12-20T02:00:00Z',
            coverageStatement='chosen_plan_idle_between_activities_v1',activities=[],evidence='Synthetic retained idle physical plan')
        ratio=lambda n:dict(n=str(n),d='1')
        storage_witness=dict(implementation='storage_assessment_witness_v1',rulesVersion=storage_version['versionId'],openingRevision=opening['revisionId'],coverageRevision=storage_plan['planId'],
            **{'from':storage_plan['from']},through=storage_plan['through'],constraint='within_limit',closing=ratio(400),intervals=[dict(**{'from':storage_plan['from']},through=storage_plan['through'],
                activities=[],opening=ratio(400),closing=ratio(400),injectionPerHour=ratio(0),withdrawalPerHour=ratio(0),bands=[0])])
        storage_operation=str(uuid4());stored_plan=storage_record(storage_plan,storage_witness,operation=storage_operation)
        tables=json.loads(query("SELECT jsonb_agg(n.nspname||'.'||c.relname ORDER BY n.nspname,c.relname) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace "
            "WHERE n.nspname IN('trading','settlement_data') AND c.relkind='r' AND EXISTS(SELECT 1 FROM pg_attribute a WHERE a.attrelid=c.oid AND a.attname='catalog_id' AND NOT a.attisdropped);"))
        def digest():
            return query('SELECT jsonb_object_agg(name,digest) FROM('+' UNION ALL '.join(
                f"SELECT '{table}'::text name,md5(coalesce(jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text),'[]')::text) digest FROM {table} t WHERE t.catalog_id='{f.catalog}'" for table in tables)+')s;')
        prepared.update(f=f,manifest=manifest,digest=digest,booked=booked,acceptance=acceptance,accept_policy=accept_policy,accepted=accepted,
            acceptance_operation=acceptance_operation,acceptance_context=acceptance_context,custody_old=custody_old,custody_current=f.read(booked['tradeId']),
            timed=timed,timed_old=timed_old,timed_current=timed.read(),selection=selection,determination_operation=determination_operation,
            determination_context=determination_context,determined=determined)
        prepared.update(storage_read=storage_read,storage_record=storage_record,storage_old=storage_old,storage_plan=storage_plan,storage_witness=storage_witness,
            storage_operation=storage_operation,stored_plan=stored_plan)
        prepared['before']=digest()
    def loaded(_):return '','','',prepared['manifest']
    def verify(query,_):
        f=prepared['f'];timed=prepared['timed']
        assert prepared['digest']()==prepared['before'],'Capacity installation changed retained title, reservations, cash or original outcomes.'
        assert query('SELECT count(*) FROM trading.gas_transport_capacity_entitlements;')=='0'
        assert query("SELECT assistant.is_prompt_page('transport_capacity') AND assistant.is_prompt_page('options') AND NOT assistant.is_prompt_page('synthetic_uninstalled_transport');")=='t'
        assert f.record(prepared['booked']['tradeId'],'accept',prepared['acceptance'],prepared['accept_policy'],operation=prepared['acceptance_operation'],context=prepared['acceptance_context'])==prepared['accepted']
        assert f.read(prepared['booked']['tradeId'],context=prepared['custody_old'])['balances']['inCustody']=='720'
        assert f.read(prepared['booked']['tradeId'])['balances']['inCustody']=='450'
        assert timed.read(prepared['timed_old'])['fee']['determinations']==[]
        assert timed.command('fee_determine',prepared['selection'],operation=prepared['determination_operation'],context=prepared['determination_context'])==prepared['determined']
        assert timed.read()['fee']['unpaid']=='-1.88'
        assert prepared['storage_read'](prepared['storage_old'])['opening'] is None
        assert prepared['storage_read']()['planAssessment']==prepared['storage_witness']
        assert prepared['storage_record'](prepared['storage_plan'],prepared['storage_witness'],operation=prepared['storage_operation'])==prepared['stored_plan']
        print('PASS populated capacity upgrade preserves retained title1000, custody720→450/reservation lineage, timed fee/cash, physical opening400/idle plan, old cuts and exact first-outcome recovery.',flush=True)
    main(checks=verify,before_migration=before,fixture_factory=loaded)


if __name__=='__main__':run()
