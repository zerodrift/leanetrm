"""The runtime-context cutover refuses prior financial bindings atomically."""
from uuid import uuid4


def run_checks(query,migration):
    sql=migration.read_text()
    assert sql.count('\nBEGIN;')==1 and sql.rstrip().endswith('COMMIT;')
    candidate=sql.replace('\nBEGIN;','\n',1).rstrip().removesuffix('COMMIT;')+'ROLLBACK;'
    tenant,workspace,actor,request=(str(uuid4()) for _ in range(4))
    seed=(f"INSERT INTO security.tenants VALUES('{tenant}','synthetic_reset');"
          f"INSERT INTO security.workspaces VALUES('{tenant}','{workspace}','synthetic_reset');"
          f"INSERT INTO security.principals VALUES('{actor}','human',true);"
          f"INSERT INTO security.request_contexts(id,actor_id,tenant_id,workspace_id,runtime_login,expires_at) "
          f"VALUES('{request}','{actor}','{tenant}','{workspace}','preview_owner',clock_timestamp()+interval '1 minute');")
    for assignment in ("trading_as_of='2020-12-18T16:00:00Z'",
                       "trading_business_date=DATE '2020-12-18'",
                       "calendar_cut=ROW(DATE '2020-12-18','2021-01-01T00:00:00Z',0,'2021-01-01T00:00:00Z',0,0)::security.calendar_read_coordinates"):
        try:
            query('BEGIN;'+seed+f"UPDATE security.request_contexts SET {assignment} WHERE id='{request}';"+candidate)
        except RuntimeError as error:
            assert 'runtime_context_reset_required' in str(error),error
        else:
            raise AssertionError('Cutover accepted a pre-existing financial request binding: '+assignment)
        assert query("SELECT to_regclass('market_data.temporal_contexts') IS NOT NULL "
                     "AND to_regtype('security.operation_read_coordinates') IS NULL;")=='t', 'Refused cutover changed the predecessor schema'
        assert query(f"SELECT count(*) FROM security.request_contexts WHERE id='{request}';")=='0', 'Negative reset fixture escaped rollback'
    print('PASS context cutover refuses predecessor scalar and calendar bindings with no partial schema change',flush=True)
    for alteration,signature in (
        ("ALTER FUNCTION physical.power_operating_context(uuid,uuid,timestamptz,date) SET work_mem='64kB';",
         'physical.power_operating_context'),
        ("GRANT EXECUTE ON FUNCTION lifecycle.option_proposal_base(uuid,jsonb) TO PUBLIC;",
         'lifecycle.option_proposal_base')):
        try:query('BEGIN;'+alteration+candidate)
        except RuntimeError as error:
            assert 'runtime_context_consumer_changed' in str(error) and signature in str(error),error
        else:raise AssertionError('Cutover accepted a changed consumer: '+signature)
        assert query("SELECT to_regclass('market_data.temporal_contexts') IS NOT NULL AND to_regtype('security.operation_read_coordinates') IS NULL;")=='t'
    print('PASS context cutover rejects changed physical/lifecycle consumer definitions and privileges atomically',flush=True)
    try:query("BEGIN;ALTER FUNCTION risk.attest_workspace_clock(integer,xid8,uuid,uuid,jsonb,jsonb) SET work_mem='64kB';"+candidate)
    except RuntimeError as error:assert 'runtime_clock_issuer_changed' in str(error),error
    else:raise AssertionError('Cutover accepted a changed clock issuer')
    assert query("SELECT to_regclass('market_data.temporal_contexts') IS NOT NULL AND to_regtype('security.operation_read_coordinates') IS NULL;")=='t'
    try:query("BEGIN;INSERT INTO risk.pending_clock_attestations VALUES(pg_backend_pid(),pg_current_xact_id(),decode('00','hex'));"+candidate)
    except RuntimeError as error:assert 'runtime_context_reset_required' in str(error),error
    else:raise AssertionError('Cutover accepted predecessor clock attestations')
    assert query("SELECT to_regclass('market_data.temporal_contexts') IS NOT NULL AND to_regtype('security.operation_read_coordinates') IS NULL;")=='t'
    print('PASS context cutover rejects changed clock issuer and pending predecessor attestations atomically',flush=True)
