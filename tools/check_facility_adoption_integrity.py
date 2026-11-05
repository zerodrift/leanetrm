"""Independent storage guard check: a plan header cannot omit accepted-target revisions."""
from check_booked_side_programs import runtime_query


def check(query, manifest, ledger, plan):
    catalog=manifest['catalogId']
    query("""CREATE FUNCTION security.synthetic_incomplete_adoption(cat uuid,ledger uuid,plan uuid) RETURNS void
      LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $test$
      DECLARE prior trading.facility_quantity_adoptions;publication integer;actor uuid;BEGIN
       PERFORM 1 FROM security.epoch WHERE id=true FOR SHARE;PERFORM pg_advisory_xact_lock(730849102);
       SELECT actor_id INTO STRICT actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
       SELECT * INTO STRICT prior FROM trading.facility_quantity_adoptions WHERE(catalog_id,ledger_id)=(cat,ledger) ORDER BY revision DESC LIMIT 1;
       SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=cat;
       INSERT INTO trading.publications VALUES(cat,publication,pg_current_xact_id());
       INSERT INTO trading.facility_quantity_adoptions VALUES(cat,ledger,gen_random_uuid(),prior.revision+1,prior.id,plan,'adopted','Synthetic incomplete writer',actor,publication,'2021-01-15T08:00:00Z');
       SET CONSTRAINTS trading.valid_facility_adoption IMMEDIATE;
      END$test$;
      ALTER FUNCTION security.synthetic_incomplete_adoption(uuid,uuid,uuid) OWNER TO openetrm_security_owner;
      REVOKE ALL ON FUNCTION security.synthetic_incomplete_adoption(uuid,uuid,uuid) FROM PUBLIC;
      GRANT EXECUTE ON FUNCTION security.synthetic_incomplete_adoption(uuid,uuid,uuid) TO openetrm_runtime;""")
    before=query(f"SELECT count(*) FROM trading.facility_quantity_adoptions WHERE catalog_id='{catalog}';")
    try:
        runtime_query(query,manifest,f"SELECT security.capture_date_context('{catalog}','2021-01-15',NULL);SELECT security.synthetic_incomplete_adoption('{catalog}','{ledger}','{plan}');")
    except RuntimeError as error:
        assert 'facility_quantity_target' in str(error), str(error)
    else:
        raise AssertionError('A definer header without the complete resulting accepted set was admitted')
    assert query(f"SELECT count(*) FROM trading.facility_quantity_adoptions WHERE catalog_id='{catalog}';")==before
    print('PASS final allocation integrity: an independent incomplete writer cannot reuse incompatible old accepted targets',flush=True)
