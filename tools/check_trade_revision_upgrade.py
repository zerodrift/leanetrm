"""Qualify retained-booking migration in a separately owned disposable database."""
import json
import subprocess
import time
from pathlib import Path
from uuid import uuid4


def run_checks(query, psql, environment, root, fixture, manifest):
    name = 'leanetrm_upgrade_' + uuid4().hex
    target = list(psql)
    target[target.index('-d') + 1] = name
    def upgraded(sql):
        result = subprocess.run([str(p) for p in target], input=sql, text=True, capture_output=True,
                                env=environment, cwd=root, timeout=30)
        if result.returncode:
            raise AssertionError(result.stderr)
        return result.stdout.strip()
    children=[]
    def migrate_with_booking(migration,contract):
        catalog,tenant,workspace=(manifest[k] for k in ('catalogId','tenantId','workspaceId'))
        token=upgraded("SET SESSION AUTHORIZATION preview_authenticator; SELECT security.issue_context("
                       f"'https://preview.invalid','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        publisher=subprocess.Popen([str(p) for p in target],env=environment,cwd=root,text=True,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE)
        children.append(publisher)
        publisher.stdin.write(f"SET SESSION AUTHORIZATION preview_runtime; BEGIN; SELECT security.use_context('{token}'); "
                              f"SELECT security.book_futures('{catalog}','{uuid4()}','d1000000-0000-0000-0000-000000000001','{manifest['revisionId']}',"
                              f"'future','{contract}','2020-12-18T16:00:00Z','2020-12-18T15:30:00Z',1,2.9,'fixed_total_cash_execution_pnl_v1');\n\\echo READY\n")
        publisher.stdin.flush()
        while publisher.stdout.readline().strip()!='READY':
            if publisher.poll() is not None:raise AssertionError(publisher.stderr.read())
        label='reference_upgrade_'+migration.name[:4]
        migrator=subprocess.Popen([str(p) for p in target],env={**environment,'PGAPPNAME':label},cwd=root,text=True,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE)
        children.append(migrator);migrator.stdin.write(migration.read_text());migrator.stdin.close();migrator.stdin=None
        for _ in range(100):
            if query(f"SELECT count(*) FROM pg_stat_activity WHERE application_name='{label}' AND wait_event='advisory';")=='1':break
            if migrator.poll() is not None:raise AssertionError(migrator.stderr.read())
            time.sleep(.025)
        else:raise AssertionError('Upgrade did not join the in-flight publication fence: '+migration.name)
        publisher.stdin.write('COMMIT;\n');publisher.stdin.flush();_,error=publisher.communicate(timeout=10)
        assert publisher.returncode==0,error
        _,error=migrator.communicate(timeout=15);assert migrator.returncode==0,error
        print('PASS '+migration.name+' includes a booking already admitted at the publication fence',flush=True)
    def without_shared_roles(sql):
        # Roles belong to the owned cluster and were installed by the main fixture.
        return '\n'.join(line for line in sql.splitlines() if not line.startswith('CREATE ROLE '))
    query(f'CREATE DATABASE {name} TEMPLATE template0;')
    try:
        assert Path(upgraded('SHOW data_directory;')).resolve() == Path(query('SHOW data_directory;')).resolve()
        for migration in sorted((root/'db/migrations').glob('*.sql')):
            if int(migration.name[:4]) <= 12:
                upgraded(without_shared_roles(migration.read_text()))
        for sql in fixture:
            upgraded(without_shared_roles(sql))
        catalog, tenant, workspace = (manifest[k] for k in ('catalogId', 'tenantId', 'workspaceId'))
        def runtime(sql):
            token = upgraded("SET SESSION AUTHORIZATION preview_authenticator; SELECT security.issue_context("
                             f"'https://preview.invalid','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
            return upgraded(f"SET SESSION AUTHORIZATION preview_runtime; BEGIN; SELECT security.use_context('{token}'); {sql} COMMIT;")
        contract = upgraded(f"SELECT id FROM market_data.contracts WHERE catalog_id='{catalog}' AND kind='future' ORDER BY delivery_from LIMIT 1;")
        result = json.loads(runtime(f"SELECT security.book_futures('{catalog}','{uuid4()}','d1000000-0000-0000-0000-000000000001',"
                                    f"'{manifest['revisionId']}','future','{contract}','2020-12-18T16:00:00Z','2020-12-18T15:30:00Z',2,2.9,'fixed_total_cash_execution_pnl_v1');"))
        context = runtime(f"SELECT security.capture_date_context('{catalog}','2020-12-18',NULL);")
        original_head=upgraded('SELECT max(publication_order) FROM trading.publications;')
        original_sql=("SELECT jsonb_build_object('trades',(SELECT jsonb_agg(t) FROM trading.trades t WHERE id='"+result['tradeId']+"'),'bindings',(SELECT jsonb_agg(p) FROM trading.trade_instruments p WHERE trade_id='"+result['tradeId']+"'),'publications',(SELECT jsonb_agg(p) FROM trading.publications p WHERE publication_order<="+original_head+"));")
        original=upgraded(original_sql)
        contracts=upgraded(f"SELECT id FROM market_data.contracts WHERE catalog_id='{catalog}' AND kind='future' ORDER BY delivery_from LIMIT 3;").splitlines()
        migrate_with_booking(root/'db/migrations/0013_trade_amendments.sql',contracts[1])
        assert original==upgraded(original_sql)
        assert upgraded("SELECT count(*)=2 AND bool_and(r.revision=1 AND r.previous_revision IS NULL AND r.event_kind='booked' AND r.event_schema='futures_booking_state_v1' AND r.publication_order=p.publication_order AND r.effective_at=p.effective_at AND r.execution_at=p.effective_at AND r.instrument_id=p.instrument_id AND r.binding_id=p.binding_id AND r.lots=p.lots AND r.execution_price=p.execution_price) FROM trading.trade_revisions r JOIN trading.trade_instruments p USING(catalog_id,trade_id);") == 't'
        migrate_with_booking(root/'db/migrations/0014_trading_references.sql',contracts[2])
        assert upgraded('SELECT (SELECT count(*) FROM trading.trade_references)=3 AND (SELECT count(*) FROM trading.instrument_references)=3;') == 't'
        retained = runtime(f"SELECT security.use_market_context('{catalog}','{context}'); SELECT security.use_trading_context('{catalog}','2020-12-18T16:00:00Z'); SELECT jsonb_build_object('trade',trade_id,'lots',lots,'execution',execution_price,'revision',revision) FROM trading.trade_revisions;")
        assert json.loads(retained) == {'trade': result['tradeId'], 'lots': 2, 'execution': 2.9, 'revision': 1}
        print('PASS retained-booking upgrade preserves original rows, publication identity and pre-upgrade certified context', flush=True)
        upgraded((root/'db/migrations/0015_financial_swaps.sql').read_text())
        index=upgraded(f"SELECT index_a_code FROM ref_data.settlement_targets WHERE catalog_id='{manifest['referenceCatalogId']}' AND revision_id='{manifest['referenceRevisionId']}' AND code='test_index_target';")
        operation=str(uuid4())
        arguments=f"'{catalog}','{operation}',NULL,NULL,'d1000000-0000-0000-0000-000000000001','{manifest['revisionId']}','{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}', 'base_forward','{index}','usd','usd','2021-01-01','2021-02-01','2021-02-18T12:00:00Z','2021-01-01','2021-02-01','2020-12-31T12:00:00Z','2021-03-18T12:00:00Z','monthly_index_price',3,100,-1,false,'2020-12-18T16:00:00Z','2020-12-18T15:00:00Z','2020-12-18T15:00:00Z','Initial booking','independent_side_financial_swap_v2'"
        legacy=json.loads(runtime('SELECT security.save_monthly_swap('+arguments+');'))
        legacy_context=runtime(f"SELECT security.capture_date_context('{catalog}','2020-12-18',NULL);")
        tables=['swap_fixed_sides','swap_floating_sides','swap_instruments','swap_trade_revisions','publications','knowledge_receipts','contexts']
        def snapshot():
            return {table:upgraded("SELECT (to_jsonb(t)-'payment_rule_id')::text FROM trading."+table+" t ORDER BY (to_jsonb(t)-'payment_rule_id')::text;") for table in tables}
        before=snapshot()
        upgraded((root/'db/migrations/0016_swap_payment_rules.sql').read_text())
        assert snapshot()==before
        assert upgraded("SELECT (SELECT bool_and(payment_rule_id IS NULL) FROM trading.swap_fixed_sides) AND(SELECT bool_and(payment_rule_id IS NULL) FROM trading.swap_floating_sides);")=='t'
        assert json.loads(runtime('SELECT security.save_scheduled_swap('+arguments+',NULL,NULL,NULL);'))==legacy
        bound=f"SELECT security.use_market_context('{catalog}','{legacy_context}');SELECT security.use_trading_context('{catalog}','2020-12-18T16:00:00Z');"
        assert runtime(bound+"SELECT payment_at='2021-02-18T12:00:00Z'::timestamptz AND payment_rule_id IS NULL FROM trading.swap_fixed_sides;")=='t'
        assert snapshot()==before
        print('PASS payment-rule upgrade preserves exact legacy side columns, instants, knowledge and pending-command replay',flush=True)

        upgraded((root/'db/migrations/0017_trade_annotations.sql').read_text())
        old_states={table:upgraded("SELECT to_jsonb(t)::text FROM trading."+table+" t ORDER BY to_jsonb(t)::text;") for table in ('trade_revisions','swap_trade_revisions')}
        upgraded((root/'db/migrations/0018_trade_parties.sql').read_text())
        party_columns="-'trade_date'-'legal_entity_id'-'legal_entity_revision'-'counterparty_id'-'counterparty_revision'"
        assert old_states=={table:upgraded("SELECT (to_jsonb(t)"+party_columns+")::text FROM trading."+table+" t ORDER BY (to_jsonb(t)"+party_columns+")::text;") for table in old_states}
        assert upgraded("SELECT NOT EXISTS(SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='security' AND p.proname IN('book_futures','amend_futures','save_monthly_swap','save_scheduled_swap','book_futures_annotated','amend_futures_annotated','save_annotated_swap') AND has_function_privilege('preview_runtime',p.oid,'EXECUTE'));")=='t'
        assert runtime(bound+"SELECT bool_and(trade_date IS NULL AND legal_entity_id IS NULL AND counterparty_id IS NULL) FROM trading.swap_trade_revisions;")=='t'
        assert json.loads(runtime(f"SELECT security.swap_operation_status('{catalog}','{operation}');"))==legacy
        print('PASS trade party upgrade preserves legacy event payloads and certified history without inferred dates/parties; old runtime writes are closed',flush=True)

        before_contracts={table:upgraded("SELECT to_jsonb(t)::text FROM trading."+table+" t ORDER BY to_jsonb(t)::text;") for table in ('trade_revisions','trade_instruments','instruments','futures_sides','instrument_sides','publications')}
        upgraded((root/'db/migrations/0019_contract_instruments.sql').read_text())
        assert before_contracts=={table:upgraded("SELECT to_jsonb(t)::text FROM trading."+table+" t ORDER BY to_jsonb(t)::text;") for table in before_contracts}
        assert upgraded("SELECT count(*)=(SELECT count(*) FROM trading.trade_revisions) FROM trading.futures_revision_markets;")=='t'
        assert runtime(bound+"SELECT bool_and(r.instrument_id=m.instrument_id AND r.publication_order=m.publication_order AND r.effective_at=m.effective_at) FROM trading.trade_revisions r JOIN trading.futures_revision_markets m USING(catalog_id,trade_id,revision);")=='t'
        assert runtime(bound+"SELECT count(*)=(SELECT count(*) FROM trading.trade_instruments) FROM trading.futures_contract_references;")=='t'
        swap_keys_before={table:upgraded("SELECT to_jsonb(t)::text FROM trading."+table+" t ORDER BY to_jsonb(t)::text;") for table in ('swap_floating_sides','swap_reset_projections','swap_instruments','swap_trade_revisions')}
        upgraded((root/'db/migrations/0020_swap_identity_keys.sql').read_text())
        assert swap_keys_before=={table:upgraded("SELECT to_jsonb(t)::text FROM trading."+table+" t ORDER BY to_jsonb(t)::text;") for table in swap_keys_before}
        print('PASS contract normalization preserves legacy UUIDs, original events, publication coordinates and historical exact pricing bindings',flush=True)
        for version in ('0021_party_editors.sql','0022_standard_futures_lots.sql','0023_deal_amount_formats.sql','0024_preview_identity_scope.sql','0025_trading_reference_authority.sql'):
            upgraded((root/'db/migrations'/version).read_text())
        assert original==upgraded(original_sql)
        assert upgraded("SELECT bool_and(t.profile_code=c.profile_code) FROM ref_data.futures_product_terms t JOIN ref_data.futures_series s ON(s.catalog_id,s.revision_id,s.product_code)=(t.catalog_id,t.revision_id,t.product_code) JOIN market_data.contracts k ON(k.reference_catalog_id,k.reference_revision_id,k.futures_series_code)=(s.catalog_id,s.revision_id,s.code) JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.contract_id)=(k.catalog_id,k.revision_id,k.id) JOIN market_data.curves c ON(c.catalog_id,c.revision_id,c.code)=(m.catalog_id,m.revision_id,m.curve_code);")=='t'
        print('PASS generalized-futures upgrade preserves original financial rows and normalizes exact authoritative delivery profiles',flush=True)

        headers_before=upgraded("SELECT jsonb_agg(to_jsonb(t) ORDER BY t.id)::text FROM trading.trades t;")
        upgraded((root/'db/migrations/0029_server_booking_facts.sql').read_text())
        assert headers_before==upgraded("SELECT jsonb_agg(to_jsonb(t) ORDER BY t.id)::text FROM trading.trades t;")
        assert upgraded("SELECT count(*)=(SELECT count(*) FROM trading.trades) AND bool_and(implementation='retained_commit_utc_v1') FROM trading.booking_facts;")=='t'
        # Introduce exact party references through a new publication, never by
        # assigning newly created reference rows to an old certified prefix.
        upgraded(f"""BEGIN;SELECT pg_advisory_xact_lock(730849102);
         INSERT INTO trading.publications SELECT '{catalog}',max(publication_order)+1,pg_current_xact_id() FROM trading.publications WHERE catalog_id='{catalog}';
         INSERT INTO trading.legal_entities SELECT '{catalog}','d4000000-0000-0000-0000-000000000001',1,'upgrade_entity','Synthetic upgrade entity',max(publication_order),'2020-01-01T00:00:00Z' FROM trading.publications WHERE catalog_id='{catalog}';
         INSERT INTO trading.counterparties SELECT '{catalog}','d5000000-0000-0000-0000-000000000001',1,'upgrade_party','Synthetic upgrade party',max(publication_order),'2020-01-01T00:00:00Z' FROM trading.publications WHERE catalog_id='{catalog}';COMMIT;""")
        from preview_fixture import trade_booking
        party_json=json.dumps(trade_booking())
        amended=runtime(f"SELECT security.amend_futures_with_parties('{catalog}','{uuid4()}','{result['tradeId']}',1,'d1000000-0000-0000-0000-000000000001','{manifest['revisionId']}','future','{contracts[0]}','2020-12-18T16:00:00Z','2020-12-18T16:00:00Z',NULL,3,2.9,'Synthetic legacy quantity correction','fixed_total_cash_execution_pnl_v1','unprocessed_futures_booking_amendment_v1',NULL,NULL,NULL,'{party_json}'::jsonb);")
        assert json.loads(amended)['revision']==2
        rule=lambda days:json.dumps(dict(frequency='monthly',rule='days_after_period_end',days=days,dayOfMonth=None,dayBasis='calendar_days',shortMonth=None,adjustment='unadjusted',calendar=None,implementation='monthly_payment_date_rules_v1'))
        amended_swap=runtime(f"SELECT security.save_swap_with_parties('{catalog}','{uuid4()}','{legacy['tradeId']}',1,'d1000000-0000-0000-0000-000000000001','{manifest['revisionId']}','{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}','base_forward','{index}','usd','usd','2021-01-01','2021-02-01','2021-02-18T00:00:00Z','2021-01-01','2021-02-01','2020-12-31T12:00:00Z','2021-03-18T00:00:00Z','monthly_index_price',3,200,-1,false,'2020-12-18T16:00:00Z',NULL,'2020-12-18T16:00:00Z','Synthetic legacy quantity correction','independent_side_financial_swap_v2','{rule(18)}'::jsonb,'{rule(46)}'::jsonb,'test_gas',NULL,NULL,NULL,'{party_json}'::jsonb);")
        assert json.loads(amended_swap)['revision']==2
        assert upgraded("SELECT bool_and(r.trade_date IS NULL) FROM(SELECT trade_date FROM trading.trade_revisions WHERE revision=1 UNION ALL SELECT trade_date FROM trading.swap_trade_revisions WHERE revision=1)r;")=='t'
        assert upgraded("SELECT bool_and(r.trade_date=f.trade_date) FROM(SELECT catalog_id,trade_id,trade_date FROM trading.trade_revisions WHERE revision=2 UNION ALL SELECT catalog_id,trade_id,trade_date FROM trading.swap_trade_revisions WHERE revision=2)r JOIN trading.booking_facts f USING(catalog_id,trade_id);")=='t'
        print('PASS server-booking upgrade: original rows unchanged, certified commit facts retained, legacy futures/swap V1 amendments derive missing date without rewriting the originals',flush=True)




    finally:
        for child in children:
            if child.poll() is None:child.kill();child.communicate(timeout=5)
        # Every connection above is a completed subprocess; never force-drop sessions.
        query(f'DROP DATABASE {name};')
