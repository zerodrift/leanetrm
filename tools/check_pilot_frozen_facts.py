"""Qualify exact frozen actual carry using only the caller's owned fixture."""
from copy import deepcopy
from datetime import datetime,timedelta,timezone
import json
from uuid import uuid4


def run_checks(query,manifest,choices):
    catalog,revision=manifest['catalogId'],manifest['revisionId']
    def human_token():return query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','approver','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',120);")
    # The exact sealed publication schedule already exists; only observations
    # invented for this disposable check are added. No market definition changes.
    actual,preliminary=str(uuid4()),str(uuid4())
    query(f"""INSERT INTO market_data.observations
     SELECT catalog_id,'{actual}',revision_id,series_id,reference_catalog_id,reference_revision_id,source_code,
      'synthetic-frozen-carry-final','1','index_fixing','final',fixing_at,clock_timestamp(),3.5,NULL
     FROM market_data.index_fixing_observations WHERE catalog_id='{catalog}' AND revision_id='{revision}'
      AND index_code='test_price' AND observation_from='2021-01-01';
     INSERT INTO market_data.observations SELECT catalog_id,'{preliminary}',definition_revision_id,series_id,
      reference_catalog_id,reference_revision_id,source_code,'synthetic-frozen-carry-preliminary','1',role,'preliminary',
      observed_at,clock_timestamp(),3.6,NULL FROM market_data.observations WHERE catalog_id='{catalog}' AND id='{actual}';""")
    fixed=json.loads(query(f"SELECT jsonb_build_object('seriesId',series_id,'observationId',id,'inputUse','frozen') FROM market_data.observations WHERE catalog_id='{catalog}' AND id='{actual}';"))
    native=json.loads(query(f"""SELECT jsonb_build_object('seriesId',o.series_id,'anchorObservationId',o.id,'moveTicks',0,'value',o.value::text,'contractId',k.id)
     FROM market_data.curve_members m JOIN market_data.contracts k ON(k.catalog_id,k.revision_id,k.id)=(m.catalog_id,m.revision_id,m.contract_id)
     JOIN market_data.curves c ON(c.catalog_id,c.revision_id,c.code)=(m.catalog_id,m.revision_id,m.curve_code)
     JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.contract_id,s.quote_code)=(k.catalog_id,k.revision_id,k.id,c.quote_code)
     JOIN market_data.observations o ON(o.catalog_id,o.definition_revision_id,o.series_id)=(s.catalog_id,s.revision_id,s.id)
     WHERE m.catalog_id='{catalog}' AND m.revision_id='{revision}' AND m.curve_code='future'
      AND k.delivery_from='2021-02-01' AND o.observed_at='2020-12-18T16:00:00Z';"""))
    members=[{'curve':'future','contractId':native.pop('contractId'),'ordinal':0}]
    pins=[{'seriesId':native['seriesId'],'observationId':native['anchorObservationId'],'inputUse':'variable'},fixed]
    def register(candidate,expected=None):
        token=human_token()
        invocation=f"security.register_market_close('{catalog}','{revision}','{uuid4()}','Synthetic exact frozen facts','2021-01-01T08:00:00Z','{json.dumps(candidate)}'::jsonb,'{json.dumps(members)}'::jsonb)"
        if expected:
            statement=f"DO $$BEGIN PERFORM {invocation};RAISE EXCEPTION 'Unqualified actual was registered';EXCEPTION WHEN SQLSTATE 'P0001' THEN IF SQLERRM<>'{expected}' THEN RAISE;END IF;END$$;"
        else:statement=f'SELECT {invocation};'
        result=query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{statement}{'ROLLBACK' if expected else 'COMMIT'};")
        return result.splitlines()[-1] if not expected else None
    register([pins[0],{**fixed,'inputUse':'variable'}],'pilot_close_inputs')
    register([pins[0],{**fixed,'observationId':preliminary}],'pilot_close_inputs')
    anchor=register(pins)
    now=datetime.now(timezone.utc)
    definition={'priorCloseId':anchor,'bookIds':[query(f"SELECT id FROM trading.books WHERE catalog_id='{catalog}' ORDER BY id LIMIT 1")],
        'curveCodes':['future'],'sourceCode':'fixture','seed':17,'leafMoves':[{'curve':'future','maxTicks':7}],
        **{key:values[0]['value'] for key,values in choices['policies'].items()},'valuationPolicy':choices['valuationPolicyChoices'][0]['policy']}
    schedule={'requestId':str(uuid4()),'name':'Synthetic frozen carry qualification','operation':choices['operation'],
        'dueAt':(now+timedelta(minutes=10)).isoformat(),'expiresAt':(now+timedelta(hours=1)).isoformat(),'intervalSeconds':None,
        **choices['schedulePolicy'],'timeoutSeconds':120,'maximumAttempts':1,'profileId':None,'profileVersion':None}
    token=human_token()
    created=json.loads(query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');SELECT automation.create_pilot_close_job('{json.dumps(schedule)}'::jsonb,'{json.dumps(definition)}'::jsonb);COMMIT;").splitlines()[-1])
    due='2021-01-02T08:00:00Z';query(f"UPDATE automation.jobs SET next_due_at='{due}' WHERE id='{created['id']}';")
    claim=json.loads(query(f"SET SESSION AUTHORIZATION pilot_fixture_scheduler;SELECT automation.claim_next('{uuid4()}',120,true);"))
    assert claim['jobId']==created['id'],'The frozen qualifier claimed an unrelated occurrence'
    service=query(f"SET SESSION AUTHORIZATION pilot_fixture_scheduler;SELECT automation.issue_pilot_context('{claim['id']}',{claim['fence']},'{claim['workerId']}');")
    def publish(carried,marks=None,expected=None):
        values=[marks if marks is not None else [native],carried,members]
        args=','.join("'"+json.dumps(value).replace("'","''")+"'::jsonb" for value in values)
        statement=f"PERFORM automation.publish_pilot_close({args});"
        if expected:
            statement=f"BEGIN {statement} RAISE EXCEPTION 'Invalid frozen payload was published';EXCEPTION WHEN SQLSTATE 'P0001' THEN IF SQLERRM<>'{expected}' THEN RAISE;END IF;END;"
        else:
            statement+=f"""IF NOT EXISTS(SELECT 1 FROM market_data.market_close_pins WHERE catalog_id='{catalog}' AND close_id='{claim['id']}' AND observation_id='{actual}' AND input_use='frozen')
             OR EXISTS(SELECT 1 FROM market_data.observations WHERE catalog_id='{catalog}' AND role='index_fixing' AND source_event_id LIKE 'pilot:%')
             THEN RAISE EXCEPTION 'Exact actual identity was replaced or generated';END IF;"""
        query(f"""SET SESSION AUTHORIZATION pilot_fixture_runtime;BEGIN;SELECT security.use_context('{service}');
         SELECT security.use_market_context('{catalog}',security.capture_date_context('{catalog}','2021-01-02',NULL));
         SELECT security.use_trading_context('{catalog}','{due}');DO $$BEGIN {statement} END$$;ROLLBACK;""")
    publish([fixed])
    publish([],expected='pilot_close_inputs')
    publish([{**fixed,'inputUse':'variable'}],expected='pilot_close_inputs')
    publish([{**fixed,'observationId':preliminary}],expected='pilot_close_inputs')
    publish([fixed],[native,{'seriesId':fixed['seriesId'],'anchorObservationId':actual,'moveTicks':0,'value':'3.5'}],'pilot_close_inputs')
    query(f"SET SESSION AUTHORIZATION pilot_fixture_scheduler;SELECT automation.finish_run('{claim['id']}',{claim['fence']},'{claim['workerId']}',false,'synthetic_qualification',NULL);")
    assert query(f"SELECT count(*) FROM market_data.market_closes WHERE run_id='{claim['id']}';")=='0'
    print('PASS frozen actual carry: final exact index fact retained, variable/preliminary/missing/substituted/generated actuals rejected, no durable generated writes',flush=True)
