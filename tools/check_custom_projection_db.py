"""Typed projection retention checks for an owned, disposable preview database."""
import json
from uuid import uuid4
from synthetic_schedule_stage import copy_stage
from typed_recipe_fixture import author_token, draft_recipe, publish_recipe


def run_checks(query, manifest):
    assert query("SELECT to_regclass('trading.custom_curve_projection_bindings') IS NOT NULL") == 't', 'Custom projection retention is missing'
    catalog = manifest['catalogId']
    curve, binding = str(uuid4()), str(uuid4())
    token=author_token(query,manifest)
    setup = f"BEGIN;SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token}');"+draft_recipe(catalog,curve,'synthetic_projection_check','result',catalog,manifest['revisionId'],'future')+publish_recipe(catalog,curve,1)+f"""
    INSERT INTO trading.publications SELECT '{catalog}',coalesce(max(publication_order),0)+1,pg_current_xact_id() FROM trading.publications WHERE catalog_id='{catalog}';
    """
    def insert(version=1, node='result', selection='single_output_position_v1'):
        return f"""INSERT INTO trading.custom_curve_projection_bindings(catalog_id,id,curve_id,version,output_node,position,selection,implementation,publication_order,effective_at)
          VALUES('{catalog}','{binding}','{curve}',{version},'{node}',0,'{selection}','published_custom_curve_projection_v1',
          (SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}'),'2000-01-01');"""

    def reject(sql, expected):
        try:
            query(sql)
        except RuntimeError as error:
            assert expected in str(error), str(error)
        else:
            raise AssertionError(f'Accepted invalid projection: expected {expected}')

    assert query(setup + insert() + f"SELECT count(*) FROM trading.custom_curve_projection_bindings WHERE id='{binding}';ROLLBACK;") == '1'
    reject(setup + insert(version=2) + 'ROLLBACK;', 'custom_projection')
    reject(setup + insert(node='other') + 'ROLLBACK;', 'custom_projection')
    reject(setup + insert(selection='') + 'ROLLBACK;', 'check constraint')
    reject(setup + insert() + f"UPDATE trading.custom_curve_projection_bindings SET position=1 WHERE id='{binding}';ROLLBACK;", 'immutable')
    assert query("""SELECT has_table_privilege('openetrm_risk_worker','trading.custom_curve_projection_bindings','SELECT')
      AND NOT has_table_privilege('openetrm_runtime','trading.custom_curve_projection_bindings','INSERT')
      AND NOT has_table_privilege('openetrm_risk_worker','trading.custom_curve_projection_bindings','INSERT')
      AND NOT pg_has_role('openetrm_risk_worker','openetrm_runtime','MEMBER');""") == 't'
    print('PASS custom projection DB: exact published output, immutable binding, explicit policy and read-only worker scope', flush=True)


def run_booking_checks(query, manifest, trade_id):
    """Exercise the canonical SQL command from a real accepted booking."""
    catalog, tenant, workspace = (manifest[key] for key in ('catalogId', 'tenantId', 'workspaceId'))
    original = json.loads(query(f"SELECT request FROM trading.swap_trade_revisions WHERE catalog_id='{catalog}' AND trade_id='{trade_id}' AND revision=1;"))
    operation = query(f"SELECT operation_id FROM trading.swap_trade_revisions WHERE catalog_id='{catalog}' AND trade_id='{trade_id}' AND revision=1;")
    other_revision = query(f"SELECT id FROM market_data.revisions WHERE catalog_id='{catalog}' AND id<>'{original['definition']}' AND state='sealed' ORDER BY id LIMIT 1;")
    assert other_revision, 'The fixture needs another visible exact market revision'

    def invoke(request, request_id):
        token = query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        value = "'" + json.dumps(request).replace("'", "''") + "'::jsonb"
        stage=copy_stage(catalog,operation,request_id) if operation!=request_id else ''
        return query(f"""BEGIN;{stage}SET SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token}');
          SELECT security.save_composed_swap('{catalog}','{request_id}',NULL,NULL,
           (p->>'book')::uuid,(p->>'definition')::uuid,(p->>'referenceCatalog')::uuid,(p->>'referenceRevision')::uuid,
           p->>'commodity',NULLIF(p->'fixed','null'::jsonb),NULLIF(p->'firstFloating','null'::jsonb),p->'floating',
           (p->>'quantity')::numeric,(p->>'coefficient')::numeric,(p->>'discounting')::boolean,
           (p->>'valuation')::timestamptz,(p->>'effective')::timestamptz,p->>'reason',p->>'implementation',
           p->>'memo',p->'booking','{{}}'::uuid[])
          FROM(SELECT {value} p)request;ROLLBACK;""")

    assert json.loads(invoke(original, operation))['tradeId'] == trade_id
    missing = str(uuid4())
    token=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','editor','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',120);")
    query(f"BEGIN;SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token}');"+
          draft_recipe(catalog,missing,'synthetic_unpublished_projection','result',catalog,original['definition'],'future')+
          'SET LOCAL SESSION AUTHORIZATION preview_runtime;COMMIT;')
    failures = []
    for changed, label, code in [({**original, 'definition': other_revision}, 'another exact market revision', 'swap_definition'),
                                 ({**original, 'floating': {**original['floating'], 'customProjection': {**original['floating']['customProjection'], 'curveId': missing,
                                     'version': 1, 'outputNode': 'result'}}}, 'an unpublished projection', 'custom_projection')]:
        try:
            invoke(changed, str(uuid4()))
        except RuntimeError as error:
            if code not in str(error):
                failures.append(f'The SQL command did not reject {label}: ' + str(error))
        else:
            failures.append(f'The SQL command accepted {label}')
    assert not failures, '\n'.join(failures)
    print('PASS custom projection SQL booking: accepted replay and rejected mismatched source revisions/unpublished projections', flush=True)
