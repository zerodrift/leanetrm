"""Qualify exact facility pricing, separate ratchets, and typed activity on an isolated database."""
from check_reference_editing import main
from check_booked_side_programs import runtime_query
from storage_facility_fixture import StorageFacilityFixture
from uuid import uuid4
import json


def checks(query, manifest):
    f=StorageFacilityFixture(query,manifest)
    cat,point=f.catalog,f.point
    facility,inp,outp=[str(uuid4()) for _ in range(3)]
    ports=[dict(portId=id,pipeline=point['pipeline_code'],zone=point['zone_code'],point=point['point_code'],product=point['product_code'],
                direction=direction,contractDirection=sign) for id,direction,sign in [(inp,'inject',1),(outp,'withdraw',-1)]]
    alternate=str(uuid4())
    other=json.loads(query("SELECT to_jsonb(p) FROM ref_data.gas_delivery_conventions p WHERE point_code='synthetic_gas_point_c' LIMIT 1;"))
    ports.append(dict(portId=alternate,pipeline=other['pipeline_code'],zone=other['zone_code'],point=other['point_code'],
                      product=other['product_code'],direction='withdraw',contractDirection=-1))
    version=f.record(dict(kind='define',facilityId=facility,expectedStateVersion=0,expectedPreviousId=None,
        referenceCatalogId=point['catalog_id'],referenceRevisionId=point['revision_id'],facilityCode=point['point_code'],
        commodity=point['product_code'],quantity=point['quantity'],clock=point['clock_code'],minimum='0',maximum='1000',
        implementation='storage_total_physical_step_ratchets_v1',inventoryBasis='total_physical_inventory_v1',
        rateBasis='native_quantity_per_elapsed_hour_v1',simultaneousFlow='separate_injection_withdrawal_v1',lossTreatment='no_loss_v1',
        ports=ports,ratchets=[dict(lower='0',upper='1000',injectionPerHour='10',withdrawalPerHour='9')],evidence='Synthetic facility register'),
        effective='2020-12-01T00:00:00Z')
    version_id=query(f"SELECT id FROM trading.storage_facility_versions WHERE facility_id='{facility}';")
    body=dict(kind='storage',name='River storage',code='river_storage',expectedRevision=0,definitionRevisionId=manifest['revisionId'],
        storageVersionId=version_id,injectionPortId=inp,withdrawalPortId=outp,effectiveAt='2020-12-01T00:00:00Z',
        ratchets=[dict(lower='0',upper='1000',maximumDailyInjection='150',maximumDailyWithdrawal='130')])
    def write(b,op):
        return json.loads(runtime_query(query,manifest,f"SELECT desk.save_profile('{cat}','{op}','{json.dumps(b)}');"))
    op=str(uuid4())
    first=write(body,op)
    assert first==write(body,op)
    alias=str(uuid4())
    assert write(body,alias)==first
    rows=json.loads(runtime_query(query,manifest,f"SELECT desk.profiles('{cat}','{manifest['revisionId']}');"))
    row=next(x for x in rows if x['id']==first['id'])
    assert (row['inputCurve'],row['inputAdjustment'],row['outputCurve'],row['ratchets'][0]['maximumDailyInjection'])==('local_gas','-0.1','local_gas','150'),row
    report=json.loads(runtime_query(query,manifest,f"SELECT desk.facility_activity('{cat}','{first['id']}','2021-01-01','2021-02-01');"))
    assert report['trades']==[] and report['totals']==[]
    assert row['hourlyRatchets'][0]['injectionPerHour']=='10'
    try:write(dict(body,code='wrong',injectionPortId=outp),str(uuid4()))
    except RuntimeError as error:assert 'desk_facility' in str(error)
    else:raise AssertionError('A withdrawal port was accepted for injection.')
    try:write(dict(body,code='cross_clock',withdrawalPortId=alternate),str(uuid4()))
    except RuntimeError as error:assert 'desk_facility' in str(error),error
    else:raise AssertionError('A single gas-day entitlement accepted ports with different day starts')
    future=write(dict(body,expectedRevision=1, name='Future entitlement',effectiveAt='2099-01-01T00:00:00Z'),str(uuid4()))
    assert future['revision']==2 and write(body,alias)==first
    active=json.loads(runtime_query(query,manifest,f"SELECT desk.profiles('{cat}','{manifest['revisionId']}');"))
    assert next(r['id'] for r in active if r['code']==body['code'])==first['id']
    print('PASS exact port pricing and distinct hourly and daily storage limits',flush=True)


if __name__=='__main__':
    main(checks)
