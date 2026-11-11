"""Power graph and close admission against two exact recorded input sources."""
from copy import deepcopy
import json
from pathlib import Path
import subprocess
from uuid import uuid4

from close_session_fixture import sql_session
from preview_client import PreviewClient,initial_frame


def run_checks(url,query,manifest,dotnet,temporary,environment):
    client=PreviewClient(url);client.bootstrap();client.call('profile',{'profile':'approver'})
    frame=initial_frame(client.call('workspace'),'2020-12-18')
    catalog=manifest['catalogId'];base=manifest['revisionId'];other=str(uuid4())
    reference=manifest['referenceCatalogId'];revision=manifest['referenceRevisionId']
    from interval_curve_fixture import PEAK,OFF
    codes=[PEAK,OFF]
    quoted=','.join("'"+c+"'" for c in codes)
    query(f"INSERT INTO market_data.revisions VALUES('{catalog}','{other}','{reference}','{revision}','draft',0);INSERT INTO market_data.revision_periods SELECT catalog_id,'{other}',effective_from,effective_to FROM market_data.revision_periods WHERE(catalog_id,revision_id)=('{catalog}','{base}');")
    # Clone only the two current native consumers. Identical logical curve codes
    # in sourceA and sourceB deliberately carry different observations.
    for table,condition in [('contracts',f"id IN(SELECT contract_id FROM market_data.curve_members WHERE(catalog_id,revision_id)=('{catalog}','{base}') AND curve_code IN({quoted}))"),('curves',f"code IN({quoted})"),('curve_members',f"curve_code IN({quoted})"),('series',f"contract_id IN(SELECT contract_id FROM market_data.curve_members WHERE(catalog_id,revision_id)=('{catalog}','{base}') AND curve_code IN({quoted}))"),('curve_blocks',f"curve_code IN({quoted})")]:
        columns=json.loads(query(f"SELECT json_agg(attname ORDER BY attnum) FROM pg_attribute WHERE attrelid='market_data.{table}'::regclass AND attnum>0 AND NOT attisdropped AND attgenerated='';"))
        names=','.join('"'+c+'"' for c in columns);projection=','.join("'"+other+"'::uuid" if c=='revision_id' else '"'+c+'"' for c in columns)
        query(f"INSERT INTO market_data.{table}({names}) SELECT {projection} FROM market_data.{table} WHERE(catalog_id,revision_id)=('{catalog}','{base}') AND {condition};")
    query(f"UPDATE market_data.revisions SET state='sealed' WHERE(catalog_id,id)=('{catalog}','{other}');")
    columns=json.loads(query("SELECT json_agg(attname ORDER BY attnum) FROM pg_attribute WHERE attrelid='market_data.observations'::regclass AND attnum>0 AND NOT attisdropped AND attgenerated='';"))
    projection=','.join("'"+other+"'::uuid" if c=='definition_revision_id' else 'gen_random_uuid()' if c=='id' else "'secondary_'||source_event_id" if c=='source_event_id' else 'value+10' if c=='value' else '"'+c+'"' for c in columns)
    query(f"INSERT INTO market_data.observations({','.join(columns)}) SELECT {projection} FROM market_data.observations WHERE(catalog_id,definition_revision_id)=('{catalog}','{base}') AND series_id IN(SELECT id FROM market_data.series WHERE(catalog_id,revision_id)=('{catalog}','{other}'));")
    def runtime(sql):
        token=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','approver','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',120);")
        return query(f"BEGIN;SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token}');{sql}COMMIT;")
    def literal(v):return "'"+json.dumps(v).replace("'","''")+"'::jsonb"
    current=json.loads(runtime(f"SELECT security.read_current_catalogue('{catalog}',NULL)::text;"))
    definition={k:current[k] for k in ('effectiveFrom','effectiveTo','curves','roles')}
    for curve in definition['curves']:
        if curve['code'] in codes:curve['definitionRevisionId']=other
    runtime(f"SELECT security.publish_catalogue('{catalog}','{uuid4()}','{current['id']}',{literal(definition)});")
    session='synthetic_power_sources';sql_session(query,manifest,session,'test_utc_every_day','16:00',codes,['approver','trader'],curve_sources={code:other for code in codes})
    selected=','.join("'"+p+"'" for p in frame['observationIds'])
    pins=json.loads(query(f"SELECT json_agg(json_build_object('definitionRevisionId',definition_revision_id,'seriesId',series_id,'observationId',id,'inputUse',CASE WHEN role IN('final_contract_settlement','index_fixing') THEN 'frozen' ELSE 'variable' END)) FROM market_data.observations WHERE catalog_id='{catalog}' AND(id IN({selected}) OR(definition_revision_id='{other}' AND EXISTS(SELECT 1 FROM market_data.observations original WHERE original.id IN({selected}) AND observations.series_id=original.series_id AND observations.source_event_id='secondary_'||original.source_event_id AND observations.source_revision_id=original.source_revision_id)));"))
    members=[dict(definitionRevisionId=base,curve='future',contractId=c,ordinal=i,scope='retained') for i,c in enumerate(frame['curveContracts']['future'])]
    for code in codes:members.extend(dict(definitionRevisionId=other,curve=code,contractId=c,ordinal=i,scope='root') for i,c in enumerate(frame['curveContracts'][code]))
    def record(code,rows):return runtime(f"SELECT security.register_market_close('{catalog}','{base}','{uuid4()}','Synthetic power source check','{code}','2020-12-18','{frame['at']}',{literal(pins)},{literal(rows)});")
    first=record(session,members)
    client.call('workspace');known=client.call('interval-series')['knownAt']
    product=next(p for p in client.call('custom-curves')['powerProducts'] if p['code']=='pjm')
    node=lambda id,kind,parameters=None,inputs=None:dict(id=id,kind=kind,parameters=parameters or {},inputs=inputs or {})
    recipe=dict(code='synthetic_source_power',name='Synthetic exact source power',output='result',nodes=[node('peak','source',dict(curve=codes[0])),node('off','source',dict(curve=codes[1])),node('hours','power_intervals',dict(implementation='flat_forward_only_v1',product=product['id'],productVersion=str(product['version']),profile='test_hourly_eastern',profileMapping='exact_native_grid_terms_v1',market='DA',**{'from':'2031-11-01','to':'2031-12-01'}),dict(peak='peak',offPeak='off')),node('result','output',inputs=dict(values='hours'))])
    result=client.call('custom-curves/preview',dict(definition=recipe,snapshotId=first))
    assert len(result['rows'])==721
    assert sum(r[2]=='70' for r in result['rows'])==304 and sum(r[2]=='50' for r in result['rows'])==417,result['rows'][:2]
    assert result['trace']['definitionRevision']['revisionId']==other
    omitted='synthetic_power_omitted';sql_session(query,manifest,omitted,'test_utc_every_day','16:00',['future'],['approver','trader'])
    primary_members=[dict(definitionRevisionId=base,curve='future',contractId=c,ordinal=i,scope='root') for i,c in enumerate(frame['curveContracts']['future'])]
    old_pins=pins;pins=[p for p in pins if p['definitionRevisionId']==base]
    missing=record(omitted,primary_members);pins=old_pins
    client.call('workspace')
    assert client.call('custom-curves/preview',dict(definition=recipe,snapshotId=missing),expected=422)['code']=='market_inputs_missing'
    # A new close with unchanged OLD observations is nevertheless after `known`.
    # Change only an optional retained primary member to create a new close fact.
    assert len(frame['curveContracts']['future'])>1
    changed=[r for r in members if not(r['definitionRevisionId']==base and r['ordinal']==len(frame['curveContracts']['future'])-1)]
    later=record(session,changed);assert later!=first
    client.call('workspace');latest=client.call('interval-series')['knownAt']
    token=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','trader','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',120);")
    connection=f"Host={temporary};Port=55439;Database=postgres;Username=preview_runtime;Pooling=false"
    path=Path(temporary)/'power-context.json';path.write_text(json.dumps(dict(manifest=manifest,connection=connection,token=token,first=first,later=later,known=known,latest=latest)))
    dll=Path(__file__).resolve().parents[1]/'tests/OpenEtrm.Api.Checks/bin/Debug/net10.0/OpenEtrm.Api.Checks.dll'
    subprocess.run([str(dotnet),str(dll),'--power-close-context',str(path)],env=environment,check=True,timeout=45)
    print('PASS power sources: non-primary recorded70/50 versus primary60/40,721hours, omitted source refusal and explicit close knowledge qualification',flush=True)
