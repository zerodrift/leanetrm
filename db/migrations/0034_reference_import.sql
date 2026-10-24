-- Source-backed, non-executable reference intake. No market/financial publication.
BEGIN;
-- Only the private managed operator holds this executor role; the API runtime cannot submit assertions.
CREATE ROLE openetrm_reference_importer NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
CREATE TABLE ref_data.reference_import_receipts(
 catalog_id uuid NOT NULL,revision_id uuid NOT NULL,provider text NOT NULL CHECK(provider IN('CME','ICE')),
 request_sha256 text NOT NULL CHECK(request_sha256 ~ '^[0-9a-f]{64}$'),package_sha256 text NOT NULL CHECK(package_sha256 ~ '^[0-9a-f]{64}$'),
 actor_id uuid NOT NULL REFERENCES security.principals(id),recorded_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,revision_id),UNIQUE(catalog_id,request_sha256),
 FOREIGN KEY(catalog_id,revision_id) REFERENCES ref_data.revisions(catalog_id,id)
);
CREATE TABLE ref_data.source_documents(
 catalog_id uuid NOT NULL,revision_id uuid NOT NULL,source_code ref_data.code NOT NULL,
 content bytea NOT NULL CHECK(octet_length(content) BETWEEN 1 AND 16777216),
 acquisition_kind text NOT NULL CHECK(acquisition_kind IN('http_response','operator_reported_download')),
 reported_received_at text NOT NULL,
 PRIMARY KEY(catalog_id,revision_id,source_code),
 FOREIGN KEY(catalog_id,revision_id) REFERENCES ref_data.reference_import_receipts(catalog_id,revision_id),
 FOREIGN KEY(catalog_id,revision_id,source_code) REFERENCES ref_data.sources(catalog_id,revision_id,code)
);
CREATE TABLE ref_data.imported_product_keys(
 catalog_id uuid NOT NULL,revision_id uuid NOT NULL,provider text NOT NULL,exchange_id text NOT NULL,
 product_id text NOT NULL CHECK(length(product_id) BETWEEN 1 AND 64),product_code ref_data.code NOT NULL,source_code ref_data.code NOT NULL,
 PRIMARY KEY(catalog_id,revision_id,provider,exchange_id,product_id),UNIQUE(catalog_id,revision_id,product_code),
 CHECK((provider,exchange_id) IN(('CME','NYMEX'),('ICE','IFED'))),
 FOREIGN KEY(catalog_id,revision_id) REFERENCES ref_data.reference_import_receipts(catalog_id,revision_id),
 FOREIGN KEY(catalog_id,revision_id,product_code) REFERENCES ref_data.futures_products(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,revision_id,source_code) REFERENCES ref_data.source_documents(catalog_id,revision_id,source_code)
);
CREATE TABLE ref_data.imported_series_keys(
 catalog_id uuid NOT NULL,revision_id uuid NOT NULL,provider text NOT NULL,exchange_id text NOT NULL,product_id text NOT NULL,
 contract_id text NOT NULL CHECK(length(contract_id) BETWEEN 1 AND 160),series_code ref_data.code NOT NULL,source_code ref_data.code NOT NULL,
 native_period text CHECK(length(native_period) BETWEEN 1 AND 32),
 first_trading_date date,last_trading_date date,first_notice_date date,last_notice_date date,
 first_delivery_date date,last_delivery_date date,final_settlement_date date,
 reported_energy_amount ref_data.exact_number CHECK(reported_energy_amount>0),
 quantity_catalog_id uuid,quantity_revision_id uuid,quantity_code ref_data.code,
 PRIMARY KEY(catalog_id,revision_id,provider,exchange_id,product_id,contract_id),UNIQUE(catalog_id,revision_id,series_code),
 FOREIGN KEY(catalog_id,revision_id,provider,exchange_id,product_id) REFERENCES ref_data.imported_product_keys(catalog_id,revision_id,provider,exchange_id,product_id),
 FOREIGN KEY(catalog_id,revision_id,series_code) REFERENCES ref_data.futures_series(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,revision_id,source_code) REFERENCES ref_data.source_documents(catalog_id,revision_id,source_code),
 FOREIGN KEY(quantity_catalog_id,quantity_revision_id,quantity_code) REFERENCES ref_data.quantity_specifications(catalog_id,revision_id,code),
 CHECK((reported_energy_amount IS NULL AND quantity_catalog_id IS NULL AND quantity_revision_id IS NULL AND quantity_code IS NULL)
    OR(reported_energy_amount IS NOT NULL AND quantity_catalog_id IS NOT NULL AND quantity_revision_id IS NOT NULL AND quantity_code IS NOT NULL)),
 CHECK(first_trading_date<=last_trading_date),CHECK(first_delivery_date<=last_delivery_date)
);
DO $$DECLARE t text;column_name text;BEGIN
 FOREACH t IN ARRAY ARRAY['reference_import_receipts','source_documents','imported_product_keys','imported_series_keys'] LOOP
  EXECUTE format('CREATE TRIGGER guard_definition BEFORE INSERT OR UPDATE OR DELETE ON ref_data.%I FOR EACH ROW EXECUTE FUNCTION ref_data.guard_definition()',t);
  EXECUTE format('ALTER TABLE ref_data.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE ref_data.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY scoped_read ON ref_data.%I FOR SELECT TO openetrm_runtime USING(security.reference_access(catalog_id,''ReadMarketData''))',t);
  EXECUTE format('CREATE POLICY temporal_read ON ref_data.%I AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(security.reference_version_visible(catalog_id,revision_id))',t);
  EXECUTE format('GRANT SELECT ON ref_data.%I TO openetrm_runtime',t);
  EXECUTE format('ALTER TABLE ref_data.%I OWNER TO openetrm_security_owner',t);
 END LOOP;
 FOREACH column_name IN ARRAY ARRAY['first_trading_date','last_trading_date','first_notice_date','last_notice_date','first_delivery_date','last_delivery_date','final_settlement_date'] LOOP
  EXECUTE format('ALTER TABLE ref_data.imported_series_keys ADD CHECK(%1$I IS NULL OR(isfinite(%1$I) AND %1$I>=DATE ''0001-01-01'' AND %1$I<DATE ''9999-12-31''))',column_name);
 END LOOP;
END$$;
CREATE POLICY quantity_scope ON ref_data.imported_series_keys AS RESTRICTIVE FOR SELECT TO openetrm_runtime
 USING(quantity_catalog_id IS NULL OR(security.reference_access(quantity_catalog_id,'ReadMarketData') AND security.reference_version_visible(quantity_catalog_id,quantity_revision_id)));

CREATE FUNCTION security.import_reference_capture(catalog uuid,operation uuid,payload jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE request_hash text;receipt ref_data.reference_import_receipts;actor uuid;provider_name text;doc jsonb;p jsonb;s jsonb;
 source_code text;exchange_code text;body bytea;key text;native_date text;product ref_data.futures_products;source ref_data.sources;
BEGIN
 IF catalog IS NULL OR operation IS NULL OR NOT security.reference_access(catalog,'WriteReferenceData') OR NOT security.reference_access(catalog,'SealReferenceData')
 OR NOT security.reference_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'reference_import_denied' USING ERRCODE='42501';END IF;
 IF jsonb_typeof(payload) IS DISTINCT FROM 'object' OR pg_column_size(payload)>33554432 THEN RAISE EXCEPTION 'reference_import_input';END IF;
 IF payload->>'protocol' IS DISTINCT FROM 'reference_import_v1' OR coalesce(payload->>'provider','') NOT IN('CME','ICE')
 OR coalesce(payload->>'packageSha256','') !~ '^[0-9a-f]{64}$' OR length(payload->>'licenseReference')>1000
 OR EXISTS(SELECT 1 FROM jsonb_object_keys(payload)k WHERE k NOT IN('protocol','provider','packageSha256','licenseReference','documents','products','series'))
 OR jsonb_typeof(payload->'documents') IS DISTINCT FROM 'array' OR jsonb_typeof(payload->'products') IS DISTINCT FROM 'array' OR jsonb_typeof(payload->'series') IS DISTINCT FROM 'array'
 THEN RAISE EXCEPTION 'reference_import_input';END IF;
 IF jsonb_array_length(payload->'documents') NOT BETWEEN 1 AND 1024 OR jsonb_array_length(payload->'products') NOT BETWEEN 1 AND 128 OR jsonb_array_length(payload->'series')>50000 THEN RAISE EXCEPTION 'reference_import_input';END IF;
 request_hash=encode(sha256(convert_to(payload::text,'UTF8')),'hex');provider_name=payload->>'provider';
 PERFORM pg_advisory_xact_lock(hashtextextended(catalog::text||operation::text,730849114));
 IF NOT security.reference_access(catalog,'ReadMarketData') OR NOT security.reference_access(catalog,'WriteReferenceData') OR NOT security.reference_access(catalog,'SealReferenceData') THEN RAISE EXCEPTION 'reference_import_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO receipt FROM ref_data.reference_import_receipts WHERE catalog_id=catalog AND revision_id=operation;
 IF receipt.revision_id IS NOT NULL THEN
  IF receipt.request_sha256<>request_hash THEN RAISE EXCEPTION 'reference_import_conflict' USING ERRCODE='40001';END IF;
 ELSE
  IF EXISTS(SELECT 1 FROM ref_data.reference_import_receipts WHERE catalog_id=catalog AND request_sha256=request_hash) THEN RAISE EXCEPTION 'reference_import_conflict' USING ERRCODE='40001';END IF;
  SELECT actor_id INTO actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user AND expires_at>clock_timestamp();
  IF actor IS NULL THEN RAISE EXCEPTION 'reference_import_denied' USING ERRCODE='42501';END IF;
  INSERT INTO ref_data.revisions(catalog_id,id,revision_key) VALUES(catalog,operation,'import_'||replace(operation::text,'-',''));
  INSERT INTO ref_data.reference_import_receipts VALUES(catalog,operation,provider_name,request_hash,payload->>'packageSha256',actor,clock_timestamp());
  FOR doc IN SELECT value FROM jsonb_array_elements(payload->'documents') LOOP
   IF jsonb_typeof(doc) IS DISTINCT FROM 'object' OR coalesce(doc->>'sha256','') !~ '^[0-9a-f]{64}$'
   OR coalesce(doc->>'receivedAtUtc','') !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}([.][0-9]{1,6})?(Z|[+]00:00)$'
   OR (doc->>'receivedAtUtc')::timestamptz>clock_timestamp() OR (doc->>'receivedAtUtc')::timestamptz<'2000-01-01T00:00:00Z'
   OR coalesce(doc->>'reportedReceivedAtUtc','') !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}[.][0-9]{7}[+]00:00$'
   OR regexp_replace(doc->>'reportedReceivedAtUtc','([.][0-9]{6})[0-9]+','\1')::timestamptz<>(doc->>'receivedAtUtc')::timestamptz
   THEN RAISE EXCEPTION 'reference_import_evidence';END IF;
   IF(provider_name='CME' AND(coalesce(doc->>'url','') !~ '^https://refdata[.]api[.]cmegroup[.]com/refdata/v3/products/[A-Za-z0-9_-]{1,64}(/instruments[?]page=[0-9]+&size=[0-9]+)?$' OR doc->>'acquisitionKind' IS DISTINCT FROM 'http_response'))
   OR(provider_name='ICE' AND(coalesce(doc->>'url','') !~ '^https://www[.]ice[.]com/api/productguide/spec/[0-9]{1,18}/(expiry/csv|pdf)$' OR doc->>'acquisitionKind' IS DISTINCT FROM 'operator_reported_download')) THEN RAISE EXCEPTION 'reference_import_evidence';END IF;
   body=decode(doc->>'contentBase64','base64');
   IF body IS NULL OR octet_length(body) NOT BETWEEN 1 AND 16777216 OR encode(sha256(body),'hex')<>doc->>'sha256' THEN RAISE EXCEPTION 'reference_import_evidence';END IF;
   INSERT INTO ref_data.sources(catalog_id,revision_id,code,kind,locator,retrieved_at,content_sha256,license_reference,distribution_review)
    VALUES(catalog,operation,doc->>'code',CASE provider_name WHEN 'CME' THEN 'licensed_feed' ELSE 'external_document' END,doc->>'url',(doc->>'receivedAtUtc')::timestamptz,doc->>'sha256',payload->>'licenseReference','not_reviewed');
   INSERT INTO ref_data.source_documents VALUES(catalog,operation,doc->>'code',body,doc->>'acquisitionKind',doc->>'reportedReceivedAtUtc');
  END LOOP;
  INSERT INTO ref_data.dimensions VALUES(catalog,operation,'energy',1,2,-2,0,0,0,0);
  -- Only the explicit supported energy-commodity vocabulary is admitted here.
  FOR key IN SELECT DISTINCT value->>'commodity' FROM jsonb_array_elements(payload->'products') LOOP
   IF key IS NULL OR key NOT IN('gas','electricity') THEN RAISE EXCEPTION 'reference_import_mapping';END IF;
   INSERT INTO ref_data.commodities VALUES(catalog,operation,key,CASE key WHEN 'gas' THEN 'Natural gas' ELSE 'Electricity' END,'energy');
  END LOOP;
  FOR key IN SELECT DISTINCT value->>'locationType' FROM jsonb_array_elements(payload->'products') LOOP
   IF key IS NULL OR key NOT IN('hub','pricing_location') THEN RAISE EXCEPTION 'reference_import_mapping';END IF;
   INSERT INTO ref_data.location_types VALUES(catalog,operation,key,CASE key WHEN 'hub' THEN 'Hub' ELSE 'Pricing location' END);
  END LOOP;
  FOR p IN SELECT value FROM jsonb_array_elements(payload->'products') LOOP
   IF p->>'provider' IS DISTINCT FROM provider_name OR(provider_name='CME' AND coalesce(p->>'exchangeId','')<>'NYMEX') OR(provider_name='ICE' AND coalesce(p->>'exchangeId','')<>'IFED')
   OR coalesce(p->>'productId','') !~ '^[A-Za-z0-9_-]{1,64}$' OR length(p->>'name') NOT BETWEEN 1 AND 300 OR length(p->>'locationName') NOT BETWEEN 1 AND 160 THEN RAISE EXCEPTION 'reference_import_mapping';END IF;
   exchange_code=CASE provider_name WHEN 'CME' THEN 'cme_nymex' ELSE 'ice_ifed' END;
   INSERT INTO ref_data.exchanges VALUES(catalog,operation,exchange_code,CASE provider_name WHEN 'CME' THEN 'NYMEX' ELSE 'ICE Futures U.S. Energy Division' END) ON CONFLICT DO NOTHING;
   INSERT INTO ref_data.locations(catalog_id,revision_id,code,name,type_code) VALUES(catalog,operation,p->>'locationCode',p->>'locationName',p->>'locationType') ON CONFLICT DO NOTHING;
   IF NOT EXISTS(SELECT 1 FROM ref_data.locations WHERE(catalog_id,revision_id,code,name,type_code)=(catalog,operation,p->>'locationCode',p->>'locationName',p->>'locationType')) THEN RAISE EXCEPTION 'reference_import_mapping';END IF;
   INSERT INTO ref_data.commodity_locations VALUES(catalog,operation,p->>'commodity',p->>'locationCode') ON CONFLICT DO NOTHING;
   INSERT INTO ref_data.futures_products(catalog_id,revision_id,code,name,definition_state,exchange_code,commodity_code,location_code)
    VALUES(catalog,operation,p->>'code',p->>'name','placeholder',exchange_code,p->>'commodity',p->>'locationCode');
   SELECT * INTO source FROM ref_data.sources WHERE(catalog_id,revision_id,code)=(catalog,operation,p->>'documentCode');
   IF source.code IS NULL OR(provider_name='CME' AND source.locator<>'https://refdata.api.cmegroup.com/refdata/v3/products/'||(p->>'productId'))
    OR(provider_name='ICE' AND source.locator NOT IN('https://www.ice.com/api/productguide/spec/'||(p->>'productId')||'/expiry/csv','https://www.ice.com/api/productguide/spec/'||(p->>'productId')||'/pdf')) THEN RAISE EXCEPTION 'reference_import_evidence';END IF;
   INSERT INTO ref_data.imported_product_keys VALUES(catalog,operation,provider_name,p->>'exchangeId',p->>'productId',p->>'code',p->>'documentCode');
   INSERT INTO ref_data.product_evidence VALUES(catalog,operation,p->>'code',p->>'documentCode','listing');
  END LOOP;
  IF (SELECT count(*) FROM jsonb_array_elements(payload->'series'))<>(SELECT count(DISTINCT(value->>'provider',value->>'exchangeId',value->>'productId',value->>'contractId')) FROM jsonb_array_elements(payload->'series'))
   OR (SELECT count(*) FROM jsonb_array_elements(payload->'series'))<>(SELECT count(DISTINCT value->>'code') FROM jsonb_array_elements(payload->'series')) THEN RAISE EXCEPTION 'reference_import_identity';END IF;
  FOR s IN SELECT value FROM jsonb_array_elements(payload->'series') LOOP
   SELECT p.* INTO product FROM ref_data.imported_product_keys k JOIN ref_data.futures_products p ON(p.catalog_id,p.revision_id,p.code)=(k.catalog_id,k.revision_id,k.product_code)
    WHERE(k.catalog_id,k.revision_id,k.provider,k.exchange_id,k.product_id,k.product_code)=(catalog,operation,provider_name,s->>'exchangeId',s->>'productId',s->>'productCode');
   IF product.code IS NULL OR s->>'provider' IS DISTINCT FROM provider_name THEN RAISE EXCEPTION 'reference_import_identity';END IF;
   FOREACH key IN ARRAY ARRAY['firstTradingDate','lastTradingDate','firstNoticeDate','lastNoticeDate','firstDeliveryDate','lastDeliveryDate','finalSettlementDate'] LOOP
    native_date=s->>key;IF native_date IS NOT NULL AND native_date !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$' THEN RAISE EXCEPTION 'reference_import_date';END IF;
   END LOOP;
   IF s->>'reportedEnergy' IS NOT NULL THEN
    IF product.commodity_code<>'electricity' OR NOT security.reference_access((s->>'quantityCatalog')::uuid,'ReadMarketData')
     OR NOT security.reference_version_visible((s->>'quantityCatalog')::uuid,(s->>'quantityRevision')::uuid)
     OR NOT EXISTS(SELECT 1 FROM ref_data.quantity_specifications q JOIN ref_data.units u ON(u.catalog_id,u.revision_id,u.code)=(q.catalog_id,q.revision_id,q.unit_code)
      JOIN ref_data.revisions v ON(v.catalog_id,v.id)=(q.catalog_id,q.revision_id) WHERE(q.catalog_id,q.revision_id,q.code)=((s->>'quantityCatalog')::uuid,(s->>'quantityRevision')::uuid,s->>'quantityCode')
      AND q.dimension_code='energy' AND q.commodity_code='electricity' AND u.symbol='MWh' AND v.state='sealed') THEN RAISE EXCEPTION 'reference_import_quantity';END IF;
   END IF;
   SELECT * INTO source FROM ref_data.sources WHERE(catalog_id,revision_id,code)=(catalog,operation,s->>'documentCode');
   IF source.code IS NULL OR(provider_name='CME' AND source.locator !~ ('^https://refdata[.]api[.]cmegroup[.]com/refdata/v3/products/'||(s->>'productId')||'/instruments[?]page=[0-9]+&size=[0-9]+$'))
     OR(provider_name='ICE' AND source.locator<>'https://www.ice.com/api/productguide/spec/'||(s->>'productId')||'/expiry/csv') THEN RAISE EXCEPTION 'reference_import_evidence';END IF;
   INSERT INTO ref_data.futures_series(catalog_id,revision_id,code,product_code,series_label,definition_state) VALUES(catalog,operation,s->>'code',product.code,s->>'label','placeholder');
   INSERT INTO ref_data.imported_series_keys VALUES(catalog,operation,provider_name,s->>'exchangeId',s->>'productId',s->>'contractId',s->>'code',s->>'documentCode',s->>'nativePeriod',
    (s->>'firstTradingDate')::date,(s->>'lastTradingDate')::date,(s->>'firstNoticeDate')::date,(s->>'lastNoticeDate')::date,(s->>'firstDeliveryDate')::date,(s->>'lastDeliveryDate')::date,(s->>'finalSettlementDate')::date,
    (s->>'reportedEnergy')::numeric,(s->>'quantityCatalog')::uuid,(s->>'quantityRevision')::uuid,s->>'quantityCode');
   INSERT INTO ref_data.product_evidence VALUES(catalog,operation,product.code,s->>'documentCode','dates') ON CONFLICT DO NOTHING;
  END LOOP;
  UPDATE ref_data.revisions SET state='sealed' WHERE catalog_id=catalog AND id=operation;
 END IF;
 IF NOT security.reference_access(catalog,'ReadMarketData') OR NOT security.reference_access(catalog,'WriteReferenceData') OR NOT security.reference_access(catalog,'SealReferenceData') THEN RAISE EXCEPTION 'reference_import_denied' USING ERRCODE='42501';END IF;
 IF EXISTS(SELECT 1 FROM ref_data.imported_series_keys k WHERE k.catalog_id=catalog AND k.revision_id=operation AND k.quantity_catalog_id IS NOT NULL
  AND(NOT security.reference_access(k.quantity_catalog_id,'ReadMarketData') OR NOT security.reference_version_visible(k.quantity_catalog_id,k.quantity_revision_id))) THEN RAISE EXCEPTION 'reference_import_denied' USING ERRCODE='42501';END IF;
 RETURN jsonb_build_object('revisionId',operation,'state','inactive','products',(SELECT count(*) FROM ref_data.imported_product_keys WHERE catalog_id=catalog AND revision_id=operation),'series',(SELECT count(*) FROM ref_data.imported_series_keys WHERE catalog_id=catalog AND revision_id=operation));
END$$;
GRANT INSERT ON ref_data.revisions,ref_data.dimensions,ref_data.commodities,ref_data.location_types,ref_data.locations,ref_data.commodity_locations,ref_data.exchanges,ref_data.futures_products,ref_data.futures_series,ref_data.sources,ref_data.product_evidence TO openetrm_security_owner;
GRANT UPDATE(state) ON ref_data.revisions TO openetrm_security_owner;
ALTER FUNCTION security.import_reference_capture(uuid,uuid,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.import_reference_capture(uuid,uuid,jsonb) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.import_reference_capture(uuid,uuid,jsonb) TO openetrm_reference_importer;
COMMENT ON FUNCTION security.import_reference_capture(uuid,uuid,jsonb) IS 'Trusted private managed operator only: source-derived assertions must be replay-validated before calling. No API/runtime execute privilege.';
COMMENT ON TABLE ref_data.imported_series_keys IS 'Exact provider keys map to immutable futures-series PKs. Native source dates/energy are unqualified facts, not UTC deadlines or executable product terms.';
COMMENT ON TABLE ref_data.reference_import_receipts IS 'Current scoped import audit. Sealing freezes storage; this is not certified historical publication, entitlement or financial activation.';
COMMIT;
