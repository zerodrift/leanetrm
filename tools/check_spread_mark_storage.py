#!/usr/bin/env python3
"""Independent native spread-option mark publication, revision and knowledge-read checks."""
from copy import deepcopy
from datetime import datetime,timezone
from uuid import uuid4
import json
import time
from check_reference_editing import main
from check_workspace_as_of import read_trading_workspace
from preview_fixture import fixture,trading_fixture

EARLY,LATE='2020-12-15T16:00:00Z','2020-12-16T16:00:00Z'


def run_checks(query,manifest):
    catalog,definition=manifest['catalogId'],manifest['revisionId']
    def runtime(sql,actor='editor'):
        token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
            f"'https://preview.invalid','{actor}','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',120);")
        return query('\\set VERBOSITY verbose\nBEGIN;SET LOCAL SESSION AUTHORIZATION preview_runtime;'+f"SELECT security.use_context('{token}');"+sql+'COMMIT;')
    def reject(sql,code,actor='editor'):
        try:runtime(sql,actor)
        except RuntimeError as error:
            assert code in str(error),str(error)
        else:raise AssertionError('Accepted an invalid native spread mark operation: expected '+code)
    literal=lambda value:json.dumps(value).replace("'","''")
    record=lambda body,at=EARLY:f"SELECT security.record_spread_marks('{catalog}','{literal(body)}'::jsonb,'{at}');"
    stored=lambda:query('SELECT(SELECT count(*) FROM trading.spread_marks)||\'/\'||(SELECT count(*) FROM trading.spread_mark_requests);')
    mark=lambda month,expected,power='0.6',fuel='0.45',rho='0.8':dict(month=month,expectedRevision=expected,longVolatility=power,shortVolatility=fuel,correlation=rho)
    body=dict(operationId=str(uuid4()),convention='spread_marks_log_return_held_v1',definitionRevisionId=definition,longCurve='local_power',shortCurve='local_gas',
        reason='Synthetic desk marks',marks=[mark('2021-02',0),mark('2021-03',0,'0.55','0.4','0.75')])
    query(trading_fixture(catalog))

    reject(record(body),'42501','viewer')
    reject('SELECT count(*) FROM trading.spread_marks;','permission denied')
    def refuse(code,change):
        changed=deepcopy(body);change(changed)
        reject(record(changed),code)
        assert stored()=='0/0','A refused mark left facts behind: '+code
    refuse('spread_mark_input',lambda b:b.update(defaultVolatility='0.5'))
    refuse('spread_mark_input',lambda b:b.update(convention='another_convention_v1'))
    refuse('spread_mark_input',lambda b:b.update(marks=[]))
    refuse('spread_mark_input',lambda b:b['marks'][0].update(longVolatility=0.6))
    refuse('spread_mark_input',lambda b:b['marks'][0].update(month='2021-13'))
    refuse('spread_mark_input',lambda b:b['marks'][0].pop('correlation'))
    refuse('spread_mark_input',lambda b:b['marks'][0].update(longVolatility='-0.1'))
    refuse('spread_mark_value',lambda b:b['marks'][0].update(correlation='1.01'))
    refuse('spread_mark_value',lambda b:b['marks'][0].update(shortVolatility='10.5'))
    refuse('spread_mark_value',lambda b:b['marks'][1].update(month='2021-02'))
    refuse('spread_mark_stale',lambda b:b['marks'][0].update(expectedRevision=1))
    refuse('spread_mark_curves',lambda b:b.update(shortCurve='gas_basis'))
    refuse('spread_mark_curves',lambda b:b.update(shortCurve='hub_power'))
    refuse('42501',lambda b:b.update(longCurve='absent_curve'))
    future=datetime.now(timezone.utc).replace(year=datetime.now(timezone.utc).year+1).strftime('%Y-%m-%dT%H:%M:%SZ')
    reject(record(body,future),'spread_mark_input')
    assert stored()=='0/0'
    print('PASS spread mark storage: publishing needs marking authority and closed, bounded, explicit values on two outright curves',flush=True)

    first=json.loads(runtime(record(body)))
    assert first['operationId']==body['operationId'] and [(m['month'],m['revision'],m['changed']) for m in first['marks']]==[('2021-02',1,True),('2021-03',1,True)],first
    assert json.loads(runtime(record(body)))==first and stored()=='2/1'
    assert json.loads(runtime(f"SELECT security.spread_mark_request_status('{catalog}','{body['operationId']}');"))==first
    changed=deepcopy(body);changed['marks'][0]['correlation']='0.7'
    reject(record(changed),'spread_mark_request_conflict')
    again=deepcopy(body);again.update(operationId=str(uuid4()));again['marks'][0].update(longVolatility='0.60',expectedRevision=7)
    same=json.loads(runtime(record(again)))
    assert [(m['markId'],m['revision'],m['changed']) for m in same['marks']]==[(m['markId'],1,False) for m in first['marks']] and stored()=='2/2',same
    print('PASS spread mark storage: a retried request returns its first outcome and the same value at the same instant is not recorded twice',flush=True)

    known_before_revision=runtime(f"SELECT security.capture_date_context('{catalog}','2020-12-16',NULL);")
    time.sleep(1)
    revised=dict(body,operationId=str(uuid4()),marks=[mark('2021-02',1,'0.7','0.5','0.85')])
    stale=dict(revised,operationId=str(uuid4()),marks=[mark('2021-02',0,'0.7','0.5','0.85')])
    reject(record(stale,LATE),'spread_mark_stale')
    second=json.loads(runtime(record(revised,LATE)))
    assert [(m['month'],m['revision'],m['changed']) for m in second['marks']]==[('2021-02',2,True)],second
    earlier=dict(body,operationId=str(uuid4()),marks=[mark('2021-02',2,'0.9','0.5','0.85')])
    reject(record(earlier,EARLY),'spread_mark_time')
    assert query("SELECT string_agg(revision||':'||(previous_id IS NOT NULL),',' ORDER BY bucket_month,revision) FROM trading.spread_marks;")=='1:false,2:true,1:false'
    for statement in ("UPDATE trading.spread_marks SET correlation=0","DELETE FROM trading.spread_marks"):
        try:query(statement+';')
        except RuntimeError:pass
        else:raise AssertionError('A retained spread mark was rewritten: '+statement)
    print('PASS spread mark storage: a new value is a new revision of the expected head, never an overwrite or an earlier-dated replacement',flush=True)

    months="ARRAY['2021-02-01','2021-03-01','2021-04-01']::date[]"
    def read(context,as_of,actor='trader'):
        return json.loads(runtime(f"SELECT security.use_market_context('{catalog}','{context}');SELECT security.use_trading_context('{catalog}','{as_of}');"
            f"SELECT security.read_spread_marks('{catalog}','{definition}','local_power','local_gas',{months});",actor))
    values=lambda rows:[(row['month'],row['revision'],row['longVolatility'],row['shortVolatility'],row['correlation']) for row in rows]
    current=runtime(f"SELECT security.capture_date_context('{catalog}','2020-12-16',NULL);")
    assert values(read(current,'2020-12-16T23:59:59.999999Z'))==[('2021-02',2,'0.7','0.5','0.85'),('2021-03',1,'0.55','0.4','0.75')]
    assert values(read(current,'2020-12-16T12:00:00Z'))==[('2021-02',1,'0.6','0.45','0.8'),('2021-03',1,'0.55','0.4','0.75')]
    assert values(read(known_before_revision,'2020-12-16T23:59:59.999999Z'))==[('2021-02',1,'0.6','0.45','0.8'),('2021-03',1,'0.55','0.4','0.75')]
    day_before=runtime(f"SELECT security.capture_date_context('{catalog}','2020-12-14',NULL);")
    assert read(day_before,'2020-12-14T23:59:59.999999Z')==[]
    selected_marks=f"SELECT security.read_spread_marks('{catalog}','{definition}','local_power','local_gas',{months});"
    for as_of in ('2020-12-16T12:00:00Z','2020-12-16T23:59:59.999999Z'):
        assert read_trading_workspace(query,runtime,catalog,as_of,selected_marks)==read(current,as_of)
    before_correction=query(f"SELECT known_at FROM market_data.temporal_contexts WHERE catalog_id='{catalog}' AND id='{known_before_revision}';")
    earlier_marks=read_trading_workspace(query,runtime,catalog,'2020-12-17T23:59:59.999999Z',selected_marks,known_at=before_correction)
    assert values(earlier_marks)==[('2021-02',1,'0.6','0.45','0.8'),('2021-03',1,'0.55','0.4','0.75')]
    reject(f"SELECT security.read_spread_marks('{catalog}','{definition}','local_power','local_gas',{months});",'knowledge_coverage','trader')
    reject(f"SELECT security.use_market_context('{catalog}','{current}');SELECT security.use_trading_context('{catalog}','2020-12-16T23:59:59.999999Z');"
        f"SELECT security.read_spread_marks('{catalog}','{definition}','local_power','local_gas',ARRAY['2021-02-15']::date[]);",'spread_mark_input','trader')
    print('PASS spread mark storage: a read returns each month\'s latest revision known and effective under its context, and omits an unmarked month',flush=True)

    # Revisiting a superseded fact at the same instant must leave its correction in force.
    correction=dict(body,operationId=str(uuid4()),marks=[mark('2021-03',1,'0.72','0.4','0.75')])
    assert json.loads(runtime(record(correction)))['marks'][0]['revision']==2
    repeated=dict(body,operationId=str(uuid4()),marks=[mark('2021-03',2,'0.550','0.4','0.75')])
    receipt=json.loads(runtime(record(repeated)))
    original=first['marks'][1]
    assert receipt['marks']==[{**original,'changed':False}],receipt
    assert json.loads(runtime(record(repeated)))==receipt
    assert query("SELECT count(*) FROM trading.spread_marks WHERE bucket_month=DATE '2021-03-01';")=='2'
    publications=lambda:int(query(f"SELECT count(*) FROM trading.publications WHERE catalog_id='{catalog}';"))
    before_rows,before_publications=stored(),publications()
    stale_batch=dict(body,operationId=str(uuid4()),marks=[mark('2021-04',0),mark('2021-03',0,'0.9')])
    reject(record(stale_batch),'spread_mark_stale')
    assert stored()==before_rows and publications()==before_publications

    mixed=dict(body,operationId=str(uuid4()),marks=[mark('2021-04',0),*repeated['marks']])
    added,existing=json.loads(runtime(record(mixed)))['marks']
    assert (added['month'],added['revision'],added['changed'])==('2021-04',1,True)
    assert existing=={**original,'changed':False} and publications()==before_publications+1
    latest=runtime(f"SELECT security.capture_date_context('{catalog}','2020-12-16',NULL);")
    march=next(row for row in read(latest,'2020-12-16T23:59:59.999999Z') if row['month']=='2021-03')
    assert (march['revision'],march['longVolatility'])==(2,'0.72')
    print('PASS spread mark storage: historical repeats preserve the correcting head and mixed batches are atomic',flush=True)


if __name__=='__main__':
    main(checks=run_checks,fixture_factory=lambda root:fixture(root))
