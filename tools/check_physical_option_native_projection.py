"""Accepted option child through native final, title and principal sources."""
import json
import os
from decimal import Decimal
from pathlib import Path
import subprocess
from uuid import uuid4

from check_reference_editing import main
from check_gas_physical_foreign_projection import fixture as native_sources,configured
from check_physical_option_value import unique_reference
from check_physical_futures_value import checks as configure_value


def fixture(root):
    reference,market,security,manifest=unique_reference(native_sources(root))
    catalog,definition=manifest['catalogId'],manifest['revisionId']
    ref_catalog,ref_revision=manifest['referenceCatalogId'],manifest['referenceRevisionId']
    refs=f"'{ref_catalog}','{ref_revision}'";contract,series=str(uuid4()),str(uuid4())
    reference_seal=f"UPDATE ref_data.revisions SET state='sealed' WHERE catalog_id='{ref_catalog}' AND id='{ref_revision}';"
    assert market.count(reference_seal)==1
    # A plainly synthetic past listing allows all observed final/title facts to
    # precede the real native knowledge clock. No future observation receipt or
    # verified public listing date is fabricated to make the test executable.
    terms=(f"INSERT INTO ref_data.sources(catalog_id,revision_id,code,kind,locator,retrieved_at,license_reference,distribution_review) "
        f"VALUES({refs},'synthetic_native_option_expiry','synthetic','synthetic:past-option-listing','2026-10-01T00:00:00Z','Original synthetic listing','not_reviewed');"
        f"INSERT INTO ref_data.futures_option_series VALUES({refs},'synthetic_past_option','nymex_on','nymex_ng_20261101','call',2,'synthetic','2026-10-27',"
        f"ref_data.close_instant({refs},'option_expiry','nymex_on','2026-10-27'),NULL,'synthetic_native_option_expiry');")
    market=market.replace(reference_seal,terms+reference_seal)
    market_seal=f"UPDATE market_data.revisions SET state='sealed' WHERE catalog_id='{catalog}' AND id='{definition}';"
    assert market.count(market_seal)==1
    rows=(f"INSERT INTO market_data.contracts SELECT '{catalog}','{definition}','{contract}',{refs},'future_option',c.delivery_from,c.delivery_to,s.expiry_at,NULL,s.code "
        f"FROM ref_data.futures_option_series s JOIN market_data.contracts c ON(c.reference_catalog_id,c.reference_revision_id,c.futures_series_code)="
        f"(s.catalog_id,s.revision_id,s.underlying_series_code) WHERE(s.catalog_id,s.revision_id,s.code,c.catalog_id,c.revision_id)=({refs},'synthetic_past_option','{catalog}','{definition}');"
        f"INSERT INTO market_data.curve_members SELECT '{catalog}','{definition}','nymex_on','{contract}',max(ordinal)+1 FROM market_data.curve_members "
        f"WHERE(catalog_id,revision_id,curve_code)=('{catalog}','{definition}','nymex_on');"
        f"INSERT INTO market_data.series VALUES('{catalog}','{definition}','{series}','synthetic_past_option',{refs},'{contract}','nymex_on_quote',NULL);")
    return reference,market.replace(market_seal,rows+market_seal),security,manifest


def option_child(query,manifest,call,capture,delivery,margin,book,booking,contract,mark):
    catalog=manifest['catalogId'];definition=manifest['revisionId']
    def literal(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
    series,strike=query("SELECT s.code||'|'||s.strike::text FROM ref_data.futures_option_series s JOIN market_data.contracts c "
        "ON(c.reference_catalog_id,c.reference_revision_id,c.futures_series_code)=(s.catalog_id,s.revision_id,s.underlying_series_code) "
        f"WHERE(c.catalog_id,c.revision_id,c.id,s.code,s.option_right)=('{catalog}','{definition}','{contract}','synthetic_past_option','call');").split('|')
    assert series,'The independent native continuation oracle requires its exact physical option.'
    option=json.loads(call(f"SELECT security.book_futures_option('{catalog}','{uuid4()}','{book}','{definition}','{series}',2,0.1,"
        f"'2026-10-30','premium_paid_up_front','2026-10-16T14:00:00Z',NULL,'futures_option_premium_v1',NULL,NULL,NULL,{literal(booking)})::text;"))
    policy=dict(operationId=str(uuid4()),policyId=str(uuid4()),expectedRevision=0,action='option.exercise',mode='manual',
        fromAt='2026-10-01T00:00:00Z',untilAt='2027-01-01T00:00:00Z',implementation='explicit_lifecycle_action_policy_v1')
    call(f"SELECT lifecycle.save_policy('{catalog}',{literal(policy)});")
    profile=dict(operationId=str(uuid4()),tradeId=option['tradeId'],action='option.exercise',expectedRevision=0,
        policyId=policy['policyId'],policyRevision=1,marginPolicyId=margin['id'],marginPolicyRevision=1,
        futureMarkCurve='nymex_ng',expiryCurve=None)
    call(f"SELECT lifecycle.save_option_profile('{catalog}',{literal(profile)});")
    request=dict(operationId=str(uuid4()),context=dict(contextId=capture(),asOf='2026-10-16T15:00:00Z',businessDate='2026-10-16'),
        target=dict(handler='listed_option',scopeId=option['tradeId'],bindingId=option['optionInstrumentId']),action='option.exercise',
        policyId=policy['policyId'],policyRevision=1,automatic=False,previewHash='0'*64,
        selection=dict(evidenceId=str(uuid4()),lots='2',expectedEventVersion=0,profileRevision=1))
    request['previewHash']=json.loads(call(f"SELECT lifecycle.option_proposal('{catalog}',{literal(request)})::text;"))['hash']
    child=json.loads(call(f"SELECT lifecycle.execute_option_action('{catalog}',{literal(request)})::text;"))
    binding=query(f"SELECT binding_id FROM settlement_data.futures_margin_adoptions WHERE(catalog_id,trade_id)=('{catalog}','{child['futuresTradeId']}');")
    assert binding
    assert Decimal(query(f"SELECT execution_price FROM trading.trade_revisions WHERE(catalog_id,trade_id,revision)=('{catalog}','{child['futuresTradeId']}',1);"))==Decimal(strike)
    assert query("SELECT count(*) FROM trading.futures_option_events e JOIN settlement_data.futures_margin_adoptions a "
        "ON(a.catalog_id,a.trade_id,a.publication_order)=(e.catalog_id,e.futures_trade_id,e.publication_order) "
        f"WHERE(e.catalog_id,e.id,e.trade_id)=('{catalog}','{child['eventId']}','{option['tradeId']}');")=='1'
    print('PASS actual option continuation origin: reviewed native exercise and exact same-publication child adoption at the original strike',flush=True)
    configured(query,manifest,dict(bookId=book,booking=booking,booked=dict(tradeId=child['futuresTradeId'],bindingId=binding),
        margin=margin,delivery=delivery,executionPrice=strike,optionOrigin=True))


if __name__=='__main__':
    root=Path(__file__).resolve().parents[1]
    build=subprocess.run([os.environ.get('DOTNET','dotnet'),'build','tests/OpenEtrm.Api.Checks','--no-restore'],cwd=root,capture_output=True,text=True,timeout=120)
    if build.returncode:raise RuntimeError(build.stdout+build.stderr)
    main(checks=lambda query,manifest:configure_value(query,manifest,option_value=option_child),fixture_factory=fixture)
