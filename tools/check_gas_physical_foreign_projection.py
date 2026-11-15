"""Native physical title and principal on exact independent A/B market sources."""
import json
import os
from decimal import Decimal
from datetime import datetime,timedelta
from pathlib import Path
import shutil
import subprocess
from uuid import uuid4

from check_reference_editing import main
from check_physical_futures_value import inventory_fixture,checks as configure_value
from check_gas_principal_carrying import fixture as principal_fixture
from close_session_fixture import sql_session
from economic_worker_fixture import provision,run_saved
from gas_physical_completion_fixture import execute


def fixture(root):
    reference,market,security,manifest=inventory_fixture(principal_fixture(root))
    original="x.value+CASE WHEN x.role='parameter' THEN 0.04 ELSE 1.2 END"
    assert market.count(original)==1
    # B is explicitly zero-rate while A remains nonzero. Substituting A's
    # carrying into the principal leg therefore changes the independent oracle.
    market=market.replace(original,"CASE WHEN x.role='parameter' THEN 0 ELSE x.value+1.2 END")
    return reference,market,security,manifest


def configured(query,manifest,state):
    catalog,tenant,workspace=(manifest[k] for k in ('catalogId','tenantId','workspaceId'))
    issuer=manifest['identityIssuer'];secondary=manifest['physicalSecondaryRevision']
    delivery_start=datetime.fromisoformat(state['delivery']['profile']['fromAt'].replace('Z','+00:00'))
    def instant(days,hours):return (delivery_start+timedelta(days=days,hours=hours)).isoformat().replace('+00:00','Z')
    at=instant(4,16);publication_at=instant(6,16)
    def literal(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
    def call(sql):
        token=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('{issuer}','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}COMMIT;")
    def capture(day):return call(f"SELECT security.capture_date_context('{catalog}','{day}',NULL);")
    policy=state['margin'];final=policy['occurrences'][0];book=state['bookId'];long=state['booked']
    short=json.loads(call(f"SELECT security.book_futures_with_parties('{catalog}','{uuid4()}','{book}','{manifest['revisionId']}',"
        f"'nymex_ng','{policy['contractId']}','2026-10-16T14:00:00Z',NULL,-2,3,'contract_lot_execution_pnl_v2',NULL,NULL,NULL,{literal(state['booking'])})::text;"))
    call(f"SELECT settlement_data.adopt_futures_margin('{catalog}',{literal(dict(operationId=str(uuid4()),tradeId=short['tradeId'],bindingId=short['bindingId'],
        expectedRevision=1,policyId=policy['id'],policyRevision=1,contextId=capture('2026-10-16'),asOf='2026-10-16T16:00:00Z',adoption='native_execution_inception_v1'))});")
    observation=str(uuid4())
    query(f"INSERT INTO market_data.observations SELECT o.catalog_id,'{observation}',o.definition_revision_id,o.series_id,o.reference_catalog_id,o.reference_revision_id,"
        f"o.source_code,'synthetic_foreign_physical_final',1,'final_contract_settlement','final','{final['at']}',clock_timestamp(),5,NULL "
        "FROM market_data.observations o JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.id)=(o.catalog_id,o.definition_revision_id,o.series_id) "
        f"WHERE(o.catalog_id,o.definition_revision_id,s.contract_id,o.observed_at)=('{catalog}','{manifest['revisionId']}','{policy['contractId']}','{manifest['example']['at']}');")
    size=Decimal(query("SELECT p.contract_size*2 FROM market_data.contracts c JOIN ref_data.futures_series s "
        "ON(s.catalog_id,s.revision_id,s.code)=(c.reference_catalog_id,c.reference_revision_id,c.futures_series_code) "
        "JOIN ref_data.futures_products p ON(p.catalog_id,p.revision_id,p.code)=(s.catalog_id,s.revision_id,s.product_code) "
        f"WHERE(c.catalog_id,c.revision_id,c.id)=('{catalog}','{manifest['revisionId']}','{policy['contractId']}');"))
    parents=[]
    for trade,quantity in ((long,size),(short,-size)):
        request=dict(operationId=str(uuid4()),tradeId=trade['tradeId'],bindingId=trade['bindingId'],expectedRevision=1,expectedMarginRevision=1,
            contextId=capture(final['at'][:10]),asOf=final['at'],throughOccurrence=final['id'],implementation='native_futures_margin_journal_v1',correctionDue=[],previewHash='0'*64)
        execution_price=Decimal(state.get('executionPrice','3')) if trade is long else Decimal('3')
        amount=str(quantity*(Decimal('5')-execution_price))
        step=dict(occurrenceId=final['id'],previousDeterminationId=None,sourceObservationId=observation,previousBasis=str(execution_price),
            exactVariation=amount,roundedTotal=amount,adjustment=amount,adjustmentDue=final['dueAt'],noChange=False)
        continuation=dict(implementation='gas_futures_delivery_continuation_v1',positionId=None,expectedVersion=0,finalOccurrenceId=final['id'],sourceObservationId=observation,steps=[])
        accepted=json.loads(call(f"SELECT settlement_data.determine_futures_margin('{catalog}',{literal(request)},{literal([step])},{literal(continuation)})::text;"))
        parents.append(dict(tradeId=trade['tradeId'],bindingId=trade['bindingId'],positionId=accepted['deliveryPositionId']))
    account=state['delivery']['accountId'];action=state['delivery']['actionPolicy']['policyId']
    call(f"SELECT security.record_gas_owned('{catalog}','{uuid4()}','bind_policy','{account}',{literal(dict(action='gas.futures.delivery',policyId=action,
        policyRevision=1,effectiveAt='2026-10-01T00:00:00Z'))},'{publication_at}','{capture(publication_at[:10])}',NULL,NULL,false);")
    def evidence(kind,quantity,when,key):
        body=dict(sourceKey=key,kind=kind,quantity=str(quantity),occurredAt=when,evidence='Synthetic observed independent-market title or dispatch',expectedRevision=0)
        return json.loads(call(f"SELECT security.record_gas_owned('{catalog}','{uuid4()}','evidence','{account}',{literal(body)},'{publication_at}','{capture(publication_at[:10])}',NULL,NULL,false)::text;"))['id']
    def perform(parent,quantity,when,title,supply):
        version=int(query(f"SELECT coalesce(max(revision),0) FROM trading.gas_futures_delivery_principal_runs WHERE(catalog_id,position_id)=('{catalog}','{parent['positionId']}');"))
        body=dict(positionId=parent['positionId'],businessReference='synthetic_foreign_'+when,expectedVersion=version,quantity=str(quantity),
            performedAt=when,titleEvidenceId=title,availableAt=when if title else None,supply=supply)
        return execute(query,manifest,capture,dict(action_policy_id=action),body,at=publication_at,business_date=publication_at[:10])
    title=perform(parents[0],size/4,instant(2,10),evidence('title_receipt',size/4,instant(2,10),'synthetic_foreign_title_first'),[])
    perform(parents[0],size/4,instant(5,10),evidence('title_receipt',size/4,instant(5,10),'synthetic_foreign_title_next'),[])
    lot=query(f"SELECT receipt_lot_id FROM trading.gas_futures_delivery_performances WHERE(catalog_id,id)=('{catalog}','{title['id']}');")
    perform(parents[1],size/8,instant(5,13),None,[dict(lotId=lot,reservationId=None,quantity=str(size/8),
        evidenceId=evidence('physical_delivery',size/8,instant(5,13),'synthetic_foreign_dispatch'))])
    profile=dict(profileId=str(uuid4()),expectedRevision=0,implementation='gas_accepted_quantity_lifecycle_projection_v3',
        quantityDisposition='retain_quantity_obligations_v1',effectiveAt='2026-10-01T00:00:00Z',performances=[],cashouts=[])
    call(f"SELECT security.configure_gas_owned_projection('{catalog}','{uuid4()}','projection_profile','{account}',{literal(profile)},'{at}','{capture(at[:10])}',false);")
    sql_session(query,manifest,'synthetic_native_physical_projection','source_business_days','16:00',['nymex_ng','synthetic_inventory'],[],
        effective=('2026-10-01','2027-01-01'),curve_sources={'synthetic_inventory':secondary})
    def pins(revision):return json.loads(query(f"SELECT jsonb_agg(id ORDER BY id) FROM market_data.observations WHERE(catalog_id,definition_revision_id,observed_at)=('{catalog}','{revision}','{manifest['example']['at']}') AND(role='parameter' OR definition_revision_id='{secondary}');"))
    contracts=json.loads(query(f"SELECT jsonb_agg(contract_id ORDER BY ordinal) FROM market_data.curve_members WHERE(catalog_id,revision_id,curve_code)=('{catalog}','{manifest['revisionId']}','nymex_ng');"))
    directory=Path(query('SHOW unix_socket_directories;'));packet=directory/'native-foreign-physical-projection.json'
    packet.write_text(json.dumps(dict(manifest=manifest,host=str(directory),contextId=capture(at[:10]),at=at,bookId=book,pins=pins(manifest['revisionId']),
        contracts=contracts,parents=parents,gasProfiles=[dict(accountId=account,profileId=profile['profileId'],revision=1)],mode='foreign',optionOrigin=state.get('optionOrigin',False),
        sources=[dict(revisionId=secondary,curve='synthetic_inventory',contractId=state['delivery']['accountTerms']['markContractId'],pins=pins(secondary))])));packet.chmod(0o600)
    root=Path(__file__).resolve().parents[1];frozen=directory/'foreign-physical-binaries';shutil.copytree(root/'tests/OpenEtrm.Api.Checks/bin/Debug/net10.0',frozen)
    provision(query,manifest,'synthetic_foreign_physical_worker',permissions=('ReadGasOwnedHoldings',))
    result=subprocess.run([os.environ.get('DOTNET','dotnet'),str(frozen/'OpenEtrm.Api.Checks.dll'),'--gas-physical-projection',str(packet)],cwd=root,capture_output=True,text=True,timeout=120)
    if result.returncode:raise RuntimeError(result.stdout+result.stderr)
    print(result.stdout.strip(),flush=True)
    run_saved(query,manifest,packet,frozen,'synthetic_foreign_physical_worker',permissions=('ReadGasOwnedHoldings',),expected_jobs=2)


if __name__=='__main__':
    build=subprocess.run([os.environ.get('DOTNET','dotnet'),'build','tests/OpenEtrm.Api.Checks','--no-restore'],cwd=Path(__file__).resolve().parents[1],capture_output=True,text=True,timeout=120)
    if build.returncode:raise RuntimeError(build.stdout+build.stderr)
    main(checks=lambda query,manifest:configure_value(query,manifest,configuration_checks=configured),fixture_factory=fixture)
