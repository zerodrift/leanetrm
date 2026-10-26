-- Read-only inventory; child tables can be used without any inbound FK.
BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY;
SET LOCAL statement_timeout='15s';
SELECT 'SELECT * FROM ('||string_agg(format(
 'SELECT %L AS table_name,count(*) AS rows,%s::integer AS inbound_fks,%s::integer AS outbound_fks,row_security_active(%s::oid) AS scoped_by_rls FROM %s',
 c.oid::regclass::text,
 (SELECT count(*) FROM pg_constraint k WHERE k.contype='f' AND k.confrelid=c.oid),
 (SELECT count(*) FROM pg_constraint k WHERE k.contype='f' AND k.conrelid=c.oid),c.oid,c.oid::regclass),
 ' UNION ALL ' ORDER BY c.relname)||')inventory ORDER BY table_name'
FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='ref_data' AND c.relkind='r'
\gexec
SELECT conrelid::regclass AS child_table,confrelid::regclass AS parent_table,conname AS constraint_name,
 convalidated AS validated,pg_get_constraintdef(oid) AS definition
FROM pg_constraint WHERE contype='f' AND (connamespace='ref_data'::regnamespace OR confrelid IN(
 SELECT c.oid FROM pg_class c WHERE c.relnamespace='ref_data'::regnamespace)) ORDER BY 1,2,3;
ROLLBACK;
