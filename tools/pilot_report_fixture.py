"""Append a synthetic retained instrument alias without rewriting original facts."""
from uuid import uuid4


def prepare_alias(query,manifest,trade_id):
    catalog=manifest['catalogId']
    alias,trade,binding,event,operation=(str(uuid4()) for _ in range(5))
    query(f"""BEGIN;
SELECT pg_advisory_xact_lock(730849102);
CREATE TEMP TABLE alias_publication ON COMMIT DROP AS
 SELECT coalesce(max(publication_order),0)+1 AS ordinal FROM trading.publications WHERE catalog_id='{catalog}';
INSERT INTO trading.publications SELECT '{catalog}',ordinal,pg_current_xact_id() FROM alias_publication;
INSERT INTO trading.instruments SELECT(jsonb_populate_record(NULL::trading.instruments,to_jsonb(i)||jsonb_build_object('id','{alias}','publication_order',p.ordinal))).*
 FROM trading.trade_revisions t JOIN trading.instruments i ON(i.catalog_id,i.id)=(t.catalog_id,t.instrument_id) CROSS JOIN alias_publication p
 WHERE(t.catalog_id,t.trade_id,t.revision)=('{catalog}','{trade_id}',1);
INSERT INTO trading.futures_contract_references SELECT(jsonb_populate_record(NULL::trading.futures_contract_references,to_jsonb(f)||jsonb_build_object('instrument_id','{alias}','publication_order',p.ordinal))).*
 FROM trading.trade_revisions t JOIN trading.futures_contract_references f ON(f.catalog_id,f.instrument_id)=(t.catalog_id,t.instrument_id) CROSS JOIN alias_publication p
 WHERE(t.catalog_id,t.trade_id,t.revision)=('{catalog}','{trade_id}',1);
INSERT INTO trading.trades SELECT(jsonb_populate_record(NULL::trading.trades,to_jsonb(t)||jsonb_build_object('id','{trade}','operation_id','{operation}','publication_order',p.ordinal))).*
 FROM trading.trades t CROSS JOIN alias_publication p WHERE(t.catalog_id,t.id)=('{catalog}','{trade_id}');
INSERT INTO trading.trade_instruments SELECT(jsonb_populate_record(NULL::trading.trade_instruments,to_jsonb(t)||jsonb_build_object('trade_id','{trade}','binding_id','{binding}','instrument_id','{alias}','publication_order',p.ordinal))).*
 FROM trading.trade_instruments t CROSS JOIN alias_publication p WHERE(t.catalog_id,t.trade_id)=('{catalog}','{trade_id}');
INSERT INTO trading.trade_revisions SELECT(jsonb_populate_record(NULL::trading.trade_revisions,to_jsonb(t)||jsonb_build_object('trade_id','{trade}','binding_id','{binding}','instrument_id','{alias}','event_id','{event}','operation_id','{operation}','reason','Synthetic retained alias fixture','publication_order',p.ordinal))).*
 FROM trading.trade_revisions t CROSS JOIN alias_publication p WHERE(t.catalog_id,t.trade_id,t.revision)=('{catalog}','{trade_id}',1);
INSERT INTO trading.futures_revision_markets SELECT(jsonb_populate_record(NULL::trading.futures_revision_markets,to_jsonb(t)||jsonb_build_object('trade_id','{trade}','instrument_id','{alias}','publication_order',p.ordinal))).*
 FROM trading.futures_revision_markets t CROSS JOIN alias_publication p WHERE(t.catalog_id,t.trade_id,t.revision)=('{catalog}','{trade_id}',1);
COMMIT;""")
    assert query(f"SELECT count(*) FROM trading.futures_revision_markets WHERE(catalog_id,trade_id,instrument_id)=('{catalog}','{trade}','{alias}');")=='1'
    return trade
