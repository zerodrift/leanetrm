"""Render explicitly supplied private feed mappings into a fresh pilot seed."""
from uuid import UUID,uuid4


def literal(value):
    if not isinstance(value,str) or not value or any(ord(c)<32 for c in value):raise ValueError('Supply explicit nonempty text without control characters.')
    return "'"+value.replace("'","''")+"'"


def prepare(collection,ids,login):
    if login!='openetrm_collection_login':raise ValueError('Use the dedicated collection login.')
    t,w,c,r,rr=[ids[k] for k in ['tenant','workspace','catalog','reference','referenceRevision']]
    actor=str(uuid4());ref=f"'{r}','{rr}'"
    reference=[f"INSERT INTO ref_data.location_types VALUES({ref},'settlement_point','Settlement point');"]
    sql=[f"INSERT INTO security.principals VALUES('{actor}','service',true);",
         f"INSERT INTO security.workspace_memberships VALUES('{t}','{w}','{actor}','power_collector','Power price collector',true,1);",
         f"INSERT INTO security.role_grants SELECT gen_random_uuid(),'{actor}',id,'{t}','{w}',clock_timestamp(),'9999-12-31',false FROM security.roles WHERE code='market_data_editor';",
         f"INSERT INTO power_data.collectors VALUES('{login}','{actor}','{t}','{w}','{c}');"]
    source_ids=set();providers={}
    for source in collection['sources']:
        sid=str(UUID(source['id']));provider=source['provider'];minutes=source['minutes']
        contract=(provider,source['dataset'],source['market'],minutes)
        if contract not in [('PJM','da_hrl_lmps','DA',60),('PJM','rt_hrl_lmps','RT',60),('ERCOT','np4-190-cd/dam_stlmnt_pnt_prices','DA',60),('ERCOT','np6-905-cd/spp_node_zone_hub','RT',15)]:raise ValueError('Unsupported explicit feed contract.')
        if source['unit']!='MWh' or source['currency']!='USD' or source['timeZone']!=({'PJM':'America/New_York','ERCOT':'America/Chicago'}[provider]):raise ValueError('Feed units or clock do not match the qualified provider contract.')
        if sid in source_ids:raise ValueError('Duplicate source identity.')
        source_ids.add(sid);providers[sid]=provider
        location='point_'+UUID(sid).hex
        reference.append(f"INSERT INTO ref_data.locations(catalog_id,revision_id,code,name,type_code,clock_code) VALUES({ref},'{location}',{literal(source['name'])},'settlement_point','{provider.lower()}');")
        sql.append(f"INSERT INTO power_data.sources VALUES('{sid}','{c}',{literal(source['name'])},{literal(provider)},{literal(source['dataset'])},{literal(source['market'])},{literal(source['point'])},{minutes},{ref},'{location}','{provider.lower()}','usd','power_mwh');")
    for job in collection['jobs']:
        jid=str(UUID(job['id']));provider=job['provider'];members=job['sourceIds']
        if provider not in ('PJM','ERCOT') or job['cron']!='0 8 * * *' or job['scheduleZone']!='UTC' or type(job['enabled']) is not bool or type(job['scheduleEnabled']) is not bool:raise ValueError('Supply the supported explicit UTC job contract and switches.')
        if not members or len(set(members))!=len(members) or any(s not in source_ids or providers[s]!=provider for s in members):raise ValueError('Every job must reference exact matching source identities.')
        first,last=job['fromDay'],job['throughDay']
        if type(first) is not int or type(last) is not int or not -31<=first<=last<=-1:raise ValueError('Supply an explicit bounded trailing window.')
        sql.append(f"INSERT INTO power_data.jobs VALUES('{jid}','{uuid4()}',{literal(job['name'])},'{login}','{c}',{literal(provider)},{str(job['enabled']).lower()},{str(job['scheduleEnabled']).lower()},'0 8 * * *','UTC',{first},{last});")
        sql.extend(f"INSERT INTO power_data.job_sources VALUES('{jid}','{s}','{c}');" for s in members)
    rates=collection['requestsPerMinute']
    for provider in sorted(set(providers.values())):
        rate=rates[provider]
        if type(rate) is not int or not 1<=rate<=120:raise ValueError('Supply the configured licensed request rate.')
        sql.append(f"INSERT INTO power_data.request_gates VALUES({literal(provider)},{rate},clock_timestamp());")
    return '\n'.join(reference),'\n'.join(sql)
