-- Atomic market-only curve mark publication and qualified revision inspection.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);
SELECT 1 FROM security.epoch WHERE id FOR UPDATE;

DO $guard$
DECLARE p pg_proc;signature text='trading.record_curve_marks(uuid,jsonb,timestamptz,text)';
BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure(signature);
 IF p.oid IS NULL OR encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')<>
  'a4398c3733fc1f5f2af1b25beb151ea37f835cfcdbbcb33ec222ef1cc5266914'
  OR p.proowner<>'openetrm_security_owner'::regrole OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner}'
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog'] OR p.prosecdef OR p.proisstrict OR p.proleakproof OR p.provolatile<>'v' OR p.proparallel<>'u'
 THEN RAISE EXCEPTION 'curve_mark_editor_upgrade: unexpected helper %',signature;END IF;
END$guard$;

-- Only the two recorders call this helper, after taking their shared publication
-- lock. The caller threads one lazily allocated publication through every cell.
CREATE FUNCTION trading.record_curve_mark_cell(catalog uuid,actor uuid,operation uuid,definition uuid,mark_kind text,
 first_code text,second_code text,month date,expected integer,amount_value numeric,effective timestamptz,publication integer)
RETURNS TABLE(batch_publication integer,mark_id uuid,mark_revision integer,changed boolean,head_at_publication jsonb)
LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE head trading.curve_mark_facts;matching trading.curve_mark_facts;
BEGIN
 SELECT m.* INTO head FROM trading.curve_mark_facts m
 WHERE(m.catalog_id,m.kind,m.definition_revision_id,m.first_curve,m.bucket_month)=(catalog,mark_kind,definition,first_code,month)
 AND m.second_curve IS NOT DISTINCT FROM second_code ORDER BY m.revision DESC LIMIT 1;
 batch_publication=publication;
 IF head.id IS NOT NULL THEN
  IF (head.amount,head.effective_at)=(amount_value,effective) THEN matching=head;
  ELSE
   SELECT m.* INTO matching FROM trading.curve_mark_facts m
   WHERE(m.catalog_id,m.kind,m.definition_revision_id,m.first_curve,m.bucket_month,m.amount,m.effective_at)=
    (catalog,mark_kind,definition,first_code,month,amount_value,effective)
   AND m.second_curve IS NOT DISTINCT FROM second_code ORDER BY m.revision DESC LIMIT 1;
  END IF;
 END IF;
 changed=matching.id IS NULL;
 IF changed THEN
  IF coalesce(head.revision,0)<>expected THEN RAISE EXCEPTION 'curve_mark_stale';END IF;
  IF head.effective_at>effective THEN RAISE EXCEPTION 'curve_mark_time';END IF;
  IF batch_publication IS NULL THEN
   SELECT coalesce(max(publication_order),0)+1 INTO batch_publication FROM trading.publications WHERE catalog_id=catalog;
   INSERT INTO trading.publications VALUES(catalog,batch_publication,pg_current_xact_id());
  END IF;
  mark_id=gen_random_uuid();mark_revision=coalesce(head.revision,0)+1;
  IF mark_kind='volatility' THEN
   INSERT INTO trading.volatility_marks VALUES(catalog,mark_id,operation,definition,first_code,month,mark_revision,head.id,
     amount_value,'curve_marks_log_return_held_v1',actor,batch_publication,effective);
  ELSE
   INSERT INTO trading.correlation_marks VALUES(catalog,mark_id,operation,definition,first_code,second_code,month,mark_revision,head.id,
     amount_value,'curve_marks_log_return_held_v1',actor,batch_publication,effective);
  END IF;
  head.id=mark_id;head.revision=mark_revision;head.amount=amount_value;head.effective_at=effective;
 ELSE mark_id=matching.id;mark_revision=matching.revision;
 END IF;
 head_at_publication=jsonb_build_object('markId',head.id,'revision',head.revision,'value',trim_scale(head.amount)::text,'effectiveAt',head.effective_at);
 RETURN NEXT;
END$$;

DO $legacy$
DECLARE definition text=pg_get_functiondef('trading.record_curve_marks(uuid,jsonb,timestamptz,text)'::regprocedure);
 first_line integer=strpos(definition,'  -- A matching historical fact');
 last_line integer=strpos(definition,E' END LOOP;\n outcome=');
BEGIN
 IF first_line=0 OR last_line<=first_line THEN RAISE EXCEPTION 'curve_mark_editor_upgrade: cell writer shape';END IF;
 definition=left(definition,first_line-1)||$cell$
  SELECT c.batch_publication,results||jsonb_build_array(jsonb_build_object('month',item->>'month','markId',c.mark_id,'revision',c.mark_revision,'changed',c.changed))
  INTO publication,results FROM trading.record_curve_mark_cell(catalog,actor,operation,definition,mark_kind,first_code,second_code,
    month,(item->>'expectedRevision')::integer,amount_value,effective,publication) c;
$cell$||substr(definition,last_line);
 EXECUTE replace(replace(definition,'head trading.curve_mark_facts;',''),'mark uuid;','');
END$legacy$;

-- Legacy commands have no editor coordinates. Each new receipt owns the four
-- certified inputs from its admitted operation plus its context correlation.
ALTER TABLE trading.curve_mark_requests ADD COLUMN editor_metadata jsonb;

CREATE FUNCTION security.curve_mark_editor_authority(catalog uuid,writing boolean) RETURNS timestamptz
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 RETURN security.point_trader_mark_authority(catalog,writing);
EXCEPTION WHEN insufficient_privilege THEN RAISE EXCEPTION 'curve_mark_denied' USING ERRCODE='42501';
END$$;

CREATE FUNCTION security.curve_mark_editor_owner(catalog uuid) RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM security.curve_mark_editor_authority(catalog,false);
 RETURN presentation.preference_owner(security.presentation_actor(),'curve_mark_editor:'||catalog::text);
END$$;

CREATE FUNCTION security.require_curve_mark_editor_commit(catalog uuid,context uuid,as_of timestamptz,source jsonb,writing boolean,until_at timestamptz)
RETURNS void LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE definition uuid;reference_catalog uuid;reference_revision uuid;q security.request_contexts;
BEGIN
 IF source IS NOT NULL THEN
  IF trading.rec_keys(source,ARRAY['definitionRevisionId','referenceCatalogId','referenceRevisionId']) IS NOT TRUE
   OR EXISTS(SELECT 1 FROM jsonb_each(source) e WHERE jsonb_typeof(e.value)<>'string')
  THEN RAISE EXCEPTION 'curve_mark_source';END IF;
  definition=(source->>'definitionRevisionId')::uuid;reference_catalog=(source->>'referenceCatalogId')::uuid;
  reference_revision=(source->>'referenceRevisionId')::uuid;
 END IF;
 PERFORM security.require_point_trader_mark_commit(catalog,definition,reference_catalog,reference_revision,writing,until_at);
 q=security.presentation_actor();
 IF writing AND q.market_context_id IS NOT NULL THEN RAISE EXCEPTION 'historical_read_only';END IF;
 IF source IS NULL AND (context IS NOT NULL OR as_of IS NOT NULL OR writing)
 THEN RAISE EXCEPTION 'curve_mark_source';END IF;
 IF context IS NULL AND as_of IS NOT NULL OR context IS NOT NULL AND (as_of IS NULL OR source IS NULL)
 THEN RAISE EXCEPTION 'curve_mark_context';END IF;
 IF context IS NOT NULL THEN
  PERFORM security.require_point_trader_mark_read(catalog,context,as_of,definition,reference_catalog,reference_revision,until_at);
 END IF;
EXCEPTION WHEN insufficient_privilege THEN RAISE EXCEPTION 'curve_mark_denied' USING ERRCODE='42501';
 WHEN invalid_text_representation THEN RAISE EXCEPTION 'curve_mark_source';
 WHEN raise_exception THEN
  CASE SQLERRM WHEN 'point_trader_mark_source' THEN RAISE EXCEPTION 'curve_mark_source';
   WHEN 'point_trader_mark_context' THEN RAISE EXCEPTION 'curve_mark_context';
   ELSE RAISE;END CASE;
END$$;

CREATE FUNCTION trading.curve_mark_editor_keys(catalog uuid,source jsonb,mark_kind text,keys jsonb)
RETURNS void LANGUAGE plpgsql STABLE SET search_path=pg_catalog AS $$
DECLARE item jsonb;curves text[];
BEGIN
 IF mark_kind IS NULL OR mark_kind NOT IN('volatility','correlation') OR jsonb_typeof(keys) IS DISTINCT FROM 'array'
 THEN RAISE EXCEPTION 'curve_mark_input';END IF;
 IF jsonb_array_length(keys) NOT BETWEEN 1 AND 240 THEN RAISE EXCEPTION 'curve_mark_bounds';END IF;
 FOR item IN SELECT value FROM jsonb_array_elements(keys) LOOP
  IF trading.rec_keys(item,ARRAY['firstCurve','secondCurve','month']) IS NOT TRUE
   OR jsonb_typeof(item->'firstCurve') IS DISTINCT FROM 'string' OR item->>'firstCurve' !~ '^[a-z][a-z0-9_.-]{0,63}$'
   OR jsonb_typeof(item->'month') IS DISTINCT FROM 'string' OR item->>'month' !~ '^[0-9]{4}-(0[1-9]|1[0-2])$'
   OR (item->>'month') COLLATE "C" NOT BETWEEN '1901-01' AND '2199-12'
   OR mark_kind='volatility' AND item->'secondCurve' IS DISTINCT FROM 'null'::jsonb
   OR mark_kind='correlation' AND (jsonb_typeof(item->'secondCurve') IS DISTINCT FROM 'string' OR item->>'secondCurve' !~ '^[a-z][a-z0-9_.-]{0,63}$')
  THEN RAISE EXCEPTION 'curve_mark_input';END IF;
  IF mark_kind='correlation' AND (item->>'firstCurve') COLLATE "C">=item->>'secondCurve' THEN RAISE EXCEPTION 'curve_mark_pair';END IF;
 END LOOP;
 IF (SELECT count(DISTINCT value) FROM jsonb_array_elements(keys))<>jsonb_array_length(keys)
 THEN RAISE EXCEPTION 'curve_mark_duplicate';END IF;
 SELECT array_agg(DISTINCT code) INTO curves FROM jsonb_array_elements(keys) k
 CROSS JOIN LATERAL unnest(ARRAY[k->>'firstCurve',k->>'secondCurve']) code WHERE code IS NOT NULL;
 PERFORM trading.require_curve_mark_sources(catalog,(source->>'definitionRevisionId')::uuid,curves,mark_kind='correlation');
END$$;

CREATE FUNCTION security.record_curve_mark_batch(catalog uuid,context uuid,as_of timestamptz,body jsonb) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE deadline timestamptz=security.curve_mark_editor_authority(catalog,true);owner_id text=security.curve_mark_editor_owner(catalog);
 actor uuid=(security.presentation_actor()).actor_id;operation uuid;mark_kind text=body->>'kind';source jsonb=body->'source';keys jsonb;
 prior trading.curve_mark_requests;cut security.operation_read_coordinates;metadata jsonb;item jsonb;amount_value numeric;
 publication integer;cell record;cells jsonb='[]';outcome jsonb;
BEGIN
 IF context IS NULL OR as_of IS NULL OR jsonb_typeof(body) IS DISTINCT FROM 'object' OR pg_column_size(body)>131072
  OR trading.rec_keys(body,ARRAY['policy','ownerId','operationId','kind','source','snapshotId','reason','changes']) IS NOT TRUE
  OR EXISTS(SELECT 1 FROM jsonb_each(body)e WHERE e.key NOT IN('source','changes','snapshotId') AND jsonb_typeof(e.value)<>'string')
  OR body->>'policy' IS DISTINCT FROM 'curve_mark_batch_v1' OR mark_kind NOT IN('volatility','correlation')
  OR jsonb_typeof(body->'source') IS DISTINCT FROM 'object' OR jsonb_typeof(body->'changes') IS DISTINCT FROM 'array'
  OR jsonb_typeof(body->'snapshotId') NOT IN('string','null') OR (body->>'snapshotId')::uuid='00000000-0000-0000-0000-000000000000'::uuid
  OR body->>'reason' !~ '[^[:space:]]' OR length(body->>'reason')>500
 THEN RAISE EXCEPTION 'curve_mark_input';END IF;
 IF owner_id IS DISTINCT FROM body->>'ownerId' THEN RAISE EXCEPTION 'curve_mark_owner_changed' USING ERRCODE='42501';END IF;
 operation=(body->>'operationId')::uuid;
 IF operation='00000000-0000-0000-0000-000000000000' THEN RAISE EXCEPTION 'curve_mark_input';END IF;
 IF jsonb_array_length(body->'changes') NOT BETWEEN 1 AND 240 THEN RAISE EXCEPTION 'curve_mark_bounds';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 PERFORM security.require_curve_mark_editor_commit(catalog,context,as_of,source,true,deadline);
 SELECT s.* INTO STRICT cut FROM security.operation_read_inputs s WHERE(s.catalog_id,s.id)=(catalog,context);
 metadata=jsonb_build_object('policy','curve_mark_batch_v1','sourceContextId',context,'sourceInputs',
  jsonb_build_object('businessOn',cut.effective_on,'knownAt',market_data.point_trader_mark_instant(cut.known_at),'systemId',cut.system_id,'timeline',cut.timeline),'authorityUntil',deadline);
 IF EXISTS(SELECT FROM jsonb_array_elements(body->'changes') e WHERE jsonb_typeof(e)<>'object')
 THEN RAISE EXCEPTION 'curve_mark_input';END IF;
 SELECT jsonb_agg(value-'value'-'expectedRevision') INTO keys FROM jsonb_array_elements(body->'changes');
 PERFORM trading.curve_mark_editor_keys(catalog,source,mark_kind,keys);
 SELECT r.* INTO prior FROM trading.curve_mark_requests r WHERE(r.catalog_id,r.actor_id,r.kind,r.operation_id)=(catalog,actor,mark_kind,operation);
 IF FOUND THEN
  IF (prior.command,prior.effective_at,prior.editor_metadata-'authorityUntil') IS DISTINCT FROM(body,as_of,metadata-'authorityUntil')
  THEN RAISE EXCEPTION 'curve_mark_request_conflict';END IF;
  PERFORM security.require_curve_mark_editor_commit(catalog,context,as_of,source,true,deadline);
  RETURN prior.outcome;
 END IF;
 FOR item IN SELECT value FROM jsonb_array_elements(body->'changes') LOOP
  IF jsonb_typeof(item->'expectedRevision') IS DISTINCT FROM 'number' OR item->>'expectedRevision' !~ '^(0|[1-9][0-9]{0,9})$'
  THEN RAISE EXCEPTION 'curve_mark_input';END IF;
  IF jsonb_typeof(item->'value') IS DISTINCT FROM 'string' OR item->>'value' !~ '^-?[0-9]+([.][0-9]{1,12})?$'
  THEN RAISE EXCEPTION 'curve_mark_value';END IF;
  amount_value=(item->>'value')::numeric;
  IF mark_kind='volatility' AND amount_value NOT BETWEEN 0 AND 10 OR mark_kind='correlation' AND abs(amount_value)>1
  THEN RAISE EXCEPTION 'curve_mark_value';END IF;
  SELECT * INTO STRICT cell FROM trading.record_curve_mark_cell(catalog,actor,operation,(source->>'definitionRevisionId')::uuid,mark_kind,
   item->>'firstCurve',item->>'secondCurve',(item->>'month'||'-01')::date,(item->>'expectedRevision')::integer,amount_value,as_of,publication);
  publication=cell.batch_publication;
  cells=cells||jsonb_build_array(jsonb_build_object('key',item-'value'-'expectedRevision','markId',cell.mark_id,'revision',cell.mark_revision,
   'value',trim_scale(amount_value)::text,'changed',cell.changed,'headAtPublication',cell.head_at_publication));
 END LOOP;
 outcome=(body-'snapshotId'-'changes')||jsonb_build_object('effectiveAt',as_of,'cells',cells);
 INSERT INTO trading.curve_mark_requests(catalog_id,actor_id,kind,operation_id,command,effective_at,outcome,editor_metadata)
 VALUES(catalog,actor,mark_kind,operation,body,as_of,outcome,metadata);
 PERFORM security.require_curve_mark_editor_commit(catalog,context,as_of,source,true,deadline);
 RETURN outcome;
EXCEPTION WHEN invalid_text_representation OR numeric_value_out_of_range THEN RAISE EXCEPTION 'curve_mark_input';
END$$;

CREATE FUNCTION trading.guard_curve_mark_editor_commit() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM security.require_curve_mark_editor_commit(NEW.catalog_id,(NEW.editor_metadata->>'sourceContextId')::uuid,
  NEW.effective_at,NEW.command->'source',true,(NEW.editor_metadata->>'authorityUntil')::timestamptz);
 RETURN NULL;
END$$;
CREATE CONSTRAINT TRIGGER curve_mark_editor_commit AFTER INSERT ON trading.curve_mark_requests
 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN(NEW.editor_metadata IS NOT NULL) EXECUTE FUNCTION trading.guard_curve_mark_editor_commit();

CREATE FUNCTION security.curve_mark_editor_status(catalog uuid,owner_id text,operation uuid,mark_kind text) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE deadline timestamptz=security.curve_mark_editor_authority(catalog,false);actor uuid=(security.presentation_actor()).actor_id;
 prior trading.curve_mark_requests;inputs jsonb;context uuid;
BEGIN
 IF owner_id IS DISTINCT FROM security.curve_mark_editor_owner(catalog) THEN RAISE EXCEPTION 'curve_mark_owner_changed' USING ERRCODE='42501';END IF;
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000' OR mark_kind IS NULL OR mark_kind NOT IN('volatility','correlation')
 THEN RAISE EXCEPTION 'curve_mark_input';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT r.* INTO prior FROM trading.curve_mark_requests r WHERE(r.catalog_id,r.actor_id,r.kind,r.operation_id)=(catalog,actor,mark_kind,operation);
 IF NOT FOUND THEN
  PERFORM security.require_curve_mark_editor_commit(catalog,NULL,NULL,NULL,false,deadline);
  RETURN jsonb_build_object('found',false,'authorityUntil',deadline);
 END IF;
 IF prior.editor_metadata->>'policy' IS DISTINCT FROM 'curve_mark_batch_v1' THEN RAISE EXCEPTION 'curve_mark_request_conflict';END IF;
 inputs=prior.editor_metadata->'sourceInputs';context=(prior.editor_metadata->>'sourceContextId')::uuid;
 PERFORM security.register_owner_coordinates(catalog,context,(inputs->>'businessOn')::date,(inputs->>'knownAt')::timestamptz,
  inputs->>'systemId',(inputs->>'timeline')::integer);
 PERFORM security.require_curve_mark_editor_commit(catalog,context,prior.effective_at,prior.command->'source',false,deadline);
 RETURN jsonb_build_object('found',true,'authorityUntil',deadline,'source',prior.command->'source','sourceContextId',context,
  'sourceInputs',inputs,'command',prior.command,'effectiveAt',prior.effective_at,'outcome',prior.outcome);
END$$;

CREATE FUNCTION trading.require_curve_mark_page(page_offset integer,page_limit integer) RETURNS void
LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
BEGIN
 IF page_offset IS NULL OR page_offset<0 OR page_limit IS NULL OR page_limit NOT BETWEEN 1 AND 50 THEN RAISE EXCEPTION 'curve_mark_bounds';END IF;
END$$;

CREATE FUNCTION security.curve_mark_editor_setup(catalog uuid,context uuid,as_of timestamptz,source jsonb,search text,page_offset integer,page_limit integer)
RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE import_until timestamptz=security.authority_until('ImportObservations');
 deadline timestamptz=security.curve_mark_editor_authority(catalog,false);result jsonb;
BEGIN
 IF context IS NULL OR as_of IS NULL OR source IS NULL THEN RAISE EXCEPTION 'curve_mark_context';END IF;
 PERFORM security.require_curve_mark_editor_commit(catalog,context,as_of,source,false,deadline);
 PERFORM trading.require_curve_mark_page(page_offset,page_limit);
 IF length(search)>256 THEN RAISE EXCEPTION 'curve_mark_bounds';END IF;
 WITH page AS (
  SELECT c.code,c.name,m.display_code AS currency FROM market_data.curves c JOIN ref_data.quote_specifications q
   ON(q.catalog_id,q.revision_id,q.code)=(c.reference_catalog_id,c.reference_revision_id,c.quote_code)
  JOIN ref_data.currencies m ON(m.catalog_id,m.revision_id,m.code)=(q.catalog_id,q.revision_id,q.currency_code)
  WHERE(c.catalog_id,c.revision_id,c.reference_catalog_id,c.reference_revision_id)=
   (catalog,(source->>'definitionRevisionId')::uuid,(source->>'referenceCatalogId')::uuid,(source->>'referenceRevisionId')::uuid)
   AND q.kind='outright' AND q.multiplier=1
   AND (strpos(lower(c.code),lower(coalesce(search,'')))>0 OR strpos(lower(c.name),lower(coalesce(search,'')))>0)
  ORDER BY c.code COLLATE "C" OFFSET page_offset LIMIT page_limit+1
 ), numbered AS (SELECT *,row_number() OVER(ORDER BY code COLLATE "C") n FROM page)
 SELECT jsonb_build_object('curves',coalesce(jsonb_agg(jsonb_build_object('code',code,'name',name,'currency',currency) ORDER BY n)
  FILTER(WHERE n<=page_limit),'[]'),'hasMoreCurves',count(*)>page_limit) INTO result FROM numbered;
 PERFORM security.require_curve_mark_editor_commit(catalog,context,as_of,source,false,deadline);
 RETURN result||jsonb_build_object('canPublish',coalesce(clock_timestamp()<import_until AND security.market_access(catalog,'ImportObservations') IS TRUE,false));
END$$;

-- A revision's own command supplies its reason, including a legacy command.
-- No acknowledgement can become the provenance of an already recorded fact.
CREATE FUNCTION trading.curve_mark_editor_facts(cut security.operation_read_coordinates,definition uuid,mark_kind text,key jsonb)
RETURNS TABLE(mark jsonb,revision integer,effective_at timestamptz,previous_id uuid,actor_id uuid,reason text)
LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('id',m.id,'revision',m.revision,'value',trim_scale(m.amount)::text,'effectiveAt',m.effective_at,'knownAt',k.known_from),
  m.revision,m.effective_at,m.previous_id,m.actor_id,r.command->>'reason'
 FROM (
  SELECT v.id,v.revision,v.volatility amount,v.effective_at,v.previous_id,v.actor_id,v.operation_id,v.publication_order
  FROM trading.volatility_marks v WHERE mark_kind='volatility' AND (v.catalog_id,v.definition_revision_id,v.curve_code,v.bucket_month)=
    (cut.catalog_id,definition,key->>'firstCurve',(key->>'month'||'-01')::date)
  UNION ALL
  SELECT p.id,p.revision,p.correlation,p.effective_at,p.previous_id,p.actor_id,p.operation_id,p.publication_order
  FROM trading.correlation_marks p WHERE mark_kind='correlation' AND (p.catalog_id,p.definition_revision_id,p.first_curve_code,p.second_curve_code,p.bucket_month)=
    (cut.catalog_id,definition,key->>'firstCurve',key->>'secondCurve',(key->>'month'||'-01')::date)
 ) m JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(cut.catalog_id,m.publication_order)
 JOIN trading.curve_mark_requests r ON(r.catalog_id,r.actor_id,r.kind,r.operation_id)=(cut.catalog_id,m.actor_id,mark_kind,m.operation_id)
 WHERE m.publication_order<=cut.trading_prefix AND k.known_from<=cut.known_at
$$;

CREATE FUNCTION security.curve_mark_editor_cells(catalog uuid,context uuid,as_of timestamptz,source jsonb,mark_kind text,keys jsonb,working boolean)
RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE import_until timestamptz=security.authority_until('ImportObservations');
 deadline timestamptz=security.curve_mark_editor_authority(catalog,false);can_write boolean;result jsonb;cut security.operation_read_coordinates;
BEGIN
 IF context IS NULL OR as_of IS NULL OR source IS NULL OR working IS NULL THEN RAISE EXCEPTION 'curve_mark_context';END IF;
 PERFORM security.require_curve_mark_editor_commit(catalog,context,as_of,source,false,deadline);
 SELECT s.* INTO STRICT cut FROM security.operation_read_inputs s WHERE(s.catalog_id,s.id)=(catalog,context);
 PERFORM trading.curve_mark_editor_keys(catalog,source,mark_kind,keys);
 can_write=working AND coalesce(clock_timestamp()<import_until AND security.market_access(catalog,'ImportObservations') IS TRUE,false);
 SELECT jsonb_agg(jsonb_build_object('key',i.key,'selected',f.selected,'writeHead',CASE WHEN can_write THEN f.head END,
  'expectedRevision',CASE WHEN can_write AND (f.head IS NULL OR f.head_at<=as_of) THEN coalesce(f.head_revision,0) END,
  'editBlock',CASE WHEN NOT can_write THEN jsonb_build_object('code','curve_mark_read_only','message','Publication requires a working market editor.')
   WHEN f.head_at>as_of THEN jsonb_build_object('code','curve_mark_time','message','A later-effective revision prevents a correction at this instant.') END)
  ORDER BY i.ordinal) INTO result FROM jsonb_array_elements(keys) WITH ORDINALITY i(key,ordinal)
 CROSS JOIN LATERAL (
  WITH known AS MATERIALIZED (SELECT * FROM trading.curve_mark_editor_facts(cut,(source->>'definitionRevisionId')::uuid,mark_kind,i.key)),
   head AS (SELECT * FROM known ORDER BY revision DESC LIMIT 1)
  SELECT (SELECT mark FROM known WHERE effective_at<=as_of ORDER BY revision DESC LIMIT 1) selected,
   (SELECT mark FROM head) head,(SELECT effective_at FROM head) head_at,(SELECT revision FROM head) head_revision
 ) f;
 PERFORM security.require_curve_mark_editor_commit(catalog,context,as_of,source,false,deadline);
 IF can_write AND (clock_timestamp()>=import_until OR security.market_access(catalog,'ImportObservations') IS NOT TRUE)
 THEN RAISE EXCEPTION 'curve_mark_denied' USING ERRCODE='42501';END IF;
 RETURN result;
END$$;

CREATE FUNCTION security.curve_mark_editor_history(catalog uuid,context uuid,as_of timestamptz,source jsonb,mark_kind text,key jsonb,page_offset integer,page_limit integer)
RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE deadline timestamptz=security.curve_mark_editor_authority(catalog,false);result jsonb;cut security.operation_read_coordinates;
BEGIN
 IF context IS NULL OR as_of IS NULL OR source IS NULL THEN RAISE EXCEPTION 'curve_mark_context';END IF;
 PERFORM security.require_curve_mark_editor_commit(catalog,context,as_of,source,false,deadline);
 SELECT s.* INTO STRICT cut FROM security.operation_read_inputs s WHERE(s.catalog_id,s.id)=(catalog,context);
 PERFORM trading.curve_mark_editor_keys(catalog,source,mark_kind,jsonb_build_array(key));
 PERFORM trading.require_curve_mark_page(page_offset,page_limit);
 WITH page AS (
  SELECT * FROM trading.curve_mark_editor_facts(cut,(source->>'definitionRevisionId')::uuid,mark_kind,key)
  ORDER BY revision DESC OFFSET page_offset LIMIT page_limit+1
 ), numbered AS (SELECT *,row_number() OVER(ORDER BY revision DESC) n FROM page)
 SELECT jsonb_build_object('revisions',coalesce(jsonb_agg(jsonb_build_object('mark',mark,'previousId',previous_id,'actorId',actor_id,
  'reason',reason,'effectiveEligible',effective_at<=as_of) ORDER BY n) FILTER(WHERE n<=page_limit),'[]'),'hasMore',count(*)>page_limit)
 INTO result FROM numbered;
 PERFORM security.require_curve_mark_editor_commit(catalog,context,as_of,source,false,deadline);
 RETURN result;
END$$;

DO $permissions$
DECLARE f regprocedure;
BEGIN
 FOREACH f IN ARRAY ARRAY[
  'trading.record_curve_mark_cell(uuid,uuid,uuid,uuid,text,text,text,date,integer,numeric,timestamptz,integer)'::regprocedure,
  'trading.curve_mark_editor_keys(uuid,jsonb,text,jsonb)'::regprocedure,
  'trading.require_curve_mark_page(integer,integer)'::regprocedure,
  'trading.guard_curve_mark_editor_commit()'::regprocedure,
  'trading.curve_mark_editor_facts(security.operation_read_coordinates,uuid,text,jsonb)'::regprocedure,
  'security.curve_mark_editor_authority(uuid,boolean)'::regprocedure,
  'security.curve_mark_editor_owner(uuid)'::regprocedure,
  'security.require_curve_mark_editor_commit(uuid,uuid,timestamptz,jsonb,boolean,timestamptz)'::regprocedure,
  'security.record_curve_mark_batch(uuid,uuid,timestamptz,jsonb)'::regprocedure,
  'security.curve_mark_editor_status(uuid,text,uuid,text)'::regprocedure,
  'security.curve_mark_editor_setup(uuid,uuid,timestamptz,jsonb,text,integer,integer)'::regprocedure,
  'security.curve_mark_editor_cells(uuid,uuid,timestamptz,jsonb,text,jsonb,boolean)'::regprocedure,
  'security.curve_mark_editor_history(uuid,uuid,timestamptz,jsonb,text,jsonb,integer,integer)'::regprocedure
 ] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f);
  IF (SELECT pronamespace='security'::regnamespace FROM pg_proc WHERE oid=f)
  THEN EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_runtime',f);END IF;
 END LOOP;
END$permissions$;
COMMIT;
