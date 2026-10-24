"""Final risk publication authority checks in the caller's disposable preview."""
import json
from pathlib import Path
from uuid import uuid4

from preview_client import initial_frame


def run_checks(Client, query, check, manifest, temporary):
    risk = Client(); risk.bootstrap(); risk.call('profile', {'profile': 'risk'})
    frame = initial_frame(risk.call('workspace'), '2020-12-18')
    context = {'snapshotId': frame['id'], 'convention': 'trading_workspace_v1', 'knowledgeMode': 'current'}
    catalog = risk.call('portfolio/risk/catalog', {'context': context, 'bookIds': [], 'portfolioIds': []})
    factor = catalog['factors'][0]
    request = {'context': {**context, 'knowledgeMode': 'retained', 'contextId': catalog['context']['contextId']},
               'bookIds': [], 'portfolioIds': [], 'detail': 'books', 'stress': [],
               'convention': catalog['convention'], 'numericalPolicy': catalog['numericalPolicy'],
               'pricingPolicy': catalog['pricingPolicy'],
               'bumps': [{'factor': factor['factor'], 'size': '0.1',
                          'convention': catalog['bumpConvention']}]}
    actor = query("SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='risk';")
    tenant, workspace = (manifest[key] for key in ('tenantId', 'workspaceId'))
    original = json.loads(query(f"SELECT jsonb_agg(jsonb_build_object('id',id,'revoked',revoked)) FROM security.role_grants WHERE principal_id='{actor}';"))
    original_ids = ','.join("'" + row['id'] + "'" for row in original)
    log = Path(temporary) / 'postgres.log'
    for permission in ('ReadMarketData', 'RunScenario'):
        persistent, expiring, grant = str(uuid4()), str(uuid4()), str(uuid4())
        marker = 'synthetic_risk_publication_' + uuid4().hex
        # The wrapper delays only the final capability projection, after the
        # financial report and its earlier authorization checks have completed.
        # Log markers survive the denied request's rollback and prove both live
        # admission and actual expiry; an earlier refusal cannot pass this check.
        query(f"""BEGIN;
            INSERT INTO security.roles VALUES('{persistent}','risk_probe_persistent','Synthetic persistent risk read'),
             ('{expiring}','risk_probe_expiring','Synthetic expiring risk read');
            INSERT INTO security.role_permissions
             SELECT DISTINCT '{persistent}'::uuid,p.permission FROM security.role_permissions p
             JOIN security.role_grants g ON g.role_id=p.role_id
             WHERE g.id IN({original_ids}) AND NOT g.revoked AND p.permission<>'{permission}';
            INSERT INTO security.role_permissions VALUES('{expiring}','{permission}');
            UPDATE security.role_grants SET revoked=true WHERE id IN({original_ids});
            INSERT INTO security.role_grants VALUES
             (gen_random_uuid(),'{actor}','{persistent}','{tenant}','{workspace}',clock_timestamp(),'9999-12-31',false),
             ('{grant}','{actor}','{expiring}','{tenant}','{workspace}',clock_timestamp(),clock_timestamp()+interval '3 seconds',false);
            ALTER FUNCTION security.has_permission(text) RENAME TO risk_probe_has_permission;
            CREATE FUNCTION security.has_permission(permission text) RETURNS boolean LANGUAGE plpgsql VOLATILE
             SECURITY DEFINER SET search_path=pg_catalog AS $$
            DECLARE until_at timestamptz;
            BEGIN
             IF permission='RunDealRisk' AND session_user='preview_runtime' AND EXISTS(
              SELECT 1 FROM security.request_contexts WHERE backend_pid=pg_backend_pid()
               AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user AND actor_id='{actor}') THEN
              IF NOT security.risk_probe_has_permission('{permission}') THEN RAISE EXCEPTION 'risk_publication_probe_not_admitted';END IF;
              SELECT granted_to INTO until_at FROM security.role_grants WHERE id='{grant}';
              RAISE LOG '{marker}_admitted';
              PERFORM pg_sleep(greatest(0,extract(epoch FROM until_at-clock_timestamp()))+0.05);
              IF security.risk_probe_has_permission('{permission}') THEN RAISE EXCEPTION 'risk_publication_probe_not_expired';END IF;
              RAISE LOG '{marker}_expired';
             END IF;
             RETURN security.risk_probe_has_permission(permission);
            END$$;
            ALTER FUNCTION security.has_permission(text) OWNER TO openetrm_security_owner;
            REVOKE ALL ON FUNCTION security.has_permission(text) FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION security.has_permission(text) TO openetrm_runtime;
            COMMIT;""")
        try:
            try:
                refused = risk.call('portfolio/risk', request, expected=403)
            except AssertionError as error:
                if 'expected 403, got 200:' in str(error):
                    raise AssertionError(f'Portfolio risk published after {permission} expired during response assembly') from None
                raise
            logs = log.read_text(errors='replace')
            check(f'portfolio publication rechecks independently expiring {permission} after response assembly',
                  refused['code'] == 'access_denied' and marker + '_admitted' in logs and marker + '_expired' in logs)
        finally:
            restore = ';'.join(f"UPDATE security.role_grants SET revoked={str(row['revoked']).lower()} WHERE id='{row['id']}'" for row in original)
            query(f"""BEGIN;DROP FUNCTION security.has_permission(text);
                ALTER FUNCTION security.risk_probe_has_permission(text) RENAME TO has_permission;
                DELETE FROM security.role_grants WHERE role_id IN('{persistent}','{expiring}');
                DELETE FROM security.role_permissions WHERE role_id IN('{persistent}','{expiring}');
                DELETE FROM security.roles WHERE id IN('{persistent}','{expiring}');
                {restore};COMMIT;""")
