"""Exact-instant cash identities and native header admission in owned PostgreSQL."""
import json
from uuid import uuid4
from check_reference_editing import main


def checks(query,manifest):
    reference=manifest['referenceCatalogId'];revision=manifest['referenceRevisionId']
    vectors=[('2031-01-01T13:02:03.123456Z','c38a72b6-25ee-ab30-3d34-288fd04201e6'),
             ('2031-01-01T00:00:00Z','e5dd70a0-4829-ffe0-4f00-742bdcf610f1')]
    for instant,expected in vectors:
        for zone in ('UTC','America/Chicago','Asia/Tokyo'):
            actual=query(f"SET TIME ZONE '{zone}';SET DateStyle='SQL, DMY';SELECT security.timed_cash_payment_instrument_id('{reference}','{revision}','usd','{instant}');")
            assert actual==expected,(zone,instant,actual)
    print('PASS timed cash identities: independent SHA-256 vectors, microseconds, midnight and session clock/date-style independence',flush=True)
    catalog=manifest['catalogId']
    due='2031-01-01T13:02:03.123456Z';timed=vectors[0][1]
    dated=query(f"SELECT security.cash_payment_instrument_id('{reference}','{revision}','usd','2031-01-01');")
    assert dated!=vectors[1][1], 'The exact-instant and date-only definitions must keep distinct identities even at midnight'
    publication=f"INSERT INTO trading.publications VALUES('{catalog}',1,pg_current_xact_id());"
    def parent(identifier):
        return f"INSERT INTO trading.instruments VALUES('{catalog}','{identifier}',1,'2020-01-01T00:00:00Z');"
    def header(identifier,date,instant):
        return ("INSERT INTO trading.cash_payment_instruments(catalog_id,instrument_id,reference_catalog_id,reference_revision_id,currency_code,payment_date,publication_order,effective_at,payment_at) "
                f"VALUES('{catalog}','{identifier}','{reference}','{revision}','usd',{date},1,'2020-01-01T00:00:00Z',{instant});")
    accepted=query('BEGIN;'+publication+parent(timed)+parent(dated)+header(timed,'NULL',f"'{due}'")+header(dated,"'2031-01-01'",'NULL')+
        f"SELECT count(*) FROM trading.cash_payment_instruments WHERE catalog_id='{catalog}';ROLLBACK;")
    assert accepted=='2',accepted
    for date,instant,identifier in [('NULL','NULL',timed),("'2031-01-01'",f"'{due}'",timed),
                                  ('NULL',f"'{due}'",dated),("'2031-01-01'",'NULL',timed),
                                  ('NULL',"'2200-01-01T00:00:00Z'",str(uuid4()))]:
        query('BEGIN;'+publication+parent(identifier)+"DO $check$ BEGIN BEGIN "+header(identifier,date,instant)+
            "RAISE EXCEPTION 'Invalid cash header accepted';EXCEPTION WHEN check_violation THEN NULL;END;END $check$;ROLLBACK;")
    assert query("SELECT has_function_privilege('openetrm_runtime','security.timed_cash_payment_instrument_id(uuid,uuid,text,timestamptz)','EXECUTE');")=='f'
    print('PASS native cash headers: both qualified forms, exact identity, mutually exclusive due terms, supported instants and private admission helper',flush=True)


if __name__=='__main__':main(checks=checks)
