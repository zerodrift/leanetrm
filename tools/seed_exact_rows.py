"""Exact, append-only source facts for the explicitly selected synthetic seed.

The seed owner may load source facts into new revisions. A repeated fact is a
no-op; an existing key with different supplied fields is a structured refusal.
Publication and validation remain the native library operations.
"""
import json
import re


def literal(value):
    return "'" + str(value).replace("'", "''") + "'"


def hosted_write(query, sql, catalog, reference_catalog, identity):
    """An owner's source-load capability, fenced by the real hosted actor.

    Runtime callers never receive table-owner rights or a durable helper. Both
    admission and commit require the declared administrator/author permissions.
    All supplied source facts commit atomically or roll back on session expiry.
    """
    if identity is None:
        return query(sql)
    auth, runtime, digest = (identity[key] for key in ('authenticator','runtime','sessionDigest'))
    if not all(re.fullmatch('[a-z][a-z0-9_]*',role) for role in (auth,runtime)) or not re.fullmatch('[0-9a-f]{64}',digest):
        raise ValueError('Supply the exact hosted seed identity')
    body = sql.strip()
    if body.startswith('BEGIN;'):
        body = body[6:].strip()
    if body.endswith('COMMIT;'):
        body = body[:-7].strip()
    if re.search(r'\b(BEGIN|COMMIT|ROLLBACK)\s*;', body, re.I):
        raise ValueError('A hosted source phase must be one atomic transaction')
    permissions = ('ReadMarketData','WriteMarketDefinitions','SealMarketDefinitions','ImportObservations','ManageAccess','BookTrade')
    check = ' '.join(f"IF security.market_access({literal(catalog)},{literal(permission)}) IS NOT TRUE THEN RAISE EXCEPTION 'regional_seed_denied' USING ERRCODE='42501';END IF;" for permission in permissions)
    check += ' '.join(f"IF security.reference_access({literal(reference_catalog)},{literal(permission)}) IS NOT TRUE THEN RAISE EXCEPTION 'regional_seed_denied' USING ERRCODE='42501';END IF;" for permission in ('WriteReferenceData','SealReferenceData'))
    check = "PERFORM security.require_hosted_session();IF security.current_write_context() IS NOT TRUE THEN RAISE EXCEPTION 'regional_seed_denied' USING ERRCODE='42501';END IF;"+check
    return query(f"""BEGIN;
CREATE TEMP TABLE seed_phase_scope(marker boolean) ON COMMIT DROP;
CREATE FUNCTION pg_temp.regional_seed_source() RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $seed_owner$
BEGIN {check} EXECUTE {literal(body)}; {check}
EXECUTE format('REVOKE USAGE ON SCHEMA %I FROM %I',pg_my_temp_schema()::regnamespace::text,'{runtime}');
END $seed_owner$;
REVOKE ALL ON FUNCTION pg_temp.regional_seed_source() FROM PUBLIC;
DO $temp_grant$ BEGIN EXECUTE format('GRANT USAGE ON SCHEMA %I TO %I',pg_my_temp_schema()::regnamespace::text,'{runtime}');END $temp_grant$;
GRANT EXECUTE ON FUNCTION pg_temp.regional_seed_source() TO {runtime};
SET LOCAL SESSION AUTHORIZATION {auth};SELECT security.issue_hosted_context('{digest}','{runtime}') AS token \\gset
SET LOCAL SESSION AUTHORIZATION {runtime};SELECT security.use_context(:'token');
SELECT pg_temp.regional_seed_source();SELECT security.finish_operation_read();
DO $seed_commit$ BEGIN {check} END $seed_commit$;COMMIT;""")


def rows_sql(table, rows, keys, *, compare=None):
    if not re.fullmatch(r'(ref_data|market_data)\.[a-z][a-z0-9_]*', table):
        raise ValueError('Select an explicit seed source table')
    if not rows:
        return ''
    columns = tuple(rows[0])
    if any(tuple(row) != columns for row in rows) or not set(keys) <= set(columns):
        raise ValueError('Seed rows require one explicit field and key contract')
    if any(not re.fullmatch('[a-z][a-z0-9_]*', column) for column in columns):
        raise ValueError('Invalid seed field')
    predicate = ' AND '.join(f't.{key}=wanted.{key}' for key in keys)
    compared = columns if compare is None else tuple(compare)
    if not set(compared) <= set(columns) or not set(keys) <= set(compared):
        raise ValueError('Seed comparison must include its exact business key')
    actual = ','.join('existing.' + column for column in compared)
    expected = ','.join('wanted.' + column for column in compared)
    payload = literal(json.dumps(rows, separators=(',', ':')))
    return f"""DO $regional_seed$
DECLARE wanted {table};existing {table};
BEGIN
 FOR wanted IN SELECT * FROM jsonb_populate_recordset(NULL::{table},{payload}::jsonb) LOOP
  SELECT * INTO existing FROM {table} t WHERE {predicate};
  IF FOUND THEN
   IF ROW({actual}) IS DISTINCT FROM ROW({expected}) THEN
    RAISE EXCEPTION 'regional_seed_conflict' USING ERRCODE='P0001';
   END IF;
  ELSE
   INSERT INTO {table}({','.join(columns)}) VALUES({','.join('wanted.' + column for column in columns)});
  END IF;
 END LOOP;
END $regional_seed$;"""
