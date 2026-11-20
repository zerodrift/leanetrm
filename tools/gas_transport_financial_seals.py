"""Independent source/counterpart attacks on the financial Transport consumer."""
from uuid import uuid4

from gas_transport_financial_fixture import literal


def source_and_seals(f, booking, outcome, rejected):
    from gas_transport_financial_managed import value
    trade = outcome['tradeId']
    before = f.capture()
    old = f.read_financial(trade, context=before)
    correction = str(uuid4())
    f.query(f"INSERT INTO market_data.observations SELECT(jsonb_populate_record(NULL::market_data.observations,"
        f"to_jsonb(o)||jsonb_build_object('id','{correction}','corrects_id',id,'source_revision_id','2','value',21,'received_at',clock_timestamp()))).* "
        f"FROM market_data.observations o WHERE(catalog_id,id)=('{f.catalog}','{f.observation}');")
    current = f.read_financial(trade)
    assert current['mark'] is None and current['unpricedReason'] == 'rights_mark_corrected'
    assert f.read_financial(trade, context=before)['mark'] == old['mark']
    assert current['cash'] == old['cash']
    value(f, trade, rate_ids=[], reason='rights_mark_corrected')
    body = f.mark(trade, current['financialState']['id'])
    body.update(observationId=correction, expectedPreviousMarkId=old['mark']['id'],
        evidence='Synthetic explicit adoption of corrected signed money')
    accepted = f.record_financial(body)
    assert f.read_financial(trade)['mark']['observationId'] == correction
    value(f, trade, rate_ids=f.rate_observations[f.valuation_at], mark=f.read_financial(trade)['mark'], expected='8.5')
    print('PASS later genuine quote correction makes current value unavailable without rewriting accepted marks/cash; original certified read remains priced and explicit re-adoption restores it.', flush=True)

    # A future owner-definer may omit application orchestration. Native seals
    # must still reject both an unattested state and forged acknowledgement.
    f.query("""CREATE FUNCTION trading.synthetic_financial_orphan(catalog uuid,trade uuid,context uuid,at timestamptz) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
 DECLARE h trading.gas_transport_financial_states;publication integer;BEGIN
  PERFORM pg_advisory_xact_lock(730849102);
  SELECT * INTO STRICT h FROM trading.gas_transport_financial_states s WHERE(s.catalog_id,s.trade_id)=(catalog,trade) ORDER BY revision DESC LIMIT 1;
  SELECT max(publication_order)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;
  INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
  INSERT INTO trading.gas_transport_financial_states SELECT(jsonb_populate_record(NULL::trading.gas_transport_financial_states,
   to_jsonb(h)||jsonb_build_object('id',gen_random_uuid(),'revision',h.revision+1,'previous_id',h.id,'source_context_id',context,
    'publication_order',publication,'source_as_of',at,'effective_at',at))).*;
 END$$;
 ALTER FUNCTION trading.synthetic_financial_orphan(uuid,uuid,uuid,timestamptz) OWNER TO openetrm_security_owner;
 GRANT EXECUTE ON FUNCTION trading.synthetic_financial_orphan(uuid,uuid,uuid,timestamptz) TO openetrm_runtime;
 CREATE FUNCTION trading.synthetic_financial_false_ack(catalog uuid,selected uuid) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
 DECLARE c trading.gas_transport_financial_commands;BEGIN
  SELECT * INTO STRICT c FROM trading.gas_transport_financial_commands WHERE(catalog_id,operation_id)=(catalog,selected);
  c.operation_id=gen_random_uuid();c.outcome=c.outcome||jsonb_build_object('changed',false);
  c.request=jsonb_set(c.request,'{body,observationId}',to_jsonb(gen_random_uuid()));
  c.fact=trading.gas_transport_financial_fact(c.request->'body',(c.request->>'effectiveAt')::timestamptz);
  INSERT INTO trading.gas_transport_financial_commands SELECT c.*;
 END$$;
 ALTER FUNCTION trading.synthetic_financial_false_ack(uuid,uuid) OWNER TO openetrm_security_owner;
 GRANT EXECUTE ON FUNCTION trading.synthetic_financial_false_ack(uuid,uuid) TO openetrm_runtime;""")
    context = f.capture()
    rejected(lambda: f.call(f"SELECT trading.synthetic_financial_orphan('{f.catalog}','{trade}','{context}','{f.valuation_at}');"
        'SELECT security.finish_operation_read();SET CONSTRAINTS ALL IMMEDIATE;', commit=False), 'gas_transport_financial_receipt')
    rejected(lambda: f.call(f"SELECT trading.synthetic_financial_false_ack('{f.catalog}','{accepted['markId']}');"
        'SELECT security.finish_operation_read();SET CONSTRAINTS ALL IMMEDIATE;', commit=False), 'gas_transport_financial_receipt')
    print('PASS independent deferred seals reject a typed financial state without a receipt and a forged changed=false observation acknowledgement.', flush=True)

    f.query("""CREATE FUNCTION trading.synthetic_financial_late_quote(catalog uuid,operation uuid,body jsonb,context uuid,at timestamptz,corrected uuid,created uuid) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
 BEGIN
  PERFORM security.record_gas_transport_financial(catalog,operation,body,at,context,at);
  INSERT INTO market_data.observations SELECT(jsonb_populate_record(NULL::market_data.observations,to_jsonb(o)||
   jsonb_build_object('id',created,'corrects_id',corrected,'source_revision_id','3','value',22,'received_at',clock_timestamp()))).*
   FROM market_data.observations o WHERE(o.catalog_id,o.id)=(catalog,corrected);
 END$$;
 ALTER FUNCTION trading.synthetic_financial_late_quote(uuid,uuid,jsonb,uuid,timestamptz,uuid,uuid) OWNER TO openetrm_security_owner;
 GRANT EXECUTE ON FUNCTION trading.synthetic_financial_late_quote(uuid,uuid,jsonb,uuid,timestamptz,uuid,uuid) TO openetrm_runtime;""")
    body.update(expectedPreviousMarkId=accepted['markId'], evidence='Synthetic reviewed source before late quote correction')
    operation, late = str(uuid4()), str(uuid4())
    count = f.query(f"SELECT count(*) FROM trading.gas_transport_financial_marks WHERE catalog_id='{f.catalog}';")
    role = f.query("SELECT id FROM security.roles WHERE code='trader';")
    had_import = f.query(f"SELECT EXISTS(SELECT 1 FROM security.role_permissions WHERE(role_id,permission)=('{role}','ImportObservations'));") == 't'
    f.query(f"INSERT INTO security.role_permissions VALUES('{role}','ImportObservations') ON CONFLICT DO NOTHING;")
    try:
        rejected(lambda: f.call(f"SELECT trading.synthetic_financial_late_quote('{f.catalog}','{operation}',{literal(body)},"
            f"'{f.capture()}','{f.valuation_at}','{correction}','{late}');"), 'gas_transport_financial_mark')
    finally:
        if not had_import:
            f.query(f"DELETE FROM security.role_permissions WHERE(role_id,permission)=('{role}','ImportObservations');")
    assert f.query(f"SELECT count(*) FROM trading.gas_transport_financial_marks WHERE catalog_id='{f.catalog}';") == count
    assert f.query(f"SELECT count(*) FROM market_data.observations WHERE(catalog_id,id)=('{f.catalog}','{late}');") == '0'
    assert f.read_financial(trade)['mark']['id'] == accepted['markId']
    print('PASS final deferred original-source admission rejects a quote corrected later in the same transaction after eager public producer validation, rolling back both facts.', flush=True)
