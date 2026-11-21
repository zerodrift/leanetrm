"""Publish synthetic library fixtures with real, owner-specific validation inputs.

The disposable database owner loads source facts. Separate authenticated phases
publish schedules, mappings and definitions, so each validation sees committed
sources at a certified cutoff. No synthetic context records are manufactured.
"""
import re

BLOCK=re.compile(r'DO \$(rate_curve|contract_curve)\$ BEGIN IF .*? THEN\n(.*?)\nEND IF; END \$\1\$;',re.S)
MARKER=re.compile(r'-- fixture-validation: ([0-9a-f-]+) ([0-9-]+) ([0-9a-f-]+)\n')


def load_fixture(query,reference,market,security,manifest):
    """Load source and identity facts before authenticating library publication."""
    query(reference)
    remaining=load_market(query,market,security,manifest)
    if remaining:query(remaining)


def load_market(query,market,security,manifest,*,before_seal=None,resume_phase=None,identity=None):
    # Some interval fixtures place their author identities beside series rows.
    # Move those exact setup facts before the first authenticated publication.
    identity_prefixes=tuple('INSERT INTO security.'+name+' ' for name in
        ('tenants','workspaces','principals','identity_bindings','workspace_memberships'))
    identities=[line for line in market.splitlines() if line.startswith(identity_prefixes)]
    market='\n'.join(line for line in market.splitlines() if line not in identities)
    matches=[m for m in BLOCK.finditer(market) if MARKER.search(m.group(2))]
    if not matches:
        if before_seal is not None:raise ValueError('Contract admission requires phased library sources')
        query(market+'\n'+'\n'.join(identities))
        return security
    # The prefix creates the catalog and draft revision before scoped identities.
    prefix=market[:matches[0].start()]
    query(prefix+'\n'+'\n'.join(identities)+'\nCOMMIT;')
    # Exchange fixtures append ordinary trading setup to their auth setup.
    # Its curve FKs become valid only after the library phases complete.
    domain=re.search(r'(?m)^(?:INSERT INTO trading\.|DO \$rate_curve\$)',security)
    remaining_security=security[domain.start():] if domain else ''
    identity_security=security[:domain.start()] if domain else security
    if identity_security:query(identity_security)
    if query("SELECT to_regprocedure('security.initialize_operating_policies(uuid,uuid)') IS NOT NULL;")=='t':
        query(f"SELECT security.initialize_operating_policies('{manifest['tenantId']}','{manifest['workspaceId']}');")
    issuer=manifest.get('identityIssuer','https://preview.invalid')
    # Historical migration qualifiers deliberately install a predecessor schema.
    # Its fixture must use that schema's real admission operation too.
    current=query("SELECT to_regprocedure('security.resolve_operation_context(uuid,date,timestamptz)') IS NOT NULL;")=='t'
    capture='security.resolve_operation_context' if current else 'security.capture_date_context'
    finish='SELECT security.finish_operation_read();' if current else ''
    offset=matches[0].start()
    for match in matches:
        intervening=market[offset:match.start()]
        if intervening.strip():query('BEGIN;\n'+intervening+'\nCOMMIT;')
        body=match.group(2).split('\nELSE\n',1)[0]
        source=MARKER.search(body)
        catalog,day,placeholder=source.groups()
        body=MARKER.sub('',body).replace("'"+placeholder+"'",'fixture_validation_id')
        guard=''
        if body.startswith('IF NOT EXISTS('):
            guard,body=body.split('\n',1)
        # Schedules commit before mapping validation; the mapping commits before
        # definition validation. Their exact dependencies are never backfilled.
        mapping_at=body.index('INSERT INTO market_data.contract_date_definitions')
        curve_at=body.index('INSERT INTO market_data.curve_definitions')
        schedules=body[:mapping_at]
        mapping=body[mapping_at:curve_at]
        curve=body[curve_at:]
        phases=[(schedules,False),(mapping,True),(curve,True)]
        for phase_index,(phase,requires_inputs) in enumerate(phases):
            if not phase.strip():continue
            if guard:
                phase=guard+'\n'+phase
                if phase.count('IF NOT EXISTS(')>phase.count('END IF;'):phase+='\nEND IF;'
            if resume_phase is not None:phase=resume_phase(phase_index,phase)
            initialize=(f"fixture_validation_id={capture}('{catalog}','{day}',NULL);" if requires_inputs else '')
            authority=''
            auth,runtime='preview_authenticator','preview_runtime'
            issue=f"security.issue_context('{issuer}','editor','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',120)"
            if identity is not None:
                auth,runtime,digest=(identity[key] for key in ('authenticator','runtime','sessionDigest'))
                if not all(re.fullmatch('[a-z][a-z0-9_]*',role) for role in (auth,runtime)) or not re.fullmatch('[0-9a-f]{64}',digest):
                    raise ValueError('Supply the exact hosted seed identity')
                issue=f"security.issue_hosted_context('{digest}','{runtime}')"
                authority=(f"PERFORM security.require_hosted_session();IF security.custom_curve_actor('{catalog}','WriteMarketDefinitions') IS NULL "
                    f"OR security.custom_curve_actor('{catalog}','SealMarketDefinitions') IS NULL THEN RAISE EXCEPTION 'regional_seed_denied' USING ERRCODE='42501';END IF;")
            # A session-local function disappears even if the loader is killed.
            # Its declaration is invisible to other sessions before this commit.
            query(f"""BEGIN;
CREATE TEMP TABLE seed_phase_scope(marker boolean) ON COMMIT DROP;
CREATE FUNCTION pg_temp.seed_library_phase() RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $fixture$
DECLARE fixture_validation_id uuid;BEGIN {authority}{initialize}\n{phase}\n{authority}
EXECUTE format('REVOKE USAGE ON SCHEMA %I FROM %I',pg_my_temp_schema()::regnamespace::text,'{runtime}');
END$fixture$;
REVOKE ALL ON FUNCTION pg_temp.seed_library_phase() FROM PUBLIC;
DO $temp_grant$ BEGIN EXECUTE format('GRANT USAGE ON SCHEMA %I TO %I',pg_my_temp_schema()::regnamespace::text,'{runtime}');END $temp_grant$;
GRANT EXECUTE ON FUNCTION pg_temp.seed_library_phase() TO {runtime};
SET LOCAL SESSION AUTHORIZATION {auth};SELECT {issue} AS token \\gset
SET LOCAL SESSION AUTHORIZATION {runtime};SELECT security.use_context(:'token');
SELECT pg_temp.seed_library_phase();{finish}
{('DO $seed_commit$ BEGIN '+authority+' END $seed_commit$;') if authority else ''}COMMIT;""")
        offset=match.end()
    suffix=market[offset:]
    if before_seal is not None:
        marker='UPDATE market_data.revisions SET'
        if suffix.count(marker)!=1:raise ValueError('Select the exact fixture revision sealing point')
        prefix,suffix=suffix.split(marker,1)
        if prefix.strip():query('BEGIN;\n'+prefix+'\nCOMMIT;')
        before_seal()
        suffix=marker+suffix
    query('BEGIN;\n'+suffix)
    return remaining_security
