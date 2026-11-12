-- Admit a trade's exact TOU from the same committed graph proof as its curve.
-- Keep the explicit PPA knowledge bound independent of the request's read pin.
BEGIN;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);
DO $guards$
DECLARE dependency record;expected aclitem[];
BEGIN
 FOR dependency IN SELECT * FROM(VALUES
  ('security.power_interval_products(uuid,uuid)','8284acdef600ab5b97033dd2e85098045dc3d4d6c176731f499db47acc789c72',false),
  ('security.ppa_tou_versions(uuid,uuid,uuid,uuid,uuid,timestamptz,jsonb)','e1fb7ecee6308bd2dd138cda65c4cda6ed6794b2dbe65a3fe7046e4c537c5f72',false),
  ('security.ppa_tou_versions_pre_portfolio_worker(uuid,uuid,uuid,uuid,uuid,timestamptz,jsonb)','98bd1290427662100830e19cc85e1e5d54964517b5ae191c13787222a164c7dd',true)
 ) d(signature,digest,private) LOOP
  expected=ARRAY['openetrm_security_owner=X/openetrm_security_owner']::aclitem[];
  IF NOT dependency.private THEN expected=expected||ARRAY['openetrm_runtime=X/openetrm_security_owner','openetrm_risk_worker=X/openetrm_security_owner']::aclitem[];END IF;
  IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid=to_regprocedure(dependency.signature)
   AND pg_get_userbyid(p.proowner)='openetrm_security_owner' AND p.proacl @> expected AND p.proacl <@ expected
   AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')=dependency.digest)
  THEN RAISE EXCEPTION 'power_product_admission_upgrade: changed dependency %',dependency.signature;END IF;
 END LOOP;
END$guards$;

CREATE FUNCTION security.power_interval_products(catalog uuid,definition uuid,known_before timestamptz)
RETURNS SETOF jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF NOT security.custom_curve_source_visible(catalog,definition) THEN RAISE EXCEPTION 'power_interval_denied' USING ERRCODE='42501';END IF;
 RETURN QUERY SELECT to_jsonb(v)||jsonb_build_object('code',p.code)
 FROM market_data.tou_product_versions v JOIN market_data.tou_products p ON(p.catalog_id,p.id)=(v.catalog_id,v.product_id)
 JOIN market_data.revisions r ON(r.catalog_id,r.id)=(catalog,definition)
 WHERE v.catalog_id=catalog AND v.state='published' AND(v.clock_catalog_id,v.clock_revision_id)=(r.reference_catalog_id,r.reference_revision_id) AND(
  EXISTS(SELECT 1 FROM market_data.curve_blocks b WHERE(b.catalog_id,b.revision_id,b.tou_product_id,b.tou_version)=(catalog,definition,v.product_id,v.version)) OR
  EXISTS(SELECT 1 FROM market_data.custom_curve_node_power_intervals n JOIN market_data.custom_curve_versions g USING(catalog_id,curve_id,version)
   WHERE(n.product_catalog_id,n.product_id,n.product_version)=(catalog,v.product_id,v.version) AND g.state='published'
    AND security.custom_curve_visible(n.catalog_id,n.curve_id,n.version) AND security.custom_curve_uses_revision(n.catalog_id,n.curve_id,n.version,definition)
    AND(known_before IS NULL OR EXISTS(SELECT 1 FROM market_data.custom_curve_knowledge_receipts k
     WHERE(k.catalog_id,k.publication_order,k.publication_xid)=(g.catalog_id,g.publication_order,g.publication_xid) AND k.known_from<=known_before))));
 IF NOT security.custom_curve_source_visible(catalog,definition) THEN RAISE EXCEPTION 'power_interval_denied' USING ERRCODE='42501';END IF;
END$$;
ALTER FUNCTION security.power_interval_products(uuid,uuid,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.power_interval_products(uuid,uuid,timestamptz) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;

-- This established entry point keeps its request-context admission contract.
-- Only the private PPA reader supplies an additional explicit knowledge bound.
CREATE OR REPLACE FUNCTION security.power_interval_products(catalog uuid,definition uuid)
RETURNS SETOF jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 RETURN QUERY SELECT * FROM security.power_interval_products(catalog,definition,NULL::timestamptz);
END$$;

DO $upgrade$
DECLARE body text;needle text:=$old$ SELECT coalesce(array_agg(to_jsonb(v)||jsonb_build_object('code',p.code) ORDER BY p.code,v.version),ARRAY[]::jsonb[]) INTO result
 FROM market_data.tou_product_versions v JOIN market_data.tou_products p ON(p.catalog_id,p.id)=(v.catalog_id,v.product_id)
 WHERE v.catalog_id=catalog AND v.state='published' AND(v.clock_catalog_id,v.clock_revision_id)=(ref_catalog,ref_revision)
  AND EXISTS(SELECT 1 FROM market_data.curve_blocks b WHERE(b.catalog_id,b.revision_id,b.tou_product_id,b.tou_version)=(catalog,definition,v.product_id,v.version));$old$;
BEGIN
 body=pg_get_functiondef('security.ppa_tou_versions_pre_portfolio_worker(uuid,uuid,uuid,uuid,uuid,timestamptz,jsonb)'::regprocedure);
 IF strpos(body,needle)=0 OR length(body)-length(replace(body,needle,''))<>length(needle)
 THEN RAISE EXCEPTION 'power_product_admission_upgrade';END IF;
 body=replace(body,needle,$new$ SELECT coalesce(array_agg(product ORDER BY product->>'code',(product->>'version')::integer),ARRAY[]::jsonb[]) INTO result
 FROM security.power_interval_products(catalog,definition,
  (SELECT t.known_at FROM market_data.temporal_contexts t WHERE(t.catalog_id,t.id)=(catalog,context))) product;$new$);
 -- Preserve the public worker fence, private-reader ACL and both market guards.
 EXECUTE body;
END$upgrade$;
COMMIT;
