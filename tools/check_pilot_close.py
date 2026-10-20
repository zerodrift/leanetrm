"""Pilot close checks in an owned synthetic database; never a host import."""
import json
from uuid import uuid4
from preview_client import PreviewClient


def prepare(query,manifest):
    # Only this new disposable fixture receives the combined operation role.
    # Existing application roles and their permissions remain unchanged.
    role,worker=str(uuid4()),str(uuid4());tenant=manifest['tenantId'];workspace=manifest['workspaceId']
    query(f"""INSERT INTO security.roles(id,code,name) VALUES('{role}','synthetic_pilot_operator','Synthetic pilot operator');
    INSERT INTO security.role_permissions SELECT '{role}',p FROM unnest(ARRAY['RunJobs','ReadMarketData','ReadTrades','RunValuation','RunScenario','ImportObservations','PublishMarketData'])p;
    INSERT INTO security.role_grants SELECT gen_random_uuid(),principal_id,'{role}','{tenant}','{workspace}',clock_timestamp(),'9999-12-31',false
    FROM security.workspace_memberships WHERE(tenant_id,workspace_id,login_handle)=('{tenant}','{workspace}','approver');
    CREATE ROLE pilot_fixture_scheduler LOGIN IN ROLE openetrm_scheduler;
    CREATE ROLE pilot_fixture_runtime LOGIN IN ROLE openetrm_runtime;
    INSERT INTO security.principals VALUES('{worker}','service',true);
    INSERT INTO security.workspace_memberships VALUES('{tenant}','{workspace}','{worker}','pilot_fixture_worker','Synthetic pilot service',true,1);
    INSERT INTO security.role_grants VALUES(gen_random_uuid(),'{worker}','{role}','{tenant}','{workspace}',clock_timestamp(),'9999-12-31',false);
    INSERT INTO automation.workers VALUES('pilot_fixture_scheduler','{worker}','{tenant}','{workspace}','scheduler');
    INSERT INTO automation.pilot_runtime_logins VALUES('pilot_fixture_scheduler','pilot_fixture_runtime','{manifest['catalogId']}');""")
    return worker


def run_checks(url, query, manifest,psql,environment):
    catalog=manifest['catalogId']
    assert query("SELECT to_regclass('market_data.market_closes') IS NOT NULL;")=='t', 'Durable closes are not implemented'
    def client(profile):
        value=PreviewClient(url);value.bootstrap();value.call('profile',{'profile':profile});return value
    prepare(query,manifest)
    editor, approver, viewer=(client(p) for p in ('editor','approver','viewer'))
    workspace=approver.call('workspace')
    assert workspace['frames'], 'The fixture must contain actual retained inputs'
    # A persistent close is registered from the exact selected snapshot. It does
    # not backdate knowledge or import a manifest-specific compatibility branch.
    frame=workspace['frames'][0]
    earlier=client('approver')
    earlier.call('market/context',{'effectiveOn':frame['at'][:10],'knownAt':None,'mode':'current','selection':'latest_correction'})
    body={'requestId':str(uuid4()),'snapshotId':frame['id'],'name':'Synthetic retained close'}
    viewer.call('market/closes/register',body,expected=403)
    editor.call('market/closes/register',body,expected=403)
    result=approver.call('market/closes/register',body)
    assert result==approver.call('market/closes/register',body)
    assert result['kind']=='retained_input' and result['at']==frame['at']
    assert result['id']!=frame['id'], 'Session snapshot identity is not durable close identity'
    bad={**body,'name':'Different replay'}
    approver.call('market/closes/register',bad,expected=409)
    rows=query(f"SELECT count(*) FROM market_data.market_close_pins WHERE catalog_id='{catalog}' AND close_id='{result['id']}';")
    assert int(rows)>0
    query(f"BEGIN;DO $$BEGIN DELETE FROM market_data.market_closes WHERE catalog_id='{catalog}' AND id='{result['id']}';RAISE EXCEPTION 'Expected immutable close';EXCEPTION WHEN SQLSTATE '55000' THEN NULL;END$$;ROLLBACK;")
    print('PASS pilot close: immutable exact registered frame, publishing authority and idempotent request',flush=True)
    choices=approver.call('jobs/pilot-close/catalog')
    assert choices['valuationPolicyChoices'] and choices['scheduleChoices'] and choices['canCreate']
    print('PASS pilot close: authorized current configuration and explicit schedule/valuation choices',flush=True)
    # Durable choices are discovered by an authenticated fresh session, without
    # editing a manifest or restarting the API. Bootstrap cannot leak them.
    fresh=client('viewer')
    assert result['id'] not in {f['id'] for f in fresh.bootstrap()['frames']}
    navigation=fresh.call('workspace/navigation')
    durable=next((f for f in navigation['frames'] if f['id']==result['id']),None)
    assert durable and durable['name']==body['name'] and durable['originCloseId']==result['id'], 'A fresh authorized session did not discover its durable close'
    assert navigation['frameCatalog']['durableSelection']=='exact_recorded_close_pins_v1'
    full=fresh.call('workspace/catalog')
    assert next(f for f in full['frames'] if f['id']==result['id'])['observationIds']==[row['observation_id'] for row in json.loads(query(f"SELECT jsonb_agg(jsonb_build_object('observation_id',observation_id) ORDER BY series_id,input_use) FROM market_data.market_close_pins WHERE close_id='{result['id']}';"))]
    assert result['id'] not in {f['id'] for f in fresh.bootstrap()['frames']}, 'Bootstrap exposed cached durable financial headers without a fresh read check'
    dates=fresh.call('market/dates',{'curve':next(iter(frame['curveContracts'])),'timeZone':'UTC'})
    assert result['id'] in {f['id'] for day in dates['dates'] for f in day['frames']}
    selected=max((saved for saved in full['frames'] if saved['id'] in full['initialFrameIds']),key=lambda saved:saved['at'])
    curve=next(iter(frame['curveContracts']));contract=frame['curveContracts'][curve][0]
    history=fresh.call('history',{'snapshotId':selected['id'],'curve':curve,'contractId':contract})
    stored_point=next((point for point in history['points'] if point['snapshotId']==result['id']),None)
    assert stored_point, 'Current price history omitted an unselected durable close'
    assert len({point['snapshotId'] for point in history['points'] if point['at']==frame['at']})==2, 'Distinct same-instant close identities were merged'
    exact=fresh.call('evaluate',{'snapshotId':result['id'],'curve':curve})
    point=next(point for point in exact['points'] if point['contractId']==contract)
    assert stored_point['value']==point['value'] and set(stored_point['observationIds'])==set(point['lineage'])
    assert result['id'] not in {f.get('originCloseId') for f in earlier.call('workspace/navigation')['frames']}, 'A close published after the certified cutoff leaked into history'
    current=fresh.call('market/context',{'effectiveOn':frame['at'][:10],'knownAt':None,'mode':'current','selection':'latest_correction'})
    retained=fresh.call('workspace/navigation')
    alias=next(f for f in retained['frames'] if f.get('originCloseId')==result['id'])
    assert alias['id']!=result['id'] and retained['context']==current['context']
    price=fresh.call('evaluate',{'snapshotId':alias['id'],'curve':next(iter(frame['curveContracts']))})
    assert price['context']==current['context']
    print('PASS durable close discovery: fresh-session navigation, workspace, calendar, exact historical alias and certified publication cutoff',flush=True)
    from check_market_close_frames import run_checks as check_reader
    check_reader(query,manifest,psql,environment,result['id'],current['context'])
    from check_pilot_frozen_facts import run_checks as check_frozen_facts
    check_frozen_facts(query,manifest,choices)
