"""Record a preview's built-in frames as D334 session closes in its owned synthetic database.

Only recorded closes are closes: each built-in frame becomes the close of a synthetic UTC session whose cutoff is the
frame's time of day and whose curves are the frame's. A later frame on the same session day replaces an earlier one.
Frames that belong to a separate bundle (PPA or swap selections), fall between minutes or select a curve with no remaining
contracts stay working snapshots.
"""
import json
from uuid import uuid4

# The synthetic reference data's UTC calendar with no weekend: built-in frames fall on any calendar day.
CALENDAR = 'test_utc_every_day'


def owner_of(query, manifest):
    """The synthetic approver, who defines, publishes and records the seeded sessions and closes."""
    return query(f"SELECT principal_id FROM security.workspace_memberships WHERE(tenant_id,workspace_id,login_handle)=('{manifest['tenantId']}','{manifest['workspaceId']}','approver');")


def publish_session(query, manifest, code, name, versions, calendar=CALENDAR, owner=None):
    """Insert one published session with its versions in order, each {'curves', 'cutoff', 'from', 'to'}."""
    catalog, revision = manifest['catalogId'], manifest['revisionId']
    reference = f"'{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}'"
    owner = owner or owner_of(query, manifest)
    query(f"INSERT INTO market_data.close_sessions VALUES('{catalog}','{code}','{name}');")
    for number, version in enumerate(versions, 1):
        rows = ','.join(f"('{catalog}','{code}',{number},'{revision}','{c}')" for c in version['curves'])
        query(f"""INSERT INTO market_data.close_session_versions(catalog_id,code,version,state,generation,definition_revision_id,reference_catalog_id,reference_revision_id,
         calendar_code,cutoff_local,effective_from,effective_to,actor_id,request_id,request,publisher_id,publish_request_id)
        VALUES('{catalog}','{code}',{number},'published',2,'{revision}',{reference},'{calendar}','{version['cutoff']}','{version['from']}','{version['to']}','{owner}','{uuid4()}','{{}}','{owner}','{uuid4()}');
        INSERT INTO market_data.close_session_curves VALUES {rows};""")


def record_close(query, manifest, code, version, frame, owner=None, retained=()):
    """Record a frame as the next close of a session version on its UTC business day; return the close id. A later close of the
    same session and day replaces it."""
    catalog, revision = manifest['catalogId'], manifest['revisionId']
    reference = f"'{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}'"
    owner = owner or owner_of(query, manifest)
    encoded = lambda value: "'" + json.dumps(value).replace("'", "''") + "'::jsonb"
    close, at = str(uuid4()), frame['at']
    parts = []
    for definition, selected in [(revision, frame), *retained]:
        members = [{'curve': curve, 'contractId': contract, 'ordinal': ordinal}
                   for curve, contracts in selected['curveContracts'].items() for ordinal, contract in enumerate(contracts)]
        if selected['observationIds'] or members:
            parts.append(dict(definition=definition, pins=selected['observationIds'],
                              frozen=selected.get('frozenObservationIds', []), members=members))
    matrix = encoded(parts)
    # Final settlements and fixings are frozen facts, as recording requires (0056).
    query(f"""BEGIN;
    INSERT INTO market_data.market_closes(catalog_id,id,definition_revision_id,reference_catalog_id,reference_revision_id,name,at,kind,actor_id,request_id,request,
     publication_order,publication_xid,session_code,session_version,business_date)
    SELECT '{catalog}','{close}','{revision}',{reference},'Initial synthetic inputs','{at}','retained_input','{owner}','{uuid4()}','{{}}',
    coalesce(max(publication_order),0)+1,pg_current_xact_id(),'{code}',{version},('{at}'::timestamptz AT TIME ZONE 'UTC')::date FROM market_data.market_closes WHERE catalog_id='{catalog}';
    INSERT INTO market_data.market_close_sources
    SELECT r.catalog_id,'{close}',r.id,r.reference_catalog_id,r.reference_revision_id
    FROM jsonb_to_recordset({matrix}) p(definition uuid)
    JOIN market_data.revisions r ON(r.catalog_id,r.id)=('{catalog}',p.definition)
    WHERE r.id<>'{revision}';
    INSERT INTO market_data.market_close_pins
    SELECT o.catalog_id,'{close}',p.definition,o.series_id,o.id,
     CASE WHEN o.role IN('final_contract_settlement','index_fixing')
      OR p.frozen ? o.id::text THEN 'frozen' ELSE 'variable' END
    FROM jsonb_to_recordset({matrix}) p(definition uuid,pins jsonb,frozen jsonb)
    CROSS JOIN LATERAL jsonb_array_elements_text(p.pins) pin(id)
    JOIN market_data.observations o ON(o.catalog_id,o.definition_revision_id,o.id)=('{catalog}',p.definition,pin.id::uuid);
    INSERT INTO market_data.market_close_members
    SELECT '{catalog}','{close}',p.definition,m.curve,m."contractId",m.ordinal
    FROM jsonb_to_recordset({matrix}) p(definition uuid,members jsonb)
    CROSS JOIN LATERAL jsonb_to_recordset(p.members) m(curve text,"contractId" uuid,ordinal integer);
    COMMIT;""")
    return close


def seed(query, manifest, working=(), input_markets=()):
    """Insert the sessions and closes; return the manifest frames, each seeded one naming its close. Frames at the instant
    prefixes in `working` stay working snapshots, for a check that needs one where a close would otherwise be."""
    owner = owner_of(query, manifest)
    # One session per cutoff time; consecutive days carrying the same curves share a version, and a change of curves starts the
    # next version, so every seeded close carries exactly its version's curves. Two frames of one day at one cutoff with
    # different curves need a second session.
    seeded, versions = [], {}
    for frame in sorted(manifest['frames'], key=lambda f: f['at']):
        # A session cuts off on a whole minute; a frame between minutes stays a working snapshot. So does one that selects a
        # curve with no remaining contracts, which a close records no members for.
        if (frame['at'].startswith(tuple(working)) or frame.get('ppaBundle') or frame.get('swapMarketSelections') or not frame['observationIds'] or not frame['curveContracts']
                or not all(frame['curveContracts'].values()) or frame['at'][16:19] != ':00' or frame['at'][19:20] not in ('', 'Z', '+')):
            continue
        cutoff, day, curves = frame['at'][11:16], frame['at'][:10], tuple(sorted(frame['curveContracts']))
        lane = 0
        while True:
            code = f"demo_close_{cutoff.replace(':', '')}" + (f'_{lane}' if lane else '')
            chain = versions.setdefault(code, [])
            if not chain or chain[-1]['days'][-1] < day or chain[-1]['curves'] == curves:
                break
            lane += 1
        if not chain or chain[-1]['curves'] != curves:
            chain.append({'curves': curves, 'days': [day], 'cutoff': cutoff})
        else:
            chain[-1]['days'].append(day)
        seeded.append((code, len(chain), frame))
    for code, chain in versions.items():
        publish_session(query, manifest, code, f"Synthetic {chain[0]['cutoff']} UTC close",
                        [{'curves': version['curves'], 'cutoff': version['cutoff'], 'from': version['days'][0] if number > 1 else '2020-01-01',
                          'to': chain[number]['days'][0] if number < len(chain) else '2100-01-01'} for number, version in enumerate(chain, 1)], owner=owner)
    for code, version, frame in seeded:
        retained = [(market['revisionId'], selected) for market in input_markets
                    for selected in market['frames'] if selected['at'] == frame['at']]
        if len({definition for definition, _ in retained}) != len(retained):
            raise ValueError('A recorded fixture close requires one selection per exact source.')
        frame['closeId'] = record_close(query, manifest, code, version, frame, owner, retained)
    # Each synthetic member has chosen the session with the most built-in closes, as a person would once (D334); that is a
    # stored choice, so pages open on it, and a check that needs no choice clears it.
    if versions:
        chosen = max(versions, key=lambda code: sum(len(version['days']) for version in versions[code]))
        query(f"""INSERT INTO presentation.close_session_choices SELECT tenant_id,workspace_id,principal_id,'{manifest['catalogId']}','{chosen}',clock_timestamp()
        FROM security.workspace_memberships WHERE(tenant_id,workspace_id)=('{manifest['tenantId']}','{manifest['workspaceId']}');""")
    return manifest['frames']
