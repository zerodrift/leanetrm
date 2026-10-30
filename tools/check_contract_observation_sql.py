"""Synthetic parity check for prompt-prefix admission at the SQL write boundary."""
from copy import deepcopy
from datetime import date, timedelta
import json

from preview_fixture import CAT, REV, RCAT, RREV, R, TENANT, WORKSPACE, identity

PREFIX_CONTRACT = identity('5', 879001)
PREFIX_SERIES = identity('9', 879001)


def prepare(market, manifest):
    manifest['schedules'].append(dict(curve='future_prefix', **{'from': '2020-12-01T00:00:00Z'},
                                      to='2021-03-28T15:30:00Z', exampleAt='2021-01-01T16:00:00Z'))
    references = f"""
INSERT INTO ref_data.futures_series SELECT (jsonb_populate_record(NULL::ref_data.futures_series,
 to_jsonb(s)||'{{"code":"synthetic_prefix_february","series_label":"synthetic_prefix_february"}}'::jsonb)).*
 FROM ref_data.futures_series s WHERE(s.catalog_id,s.revision_id,s.code)=({R},'test_gas_202102');
INSERT INTO ref_data.futures_milestones SELECT (jsonb_populate_record(NULL::ref_data.futures_milestones,
 to_jsonb(m)||jsonb_build_object('series_code','synthetic_prefix_february')||
 CASE WHEN m.milestone='first_trading' THEN '{{"business_date":"2021-01-10","event_at":"2021-01-10T00:00:00Z"}}'::jsonb ELSE '{{}}'::jsonb END)).*
 FROM ref_data.futures_milestones m WHERE(m.catalog_id,m.revision_id,m.series_code)=({R},'test_gas_202102');
INSERT INTO ref_data.futures_execution_policies(catalog_id,revision_id,series_code,implementation,eligibility_rule,eligible_from,
 source_date_offset_seconds,source_date_tzdb_version,price_role,price_source_code,evidence_source_code)
 VALUES({R},'synthetic_prefix_february','contract_lot_execution_pnl_v2','source_first_trade_instant_v1',
 '2021-01-10T00:00:00Z',NULL,NULL,'daily_settlement','fixture','fixture');
"""
    definitions = f"""
INSERT INTO market_data.contracts SELECT (jsonb_populate_record(NULL::market_data.contracts,
 to_jsonb(c)||'{{"id":"{PREFIX_CONTRACT}","futures_series_code":"synthetic_prefix_february"}}'::jsonb)).*
 FROM market_data.contracts c WHERE(c.catalog_id,c.revision_id,c.id)=('{CAT}','{REV}','{identity('5',6)}');
INSERT INTO market_data.series SELECT (jsonb_populate_record(NULL::market_data.series,
 to_jsonb(s)||'{{"id":"{PREFIX_SERIES}","code":"synthetic_prefix_february","contract_id":"{PREFIX_CONTRACT}"}}'::jsonb)).*
 FROM market_data.series s WHERE(s.catalog_id,s.revision_id,s.id)=('{CAT}','{REV}','{identity('9',101)}');
INSERT INTO market_data.curves SELECT (jsonb_populate_record(NULL::market_data.curves,
 to_jsonb(c)||'{{"code":"future_prefix","name":"Synthetic prompt prefix"}}'::jsonb)).*
 FROM market_data.curves c WHERE(c.catalog_id,c.revision_id,c.code)=('{CAT}','{REV}','future');
INSERT INTO market_data.curve_members VALUES
 ('{CAT}','{REV}','future_prefix','{PREFIX_CONTRACT}',0),
 ('{CAT}','{REV}','future_prefix','{identity('5',11)}',1),
 ('{CAT}','{REV}','future_prefix','{identity('5',16)}',2);
"""
    market = market.replace('UPDATE ref_data.revisions SET', references + '\nUPDATE ref_data.revisions SET', 1)
    return market.replace('UPDATE market_data.revisions SET', definitions + '\nUPDATE market_data.revisions SET', 1)


def run(c, context, template, query):
    body = deepcopy(template)
    floating = body['scheduled']['second']['periods'][0]['floating']
    floating['curve'] = 'future_prefix'
    floating['contractObservations']['selection']['promptOffset'] = 1
    rejected = c.call('swaps/preview', body, expected=422)
    assert rejected['code'] == 'sequence_ineligible', rejected
    valuation = '2020-12-18T16:00:00Z'
    compiled = []
    pins = []
    for ordinal in range(31):
        day = date(2021, 1, 1) + timedelta(days=ordinal)
        month = 2 if day.day < 28 else 3
        series = identity('9', month * 100 + 1)
        pin = query(f"SELECT id FROM market_data.observations WHERE catalog_id='{CAT}' AND definition_revision_id='{REV}' AND series_id='{series}' AND role='daily_settlement' AND publication_state='final' AND observed_at<='{valuation}' ORDER BY observed_at DESC LIMIT 1;").strip()
        assert pin, (day, series)
        pins.append(pin)
        compiled.append(dict(observationId=pin, ordinal=ordinal, observationFrom=day.isoformat(),
                             observationTo=(day + timedelta(days=1)).isoformat(),
                             fixingAt=day.isoformat()+'T16:00:00Z', selectionAt=day.isoformat()+'T16:00:00Z',
                             contractId=identity('5',month*5+1),seriesId=series,sourceCode='fixture'))
    def sql_json(value):
        return "'" + json.dumps(value).replace("'", "''") + "'::jsonb"
    # The exception subtransaction also rolls back any payment-rule interning
    # if the unpatched preparer mistakenly admits this source.
    query(f"""DO $check$ BEGIN
 BEGIN
  PERFORM security.prepare_contract_observation_period('{CAT}','{REV}','{RCAT}','{RREV}','test_gas',
   {sql_json(floating)},'{valuation}',1,'2020-01-01T00:00:00Z',{sql_json(compiled)},
   ARRAY[{','.join(repr(pin) for pin in sorted(set(pins)))}]::uuid[]);
  RAISE EXCEPTION 'SQL accepted an ineligible prompt prefix' USING ERRCODE='P0002';
 EXCEPTION WHEN raise_exception THEN
  IF SQLERRM<>'sequence_ineligible' THEN RAISE;END IF;
 END;
END $check$;""")
    print('PASS contract observation SQL: offset cannot skip an ineligible prompt prefix')


def reject_stale_actual_pin(template, query):
    """Current write admission must not retain a superseded historical fixing."""
    floating = deepcopy(template['scheduled']['second']['periods'][0]['floating'])
    stale = identity('7', 8700001)
    assert query(f"SELECT count(*) FROM market_data.observations WHERE catalog_id='{CAT}' AND corrects_id='{stale}';").strip() == '1'
    compiled = []
    for ordinal in range(31):
        day = date(2021, 1, 1) + timedelta(days=ordinal)
        month = 1 if day.day < 28 else 2
        compiled.append(dict(observationId=identity('7', 8700001+ordinal), ordinal=ordinal,
                             observationFrom=day.isoformat(), observationTo=(day+timedelta(days=1)).isoformat(),
                             fixingAt=day.isoformat()+'T16:00:00Z', selectionAt=day.isoformat()+'T16:00:00Z',
                             contractId=identity('5',month*5+1), seriesId=identity('9',month*100+1), sourceCode='fixture'))
    def sql_json(value):
        return "'" + json.dumps(value).replace("'", "''") + "'::jsonb"
    pins = ','.join(repr(row['observationId']) for row in compiled)
    token = query("SET SESSION AUTHORIZATION preview_authenticator; SELECT security.issue_context("
                  f"'https://preview.invalid','trader','human_interactive','{TENANT}','{WORKSPACE}',NULL,'preview_runtime',60);").strip()
    # Only the owned fixture may invoke this private preparer. Keep the actual
    # runtime session's authority visible inside a rollback-only test wrapper.
    query(f"""BEGIN;
 CREATE FUNCTION pg_temp.check_stale_contract_pin() RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $check$ BEGIN
 BEGIN
  PERFORM security.prepare_contract_observation_period('{CAT}','{REV}','{RCAT}','{RREV}','test_gas',
   {sql_json(floating)},'2021-02-01T08:00:00Z',1,'2020-01-01T00:00:00Z',{sql_json(compiled)},ARRAY[{pins}]::uuid[]);
  RAISE EXCEPTION 'SQL accepted a superseded historical observation pin' USING ERRCODE='P0002';
 EXCEPTION WHEN raise_exception THEN
  IF SQLERRM<>'missing_observation' THEN RAISE;END IF;
 END;
END $check$;
 SET LOCAL SESSION AUTHORIZATION preview_runtime;
 SELECT security.use_context('{token}');
 SELECT pg_temp.check_stale_contract_pin();
 ROLLBACK;""")
    print('PASS contract observation SQL: visible correction excludes stale historical pin')
