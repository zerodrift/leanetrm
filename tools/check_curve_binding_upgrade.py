"""Reviewed shared-query upgrade boundaries, inside an owned database only."""
import re
from assistant_context import check_guard, migration_body


def run_checks(query, migration):
    text=migration.read_text()
    signatures=re.findall(r"^\s*\('([^']+)','[a-f0-9]{64}'",text,re.MULTILINE)
    assert len(signatures)==9
    changes=[]
    for signature in signatures:
        changes.extend((change,signature) for change in (
            f"ALTER FUNCTION {signature} SET work_mem='64kB';",
            f"{'REVOKE' if signature.startswith('trading.side_schedule_parameters') else 'GRANT'} EXECUTE ON FUNCTION {signature} {'FROM' if signature.startswith('trading.side_schedule_parameters') else 'TO'} PUBLIC;",
            f"""DO $owner$ BEGIN EXECUTE format('ALTER FUNCTION {signature} OWNER TO %I',
             CASE WHEN (SELECT proowner FROM pg_proc WHERE oid='{signature}'::regprocedure)=current_user::regrole THEN 'openetrm_security_owner' ELSE current_user END);END$owner$;""",
            f"""DO $body$ DECLARE d text=pg_get_functiondef('{signature}'::regprocedure);BEGIN
             EXECUTE replace(d,'AS $function$','AS $function$ /* synthetic upgrade drift */ ');END$body$;"""))
    new_signature=next(signature for signature in signatures if signature.startswith('trading.require_gas_program_shape('))[:-1]+',jsonb)'
    changes.append((f"CREATE FUNCTION {new_signature} RETURNS void LANGUAGE plpgsql SECURITY DEFINER AS 'BEGIN RETURN;END';GRANT EXECUTE ON FUNCTION {new_signature} TO openetrm_runtime;",new_signature))
    check_guard(query,text,'0201','curve_binding_upgrade',changes)
    for table,constraint in [('trading.custom_curve_projection_bindings','custom_projection_coordinate'),('risk.job_custom_curves','custom_projection_coordinate'),('trading.side_schedule_rows','side_schedule_source_output_kind')]:
        try:query(f'BEGIN;ALTER TABLE {table} DROP CONSTRAINT {constraint};'+migration_body(text)+'ROLLBACK;')
        except RuntimeError as error:assert f'curve_binding_upgrade: unexpected constraint {table}' in str(error),error
        else:raise AssertionError('Unreviewed query constraint admitted')
    print('PASS curve query upgrade: all nine helpers refuse body/owner/configuration/ACL drift; selector/source constraints and independent caller search path are pinned',flush=True)
