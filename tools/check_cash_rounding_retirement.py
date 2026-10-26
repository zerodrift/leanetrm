"""Qualify guarded retirement only in the launcher's owned stock database."""

def run_checks(query,migration):
    def reject(prefix,code):
        try:query('\\set VERBOSITY verbose\n'+prefix+migration)
        except RuntimeError as error:
            assert code in str(error),str(error)
        else:raise AssertionError('Unsafe retirement succeeded')
        assert query("SELECT to_regclass('ref_data.cash_rounding_conventions') IS NOT NULL;")=='t'
    prefix="""BEGIN;
      INSERT INTO ref_data.catalogs VALUES('aa000000-0000-0000-0000-000000000001','rounding_retirement','Synthetic');
      INSERT INTO ref_data.revisions(catalog_id,id,revision_key) VALUES('aa000000-0000-0000-0000-000000000001','aa000000-0000-0000-0000-000000000002','synthetic');
      INSERT INTO ref_data.currencies VALUES('aa000000-0000-0000-0000-000000000001','aa000000-0000-0000-0000-000000000002','usd','USD','Synthetic dollar',2);
      INSERT INTO ref_data.cash_rounding_conventions VALUES('aa000000-0000-0000-0000-000000000001','aa000000-0000-0000-0000-000000000002','cash','usd',0.01,'to_even','payment_total');
    """
    reject(prefix,'55000')
    filtered=prefix+"CREATE ROLE rounding_filtered NOLOGIN NOBYPASSRLS; GRANT USAGE,CREATE ON SCHEMA ref_data TO rounding_filtered; ALTER TABLE ref_data.cash_rounding_conventions OWNER TO rounding_filtered; SET SESSION AUTHORIZATION rounding_filtered;"
    reject(filtered,'42501')
    reject('BEGIN ISOLATION LEVEL REPEATABLE READ;','25000')
    reject('BEGIN;CREATE TABLE public.rounding_dependency(catalog_id uuid,revision_id uuid,code text,FOREIGN KEY(catalog_id,revision_id,code) REFERENCES ref_data.cash_rounding_conventions);','2BP01')
    print('PASS disconnected cash reference retirement: retained rows, stale snapshots and unexpected dependencies refuse atomically',flush=True)
