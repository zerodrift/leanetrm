"""Raw business operations must obey current reference scope after catalog revocation."""
import json
from uuid import uuid4
from preview_fixture import trade_booking
from swap_fixture import REVISION


def run_checks(query,manifest):
    catalog,ref,revision,tenant,workspace=(manifest[k] for k in ('catalogId','referenceCatalogId','referenceRevisionId','tenantId','workspaceId'))
    contract=query(f"SELECT id FROM market_data.contracts WHERE catalog_id='{catalog}' AND revision_id='{manifest['revisionId']}' AND kind='future' ORDER BY delivery_from LIMIT 1;")
    book='d1000000-0000-0000-0000-000000000001'
    booking=json.dumps(trade_booking()).replace("'","''")
    index=query(f"SELECT index_a_code FROM ref_data.settlement_targets WHERE catalog_id='{ref}' AND revision_id='{revision}' AND code='test_index_target';")
    def issue():return query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
    def runtime(sql):return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{issue()}');{sql}COMMIT;")
    future_op=str(uuid4());swap_op=str(uuid4())
    def future(operation,trade=None):
        common=f"'{catalog}','{operation}'"
        terms=f"'{book}','{manifest['revisionId']}','future','{contract}','2020-12-18T16:00:00Z'"
        if trade is None:return f"security.book_futures_with_parties({common},{terms},NULL,1,3,'fixed_total_cash_execution_pnl_v1',NULL,NULL,NULL,'{booking}'::jsonb)"
        return f"security.amend_futures_with_parties({common},'{trade}',1,{terms},'2020-12-18T16:00:00Z',NULL,2,3,'Synthetic correction','fixed_total_cash_execution_pnl_v1','unprocessed_futures_booking_amendment_v1',NULL,NULL,NULL,'{booking}'::jsonb)"
    def swap(operation,trade=None):
        def rule(days):return json.dumps(dict(frequency='monthly',rule='days_after_period_end',days=days,dayOfMonth=None,dayBasis='calendar_days',shortMonth=None,adjustment='unadjusted',calendar=None,implementation='monthly_payment_date_rules_v1'))
        identity='NULL,NULL' if trade is None else f"'{trade}',1"
        return f"""security.save_indexed_swap_with_parties('{catalog}','{operation}',{identity},'{book}','{REVISION}','{ref}','{revision}',
          'daily_gas','{index}','usd','usd','2021-01-01','2021-02-01','2021-02-18T00:00:00Z','2021-01-01','2021-02-01',
          '2021-03-18T00:00:00Z','calendar_day_arithmetic',3,{100 if trade is None else 200},-1,false,'2020-12-18T16:00:00Z',NULL,'2020-12-18T16:00:00Z',
          'Synthetic booking','independent_side_financial_swap_v2','{rule(18)}'::jsonb,'{rule(46)}'::jsonb,'test_gas',NULL,NULL,NULL,'{booking}'::jsonb,'{{}}'::uuid[])"""
    original_future=json.loads(runtime('SELECT '+future(future_op)+';'))
    original_swap=json.loads(runtime('SELECT '+swap(swap_op)+';'))
    default=f"security.swap_discount_default('{catalog}','{ref}','{revision}','usd','{REVISION}','2020-12-18T16:00:00Z')"
    assert json.loads(runtime('SELECT '+default+';'))['reference_catalog_id']==ref
    context=runtime(f"SELECT security.capture_date_context('{catalog}','2020-12-18',NULL);")
    later=str(uuid4())
    query(f"""BEGIN;INSERT INTO market_data.revisions VALUES('{catalog}','{later}','{ref}','{revision}','draft',0);
      INSERT INTO market_data.revision_periods VALUES('{catalog}','{later}','2020-01-01','2022-01-01');
      UPDATE market_data.revisions SET state='sealed' WHERE catalog_id='{catalog}' AND id='{later}';COMMIT;""")
    hidden_default=default.replace(REVISION,later)
    assert runtime('SELECT '+hidden_default+';')==''
    runtime(f"""SELECT security.use_market_context('{catalog}','{context}');SELECT security.use_trading_context('{catalog}','2020-12-18T16:00:00Z');
      DO $$ BEGIN PERFORM {hidden_default};RAISE EXCEPTION 'Expected historical definition rejection';
      EXCEPTION WHEN insufficient_privilege THEN NULL;END $$;""")
    statements=[future(str(uuid4())),future(str(uuid4()),original_future['tradeId']),swap(str(uuid4())),swap(str(uuid4()),original_swap['tradeId']),default]
    for statement in statements:
        before=query(f"SELECT count(*) FROM trading.publications WHERE catalog_id='{catalog}';")
        query(f"""BEGIN;DELETE FROM security.reference_catalog_scopes WHERE catalog_id='{ref}' AND tenant_id='{tenant}' AND workspace_id='{workspace}';
          SET SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{issue()}');
          DO $$ BEGIN
           PERFORM {statement};RAISE EXCEPTION 'Expected revoked reference rejection';
          EXCEPTION WHEN insufficient_privilege THEN NULL;END $$;ROLLBACK;""")
        assert before==query(f"SELECT count(*) FROM trading.publications WHERE catalog_id='{catalog}';")
    # Revocation must not prevent a currently authorized actor from reconciling
    # exact accepted requests: those operations return retained control IDs only.
    query(f"""BEGIN;DELETE FROM security.reference_catalog_scopes WHERE catalog_id='{ref}' AND tenant_id='{tenant}' AND workspace_id='{workspace}';
      SET SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{issue()}');
      SELECT {future(future_op)};SELECT {swap(swap_op)};
      SELECT security.futures_booking_status('{catalog}','{future_op}');SELECT security.swap_operation_status('{catalog}','{swap_op}');ROLLBACK;""")
    print('PASS current reference authority: raw futures/swap bookings, amendments and discount lookup reject revoked catalogs; exact replay/status remains available, historical lookup rejects later definitions and rejected writes leave no publication.',flush=True)
