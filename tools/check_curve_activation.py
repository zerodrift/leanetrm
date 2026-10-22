"""Effective custom-curve versions under certified synthetic knowledge contexts."""
from copy import deepcopy
from concurrent.futures import ThreadPoolExecutor
import json
from uuid import uuid4

from preview_client import PreviewClient, initial_frame


POLICY = 'complete_effective_timeline_v1'


def run_checks(url, manifest, query):
    client = PreviewClient(url); client.bootstrap(); client.call('profile', {'profile': 'editor'})
    frame = initial_frame(client.call('workspace'), '2020-12-18')
    definition = deepcopy(next(t['definition'] for t in client.call('custom-curves')['templates'] if t['code'] == 'ng_prompt'))
    definition.update(code='activation_curve', name='Synthetic effective curve')
    first = client.call('custom-curves/save', {'requestId': str(uuid4()), 'snapshotId': frame['id'], 'definition': definition, 'layout': {}})
    client.call('profile', {'profile': 'approver'})
    first = client.call('custom-curves/publish', {'requestId': str(uuid4()), 'id': first['id'], 'version': first['version'], 'generation': first['generation']})
    client.call('profile', {'profile': 'editor'})
    for node in definition['nodes']:
        if node['kind'] == 'select': node['parameters']['first'] = '2'
    second = client.call('custom-curves/save', {'requestId': str(uuid4()), 'snapshotId': frame['id'], 'definition': definition, 'layout': {},
        'id': first['id'], 'version': first['version'], 'generation': first['generation'], 'expectedState': first['state']})
    client.call('profile', {'profile': 'approver'})
    second = client.call('custom-curves/publish', {'requestId': str(uuid4()), 'id': second['id'], 'version': second['version'], 'generation': second['generation']})
    segment = {'effectiveFrom': '2020-12-01', 'effectiveTo': '2021-01-01', 'version': 1, 'outputNode': definition['output']}
    request = {'requestId': str(uuid4()), 'curveId': first['id'], 'expectedRevision': 0, 'policy': POLICY,
        'reason': 'Synthetic initial activation', 'segments': [segment]}
    omitted={k:v for k,v in request.items() if k!='expectedRevision'}
    assert client.call('custom-curves/activation',omitted,expected=400)['code']=='request_format'
    assert client.call('custom-curves/activation',{**request,'segments':[{k:v for k,v in segment.items() if k!='effectiveFrom'}]},expected=400)['code']=='request_format'
    assert query(f"SELECT count(*) FROM market_data.custom_curve_activation_plans WHERE curve_id='{first['id']}';")=='0'
    published = client.call('custom-curves/activation', request)
    assert published['revision'] == 1 and published['segments'] == [segment]
    assert client.call('custom-curves/activation', request) == published
    client.call('custom-curves/activation', {**request, 'reason': 'Different request'}, expected=409)
    client.call('custom-curves/activation', {**request, 'requestId': str(uuid4())}, expected=409)
    selected = {'curveId': first['id'], 'snapshotId': frame['id'], 'policy': POLICY}
    before = client.call('custom-curves/effective-preview', selected)
    assert before['recipe']['version'] == 1 and before['activation']['revision'] == 1
    cutoff = before['preview']['context']['knownAt']
    revised = {**request, 'requestId': str(uuid4()), 'expectedRevision': 1,
        'reason': 'Synthetic correction of effective structure', 'segments': [{**segment, 'version': 2}]}
    client.call('custom-curves/activation', revised)
    after = client.call('custom-curves/effective-preview', selected)
    assert after['recipe']['version'] == 2 and after['activation']['revision'] == 2
    assert after['preview']['rows'] != before['preview']['rows']
    client.call('market/context', {'effectiveOn': '2020-12-18', 'knownAt': cutoff, 'mode': 'at', 'selection': 'latest_correction'})
    historical = initial_frame(client.call('workspace/navigation'), '2020-12-18')
    retained = client.call('custom-curves/effective-preview', {**selected, 'snapshotId': historical['id']})
    assert retained['recipe']['version'] == 1 and retained['preview']['rows'] == before['preview']['rows']
    client.call('custom-curves/activation', {**revised, 'requestId': str(uuid4()), 'expectedRevision': 2}, expected=422)
    client.call('market/current', {})
    for segments in ([segment, segment], [{**segment, 'version': 64}], [{**segment, 'outputNode': 'missing'}]):
        client.call('custom-curves/activation', {**revised, 'requestId': str(uuid4()), 'expectedRevision': 2, 'segments': segments}, expected=422)
    client.call('custom-curves/activation', {**revised, 'requestId': str(uuid4()), 'expectedRevision': 2, 'policy': 'latest'}, expected=422)
    client.call('custom-curves/activation', {**revised, 'requestId': str(uuid4()), 'expectedRevision': 2, 'segments': []})
    missing = client.call('custom-curves/effective-preview', selected, expected=422)
    assert missing['code'] == 'curve_activation_missing', missing
    # An inactive latest timeline must not resurrect an earlier covering plan.
    exact = client.call('custom-curves/preview', {'snapshotId': frame['id'], 'id': first['id'], 'version': 1, 'definition': first['definition']})
    assert exact['rows'] == before['preview']['rows']
    client.call('profile', {'profile': 'viewer'})
    client.call('custom-curves/activation', {**revised, 'requestId': str(uuid4()), 'expectedRevision': 3}, expected=403)
    assert query(f"SELECT count(*) FROM market_data.custom_curve_activation_plans WHERE curve_id='{first['id']}';") == '3'
    # A separately sealed source bundle deliberately reuses the same native code
    # with different observations. An activated exact recipe must not read that
    # code from the header's other revision.
    catalog, original_revision = manifest['catalogId'], manifest['revisionId']
    other_revision, other_curve = str(uuid4()), str(uuid4())
    token = query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','editor','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',120);")
    source = next(n['parameters']['curve'] for n in first['definition']['nodes'] if n['kind'] == 'source')
    query(f"""BEGIN;
      INSERT INTO market_data.revisions SELECT(jsonb_populate_record(NULL::market_data.revisions,to_jsonb(r)||jsonb_build_object('id','{other_revision}','state','draft','generation',0))).* FROM market_data.revisions r WHERE(catalog_id,id)=('{catalog}','{original_revision}');
      INSERT INTO market_data.contracts SELECT(jsonb_populate_record(NULL::market_data.contracts,to_jsonb(k)||jsonb_build_object('revision_id','{other_revision}'))).* FROM market_data.contracts k WHERE(k.catalog_id,k.revision_id)=('{catalog}','{original_revision}') AND k.id IN(SELECT contract_id FROM market_data.curve_members WHERE(catalog_id,revision_id,curve_code)=('{catalog}','{original_revision}','{source}'));
      INSERT INTO market_data.series SELECT(jsonb_populate_record(NULL::market_data.series,to_jsonb(s)||jsonb_build_object('revision_id','{other_revision}'))).* FROM market_data.series s WHERE(s.catalog_id,s.revision_id)=('{catalog}','{original_revision}') AND EXISTS(SELECT 1 FROM market_data.contracts k WHERE(k.catalog_id,k.revision_id,k.id)=('{catalog}','{other_revision}',s.contract_id));
      INSERT INTO market_data.curves SELECT(jsonb_populate_record(NULL::market_data.curves,to_jsonb(c)||jsonb_build_object('revision_id','{other_revision}'))).* FROM market_data.curves c WHERE(catalog_id,revision_id,code)=('{catalog}','{original_revision}','{source}');
      INSERT INTO market_data.curve_members SELECT catalog_id,'{other_revision}',curve_code,contract_id,ordinal FROM market_data.curve_members WHERE(catalog_id,revision_id,curve_code)=('{catalog}','{original_revision}','{source}');
      INSERT INTO market_data.revision_periods SELECT catalog_id,'{other_revision}',effective_from,effective_to FROM market_data.revision_periods WHERE(catalog_id,revision_id)=('{catalog}','{original_revision}');
      UPDATE market_data.revisions SET state='sealed' WHERE(catalog_id,id)=('{catalog}','{other_revision}');
      INSERT INTO market_data.observations SELECT(jsonb_populate_record(NULL::market_data.observations,to_jsonb(o)||jsonb_build_object('id',gen_random_uuid(),'definition_revision_id','{other_revision}','source_event_id',gen_random_uuid()::text,'source_revision_id','1','received_at',clock_timestamp(),'corrects_id',NULL,'value',o.value+20))).* FROM market_data.observations o WHERE(o.catalog_id,o.definition_revision_id)=('{catalog}','{original_revision}') AND EXISTS(SELECT 1 FROM market_data.series s WHERE(s.catalog_id,s.revision_id,s.id)=('{catalog}','{other_revision}',o.series_id));
      SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token}');
      INSERT INTO market_data.custom_curves VALUES('{catalog}','{other_curve}','activation_other_bundle');
      INSERT INTO market_data.custom_curve_versions(catalog_id,curve_id,version,generation,state,name,output_node,engine_version,layout) SELECT catalog_id,'{other_curve}',1,1,'draft','Synthetic other bundle',output_node,engine_version,layout FROM market_data.custom_curve_versions WHERE(catalog_id,curve_id,version)=('{catalog}','{first['id']}',1);
      INSERT INTO market_data.custom_curve_nodes SELECT catalog_id,'{other_curve}',version,node_id,kind,parameters FROM market_data.custom_curve_nodes WHERE(catalog_id,curve_id,version)=('{catalog}','{first['id']}',1);
      INSERT INTO market_data.custom_curve_edges SELECT catalog_id,'{other_curve}',version,node_id,port,source_node FROM market_data.custom_curve_edges WHERE(catalog_id,curve_id,version)=('{catalog}','{first['id']}',1);
      INSERT INTO market_data.custom_curve_node_sources SELECT catalog_id,'{other_curve}',version,node_id,source_catalog_id,'{other_revision}',source_curve_code FROM market_data.custom_curve_node_sources WHERE(catalog_id,curve_id,version)=('{catalog}','{first['id']}',1);
      COMMIT;""")
    client.call('profile', {'profile': 'approver'})
    publisher = query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','approver','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',120);")
    query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{publisher}');SELECT security.publish_custom_curve('{catalog}','{other_curve}',1,1);COMMIT;")
    client.call('custom-curves/activation', {**request, 'requestId': str(uuid4()), 'curveId': other_curve})
    mismatch = client.call('custom-curves/effective-preview', {**selected, 'curveId': other_curve}, expected=422)
    assert mismatch['code'] == 'custom_curve_definition', mismatch
    foreign_catalog, foreign_curve = str(uuid4()), str(uuid4())
    authentication = query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','approver','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',120);")
    accepted=[]
    def reject_sql(sql, message, marker=None):
        try:query(sql)
        except RuntimeError as error:
            assert message in str(error) and (marker is None or marker in str(error)), str(error)
        else:accepted.append(message+(' '+marker if marker else ''))
    reject_sql(f"""BEGIN;
      INSERT INTO market_data.catalogs VALUES('{foreign_catalog}','synthetic_activation_other');
      INSERT INTO security.market_catalog_scopes VALUES('{foreign_catalog}','{manifest['tenantId']}','{manifest['workspaceId']}');
      INSERT INTO market_data.custom_curves VALUES('{foreign_catalog}','{foreign_curve}','synthetic_empty_activation');
      INSERT INTO market_data.custom_curve_activation_plans SELECT '{foreign_catalog}','{foreign_curve}',1,principal_id,gen_random_uuid(),
       '{{"policy":"complete_effective_timeline_v1","reason":"Synthetic foreign empty timeline","segments":[]}}'::jsonb,1,pg_current_xact_id()
       FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='approver';
      SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{authentication}');
      SELECT security.use_market_context('{catalog}','{before['preview']['context']['contextId']}');
      DO $$BEGIN IF NOT security.market_access('{foreign_catalog}','ReadMarketData') THEN RAISE EXCEPTION 'probe_not_authorized';END IF;
       PERFORM security.read_custom_curve_activation('{foreign_catalog}','{foreign_curve}');END$$;ROLLBACK;""",'curve_activation_denied')
    replay=json.loads(query(f"SELECT jsonb_build_object('operation',request_id,'payload',payload) FROM market_data.custom_curve_activation_plans WHERE curve_id='{first['id']}' AND revision=3;"))
    for is_replay in (False,True):
        role=str(uuid4())
        authentication=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','approver','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',120);")
        operation=replay['operation'] if is_replay else str(uuid4())
        payload=json.dumps(replay['payload'] if is_replay else {'policy':POLICY,'reason':'Synthetic expiry probe','segments':[]})
        expected=2 if is_replay else 3
        reject_sql(f"""BEGIN;
          INSERT INTO security.roles VALUES('{role}','synthetic_activation_read','Synthetic activation read');
          INSERT INTO security.role_permissions VALUES('{role}','ReadMarketData');
          INSERT INTO security.role_grants SELECT gen_random_uuid(),principal_id,'{role}','{manifest['tenantId']}','{manifest['workspaceId']}',clock_timestamp(),'9999-12-31',false FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='approver';
          ALTER FUNCTION security.read_custom_curve_activation(uuid,uuid,integer) RENAME TO read_activation_before_expiry_probe;
          CREATE FUNCTION security.read_custom_curve_activation(catalog uuid,curve uuid,requested_revision integer DEFAULT NULL) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
          DECLARE assembled jsonb;until_at timestamptz;
          BEGIN
           assembled=security.read_activation_before_expiry_probe(catalog,curve,requested_revision);
           IF assembled IS NULL OR NOT security.market_access(catalog,'SealMarketDefinitions') THEN RAISE EXCEPTION 'expiry_probe_not_admitted';END IF;
           SELECT max(granted_to) INTO until_at FROM security.role_grants WHERE principal_id=(SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='approver') AND role_id<>'{role}' AND NOT revoked;
           PERFORM pg_sleep(greatest(0,extract(epoch FROM until_at-clock_timestamp()))+0.05);
           IF security.market_access(catalog,'SealMarketDefinitions') OR NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'expiry_probe_wrong_authority';END IF;
           RAISE NOTICE 'synthetic_activation_response_assembled_after_seal_expiry';RETURN assembled;
          END$$;
          ALTER FUNCTION security.read_custom_curve_activation(uuid,uuid,integer) OWNER TO openetrm_security_owner;
          REVOKE ALL ON FUNCTION security.read_custom_curve_activation(uuid,uuid,integer) FROM PUBLIC;
          GRANT EXECUTE ON FUNCTION security.read_custom_curve_activation(uuid,uuid,integer) TO openetrm_runtime;
          UPDATE security.role_grants SET granted_to=clock_timestamp()+interval '1 second' WHERE principal_id=(SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='approver') AND role_id<>'{role}' AND NOT revoked;
          SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{authentication}');
          SELECT security.publish_custom_curve_activation('{catalog}','{first['id']}','{operation}',{expected},$contract${payload}$contract$::jsonb);ROLLBACK;""",
            'curve_activation_denied','synthetic_activation_response_assembled_after_seal_expiry')
    authentication=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','approver','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',120);")
    reject_sql(f"""BEGIN;
      ALTER FUNCTION security.calculation_pre_activation_head(uuid) RENAME TO calculation_before_expiry_probe;
      CREATE FUNCTION security.calculation_pre_activation_head(catalog uuid) RETURNS text LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
      DECLARE assembled text;until_at timestamptz;
      BEGIN
       assembled=security.calculation_before_expiry_probe(catalog);
       IF assembled IS NULL OR NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'head_probe_not_admitted';END IF;
       SELECT max(granted_to) INTO until_at FROM security.role_grants WHERE principal_id=(SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='approver') AND NOT revoked;
       PERFORM pg_sleep(greatest(0,extract(epoch FROM until_at-clock_timestamp()))+0.05);
       IF security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'head_probe_wrong_authority';END IF;
       RAISE NOTICE 'synthetic_activation_head_assembled_after_read_expiry';RETURN assembled;
      END$$;
      ALTER FUNCTION security.calculation_pre_activation_head(uuid) OWNER TO openetrm_security_owner;
      REVOKE ALL ON FUNCTION security.calculation_pre_activation_head(uuid) FROM PUBLIC;
      UPDATE security.role_grants SET granted_to=clock_timestamp()+interval '1 second' WHERE principal_id=(SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='approver') AND NOT revoked;
      SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{authentication}');
      SELECT security.calculation_head('{catalog}');ROLLBACK;""",'curve_activation_denied','synthetic_activation_head_assembled_after_read_expiry')
    for field in ('effectiveFrom','effectiveTo','outputNode'):
        authentication=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','approver','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',120);")
        payload=json.dumps({'policy':POLICY,'reason':'Synthetic malformed boundary','segments':[{**segment,field:None}]})
        reject_sql(f"BEGIN;SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{authentication}');SELECT security.publish_custom_curve_activation('{catalog}','{first['id']}','{uuid4()}',3,$contract${payload}$contract$::jsonb);ROLLBACK;",'curve_activation_input')
    assert not accepted, 'Accepted invalid activation boundaries: '+repr(accepted)
    contenders=[]
    for _ in range(2):
        contender=PreviewClient(url);contender.bootstrap();contender.call('profile',{'profile':'approver'});contenders.append(contender)
    boundary={**segment,'effectiveFrom':'2020-12-18','effectiveTo':'2020-12-19'}
    def compete(contender):
        try:return contender.call('custom-curves/activation',{**request,'requestId':str(uuid4()),'expectedRevision':3,'segments':[boundary]})
        except AssertionError as error:
            assert 'got 409:' in str(error) and 'curve_activation_conflict' in str(error),str(error)
            return None
    with ThreadPoolExecutor(max_workers=2) as executor:winners=list(executor.map(compete,contenders))
    assert sum(result is not None for result in winners)==1 and next(result for result in winners if result)['revision']==4
    assert client.call('custom-curves/effective-preview',selected)['recipe']['version']==1
    end_frame=initial_frame(client.call('workspace/navigation'),'2020-12-19')
    assert client.call('custom-curves/effective-preview',{**selected,'snapshotId':end_frame['id']},expected=422)['code']=='curve_activation_missing'
    print('PASS curve activation: exact effective/known selection, immutable pins, gaps, optimistic/idempotent writes and current authority', flush=True)
