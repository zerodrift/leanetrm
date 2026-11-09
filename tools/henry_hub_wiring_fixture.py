"""Admit explicit synthetic forward inputs into the separate owned Henry Hub bundle."""
from copy import deepcopy
from henry_hub_fixture import qualify


def admit(query, manifest):
    manifest = deepcopy(manifest)
    qualify(query, manifest)
    from check_henry_hub_forward_inputs import run_checks as check_forward_inputs
    manifest['curveMarkets']=[check_forward_inputs(query,manifest)]
    # The installation command requires both administration and the same business
    # permissions as its public operations; no role's permission list is changed.
    query(f"""INSERT INTO security.role_grants SELECT gen_random_uuid(),b.principal_id,r.id,'{manifest['tenantId']}','{manifest['workspaceId']}',
 clock_timestamp()-interval '1 minute',clock_timestamp()+interval '1 day',false
 FROM security.identity_bindings b CROSS JOIN security.roles r WHERE b.issuer='https://preview.invalid' AND b.subject='admin'
 AND r.id IN('a0000000-0000-0000-0000-000000000004','a0000000-0000-0000-0000-000000000005');""")
    return manifest
