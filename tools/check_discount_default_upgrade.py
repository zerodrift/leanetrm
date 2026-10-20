"""Qualify the discount-default key migration over retained synthetic mappings."""

LOOKUP = 'security.swap_discount_default(uuid,uuid,uuid,text,uuid,timestamp with time zone)'
KEY = ('UNIQUE (catalog_id, definition_revision_id, reference_catalog_id, reference_revision_id, '
       'currency_code, effective_at, publication_order)')


def run_checks(query, retained, migration):
    """Apply the migration after loading retained fixtures; every case rolls back."""
    def body(sql):
        return sql.strip().replace('BEGIN;', '', 1).removesuffix('COMMIT;')
    loaded = 'BEGIN;' + ''.join(body(sql) + '\n' for sql in retained)
    upgrade = body(migration)
    state = f"""SELECT jsonb_build_object('defaults',(SELECT jsonb_agg(to_jsonb(d) ORDER BY d.catalog_id,d.id) FROM trading.currency_discount_defaults d),
     'owner',pg_get_userbyid(p.proowner),'grants',p.proacl::text,'definer',p.prosecdef,'settings',p.proconfig) FROM pg_proc p WHERE p.oid='{LOOKUP}'::regprocedure"""
    preserved = f"""DO $$BEGIN
     IF (SELECT value FROM retained_state) IS DISTINCT FROM ({state}) OR (SELECT pg_get_constraintdef(oid) FROM pg_constraint
      WHERE conrelid='trading.currency_discount_defaults'::regclass AND conname='currency_discount_default_key') IS DISTINCT FROM '{KEY}'
     THEN RAISE EXCEPTION 'Retained discount defaults, lookup security or exact key changed';END IF;
    END$$;"""
    query(loaded + f'CREATE TEMP TABLE retained_state AS SELECT ({state}) AS value;' + upgrade + preserved + 'ROLLBACK;')
    print('PASS discount-default upgrade preserves retained mappings and lookup security and installs the exact key', flush=True)
    # A retained duplicate of one mapping under a new identity has the same key, instant and publication.
    tie = """INSERT INTO trading.currency_discount_defaults SELECT catalog_id,gen_random_uuid(),definition_revision_id,reference_catalog_id,
     reference_revision_id,currency_code,curve_code,interpolation_policy,publication_order,effective_at
     FROM trading.currency_discount_defaults ORDER BY catalog_id,id LIMIT 1;"""
    try:
        query(loaded + tie + upgrade + 'ROLLBACK;')
    except RuntimeError as error:
        if 'could not create unique index "currency_discount_default_key"' not in str(error):
            raise
    else:
        raise AssertionError('Retained tied discount defaults were silently admitted')
    print('PASS discount-default upgrade stops on retained tied mappings without merging or deleting them', flush=True)
    # A locally edited lookup body is never silently replaced.
    drift = f"DO $$BEGIN EXECUTE replace(pg_get_functiondef('{LOOKUP}'::regprocedure),'LIMIT 1','LIMIT 2');END$$;"
    try:
        query('BEGIN;' + drift + upgrade + 'ROLLBACK;')
    except RuntimeError as error:
        if 'unambiguous_discount_defaults: unexpected function' not in str(error):
            raise
    else:
        raise AssertionError('An unreviewed discount-default lookup was accepted')
    print('PASS discount-default upgrade refuses an unreviewed lookup definition', flush=True)
