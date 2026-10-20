-- Match retained unit symbols to the amount-label contract. Never truncate data.
-- Stop runtime admission and drain calls before this schema upgrade.
BEGIN;
CREATE FUNCTION ref_data.unit_symbol_supported(symbol text) RETURNS boolean
LANGUAGE sql IMMUTABLE PARALLEL SAFE SET search_path=pg_catalog AS $$
 SELECT coalesce(length(symbol) BETWEEN 1 AND 80
  AND symbol !~ U&'[\0001-\001f\007f-\009f]'
  AND translate(symbol,U&'\0009\000a\000b\000c\000d\0020\0085\00a0\1680\2000\2001\2002\2003\2004\2005\2006\2007\2008\2009\200a\2028\2029\202f\205f\3000','')<>'',false);
$$;
ALTER FUNCTION ref_data.unit_symbol_supported(text) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION ref_data.unit_symbol_supported(text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION ref_data.unit_symbol_supported(text) TO openetrm_runtime,openetrm_security_owner;
ALTER TABLE ref_data.units ADD CONSTRAINT units_amount_symbol_check CHECK(ref_data.unit_symbol_supported(symbol)) NOT VALID;
-- Invalid old symbols require explicit review. Validation failure rolls back the
-- constraint and descriptor change without rewriting any retained reference row.
ALTER TABLE ref_data.units VALIDATE CONSTRAINT units_amount_symbol_check;
CREATE OR REPLACE FUNCTION security.reference_display_editor_fields(table_name text) RETURNS jsonb
LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT coalesce(jsonb_agg(jsonb_build_object('key',field_key,'label',field_label,'kind',field_kind,'nullable',optional,'maxLength',maximum) ORDER BY position),'[]'::jsonb)
 FROM(VALUES
  ('currencies',1,'name','Name','text',false,300),('commodities',1,'name','Name','text',false,300),
  ('locations',1,'name','Name','text',false,300),('locations',2,'latitude','Latitude','decimal',true,48),('locations',3,'longitude','Longitude','decimal',true,48),
  ('location_types',1,'description','Description','text',false,300),('units',1,'symbol','Display symbol','text',false,80),
  ('exchanges',1,'name','Name','text',false,300),('clearing_houses',1,'name','Name','text',false,300),
  ('futures_products',1,'name','Name','text',false,300),('futures_series',1,'series_label','Contract label','text',false,300)
 )d(category,position,field_key,field_label,field_kind,optional,maximum) WHERE category=table_name;
$$;
COMMIT;
