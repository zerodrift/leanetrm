"""Startup metadata is a fresh projection, never business-read authority."""
from uuid import UUID


def run_checks(c, check, query):
    legacy=c.call('bootstrap?includeFrames=false')
    check('legacy bootstrap keeps its response shape without an access projection',
          'access' not in legacy and c.call('bootstrap?includeFrames=false&includeAccess=false').keys()==legacy.keys())
    for profile in ['admin','trader','viewer']:
        c.call('profile',{'profile':profile})
        startup=c.call('bootstrap?includeFrames=false&includeAccess=true')
        check('startup permissions match a fresh access read for '+profile,
              startup['access']=={'profile':profile,'permissions':c.call('access')['permissions']}
              and startup['frames']==[] and startup['profile']==profile)
        if profile=='admin':
            check('administration startup metadata does not require market-data authority',
                  'ManageAccess' in startup['access']['permissions'] and 'ReadMarketData' not in startup['access']['permissions'])
            c.call('workspace/catalog',expected=403)
    c.call('bootstrap?includeAccess=unknown',expected=400)
    check('invalid startup access selection is rejected explicitly')
    bindings=query("SELECT issuer FROM security.identity_bindings WHERE subject='viewer';").splitlines()
    if len(bindings)!=1:raise AssertionError('Owned viewer identity binding is ambiguous or missing')
    issuer=bindings[0].replace("'","''")
    grants=query(f"SELECT g.id FROM security.role_grants g JOIN security.identity_bindings b ON b.principal_id=g.principal_id WHERE b.issuer='{issuer}' AND b.subject='viewer' AND NOT g.revoked ORDER BY g.id;").splitlines()
    if not grants:raise AssertionError('Owned viewer has no active grant before bootstrap revocation check')
    ids=','.join("'"+str(UUID(value))+"'" for value in grants)
    before=c.call('bootstrap?includeFrames=false&includeAccess=true')
    try:
        query(f'UPDATE security.role_grants SET revoked=true WHERE id IN({ids});')
        c.call('workspace/catalog',expected=403)
        after=c.call('bootstrap?includeFrames=false&includeAccess=true')
        check('bootstrap grants cannot authorize a business read after current revocation',
              'ReadMarketData' in before['access']['permissions'] and after['access']['permissions']==[]
              and after['access']=={'profile':'viewer','permissions':c.call('access')['permissions']})
    finally:
        query(f'UPDATE security.role_grants SET revoked=false WHERE id IN({ids});')
