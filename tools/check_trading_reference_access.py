"""Raw business operations must obey current reference scope after catalog revocation."""
import json
from uuid import uuid4
from preview_fixture import trade_booking
from preview_client import PreviewClient, initial_frame
from swap_fixture import REVISION


def run_checks(query,manifest,url,client=None):
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
    original_future=json.loads(runtime('SELECT '+future(future_op)+';'))
    # Current admission retains a complete schedule through the ordinary API.
    # Raw probes below use its exact accepted command, independent of prior tests
    # having warmed a matching side or omitted nullable command fields.
    c=client or PreviewClient(url);c.bootstrap();c.call('profile',{'profile':'trader'})
    frame=initial_frame(c.call('workspace'),'2020-12-18')
    context={'snapshotId':frame['id'],'convention':'independent_side_financial_swap_v2','knowledgeMode':'current'}
    def rule(days):return dict(frequency='monthly',rule='days_after_period_end',days=days,dayOfMonth=None,dayBasis='calendar_days',shortMonth=None,adjustment='unadjusted',calendar=None,implementation='monthly_payment_date_rules_v1')
    selection={'mode':'curve_target','index':None,'projectionPolicy':None}
    original_swap=c.call('swaps/book',{**context,**trade_booking(),'operationId':swap_op,'bookId':book,'definitionRevisionId':REVISION,
        'commodity':'test_gas','quantity':'100','fixedCoefficient':'-1','discounting':False,
        'fixed':{'price':'3','currency':'usd','deliveryFrom':'2021-01-01','deliveryTo':'2021-02-01','paymentSchedule':rule(18)},
        'floating':{'curve':'daily_gas','index':index,'currency':'usd','deliveryFrom':'2021-01-01','deliveryTo':'2021-02-01',
            'paymentSchedule':rule(46),'resetFrequency':'daily','averagingMethod':'arithmetic','spread':'0','settlement':selection}})
    retained=json.loads(query(f"SELECT request::text FROM trading.swap_trade_revisions WHERE catalog_id='{catalog}' AND operation_id='{swap_op}';"))
    assert retained['floating']['settlement']==selection
    def literal(value):return 'NULL' if value is None else "'"+str(value).replace("'","''")+"'"
    def json_argument(value):return 'NULL' if value is None else literal(json.dumps(value))+'::jsonb'
    def swap(operation,trade=None):
        args=[literal(catalog),literal(operation),literal(trade),'NULL' if trade is None else '1',literal(retained['book']),
            literal(retained['definition']),literal(retained['referenceCatalog']),literal(retained['referenceRevision']),literal(retained['commodity']),
            json_argument(retained['fixed']),json_argument(retained['firstFloating']),json_argument(retained['floating']),
            str(retained['quantity'] if trade is None else 200),str(retained['coefficient']),str(retained['discounting']).lower(),
            literal(retained['valuation']),literal(retained['effective']),literal(retained['reason'] if trade is None else 'Synthetic correction'),
            literal(retained['implementation']),literal(retained['memo']),json_argument(retained['booking']),"'{}'::uuid[]"]
        return 'security.save_composed_swap('+','.join(args)+')'
    assert json.loads(runtime('SELECT '+swap(swap_op)+';'))['tradeId']==original_swap['tradeId']
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
