-- Run once as the actual migration creator, before any application schema.
-- Keep object-owner ACLs explicit across stock PostgreSQL versions. Maintenance
-- privileges beyond these baseline rights require an explicit operator grant.
DO $$BEGIN
 IF EXISTS(SELECT 1 FROM pg_namespace WHERE nspname IN('ref_data','market_data','trading','security','automation','risk','assistant'))
 THEN RAISE EXCEPTION 'bootstrap_requires_empty_database' USING ERRCODE='55000';END IF;
END$$;
ALTER DEFAULT PRIVILEGES REVOKE ALL ON TABLES FROM CURRENT_USER;
ALTER DEFAULT PRIVILEGES GRANT SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER ON TABLES TO CURRENT_USER;
