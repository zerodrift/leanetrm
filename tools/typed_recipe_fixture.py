"""A complete, source-pinned synthetic recipe for native storage checks."""
import json
from uuid import uuid4
from seed_exact_rows import literal


def author_token(query, manifest):
    """Own a synthetic author/publisher without changing the permission-separation profiles."""
    principal=str(uuid4())
    subject='recipe_'+uuid4().hex
    tenant,workspace=(literal(manifest[key]) for key in ('tenantId','workspaceId'))
    query(f"""BEGIN;
INSERT INTO security.principals(id,kind,enabled) VALUES('{principal}','human',true);
INSERT INTO security.identity_bindings(issuer,subject,principal_id,login_kind)
 VALUES('https://preview.invalid','{subject}','{principal}','human_interactive');
INSERT INTO security.workspace_memberships(tenant_id,workspace_id,principal_id,login_handle,display_name,enabled,version)
 VALUES({tenant},{workspace},'{principal}','{subject}','Synthetic recipe author',true,1);
INSERT INTO security.role_grants(id,principal_id,role_id,tenant_id,workspace_id,granted_from,granted_to,revoked)
 SELECT gen_random_uuid(),'{principal}',id,{tenant},{workspace},CURRENT_TIMESTAMP-INTERVAL '1 day',CURRENT_TIMESTAMP+INTERVAL '1 day',false
 FROM security.roles WHERE code IN('market_data_editor','market_data_approver');COMMIT;""")
    return query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','{subject}','human_interactive',{tenant},{workspace},NULL,'preview_runtime',120);")


def draft_recipe(catalog, curve, code, output, source_catalog, source_revision, source_curve):
    """Insert under the transaction's already-bound author context, then resume owner-only fixture setup."""
    # Reference parameters are normalized into custom_curve_node_sources, never stored in JSON.
    nodes=[('source','source',{}),
           ('select','select',dict(mode='positions',first='1',count='1',binding='context')),
           ('dates','dates',dict(mode='prior_calendar',count='1',includeContext='true',sampling='context_utc_time',corrections='exact_context_frame_history_latest_visible')),
           ('sample','sample',{}),(output,'output',{})]
    prefix=literal(catalog)+','+literal(curve)+',1'
    values=','.join('('+prefix+','+literal(node)+','+literal(kind)+','+literal(json.dumps(parameters))+'::jsonb)' for node,kind,parameters in nodes)
    edges=','.join('('+prefix+','+literal(node)+','+literal(port)+','+literal(source)+')' for node,port,source in
                   [('select','source','source'),('sample','selection','select'),('sample','dates','dates'),(output,'values','sample')])
    return f"""SET LOCAL SESSION AUTHORIZATION preview_runtime;
INSERT INTO market_data.custom_curves(catalog_id,id,code) VALUES({literal(catalog)},{literal(curve)},{literal(code)});
INSERT INTO market_data.custom_curve_versions(catalog_id,curve_id,version,generation,state,name,output_node,engine_version,layout)
 VALUES({prefix},1,'draft',{literal('Synthetic '+code)},{literal(output)},'custom_curve_recipe_v2','{{}}');
INSERT INTO market_data.custom_curve_nodes(catalog_id,curve_id,version,node_id,kind,parameters) VALUES{values};
INSERT INTO market_data.custom_curve_edges(catalog_id,curve_id,version,node_id,port,source_node) VALUES{edges};
INSERT INTO market_data.custom_curve_node_sources(catalog_id,curve_id,version,node_id,source_catalog_id,source_revision_id,source_curve_code)
 VALUES({prefix},'source',{literal(source_catalog)},{literal(source_revision)},{literal(source_curve)});
RESET SESSION AUTHORIZATION;"""


def publish_recipe(catalog, curve, generation):
    return f"""SET LOCAL SESSION AUTHORIZATION preview_runtime;
DO $publish_fixture$ BEGIN PERFORM security.publish_custom_curve({literal(catalog)},{literal(curve)},1,{generation});END $publish_fixture$;
RESET SESSION AUTHORIZATION;"""
