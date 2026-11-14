"""Contract observation terms stored on custom curve versions, in an owned synthetic preview.

The recipe is saved and published through the API; every database rule is then checked by direct SQL under the
runtime role, independently of the API's own validation."""
from copy import deepcopy
from uuid import uuid4
from preview_client import PreviewClient

TERMS=[{'ordinal':0,'group':'average','weight':'1','position':1,'lessPosition':None,'period':'calendar_month_of_delivery_v1'},
       {'ordinal':1,'group':'roll','weight':'2','position':1,'lessPosition':2,'period':'trade_month_of_delivery_contract_v1'},
       {'ordinal':2,'group':'roll','weight':'1','position':1,'lessPosition':3,'period':'trade_month_of_delivery_contract_v1'}]
# The custom-curve fixture's monthly source holds 2020-11 through 2022-05, so 2021-01 has a prior contract and two later ones.
PARAMETERS={'implementation':'contract_observation_terms_v1','targetFrom':'2021-01-01','targetCount':'1','days':'calendar_days_v1',
            'roll':'contract_expiry_roll_v1','sampling':'context_utc_time','corrections':'exact_context_frame_history_latest_visible'}
DEFINITION={'code':'synthetic_observed','name':'Synthetic contract observations','output':'output','nodes':[
    {'id':'source','kind':'source','parameters':{'curve':'ng_monthly'},'inputs':{}},
    {'id':'observed','kind':'contract_observations','parameters':PARAMETERS,'inputs':{'source':'source'},'observationTerms':TERMS},
    {'id':'output','kind':'output','parameters':{},'inputs':{'values':'observed'}}]}


def stored_terms(version):
    return next(node for node in version['definition']['nodes'] if node['id']=='observed')['observationTerms']


def run_checks(url,query,manifest):
    catalog,tenant,workspace=(manifest[key] for key in ('catalogId','tenantId','workspaceId'))
    client=PreviewClient(url)
    client.bootstrap()
    client.call('profile',{'profile':'editor'})
    frame=next(frame for frame in client.call('workspace')['frames'] if frame['at'].startswith('2020-12-18T16:00:00'))
    body={'requestId':str(uuid4()),'snapshotId':frame['id'],'definition':deepcopy(DEFINITION),'layout':{}}
    saved=client.call('custom-curves/save',body)
    curve=saved['id']
    # Every term reads back with identical strings, ordered by ordinal; a term without a second position carries an
    # explicit null, which the required JSON member needs to deserialize at all.
    assert stored_terms(saved)==TERMS,stored_terms(saved)
    assert 'lessPosition' in stored_terms(saved)[0] and stored_terms(saved)[0]['lessPosition'] is None
    assert stored_terms(client.call('custom-curves/save',body))==TERMS,'A replayed save must return the stored terms'
    assert query(f"SELECT string_agg(ordinal||':'||term_group||':'||weight||':'||position||':'||coalesce(less_position::text,'null')||':'||period,',' ORDER BY ordinal) "
                 f"FROM market_data.custom_curve_observation_terms WHERE(catalog_id,curve_id,version,node_id)=('{catalog}','{curve}',1,'observed');")==\
        '0:average:1:1:null:calendar_month_of_delivery_v1,1:roll:2:1:2:trade_month_of_delivery_contract_v1,2:roll:1:1:3:trade_month_of_delivery_contract_v1'
    # Other node kinds carry no terms member at all.
    assert all('observationTerms' not in node for node in saved['definition']['nodes'] if node['id']!='observed')
    client.call('profile',{'profile':'approver'})
    published=client.call('custom-curves/publish',{'requestId':str(uuid4()),'id':curve,'version':1,'generation':1})
    assert published['state']=='published' and stored_terms(published)==TERMS
    listed=next(v for v in client.call('custom-curves')['recipes'] if v['id']==curve)
    assert listed['version']==1 and stored_terms(listed)==TERMS and 'lessPosition' in stored_terms(listed)[0]
    # The node is admitted as a computed-curve combination input, saved and published with its panel weight.
    client.call('profile',{'profile':'editor'})
    combined=deepcopy(DEFINITION);combined['code']='synthetic_observed_panel';combined['name']='Synthetic observed combination'
    combined['nodes'][2]['inputs']['values']='combined'
    combined['nodes'].insert(2,{'id':'combined','kind':'weighted_panels','parameters':{'aggregation':'signed_sum_v1','alignment':'exact_delivery_intervals_v1'},
                                'inputs':{'formula':'observed'},'panelWeights':[{'sourcePort':'formula','weight':'1'}]})
    panel=client.call('custom-curves/save',{'requestId':str(uuid4()),'snapshotId':frame['id'],'definition':combined,'layout':{}})
    assert panel['kind']=='observation_formula' and stored_terms(panel)==TERMS,panel['kind']
    client.call('profile',{'profile':'approver'})
    assert client.call('custom-curves/publish',{'requestId':str(uuid4()),'id':panel['id'],'version':1,'generation':1})['state']=='published'
    client.call('profile',{'profile':'editor'})
    draft=client.call('custom-curves/save',{**body,'requestId':str(uuid4()),'id':curve,'version':1,'generation':1,'expectedState':'published'})
    assert (draft['version'],draft['state'])==(2,'draft') and stored_terms(draft)==TERMS
    print('PASS contract observation storage API: saved, replayed, published and listed terms read back identically, with an explicit null second position',flush=True)

    def token(profile):
        return query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','{profile}','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
    def refused(statement,sqlstate,message=None,setup='',profile='editor',grant=False):
        """Run `statement` under the runtime role after `setup`, in a rolled-back transaction; it must fail with `sqlstate`
        and, when given, the exact exception name or a message containing the table name."""
        match='' if message is None else f"IF SQLERRM<>'{message}' AND strpos(SQLERRM,'\"{message}\"')=0 THEN RAISE;END IF;"
        permission="INSERT INTO security.role_permissions SELECT id,'WriteMarketDefinitions' FROM security.roles WHERE code='market_data_approver';" if grant else ''
        query(f"""BEGIN;{permission}SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token(profile)}');{setup}
            DO $$BEGIN {statement};
              RAISE EXCEPTION 'Expected a refusal' USING ERRCODE='XX000';
            EXCEPTION WHEN SQLSTATE '{sqlstate}' THEN {match}END$$;ROLLBACK;""")
    key=f"'{catalog}','{curve}',2,'observed'"
    def term(ordinal='3',group="'extra'",weight='1',position='1',less='NULL',period="'calendar_month_of_delivery_v1'",node="'observed'",version='2'):
        return (f"INSERT INTO market_data.custom_curve_observation_terms(catalog_id,curve_id,version,node_id,ordinal,term_group,weight,position,less_position,period) "
                f"VALUES('{catalog}','{curve}',{version},{node},{ordinal},{group},{weight},{position},{less},{period})")
    # The table's identity and relationships.
    assert query("SELECT string_agg(a.attname,',' ORDER BY k.n) FROM pg_constraint c CROSS JOIN LATERAL unnest(c.conkey) WITH ORDINALITY k(attnum,n) "
                 "JOIN pg_attribute a ON(a.attrelid,a.attnum)=(c.conrelid,k.attnum) "
                 "WHERE c.conrelid='market_data.custom_curve_observation_terms'::regclass AND c.contype='p';")=='catalog_id,curve_id,version,node_id,ordinal'
    assert query("SELECT string_agg(confrelid::regclass::text,',') FROM pg_constraint WHERE conrelid='market_data.custom_curve_observation_terms'::regclass AND contype='f';")=='market_data.custom_curve_nodes'
    # Security parity with the other node child tables: forced row-level security, the same guards, policies and grants.
    parity="""SELECT c.relrowsecurity AND c.relforcerowsecurity,
        (SELECT string_agg(tgname||':'||tgfoid::regproc::text,',' ORDER BY tgname) FROM pg_trigger WHERE tgrelid=c.oid AND NOT tgisinternal AND tgname IN('guard_custom_curve','agent_draft_write')),
        (SELECT string_agg(concat_ws(':',policyname,permissive,cmd,array_to_string(roles,'+'),qual,with_check),',' ORDER BY policyname) FROM pg_policies WHERE schemaname='market_data' AND tablename=c.relname),
        (SELECT string_agg(grantee||':'||privilege_type,',' ORDER BY grantee,privilege_type) FROM information_schema.role_table_grants WHERE table_schema='market_data' AND table_name=c.relname)
        FROM pg_class c WHERE c.oid='market_data.%s'::regclass;"""
    assert query(parity%'custom_curve_observation_terms')==query(parity%'custom_curve_node_power_intervals'),query(parity%'custom_curve_observation_terms')
    # Each column rule refuses, independently of the API.
    for statement in (term(ordinal='-1'),term(weight='0'),term(weight='-1'),term(weight="'NaN'"),term(position='0'),term(position='121'),
                      term(less='0'),term(less='121'),term(less='1'),term(period="'other_period_v1'"),term(group="'Average'"),term(group="'1average'")):
        refused(statement,'23514')
    refused(term(ordinal='0'),'23505')
    # A term row belongs only to a contract observation node.
    refused(term(node="'source'"),'P0001','recipe_observation_terms')
    refused(term(node="'missing'"),'P0001','recipe_observation_terms')
    refused(f"DELETE FROM market_data.custom_curve_nodes WHERE(catalog_id,curve_id,version,node_id)=({key})",'23503','custom_curve_observation_terms',
            setup=f"DELETE FROM market_data.custom_curve_edges WHERE(catalog_id,curve_id,version)=('{catalog}','{curve}',2);")
    # A published version's term rows cannot change.
    refused(f"UPDATE market_data.custom_curve_observation_terms SET weight=3 WHERE(catalog_id,curve_id,version)=('{catalog}','{curve}',1)",'55000')
    refused(f"DELETE FROM market_data.custom_curve_observation_terms WHERE(catalog_id,curve_id,version)=('{catalog}','{curve}',1)",'55000')
    refused(term(version='1'),'55000')
    # Writing terms needs authoring permission: a publisher sees the draft but cannot change it, and a reader cannot see it.
    refused(term(),'42501',profile='approver')
    refused(term(),'55000',profile='viewer')
    # The rewritten panel guards still refuse a weight on an unlisted input kind: a source feeding a combination.
    refused(f"INSERT INTO market_data.custom_curve_panel_weights VALUES('{catalog}','{curve}',2,'combined','formula','source',1)",'P0001','recipe_panel_weights',
            setup=f"""INSERT INTO market_data.custom_curve_nodes VALUES('{catalog}','{curve}',2,'combined','weighted_panels','{{"aggregation":"signed_sum_v1","alignment":"exact_delivery_intervals_v1"}}');
            INSERT INTO market_data.custom_curve_edges VALUES('{catalog}','{curve}',2,'combined','formula','source');""")
    print('PASS contract observation storage DB: exact key and node relationship, column rules, node-kind admission, security parity, immutable published terms and the panel input-kind guard',flush=True)

    # Publication refuses a malformed node; each draft change is made and published by one authorized approver.
    generation=query(f"SELECT generation FROM market_data.custom_curve_versions WHERE(catalog_id,curve_id,version)=('{catalog}','{curve}',2);")
    publish=f"PERFORM security.publish_custom_curve('{catalog}','{curve}',2,{generation})"
    def node(update):
        return f"UPDATE market_data.custom_curve_nodes SET parameters={update} WHERE(catalog_id,curve_id,version,node_id)=({key});"
    reference=query(f"SELECT c.reference_catalog_id||','||c.reference_revision_id||','||(SELECT min(k.code) FROM ref_data.calendars k WHERE(k.catalog_id,k.revision_id)=(c.reference_catalog_id,c.reference_revision_id)) "
                    f"FROM market_data.curves c JOIN market_data.custom_curve_node_sources s ON(s.source_catalog_id,s.source_revision_id,s.source_curve_code)=(c.catalog_id,c.revision_id,c.code) "
                    f"WHERE(s.catalog_id,s.curve_id,s.version,s.node_id)=('{catalog}','{curve}',2,'source');").split(',')
    calendar=f"INSERT INTO market_data.custom_curve_node_calendars VALUES({key},'{reference[0]}','{reference[1]}','{reference[2]}');"
    business=node("parameters||'{\"days\":\"business_days_v1\"}'")
    def unpublished(setup,message):
        refused(publish,'P0001',message,setup=setup,profile='approver',grant=True)
    unpublished(f"DELETE FROM market_data.custom_curve_observation_terms WHERE(catalog_id,curve_id,version,node_id)=({key});",'recipe_observation_terms')
    unpublished(f"UPDATE market_data.custom_curve_observation_terms SET ordinal=3 WHERE(catalog_id,curve_id,version,node_id,ordinal)=({key},2);",'recipe_observation_terms')
    unpublished(f"INSERT INTO market_data.custom_curve_observation_terms SELECT catalog_id,curve_id,version,node_id,o,term_group,weight,position,less_position,period "
                f"FROM market_data.custom_curve_observation_terms CROSS JOIN generate_series(3,16) o WHERE(catalog_id,curve_id,version,node_id,ordinal)=({key},0);",'recipe_observation_terms')
    unpublished(f"UPDATE market_data.custom_curve_nodes SET kind='mean',parameters='{{\"axis\":\"dates\"}}' WHERE(catalog_id,curve_id,version,node_id)=({key});",'recipe_observation_terms')
    for parameter in PARAMETERS:
        unpublished(node(f"parameters-'{parameter}'"),'recipe_observation_policy')
    for parameter,value in [('implementation','contract_observation_terms_v2'),('roll','explicit_roll_v1'),('sampling','fixed_utc_time'),
                            ('corrections','first_visible'),('days','weekdays_v1'),('targetFrom','2021-01-02'),('targetFrom','2021-13-01'),
                            ('targetFrom','21-01-01'),('targetFrom','1900-12-01'),
                            ('targetCount','0'),('targetCount','121'),('targetCount','one'),('lagDays','1')]:
        unpublished(node(f"parameters||jsonb_build_object('{parameter}','{value}')"),'recipe_observation_policy')
    unpublished(node("parameters||jsonb_build_object('targetCount',1)"),'recipe_observation_policy')
    unpublished(business,'recipe_observation_calendar')
    unpublished(calendar,'recipe_observation_calendar')
    # The same version publishes once the business-day node holds its calendar, and as saved.
    for setup in (business+calendar,''):
        query(f"""BEGIN;INSERT INTO security.role_permissions SELECT id,'WriteMarketDefinitions' FROM security.roles WHERE code='market_data_approver';
            SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token('approver')}');{setup}
            SELECT security.publish_custom_curve('{catalog}','{curve}',2,{generation});ROLLBACK;""")
    print('PASS contract observation publication: refuses missing or gapped terms, every missing or unsupported parameter and a business-day node without exactly its calendar',flush=True)
