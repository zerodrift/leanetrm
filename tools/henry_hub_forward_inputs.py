"""Prepare a bounded copy of explicitly pinned synthetic Henry forward observations; never apply it.

The primary bundle is read-only. Prices, observation times, roles, contract members and frozen selections all come from
the supplied source facts. Only destination observation identities, synthetic provenance and receipt time are new.
"""
from copy import deepcopy
from datetime import datetime, timedelta
import json
import re
from uuid import UUID, uuid5
from henry_hub_support import CURVES, validate_spec

RAW_CURVES = ('hhd_daily', 'hhd_daily_basis', 'h_monthly', 'h_monthly_basis')
META_FIELDS = {'catalog_id', 'revision_id', 'reference_catalog_id', 'reference_revision_id', 'curve_code', 'contract_id',
               'series_id', 'quote_code', 'ordinal', 'rule', 'direct_source_code', 'direct_role', 'source_kind'}
OBS_FIELDS = {'catalog_id', 'id', 'definition_revision_id', 'series_id', 'reference_catalog_id', 'reference_revision_id',
              'source_code', 'source_event_id', 'source_revision_id', 'role', 'publication_state', 'observed_at', 'received_at', 'value', 'corrects_id'}


def _instant(text):
    if not isinstance(text, str) or not re.fullmatch(r'\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:\.\d{1,6})?(?:Z|\+00(?::00)?)', text):
        raise ValueError('Captured instants must be explicit exact UTC microseconds.')
    value = datetime.fromisoformat(text)
    if value.utcoffset() != timedelta(0): raise ValueError('Captured instant is not UTC.')
    return value


def _uuid(text):
    if not isinstance(text, str) or str(UUID(text)) != text or UUID(text).int == 0:
        raise ValueError('Supply exact nonempty identities.')
    return text


def prepare(spec, source_manifest, source_observation_rows, source_series_metadata):
    """Return {sql,curveMarket} from captured native rows and their original primary frame selections.

    Metadata is an explicit series/member/direct-curve/source join with META_FIELDS. Observation rows have OBS_FIELDS,
    including value as decimal text. Unrelated primary frame pins may remain in the manifest, but supplied rows must all
    belong to the four raw curves and be selected by at least one frame. A frame's optional explicit requiredSeries list
    narrows its raw-input expectation (including an empty list); its six memberships remain unchanged. No expiry or latest
    price selection is performed, and omitted raw inputs are never manufactured.
    """
    validate_spec(spec)
    cat, old, new = (spec[k] for k in ('catalog_id', 'source_market_revision_id', 'target_market_revision_id'))
    ref, old_ref, new_ref = (spec[k] for k in ('reference_catalog_id', 'source_reference_revision_id', 'target_reference_revision_id'))
    if any(source_manifest.get(key) != expected for key, expected in
           [('catalogId', cat), ('revisionId', old), ('referenceCatalogId', ref), ('referenceRevisionId', old_ref)]):
        raise ValueError('The captured primary manifest does not match the explicit source bundle.')
    if not source_series_metadata or len(source_series_metadata) > 2000 or not isinstance(source_observation_rows, list) or len(source_observation_rows) > 100000:
        raise ValueError('Supply bounded captured native series and observations.')
    metadata, membership = {}, {}
    for row in source_series_metadata:
        if set(row) != META_FIELDS or (row['catalog_id'], row['revision_id'], row['reference_catalog_id'], row['reference_revision_id']) != (cat, old, ref, old_ref) or \
           row['curve_code'] not in RAW_CURVES or row['rule'] != 'direct' or row['source_kind'] != 'synthetic' or \
           row['direct_role'] not in ('daily_settlement', 'trader_mark') or type(row['ordinal']) is not int or row['ordinal'] < 0:
            raise ValueError('Only exact native direct curves from synthetic sources are supported.')
        series, contract = _uuid(row['series_id']), _uuid(row['contract_id'])
        if series in metadata or (row['curve_code'], contract) in membership:
            raise ValueError('A captured source member must have one exact price series.')
        metadata[series] = row; membership[row['curve_code'], contract] = series
    if {row['curve_code'] for row in metadata.values()} != set(RAW_CURVES):
        raise ValueError('Capture all four raw Henry curve families explicitly.')
    observations = {}
    for row in source_observation_rows:
        if set(row) != OBS_FIELDS or row['series_id'] not in metadata or row['publication_state'] != 'final' or \
           (row['catalog_id'], row['definition_revision_id'], row['reference_catalog_id'], row['reference_revision_id']) != (cat, old, ref, old_ref):
            raise ValueError('Every source observation must be an exact final synthetic raw input.')
        source = metadata[row['series_id']]
        roles = {source['direct_role']} | ({'final_contract_settlement'} if source['direct_role'] == 'daily_settlement' else set())
        if row['source_code'] != source['direct_source_code'] or row['role'] not in roles or \
           not isinstance(row['value'], str) or not re.fullmatch(r'-?\d{1,27}(?:\.\d{1,18})?', row['value']):
            raise ValueError('Source, role and exact decimal value must match the captured native price input.')
        if _instant(row['observed_at']) > _instant(row['received_at']): raise ValueError('Invalid source receipt chronology.')
        key = _uuid(row['id'])
        if key in observations: raise ValueError('Duplicate captured observation identity.')
        if row['corrects_id'] is not None: _uuid(row['corrects_id'])
        observations[key] = row
    destination = {key: str(uuid5(UUID(cat), 'synthetic_henry_forward_v1:' + new + ':' + key)) for key in observations}
    frames = source_manifest.get('frames')
    if not isinstance(frames, list) or not 1 <= len(frames) <= 256: raise ValueError('Supply bounded explicit source frames.')
    target_frames, used, instants = [], set(), set()
    physical = {curve: {r['contract_id'] for r in spec['fixings'] if r['curve'] == curve} for curve in ('hhd_location', 'h_location')}
    for frame in frames:
        at = _instant(frame['at'])
        if at in instants: raise ValueError('A source market requires one exact frame per instant.')
        instants.add(at)
        members = frame.get('curveContracts', {})
        if any(not isinstance(members.get(curve), list) or not members[curve] or len(set(members[curve])) != len(members[curve]) for curve in CURVES) or \
           any(set(members[curve]) != contracts for curve, contracts in physical.items()):
            raise ValueError('Each frame must explicitly retain all six curve memberships and physical target coverage.')
        expected = set()
        for curve in RAW_CURVES:
            for contract in members[curve]:
                if (curve, contract) not in membership: raise ValueError('Frame membership has no exact captured raw series.')
                expected.add(membership[curve, contract])
        if 'requiredSeries' in frame:
            required = frame['requiredSeries']
            if not isinstance(required, list) or len(set(required)) != len(required) or not set(required) <= expected:
                raise ValueError('The explicit required series must be unique members of this exact frame.')
            expected = set(required)
        regular, frozen = frame.get('observationIds'), frame.get('frozenObservationIds', [])
        if frozen is None: frozen = []
        if not isinstance(regular, list) or not isinstance(frozen, list) or len(set(regular)) != len(regular) or len(set(frozen)) != len(frozen):
            raise ValueError('Frame observation selections must be explicit and unique.')
        selected = [key for key in dict.fromkeys(regular + frozen) if key in observations]
        facts = [observations[key] for key in selected]
        if {row['series_id'] for row in facts} != expected or len({(r['series_id'], r['role']) for r in facts}) != len(facts) or \
           any(_instant(row['observed_at']) > at for row in facts):
            raise ValueError('Each frame requires complete, unambiguous, already-issued exact raw inputs.')
        used.update(selected)
        target = dict(at=frame['at'], observationIds=[destination[key] for key in regular if key in observations],
                      curveContracts={curve: deepcopy(members[curve]) for curve in CURVES})
        if any(key in observations for key in frozen): target['frozenObservationIds'] = [destination[key] for key in frozen if key in observations]
        target_frames.append(target)
    if used != set(observations): raise ValueError('Every supplied raw observation must be explicitly pinned by a captured source frame.')
    literal = lambda text: "'" + str(text).replace("'", "''") + "'"
    metadata_json = literal(json.dumps(source_series_metadata))
    observations_json = literal(json.dumps(source_observation_rows))
    identities_json = literal(json.dumps([dict(old_id=old_id, new_id=new_id) for old_id, new_id in destination.items()]))
    sql = f"""-- Prepared synthetic forward inputs only; primary facts and frame selection are unchanged.
BEGIN;
SET LOCAL lock_timeout='5s';SET LOCAL statement_timeout='60s';SET LOCAL search_path=pg_catalog;
SELECT pg_advisory_xact_lock(730849102);
CREATE TEMP TABLE henry_forward_sources(LIKE market_data.observations) ON COMMIT DROP;
INSERT INTO henry_forward_sources SELECT * FROM jsonb_populate_recordset(NULL::market_data.observations,{observations_json}::jsonb);
CREATE TEMP TABLE henry_forward_metadata(catalog_id uuid,revision_id uuid,reference_catalog_id uuid,reference_revision_id uuid,
 curve_code text,contract_id uuid,series_id uuid,quote_code text,ordinal integer,rule text,direct_source_code text,direct_role text,source_kind text) ON COMMIT DROP;
INSERT INTO henry_forward_metadata SELECT * FROM jsonb_populate_recordset(NULL::henry_forward_metadata,{metadata_json}::jsonb);
CREATE TEMP TABLE henry_forward_identities(old_id uuid,new_id uuid) ON COMMIT DROP;
INSERT INTO henry_forward_identities SELECT * FROM jsonb_populate_recordset(NULL::henry_forward_identities,{identities_json}::jsonb);
CREATE TEMP TABLE henry_forward_expected(LIKE market_data.observations) ON COMMIT DROP;
DO $inputs$
BEGIN
 IF NOT EXISTS(SELECT 1 FROM market_data.revisions WHERE(catalog_id,id,reference_catalog_id,reference_revision_id,state)=('{cat}','{old}','{ref}','{old_ref}','sealed'))
 OR NOT EXISTS(SELECT 1 FROM market_data.revisions WHERE(catalog_id,id,reference_catalog_id,reference_revision_id,state)=('{cat}','{new}','{ref}','{new_ref}','sealed'))
 THEN RAISE EXCEPTION 'henry_forward_bundle';END IF;
 IF EXISTS(SELECT 1 FROM henry_forward_sources e LEFT JOIN market_data.observations a ON(a.catalog_id,a.id)=(e.catalog_id,e.id)
  WHERE to_jsonb(a) IS DISTINCT FROM to_jsonb(e)) THEN RAISE EXCEPTION 'henry_forward_source_fact';END IF;
 IF EXISTS(SELECT 1 FROM henry_forward_metadata e
  LEFT JOIN market_data.curves c ON(c.catalog_id,c.revision_id,c.code)=(e.catalog_id,e.revision_id,e.curve_code)
  LEFT JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.curve_code,m.contract_id)=(e.catalog_id,e.revision_id,e.curve_code,e.contract_id)
  LEFT JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.id)=(e.catalog_id,e.revision_id,e.series_id)
  LEFT JOIN ref_data.sources p ON(p.catalog_id,p.revision_id,p.code)=(e.reference_catalog_id,e.reference_revision_id,e.direct_source_code)
  LEFT JOIN market_data.series n ON(n.catalog_id,n.revision_id,n.id)=(e.catalog_id,'{new}'::uuid,e.series_id)
  LEFT JOIN market_data.curves nc ON(nc.catalog_id,nc.revision_id,nc.code)=(e.catalog_id,'{new}'::uuid,e.curve_code)
  LEFT JOIN market_data.curve_members nm ON(nm.catalog_id,nm.revision_id,nm.curve_code,nm.contract_id)=(e.catalog_id,'{new}'::uuid,e.curve_code,e.contract_id)
  WHERE(c.reference_catalog_id,c.reference_revision_id,c.quote_code,c.rule,c.direct_source_code,c.direct_role,m.ordinal,s.contract_id,s.quote_code,s.reference_catalog_id,s.reference_revision_id,p.kind)
   IS DISTINCT FROM(e.reference_catalog_id,e.reference_revision_id,e.quote_code,e.rule,e.direct_source_code,e.direct_role,e.ordinal,e.contract_id,e.quote_code,e.reference_catalog_id,e.reference_revision_id,e.source_kind)
  OR to_jsonb(n) IS DISTINCT FROM(to_jsonb(s)||jsonb_build_object('revision_id','{new}','reference_revision_id','{new_ref}'))
  OR to_jsonb(nc) IS DISTINCT FROM(to_jsonb(c)||jsonb_build_object('revision_id','{new}','reference_revision_id','{new_ref}'))
  OR nm.ordinal IS DISTINCT FROM m.ordinal) THEN RAISE EXCEPTION 'henry_forward_source_metadata';END IF;
END $inputs$;
INSERT INTO henry_forward_expected SELECT o.catalog_id,i.new_id,'{new}',o.series_id,o.reference_catalog_id,'{new_ref}',o.source_code,
 'synthetic_henry_forward_v1:{new}:'||o.id::text,'initial',o.role,o.publication_state,o.observed_at,clock_timestamp(),o.value,NULL
 FROM henry_forward_sources o JOIN henry_forward_identities i ON i.old_id=o.id;
DO $replay$
BEGIN
 IF EXISTS(SELECT 1 FROM henry_forward_expected e JOIN market_data.observations a ON a.catalog_id=e.catalog_id AND
  (a.id=e.id OR(a.source_code,a.source_event_id,a.source_revision_id)=(e.source_code,e.source_event_id,e.source_revision_id))
  WHERE(to_jsonb(a)-'received_at') IS DISTINCT FROM(to_jsonb(e)-'received_at')) THEN RAISE EXCEPTION 'henry_forward_replay_conflict';END IF;
END $replay$;
INSERT INTO market_data.observations SELECT e.* FROM henry_forward_expected e
 WHERE NOT EXISTS(SELECT 1 FROM market_data.observations a WHERE(a.catalog_id,a.id)=(e.catalog_id,e.id));
COMMIT;
"""
    return dict(sql=sql, curveMarket=dict(name='Synthetic Henry Hub publication market', revisionId=new, frames=target_frames))
