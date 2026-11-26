"""Roles made in a check for an identity that must lack something its installed roles hold."""
from uuid import uuid4


def hold_without(query, manifest, principal, *permissions):
    """Give `principal` one new role in place of every role it holds. The role carries each permission the principal
    holds now except `permissions`, from now on. The installed roles stay as installed. Returns the role's id."""
    if not permissions:
        raise ValueError('Name the permissions this identity must lack.')
    role = str(uuid4())
    withheld = ','.join("'" + permission.replace("'", "''") + "'" for permission in permissions)
    query(f"BEGIN;INSERT INTO security.roles(id,code,name) VALUES('{role}','synthetic_holder_{role[:8]}','Synthetic holder {role[:8]}');"
          f"INSERT INTO security.role_permissions SELECT DISTINCT '{role}'::uuid,p.permission FROM security.role_permissions p "
          f"JOIN security.role_grants g ON g.role_id=p.role_id WHERE g.principal_id='{principal}' AND NOT g.revoked "
          f"AND p.permission NOT IN({withheld});"
          f"DELETE FROM security.role_grants WHERE principal_id='{principal}';"
          f"INSERT INTO security.role_grants VALUES(gen_random_uuid(),'{principal}','{role}','{manifest['tenantId']}',"
          f"'{manifest['workspaceId']}',clock_timestamp(),'9999-12-31',false);COMMIT;")
    return role


def person(query, issuer, subject):
    """The principal of a fixture person, by sign-in name."""
    return query(f"SELECT principal_id FROM security.identity_bindings WHERE(issuer,subject)=('{issuer}','{subject}');")
