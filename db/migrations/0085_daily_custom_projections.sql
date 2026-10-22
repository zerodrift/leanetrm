-- Exact delivery-interval mapping is separate from a monthly scalar output.
-- The original reset contracts, fixing rules and averaging weights are retained.
BEGIN;
SELECT pg_advisory_xact_lock(730849102);
ALTER TABLE trading.custom_curve_projection_bindings ALTER COLUMN position DROP NOT NULL;
ALTER TABLE trading.custom_curve_projection_bindings DROP CONSTRAINT custom_curve_projection_bindings_selection_check;
ALTER TABLE trading.custom_curve_projection_bindings ADD CONSTRAINT custom_projection_coordinate
 CHECK((selection='single_output_position_v1' AND position IS NOT NULL AND position>=0)
  OR(selection='exact_delivery_interval_v1' AND position IS NULL));
DO $$DECLARE key_name text;BEGIN
 SELECT c.conname INTO STRICT key_name FROM pg_constraint c WHERE c.conrelid='trading.custom_curve_projection_bindings'::regclass
  AND c.contype='u' AND(SELECT array_agg(a.attname ORDER BY k.ordinality) FROM unnest(c.conkey) WITH ORDINALITY k(attnum,ordinality)
   JOIN pg_attribute a ON(a.attrelid,a.attnum)=(c.conrelid,k.attnum))=ARRAY['catalog_id','curve_id','version','output_node','position','selection','implementation']::name[];
 EXECUTE format('ALTER TABLE trading.custom_curve_projection_bindings DROP CONSTRAINT %I',key_name);
END$$;
ALTER TABLE trading.custom_curve_projection_bindings ADD CONSTRAINT custom_projection_exact_key
 UNIQUE NULLS NOT DISTINCT(catalog_id,curve_id,version,output_node,position,selection,implementation);
ALTER TABLE risk.job_custom_curves DROP CONSTRAINT job_custom_curves_pkey;
ALTER TABLE risk.job_custom_curves ALTER COLUMN position DROP NOT NULL;
ALTER TABLE risk.job_custom_curves DROP CONSTRAINT job_custom_curves_selection_check;
ALTER TABLE risk.job_custom_curves ADD CONSTRAINT custom_projection_coordinate
 CHECK((selection='single_output_position_v1' AND position IS NOT NULL AND position>=0)
  OR(selection='exact_delivery_interval_v1' AND position IS NULL));
ALTER TABLE risk.job_custom_curves ADD CONSTRAINT job_custom_curve_coordinate_key
 UNIQUE NULLS NOT DISTINCT(job_id,revision_id,curve_id,version,output_node,position,selection);
DO $$DECLARE key_name text;definition text;BEGIN
 SELECT conname,pg_get_constraintdef(oid) INTO STRICT key_name,definition FROM pg_constraint
  WHERE conrelid='trading.swap_floating_sides'::regclass AND contype='c' AND pg_get_constraintdef(oid) LIKE '%custom_projection_id%';
 IF strpos(definition,'monthly_index_price')=0 OR strpos(definition,'index_observation_fixings_v1')=0 THEN RAISE EXCEPTION 'Unexpected custom projection constraint';END IF;
 EXECUTE format('ALTER TABLE trading.swap_floating_sides DROP CONSTRAINT %I',key_name);
END$$;
ALTER TABLE trading.swap_floating_sides ADD CONSTRAINT indexed_custom_projection
 CHECK(custom_projection_id IS NULL OR(averaging IN('monthly_index_price','calendar_day_arithmetic') AND implementation='index_observation_fixings_v1'));

CREATE OR REPLACE FUNCTION security.validate_custom_projection(catalog uuid,projection jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE curve uuid;requested_version integer;node text;ordinal integer;selection text;
BEGIN
 IF projection IS NULL THEN RETURN NULL;END IF;
 IF jsonb_typeof(projection) IS DISTINCT FROM 'object' OR NOT projection ?& ARRAY['catalogId','curveId','version','outputNode','position','selection','implementation']
  OR(SELECT count(*) FROM jsonb_object_keys(projection))<>7 OR(projection->>'catalogId')::uuid IS DISTINCT FROM catalog
  OR projection->>'implementation' IS DISTINCT FROM 'published_custom_curve_projection_v1'
 THEN RAISE EXCEPTION 'custom_projection';END IF;
 selection=projection->>'selection';ordinal=(projection->>'position')::integer;
 IF NOT((selection='single_output_position_v1' AND jsonb_typeof(projection->'position')='number' AND ordinal>=0)
  OR(selection='exact_delivery_interval_v1' AND projection->'position'='null'::jsonb)) OR selection IS NULL
 THEN RAISE EXCEPTION 'custom_projection';END IF;
 curve=(projection->>'curveId')::uuid;requested_version=(projection->>'version')::integer;node=projection->>'outputNode';
 IF NOT security.custom_curve_visible(catalog,curve,requested_version)
  OR NOT EXISTS(SELECT 1 FROM market_data.custom_curve_versions v WHERE(v.catalog_id,v.curve_id,v.version,v.output_node)=(catalog,curve,requested_version,node) AND v.state='published' AND v.engine_version='custom_curve_recipe_v2')
  OR NOT EXISTS(SELECT 1 FROM security.custom_curve_native_sources(catalog,curve,requested_version))
 THEN RAISE EXCEPTION 'custom_projection';END IF;
 RETURN jsonb_build_object('catalogId',catalog,'curveId',curve,'version',requested_version,'outputNode',node,'position',ordinal,
  'selection',selection,'implementation','published_custom_curve_projection_v1');
EXCEPTION WHEN invalid_text_representation OR numeric_value_out_of_range THEN RAISE EXCEPTION 'custom_projection';
END$$;
CREATE OR REPLACE FUNCTION security.retain_custom_projection(catalog uuid,projection jsonb,publication integer,effective timestamptz) RETURNS uuid
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE retained uuid;
BEGIN
 projection=security.validate_custom_projection(catalog,projection);
 IF projection IS NULL THEN RETURN NULL;END IF;
 SELECT p.id INTO retained FROM trading.custom_curve_projection_bindings p WHERE p.catalog_id=catalog AND
  (p.curve_id,p.version,p.output_node,p.position,p.selection,p.implementation) IS NOT DISTINCT FROM
  ((projection->>'curveId')::uuid,(projection->>'version')::integer,projection->>'outputNode',(projection->>'position')::integer,projection->>'selection',projection->>'implementation');
 IF retained IS NULL THEN
  retained=gen_random_uuid();INSERT INTO trading.custom_curve_projection_bindings VALUES(catalog,retained,(projection->>'curveId')::uuid,
   (projection->>'version')::integer,projection->>'outputNode',(projection->>'position')::integer,projection->>'selection',projection->>'implementation',publication,effective);
 END IF;
 RETURN retained;
END$$;
DO $$DECLARE signature text;definition text;before text;after text;BEGIN
 signature='security.save_projected_swap_with_parties(uuid,uuid,uuid,integer,uuid,uuid,uuid,uuid,text,text,text,text,date,date,timestamp with time zone,date,date,timestamp with time zone,text,numeric,numeric,numeric,boolean,timestamp with time zone,timestamp with time zone,timestamp with time zone,text,text,jsonb,jsonb,text,uuid,integer,text,jsonb,uuid[],jsonb)';
 definition=pg_get_functiondef(signature::regprocedure);
 before='IF custom_projection IS NOT NULL AND(averaging<>''monthly_index_price'' OR EXISTS(';
 after='IF custom_projection IS NOT NULL AND((averaging=''monthly_index_price'' AND custom_projection->>''selection''<>''single_output_position_v1'') OR(averaging=''calendar_day_arithmetic'' AND custom_projection->>''selection''<>''exact_delivery_interval_v1'') OR EXISTS(';
 IF strpos(definition,before)=0 THEN RAISE EXCEPTION 'Unexpected projected swap command';END IF;
 EXECUTE replace(definition,before,after);
END$$;
COMMIT;
