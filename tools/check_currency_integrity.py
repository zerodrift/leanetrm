"""Currency relationships qualified in an owned, synthetic regional database."""
from uuid import uuid4


def run_checks(query):
    def rejects(change):
        # Each attempt is rolled back, including an unexpectedly accepted insert.
        query("""BEGIN;DO $check$ BEGIN
          BEGIN
            INSERT INTO trading.currency_discount_defaults
             SELECT (jsonb_populate_record(NULL::trading.currency_discount_defaults,to_jsonb(d)||
              jsonb_build_object('id','"""+str(uuid4())+"""','effective_at','2021-01-01T00:00:00Z')||"""+change+""" )).*
             FROM trading.currency_discount_defaults d WHERE currency_code='gbp' LIMIT 1;
            IF NOT FOUND THEN RAISE EXCEPTION 'Missing synthetic GBP mapping';END IF;
            RAISE EXCEPTION 'Incompatible currency default was accepted';
          EXCEPTION WHEN foreign_key_violation THEN NULL;END;
        END $check$;ROLLBACK;""")
    rejects("jsonb_build_object('currency_code','eur')")
    rejects("jsonb_build_object('reference_revision_id',(SELECT reference_revision_id FROM trading.currency_discount_defaults WHERE currency_code='usd' LIMIT 1))")
    print('PASS currency integrity: direct inserts cannot pair a discount curve with another currency or reference revision',flush=True)

    # Rehearse the additive populated-upgrade constraint, including atomic failure.
    before=query('SELECT count(*) FROM trading.currency_discount_defaults;')
    fk="ALTER TABLE trading.currency_discount_defaults ADD CONSTRAINT currency_discount_curve_currency FOREIGN KEY(catalog_id,definition_revision_id,curve_code,reference_catalog_id,reference_revision_id,currency_code) REFERENCES market_data.rate_curves(catalog_id,revision_id,code,reference_catalog_id,reference_revision_id,currency_code);"
    query('BEGIN;ALTER TABLE trading.currency_discount_defaults DROP CONSTRAINT currency_discount_curve_currency;'+fk+'ROLLBACK;')
    try:
        query("BEGIN;ALTER TABLE trading.currency_discount_defaults DROP CONSTRAINT currency_discount_curve_currency;INSERT INTO trading.currency_discount_defaults SELECT(jsonb_populate_record(NULL::trading.currency_discount_defaults,to_jsonb(d)||jsonb_build_object('id',gen_random_uuid(),'currency_code','eur','effective_at','2021-01-01T00:00:00Z'))).* FROM trading.currency_discount_defaults d WHERE currency_code='gbp';"+fk)
    except RuntimeError as error:assert 'violates foreign key constraint' in str(error),str(error)
    else:raise AssertionError('The populated upgrade inferred a repair for an invalid mapping')
    assert query('SELECT count(*) FROM trading.currency_discount_defaults;')==before
    assert query("SELECT count(*) FROM pg_constraint WHERE conname='currency_discount_curve_currency';")=='1'
    print('PASS currency upgrade: valid populated mappings retain their rows; conflicting configuration refuses with the predecessor state intact',flush=True)
