"""Each later recurrence resolves a new input policy outcome, once."""
import json
from uuid import uuid4


def run_checks(owner,definition,query):
    body=json.loads(json.dumps(definition))
    body['schedule'].update(requestId=str(uuid4()),name='Synthetic occurrence inputs',intervalSeconds=86400,maximumAttempts=1)
    job=owner.call('jobs/pilot-close/create',body)
    inputs=[];runs=[]
    before=query('SELECT (SELECT count(*) FROM market_data.observations),(SELECT count(*) FROM trading.publications);')
    for day in ('2020-12-20','2020-12-21'):
        # Select past due instants in this owned fixture; never change the server clock.
        query(f"UPDATE automation.jobs SET next_due_at='{day}T16:00:00Z' WHERE id='{job['id']}';")
        claimed=query(f"SET SESSION AUTHORIZATION pilot_fixture_scheduler;SELECT automation.claim_next('{uuid4()}',120,true);")
        assert claimed,query(f"SELECT row_to_json(j) FROM automation.jobs j WHERE id='{job['id']}';")
        claim=json.loads(claimed)
        assert claim['jobId']==job['id'],claim
        runs.append(claim['id'])
        def token():
            return query(f"SET SESSION AUTHORIZATION pilot_fixture_scheduler;SELECT automation.issue_pilot_context('{claim['id']}',{claim['fence']},'{claim['workerId']}');")
        def operation(sql):
            return query(f"SET SESSION AUTHORIZATION pilot_fixture_runtime;BEGIN;SELECT security.use_context('{token()}');{sql}COMMIT;")
        operation('SELECT automation.admit_occurrence_inputs();')
        source=json.loads(operation('SELECT row_to_json(s) FROM automation.occurrence_read_inputs() s;'))
        assert source['effective_on']==day
        operation('SELECT automation.admit_occurrence_inputs();')
        assert json.loads(operation('SELECT row_to_json(s) FROM automation.occurrence_read_inputs() s;'))==source
        inputs.append(source)
        query(f"SET SESSION AUTHORIZATION pilot_fixture_scheduler;SELECT automation.finish_run('{claim['id']}',{claim['fence']},'{claim['workerId']}',false,'synthetic_input_qualification',NULL);")
        if day=='2020-12-20':
            # This qualifier intentionally stops before any financial work. The
            # owner resumes the blocked schedule for its next logical occurrence.
            version=int(query(f"SELECT version FROM automation.jobs WHERE id='{job['id']}';"))
            owner.call('jobs/control',dict(requestId=str(uuid4()),id=job['id'],action='run_again',expectedVersion=version))
    assert runs[0]!=runs[1] and inputs[0]['id']!=inputs[1]['id'] and inputs[0]['known_at']<inputs[1]['known_at']
    assert query(f"SELECT input_known_at='{inputs[0]['known_at']}'::timestamptz FROM automation.runs WHERE id='{runs[0]}';")=='t'
    assert query('SELECT (SELECT count(*) FROM market_data.observations),(SELECT count(*) FROM trading.publications);')==before
    version=int(query(f"SELECT version FROM automation.jobs WHERE id='{job['id']}';"))
    owner.call('jobs/control',dict(requestId=str(uuid4()),id=job['id'],action='cancel',expectedVersion=version))
    print('PASS repeated admission keeps each occurrence exact; a later recurrence independently selects a newer cutoff without financial writes',flush=True)
