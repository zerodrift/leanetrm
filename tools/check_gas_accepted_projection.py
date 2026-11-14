"""Source-known accepted gas projection in an owned native fixture."""
import json
from uuid import uuid4

from check_reference_editing import main
from check_gas_owned_holdings import checks as owned_checks
from check_gas_cashout_projection import upgrade_checks as cashout_upgrade


SIGNATURES=('security.configure_gas_owned_projection(uuid,uuid,text,uuid,jsonb,timestamptz,uuid,boolean)',
    'trading.require_gas_cashout_projection(uuid,uuid,jsonb,uuid,timestamptz)')
before={}


def upgrade_checks(query,migration):
    cashout_upgrade(query,migration)
    if migration.name!='0295_gas_accepted_projection.sql':return
    for signature in SIGNATURES:before[signature]=query(f"SELECT oid FROM pg_proc WHERE oid='{signature}'::regprocedure;")
    from check_fact_anchor_upgrade import run_checks
    run_checks(query,migration,number='0295',guard='gas_accepted_projection_upgrade',signatures=SIGNATURES)
    from assistant_context import check_guard
    changes=[]
    for signature in SIGNATURES:
        count=len(signature.split('(',1)[1][:-1].split(','))
        for alteration in (f"proargnames=array_fill('changed'::text,ARRAY[{count}])",'pronargdefaults=1',
            f"proargmodes=array_fill('i'::\"char\",ARRAY[{count}])",'proretset=true','provariadic=23'):
            changes.append((f"UPDATE pg_proc SET {alteration} WHERE oid='{signature}'::regprocedure;",signature))
    check_guard(query,migration.read_text(),'0295','gas_accepted_projection_upgrade',changes)


def accepted_checks(query,manifest,fixture):
    f=json.loads(fixture.read_text());catalog=manifest['catalogId']
    def call(sql):
        token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
            f"'https://preview.invalid','trader','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}COMMIT;")
    for signature in SIGNATURES:
        assert before[signature]==query(f"SELECT oid FROM pg_proc WHERE oid='{signature}'::regprocedure;"),'Upgrade replaced native operation identity'
    source=call(f"SELECT security.capture_date_context('{catalog}','{f['at'][:10]}',NULL);")
    body=dict(profileId=str(uuid4()),expectedRevision=0,implementation='gas_accepted_quantity_lifecycle_projection_v3',
        quantityDisposition='retain_quantity_obligations_v1',effectiveAt=f['at'],performances=[],cashouts=[])
    def configure(selected,operation=None,context=None):
        current=context or call(f"SELECT security.capture_date_context('{catalog}','{f['at'][:10]}',NULL);")
        literal="'"+json.dumps(selected).replace("'","''")+"'::jsonb"
        return json.loads(call(f"SELECT security.configure_gas_owned_projection('{catalog}','{operation or uuid4()}','projection_profile','{f['accountId']}',"
            f"{literal},'{f['at']}','{current}',false)::text;"))
    operation=str(uuid4());result=configure(body,operation,source)
    assert result['changed'] and result['currentRevision']==1,result
    assert configure(body,operation,source)==result,'Retry changed its first native outcome'
    publication=query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")
    same=configure(body)
    assert not same['changed'] and same['acknowledgedRevision']==1 and same['currentRevision']==1,same
    assert query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")==publication
    changed=configure({**body,'expectedRevision':1,'quantityDisposition':'perform_full_quantity_at_contractual_due_v1'})
    assert changed['changed'] and changed['currentRevision']==2,changed
    publication=query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")
    older=configure(body)
    assert not older['changed'] and older['acknowledgedRevision']==1 and older['currentRevision']==2,older
    assert query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")==publication
    for invalid in ({k:v for k,v in body.items()if k!='cashouts'},
        {**body,'profileId':str(uuid4()),'cashouts':None},
        {**body,'profileId':str(uuid4()),'implementation':'gas_quantity_and_cashout_lifecycle_projection_v2'}):
        try:configure(invalid)
        except RuntimeError as error:assert 'gas_owned_input' in str(error) or 'gas_owned_projection_policy' in str(error),str(error)
        else:raise AssertionError('A missing policy choice or empty v2 cashout model was accepted')
    print('PASS explicit accepted-quantity projection profile preserves an empty cashout choice',flush=True)


def checks(query,manifest):owned_checks(query,manifest,economic_worker=accepted_checks)


if __name__=='__main__':main(checks=checks,before_migration=upgrade_checks)
