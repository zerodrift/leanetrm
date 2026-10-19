#!/usr/bin/env python3
"""Prepare a fresh private pilot catalog and one-time owner invitation; never connect to a database."""
import argparse
import hashlib
import json
from pathlib import Path
import secrets
from urllib.parse import urlparse
from uuid import UUID, uuid4


def prepare(origin, installation, tzdb, directory, collection=None):
    parsed=urlparse(origin)
    if parsed.scheme!='https' or not parsed.netloc or parsed.path or parsed.query or parsed.fragment or parsed.username:
        raise ValueError('Supply the exact HTTPS origin without a trailing slash.')
    UUID(installation)
    if not tzdb or not all(c.isalnum() for c in tzdb):raise ValueError('Supply the qualified runtime tzdb release.')
    directory.mkdir(mode=0o700,parents=True,exist_ok=False)
    ids={name:str(uuid4()) for name in ['tenant','workspace','reference','referenceRevision','catalog','revision','owner','portfolio','book']}
    t,w,r,rr,c,m,owner=[ids[n] for n in ['tenant','workspace','reference','referenceRevision','catalog','revision','owner']]
    token=secrets.token_hex(32);digest=hashlib.sha256(token.encode()).hexdigest()
    ref=f"'{r}','{rr}'"
    sql=f"""BEGIN;
DO $$ BEGIN IF EXISTS(SELECT 1 FROM security.tenants) OR EXISTS(SELECT 1 FROM market_data.catalogs) THEN RAISE EXCEPTION 'Fresh isolated database required';END IF;END $$;
INSERT INTO security.tenants VALUES('{t}','pilot');
INSERT INTO security.workspaces VALUES('{t}','{w}','trading');
INSERT INTO ref_data.catalogs VALUES('{r}','pilot_reference','Trading reference data');
INSERT INTO ref_data.revisions(catalog_id,id,revision_key) VALUES({ref},'initial_units_v1');
INSERT INTO ref_data.currencies VALUES({ref},'usd','USD','US Dollar',2);
INSERT INTO ref_data.dimensions VALUES({ref},'energy',1,2,-2,0,0,0,0),({ref},'power',1,2,-3,0,0,0,0);
INSERT INTO ref_data.units VALUES({ref},'megawatt_hour','MWh','energy'),({ref},'kilowatt_hour','kWh','energy'),({ref},'megawatt','MW','power'),({ref},'kilowatt','kW','power');
INSERT INTO ref_data.unit_conversions VALUES({ref},'megawatt_hour','kilowatt_hour','energy',1000,1),({ref},'megawatt','kilowatt','power',1000,1);
INSERT INTO ref_data.commodities VALUES({ref},'electricity','Electricity','energy');
INSERT INTO ref_data.measurement_bases VALUES({ref},'electrical_energy','electricity','energy'),({ref},'electrical_power','electricity','power');
INSERT INTO ref_data.commodity_units VALUES({ref},'electricity','megawatt_hour','energy'),({ref},'electricity','kilowatt_hour','energy'),({ref},'electricity','megawatt','power'),({ref},'electricity','kilowatt','power');
INSERT INTO ref_data.quantity_specifications VALUES({ref},'power_mwh','electricity','electrical_energy','megawatt_hour','energy'),({ref},'power_kwh','electricity','electrical_energy','kilowatt_hour','energy'),({ref},'power_mw','electricity','electrical_power','megawatt','power');
INSERT INTO ref_data.clock_rules VALUES({ref},'utc','fixed_offset',0,NULL,NULL),({ref},'pjm','iana',NULL,'America/New_York','{tzdb}'),({ref},'ercot','iana',NULL,'America/Chicago','{tzdb}');
UPDATE ref_data.revisions SET state='sealed' WHERE(catalog_id,id)=({ref});
INSERT INTO market_data.catalogs VALUES('{c}','trading');
INSERT INTO market_data.revisions VALUES('{c}','{m}',{ref},'draft',0);
INSERT INTO market_data.revision_periods VALUES('{c}','{m}','0001-01-01','9999-12-31');
UPDATE market_data.revisions SET state='sealed' WHERE(catalog_id,id)=('{c}','{m}');
INSERT INTO security.market_catalog_scopes VALUES('{c}','{t}','{w}');
INSERT INTO security.reference_catalog_scopes VALUES('{r}','{t}','{w}','owner');
INSERT INTO security.principals VALUES('{owner}','human',true);
INSERT INTO security.identity_bindings VALUES('{origin}/local','{owner}','{owner}','human_interactive');
INSERT INTO security.workspace_memberships VALUES('{t}','{w}','{owner}','pending_owner','Pending private invitation',true,1);
INSERT INTO security.role_grants SELECT gen_random_uuid(),'{owner}',id,'{t}','{w}',clock_timestamp(),'9999-12-31',false FROM security.roles;
INSERT INTO security.hosted_accounts(principal_id,issuer,tenant_id,workspace_id) VALUES('{owner}','{origin}/local','{t}','{w}');
INSERT INTO security.hosted_invitations(token_digest,principal_id,purpose,expires_at) VALUES('{digest}','{owner}','claim',clock_timestamp()+interval '24 hours');
INSERT INTO trading.publications VALUES('{c}',1,pg_current_xact_id());
INSERT INTO trading.portfolios VALUES('{c}','{ids['portfolio']}','Trading',1,clock_timestamp());
INSERT INTO trading.books VALUES('{c}','{ids['book']}','{ids['portfolio']}','Trading',1,clock_timestamp());
COMMIT;
"""
    if collection is not None:
        from provision_collection import prepare as prepare_collection
        reference_sql,collection_sql=prepare_collection(collection,ids,'openetrm_collection_login')
        sql=sql.replace('UPDATE ref_data.revisions SET state=',reference_sql+'\nUPDATE ref_data.revisions SET state=',1)
        sql=sql.replace('COMMIT;',collection_sql+'\nCOMMIT;')
    manifest={'mode':'hosted-pilot','catalogId':c,'revisionId':m,'referenceCatalogId':r,'referenceRevisionId':rr,'tenantId':t,'workspaceId':w,
              'frames':[],'example':None,'identityIssuer':origin+'/local','futuresConvention':'fixed_total_cash_execution_pnl_v1'}
    for name,content in [('seed.sql',sql),('manifest.json',json.dumps(manifest,indent=2)+'\n'),('owner-invitation.txt',origin+'/auth#invite='+token+'\n'),('installation.json',json.dumps({'installationId':installation,**ids},indent=2)+'\n')]:
        path=directory/name;path.write_text(content);path.chmod(0o600)
    return ids


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--origin',required=True);parser.add_argument('--installation-id',required=True)
    parser.add_argument('--tzdb',required=True);parser.add_argument('--output',required=True,type=Path)
    parser.add_argument('--collection',type=Path,help='Private explicit source/job mapping contract')
    args=parser.parse_args();prepare(args.origin,args.installation_id,args.tzdb,args.output,json.loads(args.collection.read_text()) if args.collection else None)
    print('Prepared private seed, exact manifest and owner invitation. No database was modified.')


if __name__=='__main__':main()
