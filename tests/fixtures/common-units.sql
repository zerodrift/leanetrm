-- Project-authored definitions for the unit-conversion consumer. Btu/therm/Dth
-- explicitly use the International Table convention. Gas volume has no assumed
-- temperature, pressure or heating value. See docs/reference-data.md for sources.
INSERT INTO ref_data.dimensions VALUES
 ('10000000-0000-0000-0000-000000000001','20000000-0000-0000-0000-000000000001','mass',1,0,0,0,0,0,0);
INSERT INTO ref_data.units(catalog_id,revision_id,code,symbol,dimension_code)
 SELECT '10000000-0000-0000-0000-000000000001','20000000-0000-0000-0000-000000000001',code,symbol,dimension FROM(VALUES
 ('btu_it','Btu (IT)','energy'),('therm_it','therm (IT)','energy'),('dekatherm_it','Dth (IT)','energy'),
 ('joule','J','energy'),('megajoule','MJ','energy'),('gigajoule','GJ','energy'),('kilowatt_hour','kWh','energy'),
 ('kilowatt','kW','power'),('gigawatt','GW','power'),
 ('cubic_metre','m³','volume'),('litre','L','volume'),('cubic_foot','ft³','volume'),('mcf','Mcf','volume'),('mmcf','MMcf','volume'),
 ('us_gallon','US gal','volume'),('petroleum_barrel','bbl (42 US gal)','volume'),
 ('kilogram','kg','mass'),('metric_tonne','t','mass'),('pound','lb','mass'),('short_ton','short ton','mass'),('troy_ounce','oz t','mass')
 )u(code,symbol,dimension);
INSERT INTO ref_data.unit_conversions
 SELECT '10000000-0000-0000-0000-000000000001','20000000-0000-0000-0000-000000000001',a,b,dimension,n,d FROM(VALUES
 ('btu_it','mmbtu_it','energy',1::numeric,1000000::numeric),('therm_it','mmbtu_it','energy',1,10),('dekatherm_it','mmbtu_it','energy',1,1),
 ('mmbtu_it','gigajoule','energy',105505585262,100000000000),('joule','gigajoule','energy',1,1000000000),
 ('megajoule','gigajoule','energy',1,1000),('megawatt_hour','gigajoule','energy',18,5),('kilowatt_hour','megawatt_hour','energy',1,1000),
 ('kilowatt','megawatt','power',1,1000),('gigawatt','megawatt','power',1000,1),
 ('litre','cubic_metre','volume',1,1000),('cubic_foot','cubic_metre','volume',55306341,1953125000),
 ('mcf','cubic_foot','volume',1000,1),('mmcf','cubic_foot','volume',1000000,1),
 ('us_gallon','cubic_metre','volume',473176473,125000000000),('petroleum_barrel','us_gallon','volume',42,1),
 ('metric_tonne','kilogram','mass',1000,1),('pound','kilogram','mass',45359237,100000000),
 ('short_ton','pound','mass',2000,1),('troy_ounce','kilogram','mass',311034768,10000000000)
 )v(a,b,dimension,n,d);
-- Built from named fields: this file also loads on schemas that precede the icon column, which ignore it.
INSERT INTO ref_data.commodities SELECT (jsonb_populate_record(NULL::ref_data.commodities,jsonb_build_object(
 'catalog_id','10000000-0000-0000-0000-000000000001','revision_id','20000000-0000-0000-0000-000000000001',
 'code',v.code,'name',v.name,'quantity_dimension_code',v.dimension,'icon_code','cube'))).*
 FROM (VALUES ('test_liquid','Synthetic liquid','volume'),('test_bulk','Synthetic bulk material','mass'))v(code,name,dimension);
INSERT INTO ref_data.measurement_bases VALUES
 ('10000000-0000-0000-0000-000000000001','20000000-0000-0000-0000-000000000001','test_liquid_volume','test_liquid','volume'),
 ('10000000-0000-0000-0000-000000000001','20000000-0000-0000-0000-000000000001','test_bulk_mass','test_bulk','mass'),
 ('10000000-0000-0000-0000-000000000001','20000000-0000-0000-0000-000000000001','test_power_flow','test_power','power');
INSERT INTO ref_data.quantity_specifications
 SELECT u.catalog_id,u.revision_id,b.commodity_code||'_'||u.code,b.commodity_code,b.code,u.code,u.dimension_code
 FROM ref_data.units u JOIN ref_data.measurement_bases b ON(b.catalog_id,b.revision_id,b.dimension_code)=(u.catalog_id,u.revision_id,u.dimension_code)
 WHERE u.catalog_id='10000000-0000-0000-0000-000000000001' AND u.revision_id='20000000-0000-0000-0000-000000000001'
 AND b.code IN('test_gas_energy','test_power_energy','test_gas_volume','test_liquid_volume','test_bulk_mass','test_power_flow')
 AND u.code NOT IN('energy_b','power_a','volume_a')
 AND NOT EXISTS(SELECT 1 FROM ref_data.quantity_specifications q WHERE(q.catalog_id,q.revision_id,q.commodity_code,q.basis_code,q.unit_code,q.dimension_code)=(u.catalog_id,u.revision_id,b.commodity_code,b.code,u.code,u.dimension_code));
