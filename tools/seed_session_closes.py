"""Record a preview's built-in frames as D319 session closes in its owned synthetic database.

Only recorded closes are closes: each built-in frame becomes the close of a synthetic UTC session whose cutoff is the
frame's time of day and whose curves are the frame's. A later frame on the same session day replaces an earlier one.
Frames that belong to a separate bundle (PPA or swap selections) stay as they are.
"""
import json
from uuid import uuid4

# The synthetic reference data's UTC calendar with no weekend: built-in frames fall on any calendar day.
CALENDAR = 'test_utc_every_day'


def seed(query, manifest):
    """Insert the sessions and closes; return the manifest frames, each seeded one naming its close."""
    catalog, revision = manifest['catalogId'], manifest['revisionId']
    reference = f"'{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}'"
    owner = query(f"SELECT principal_id FROM security.workspace_memberships WHERE(tenant_id,workspace_id,login_handle)=('{manifest['tenantId']}','{manifest['workspaceId']}','approver');")
    seeded, sessions = [], {}
    for frame in manifest['frames']:
        if frame.get('ppaBundle') or frame.get('swapMarketSelections') or not frame['observationIds'] or not frame['curveContracts']:
            continue
        key = (frame['at'][11:16], tuple(sorted(frame['curveContracts'])))
        if key not in sessions:
            sessions[key] = f"demo_close_{key[0].replace(':', '')}" + ('' if all(k[0] != key[0] for k in sessions) else f'_{len(sessions)}')
        seeded.append((sessions[key], frame))
    for (cutoff, curves), code in sessions.items():
        rows = ','.join(f"('{catalog}','{code}',1,'{revision}','{c}')" for c in curves)
        query(f"""INSERT INTO market_data.close_sessions VALUES('{catalog}','{code}','Synthetic {cutoff} UTC close');
        INSERT INTO market_data.close_session_versions(catalog_id,code,version,state,generation,definition_revision_id,reference_catalog_id,reference_revision_id,
         calendar_code,cutoff_local,effective_from,effective_to,actor_id,request_id,request,publisher_id,publish_request_id)
        VALUES('{catalog}','{code}',1,'published',2,'{revision}',{reference},'{CALENDAR}','{cutoff}','2020-01-01','2023-01-01','{owner}','{uuid4()}','{{}}','{owner}','{uuid4()}');
        INSERT INTO market_data.close_session_curves VALUES {rows};""")
    encoded = lambda value: "'" + json.dumps(value).replace("'", "''") + "'::jsonb"
    for code, frame in sorted(seeded, key=lambda item: item[1]['at']):
        close, at = str(uuid4()), frame['at']
        frame['closeId'] = close
        members = [{'curve': c, 'contractId': k, 'ordinal': n} for c in sorted(frame['curveContracts']) for n, k in enumerate(frame['curveContracts'][c])]
        # Final settlements and fixings are frozen facts, as recording requires (0056).
        query(f"""BEGIN;
        INSERT INTO market_data.market_closes(catalog_id,id,definition_revision_id,reference_catalog_id,reference_revision_id,name,at,kind,actor_id,request_id,request,
         publication_order,publication_xid,session_code,session_version,business_date)
        SELECT '{catalog}','{close}','{revision}',{reference},'Initial synthetic inputs','{at}','retained_input','{owner}','{uuid4()}','{{}}',
         coalesce(max(publication_order),0)+1,pg_current_xact_id(),'{code}',1,('{at}'::timestamptz AT TIME ZONE 'UTC')::date FROM market_data.market_closes WHERE catalog_id='{catalog}';
        INSERT INTO market_data.market_close_pins SELECT '{catalog}','{close}','{revision}',o.series_id,o.id,
         CASE WHEN o.role IN('final_contract_settlement','index_fixing') THEN 'frozen' ELSE 'variable' END
         FROM market_data.observations o WHERE o.catalog_id='{catalog}' AND o.id IN(SELECT jsonb_array_elements_text({encoded(frame['observationIds'])})::uuid);
        INSERT INTO market_data.market_close_members SELECT '{catalog}','{close}','{revision}',x.curve,x."contractId",x.ordinal FROM jsonb_to_recordset({encoded(members)})x(curve text,"contractId" uuid,ordinal integer);
        COMMIT;""")
    return manifest['frames']
