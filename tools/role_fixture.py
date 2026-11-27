"""Roles made in a check for an identity that must lack something its installed roles hold."""
from uuid import uuid4


def hold_without(query, manifest, principal, *permissions):
    """Give `principal` one new role in place of every role it holds. The role carries each permission the principal
    holds now except `permissions`, from now on. The installed roles stay as installed. Returns the role's id."""
    if not permissions:
        raise ValueError('Name the permissions this identity must lack.')
    tenant, workspace = manifest['tenantId'], manifest['workspaceId']
    held = ("SELECT p.code FROM security.permissions p WHERE security.principal_has_permission("
            f"'{principal}','{tenant}','{workspace}',p.code,clock_timestamp());")
    kept = set(query(held).split()) - set(permissions)
    if not kept:
        # Every later refusal would then be about holding nothing, not about the named permissions.
        raise ValueError('This identity would hold no permission at all.')
    role = str(uuid4())
    codes = ','.join("'" + code.replace("'", "''") + "'" for code in sorted(kept))
    query(f"BEGIN;INSERT INTO security.roles(id,code,name) VALUES('{role}','synthetic_holder_{role[:8]}','Synthetic holder {role[:8]}');"
          f"INSERT INTO security.role_permissions SELECT '{role}'::uuid,code FROM security.permissions WHERE code IN({codes});"
          f"DELETE FROM security.role_grants WHERE principal_id='{principal}';"
          f"INSERT INTO security.role_grants VALUES(gen_random_uuid(),'{principal}','{role}','{tenant}','{workspace}',"
          "clock_timestamp(),'9999-12-31',false);COMMIT;")
    now = set(query(held).split())
    assert now == kept, 'The identity does not hold exactly what it held without the named permissions: ' + ','.join(sorted(now ^ kept))
    return role


def person(query, issuer, subject):
    """The principal of a fixture person, by sign-in name."""
    return query(f"SELECT principal_id FROM security.identity_bindings WHERE(issuer,subject)=('{issuer}','{subject}');")
