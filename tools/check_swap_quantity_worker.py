"""Exact clocked quantity bounds and worker authority in the owned power fixture."""
import json
from datetime import datetime
from uuid import uuid4
from preview_client import PreviewClient, initial_frame
from check_blotter_valuation_status import risk_catalog, risk_request


def run(url, query, manifest, at, trade):
    client=PreviewClient(url);client.bootstrap();client.call('profile',{'profile':'risk'})
    frame=initial_frame(client.call('workspace'),at[:10])
    assert frame['at']==at
    context={'snapshotId':frame['id'],'knowledgeMode':'current','convention':'trading_workspace_v1'}
    choices=risk_catalog(client,context)
    retained={**context,'knowledgeMode':'retained','contextId':choices['context']['contextId']}
    request=risk_request(choices,retained,choices['factors'][0]['factor'])
    job=client.call('portfolio/risk/jobs',{'requestId':str(uuid4()),'request':request})
    engine=query(f"SELECT request->>'engineVersion' FROM risk.jobs WHERE id='{job['id']}';")
    claim=json.loads(query(f"SET SESSION AUTHORIZATION preview_risk_worker;SELECT risk.claim(30,'{engine}');"))
    assert claim['jobId']==job['id']
    catalog=manifest['catalogId'];reference=manifest['referenceCatalogId']
    reader=f"security.read_swap_power_quantity_intervals('{catalog}','{trade}',1)"
    bind=f"BEGIN;SET SESSION AUTHORIZATION preview_risk_worker;SELECT risk.bind('{job['id']}','{claim['leaseToken']}');"
    bounds=f"""SELECT jsonb_agg(to_jsonb(b) ORDER BY slot,period) FROM(
     SELECT (value->>'slot')::integer slot,(value->>'period')::integer period,
      min((value->>'start')::timestamptz) starts,max((value->>'end')::timestamptz) ends,count(*) hours
     FROM jsonb_array_elements({reader}) GROUP BY 1,2)b;"""
    actual=json.loads(query(bind+bounds+'ROLLBACK;').splitlines()[-1])
    expected=[(slot,period,start,end,hours) for slot in (0,1) for period,start,end,hours in (
        (0,'2021-09-30T23:00:00+00:00','2021-11-01T00:00:00+00:00',745),
        (1,'2021-11-01T00:00:00+00:00','2021-12-01T00:00:00+00:00',720))]
    assert [(r['slot'],r['period'],datetime.fromisoformat(r['starts']),datetime.fromisoformat(r['ends']),r['hours']) for r in actual]==[
        (slot,period,datetime.fromisoformat(start),datetime.fromisoformat(end),hours) for slot,period,start,end,hours in expected],actual

    def denied(sql, expected='42501'):
        try:query('\\set VERBOSITY verbose\n'+sql)
        except RuntimeError as error:
            assert expected in str(error),str(error)
            return
        raise AssertionError('Unqualified worker quantity read succeeded')
    denied(f'SET SESSION AUTHORIZATION preview_risk_worker;SELECT {reader};')
    denied(bind+f"RESET SESSION AUTHORIZATION;UPDATE risk.jobs SET lease_token=gen_random_uuid() WHERE id='{job['id']}';SET SESSION AUTHORIZATION preview_risk_worker;SELECT {reader};ROLLBACK;")
    denied(bind+f"RESET SESSION AUTHORIZATION;UPDATE risk.jobs SET lease_expires_at=clock_timestamp()-interval '1 second' WHERE id='{job['id']}';SET SESSION AUTHORIZATION preview_risk_worker;SELECT {reader};ROLLBACK;")
    denied(bind+f"RESET SESSION AUTHORIZATION;UPDATE risk.jobs SET state='cancellation_requested' WHERE id='{job['id']}';SET SESSION AUTHORIZATION preview_risk_worker;SELECT {reader};ROLLBACK;", 'risk_job_cancelled')
    denied(bind+f"RESET SESSION AUTHORIZATION;DELETE FROM security.reference_catalog_scopes WHERE(catalog_id,tenant_id,workspace_id)=('{reference}','{manifest['tenantId']}','{manifest['workspaceId']}');SET SESSION AUTHORIZATION preview_risk_worker;SELECT {reader};ROLLBACK;")
    for role in ('openetrm_runtime','openetrm_risk_worker'):
        assert query(f"SELECT has_function_privilege('{role}','security.read_swap_power_quantity_intervals_pre_worker(uuid,uuid,integer)','EXECUTE');")=='f'
    client.call('portfolio/risk/jobs/'+job['id']+'/cancel',{})
    assert query(f"SET SESSION AUTHORIZATION preview_risk_worker;SELECT risk.acknowledge_cancel('{job['id']}','{claim['leaseToken']}');")=='cancelled'
    print('PASS retained quantity worker: exact DST bounds, bound current authority, stale-token/reference refusal and private reader fencing',flush=True)
