"""A valid counterpart cannot disguise an unchanged predecessor as a new fact."""
from uuid import uuid4

from point_trader_mark_fixture import literal


def same_value_changed(f, sample, target, rejected):
    f.query("""CREATE FUNCTION market_data.synthetic_equal_point_change(catalog uuid,operation uuid,body jsonb,source uuid,at timestamptz,sample uuid)
 RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
 DECLARE prior market_data.point_trader_mark_commands;observation uuid=gen_random_uuid();event market_data.knowledge_events;
  deadline timestamptz;actor uuid;outcome jsonb;request jsonb;BEGIN
  deadline=security.point_trader_mark_authority(catalog,true);
  body=market_data.point_trader_mark_body(body);
  SELECT actor_id INTO STRICT actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid()
   AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
  SELECT * INTO STRICT prior FROM market_data.point_trader_mark_commands c WHERE(c.catalog_id,c.operation_id)=(catalog,sample);
  PERFORM pg_advisory_xact_lock(730849102);
  INSERT INTO market_data.observations(catalog_id,id,definition_revision_id,series_id,reference_catalog_id,reference_revision_id,
   source_code,source_event_id,source_revision_id,role,publication_state,observed_at,received_at,value,corrects_id)
  VALUES(catalog,observation,(body#>>'{target,definitionRevisionId}')::uuid,(body#>>'{target,seriesId}')::uuid,
   (body#>>'{target,referenceCatalogId}')::uuid,(body#>>'{target,referenceRevisionId}')::uuid,body#>>'{target,source}',
   body#>>'{target,sourceEventId}',body#>>'{target,sourceRevisionToken}','trader_mark','final',at,clock_timestamp(),
   (body->>'value')::numeric,(body#>>'{target,expectedPreviousObservationId}')::uuid);
  SELECT * INTO STRICT event FROM market_data.knowledge_events e WHERE(e.catalog_id,e.observation_id)=(catalog,observation);
  outcome=jsonb_build_object('operationId',operation,'observationId',observation,'changed',true,
   'definitionRevisionId',body#>'{target,definitionRevisionId}','referenceCatalogId',body#>'{target,referenceCatalogId}',
   'referenceRevisionId',body#>'{target,referenceRevisionId}','contractId',body#>'{target,contractId}','seriesId',body#>'{target,seriesId}',
   'source',body#>'{target,source}','quote',body#>'{target,quote}','observedAt',market_data.point_trader_mark_instant(at),
   'sourceEventId',body#>'{target,sourceEventId}','sourceRevisionId',body#>'{target,sourceRevisionToken}',
   'previousObservationId',body#>'{target,expectedPreviousObservationId}','value',body->'value',
   'knowledgeEventOrder',event.event_order,'publicationXid',event.publication_xid::text);
  request=jsonb_build_object('body',body,'sourceContextId',source,'asOf',market_data.point_trader_mark_instant(at));
  INSERT INTO market_data.point_trader_mark_commands SELECT(jsonb_populate_record(NULL::market_data.point_trader_mark_commands,
   to_jsonb(prior)||jsonb_build_object('operation_id',operation,'actor_id',actor,'observation_id',observation,'request',request,
    'fact',market_data.point_trader_mark_fact(body),'outcome',outcome,'source_context_id',source,'source_as_of',at,'authority_until',deadline))).*;
 END$$;
 ALTER FUNCTION market_data.synthetic_equal_point_change(uuid,uuid,jsonb,uuid,timestamptz,uuid) OWNER TO openetrm_security_owner;
 REVOKE ALL ON FUNCTION market_data.synthetic_equal_point_change(uuid,uuid,jsonb,uuid,timestamptz,uuid) FROM PUBLIC;
 GRANT EXECUTE ON FUNCTION market_data.synthetic_equal_point_change(uuid,uuid,jsonb,uuid,timestamptz,uuid) TO openetrm_runtime;""")
    body = f.command('20', target=target)
    before = f.publication_heads()
    rejected(lambda: f.call(f"SELECT market_data.synthetic_equal_point_change('{f.catalog}','{uuid4()}',{literal(body)},"
        f"'{f.market_context()}','{f.valuation_at}','{sample['operationId']}');SELECT security.finish_operation_read();SET CONSTRAINTS ALL IMMEDIATE;", profile='editor'),
        'point_trader_mark_receipt')
    assert f.publication_heads() == before
    print('PASS an independently linked observation/event/changed receipt cannot materialize an equal predecessor value.', flush=True)
