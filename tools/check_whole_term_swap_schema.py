"""Relational contracts for side-owned swap periods on an owned stock database."""


def run_checks(query):
    assert query("SELECT to_regclass('trading.swap_side_periods') IS NOT NULL;") == 't', 'Whole-term sides need owned period parameters'
    assert query("SELECT to_regclass('trading.swap_scheduled_periods') IS NULL;") == 't', 'A schedule must not retain child-side membership rows'
    assert query("""
        SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
        WHERE n.nspname='security' AND(p.proname IN('retry_legacy_swap_with_parties','save_projected_swap_core','save_projected_swap_with_parties',
          'save_indexed_swap_with_parties','save_observation_swap_with_parties','save_swap_with_parties','save_annotated_swap','save_monthly_swap')
          OR p.proname='save_scheduled_swap' AND p.pronargs<>7);
    """) == '0', 'The cutover must remove obsolete writers instead of retaining compatibility routes'
    assert query("""
        SELECT count(*) FROM pg_constraint
        WHERE conrelid='trading.swap_side_periods'::regclass AND contype='f'
         AND confrelid IN('trading.swap_fixed_sides'::regclass,'trading.swap_floating_sides'::regclass);
    """) == '0', 'Owned periods must not reference another Side'
    assert query("""
        SELECT count(*) FROM pg_constraint
        WHERE conrelid='trading.swap_side_periods'::regclass AND contype='f'
         AND confrelid='trading.swap_whole_term_sides'::regclass;
    """) == '1', 'Each period belongs to one exact side definition'
    assert query("""
        SELECT count(*) FROM pg_constraint
        WHERE conrelid='trading.swap_period_projections'::regclass AND contype='f'
         AND confrelid='market_data.curve_members'::regclass;
    """) == '1', 'Projection links need exact definition-version and contract foreign keys'
    assert query("""
        SELECT count(*) FROM pg_class
        WHERE oid IN('trading.swap_side_periods'::regclass,'trading.swap_period_projections'::regclass)
         AND relrowsecurity AND relforcerowsecurity;
    """) == '2', 'New terms and projections retain forced row authorization'
    assert query("""
        SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
        CROSS JOIN LATERAL aclexplode(coalesce(p.proacl,acldefault('f',p.proowner))) a
        WHERE n.nspname='security' AND p.proname IN('prepare_fixed_swap_period','prepare_floating_swap_period')
         AND a.grantee=0 AND a.privilege_type='EXECUTE';
    """) == '0', 'Only the authorized booking operation may prepare retained parameters'
    print('PASS whole-term swap schema: owned parameters, exact projection relationships and fresh authorization', flush=True)


def run_storage_checks(query, manifest, require_retention=False):
    """Use a caller-owned synthetic fixture after its other book assertions."""
    from copy import deepcopy
    import json
    from uuid import uuid4
    from preview_fixture import trade_booking

    catalog=manifest['catalogId'];reference=manifest['referenceCatalogId'];revision=manifest['referenceRevisionId']
    tenant=manifest['tenantId'];workspace=manifest['workspaceId']
    quantity=query(f"SELECT quantity_code FROM ref_data.quote_specifications WHERE(catalog_id,revision_id,code)=('{reference}','{revision}','test_gas_outright');")
    exact=dict(revision=dict(catalogId=reference,revisionId=revision),code=quantity)
    rule=dict(frequency='monthly',rule='days_after_period_end',days=18,dayOfMonth=None,dayBasis='calendar_days',shortMonth=None,adjustment='unadjusted',calendar=None,implementation='monthly_payment_date_rules_v1')
    windows=[('2021-01-01','2021-02-01'),('2021-02-01','2021-03-01')]
    def side(fixed):
        return dict(basis='period_total',quantity=exact,amount=None,allocation=None,priceApplication='period_average_times_quantity_v1',power=None,
            periods=[dict(amount=amount,fixed=dict(price=price,currency='usd',deliveryFrom=start,deliveryTo=end,paymentSchedule=rule) if fixed else None,
                floating=None if fixed else dict(settlement=dict(mode='curve_target',index=None,projectionPolicy=None),curve='base_forward',index='test_price',currency='usd',deliveryFrom=start,deliveryTo=end,paymentSchedule=rule,resetFrequency='monthly',averagingMethod='none',spread='0'))
                for (start,end),amount,price in zip(windows,['100','200'],['3','4'])])
    body=dict(operationId=str(uuid4()),knowledgeMode='current',convention='independent_side_financial_swap_v2',bookId='d1000000-0000-0000-0000-000000000001',definitionRevisionId=manifest['revisionId'],commodity='test_gas',
        swapType='scheduled_financial_swap',quantity=None,fixed=None,floating=None,firstFloating=None,fixedCoefficient=None,firstCoefficient='-1',discounting=False,
        scheduled=dict(implementation='whole_term_scheduled_financial_swap_v1',first=side(True),second=side(False)),**trade_booking())
    compiled=[dict(relativeNumerator='1',relativeDenominator='1',clock=None,periods=[dict(numerator=n,denominator='3',fixingPins=[],power=None) for n in ['1','2']]) for _ in range(2)]
    def literal(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
    def runtime(statement):
        token=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{statement} COMMIT;")
    def save(terms):
        return json.loads(runtime(f"SELECT security.save_scheduled_swap('{catalog}',{literal(terms)},{literal(compiled)},300,'2020-12-18T16:00:00Z',{literal(trade_booking())},'{{}}'::uuid[]);"))
    def counts():
        return [int(query(f'SELECT count(*) FROM trading.{table};')) for table in ('swap_fixed_sides','swap_floating_sides','swap_whole_term_sides','swap_side_periods','swap_period_projections','swap_instruments','publications')]
    before=counts()
    missing=deepcopy(body);del missing['scheduled']['second']['periods'][0]['floating']['settlement']
    try:save(missing)
    except RuntimeError as error:
        assert 'price_settlement_selection' in str(error),str(error)
    else:raise AssertionError('A raw booking inferred a settlement mode from its curve')
    assert counts()==before,'Missing settlement selection must leave no financial definition or publication'
    if require_retention:
        try:save(body)
        except RuntimeError as error:
            assert 'side_schedule_missing' in str(error),str(error)
        else:raise AssertionError('A raw booking committed without its complete retained schedules')
        assert counts()==before,'A rejected incomplete booking must leave no financial definition or publication'
        print('PASS retained schedule admission: a raw booking without its schedules refuses at commit and rolls back every publication',flush=True)
        return
    receipt=save(body);first=counts()
    assert first[:2]==before[:2], 'Whole-term booking must not persist independent monthly child sides'
    assert [a-b for a,b in zip(first[2:5],before[2:5])]==[2,4,2], 'One two-side contract owns four periods and two exact projections'
    assert save(body)==receipt and counts()==first, 'A replay must return its first outcome without another publication or definition'
    copy=deepcopy(body);copy['operationId']=str(uuid4());second=save(copy)
    assert second['tradeId']!=receipt['tradeId'] and counts()[2:6]==first[2:6], 'Equivalent terms must reuse whole-term side and instrument identities'
    instrument=query(f"SELECT instrument_id FROM trading.swap_trade_revisions WHERE(catalog_id,trade_id,revision)=('{catalog}','{receipt['tradeId']}',1);")
    context=runtime(f"SELECT security.capture_date_context('{catalog}','2020-12-18',NULL);")
    bind=f"SELECT security.use_market_context('{catalog}','{context}');SELECT security.use_trading_context('{catalog}','2020-12-18T16:00:00Z');"
    retained=json.loads(runtime(bind+f"SELECT trading.scheduled_swap_terms('{catalog}','{receipt['tradeId']}',1,'{instrument}');"))
    assert [p['fixed']['price'] for p in retained['terms']['first']['periods']]==['3','4']
    assert all(p['floating']['settlement']==dict(mode='curve_target',index=None,projectionPolicy=None) for p in retained['terms']['second']['periods']), 'Readback must carry the exact authored settlement policy'
    assert [p['n']+'/'+p['d'] for p in retained['bindings'][0]['periods']]==['1/3','2/3']
    assert query(f"SELECT num_nonnulls(fixed_side_id,first_floating_side_id,floating_side_id) FROM trading.swap_instruments WHERE(catalog_id,instrument_id)=('{catalog}','{instrument}');")=='0'
    assert query(f"SELECT count(*) FROM trading.swap_side_periods p JOIN trading.swap_whole_term_sides s ON(s.catalog_id,s.id)=(p.catalog_id,p.side_id) WHERE(p.catalog_id,p.side_id) IN(SELECT catalog_id,scheduled_first_id FROM trading.swap_instruments WHERE instrument_id='{instrument}') AND p.kind=s.kind;")=='2'
    copy['operationId']=str(uuid4());copy['scheduled']['first']['periods'][1]['fixed']['price']='5';last=save(copy)
    changed=counts()
    assert [a-b for a,b in zip(changed[2:5],first[2:5])]==[1,2,0], 'A changed fixed term creates one new whole-term side while sharing the floating definition'
    latest=query(f"SELECT instrument_id FROM trading.swap_trade_revisions WHERE(catalog_id,trade_id,revision)=('{catalog}','{last['tradeId']}',1);")
    for ordinal,equal in [(0,'t'),(1,'f')]:
        identity=query(f"SELECT settlement_data.swap_owned_period_identity('{catalog}',a.id)=settlement_data.swap_owned_period_identity('{catalog}',b.id) FROM trading.swap_instruments old JOIN trading.swap_side_periods a ON(a.catalog_id,a.side_id,a.ordinal)=(old.catalog_id,old.scheduled_first_id,{ordinal}) CROSS JOIN trading.swap_instruments new JOIN trading.swap_side_periods b ON(b.catalog_id,b.side_id,b.ordinal)=(new.catalog_id,new.scheduled_first_id,{ordinal}) WHERE(old.catalog_id,old.instrument_id)=('{catalog}','{instrument}') AND(new.catalog_id,new.instrument_id)=('{catalog}','{latest}');")
        assert identity==equal, 'Period continuity must compare its own terms independently of a changed future term or parent identity'
    print('PASS whole-term swap storage: two-period ownership, exact replay, definition deduplication and retained reconstruction', flush=True)


def check_rebooking_guard(query, psql, environment, root):
    """Old definitions are refused without rewriting their retained economics."""
    import subprocess
    from uuid import uuid4
    from preview_fixture import fixture, trading_fixture

    database='openetrm_side_upgrade_'+uuid4().hex
    target=list(psql);target[target.index('-d')+1]=database
    def sql(statement, expected=None):
        result=subprocess.run([str(x) for x in target],input=statement,text=True,capture_output=True,env=environment,cwd=root,timeout=30)
        if expected is not None:
            assert result.returncode and expected in result.stderr, result.stderr
        elif result.returncode:
            raise AssertionError(result.stderr)
        return result.stdout.strip()
    def without_roles(statement):return '\n'.join(line for line in statement.splitlines() if not line.startswith('CREATE ROLE '))
    query('CREATE DATABASE '+database+' TEMPLATE template0;')
    try:
        for migration in sorted((root/'db/migrations').glob('*.sql')):
            if int(migration.name[:4])<123:sql(without_roles(migration.read_text()))
        reference,market,security,manifest=fixture(root)
        for statement in [reference,market,security,trading_fixture(manifest['catalogId'])]:sql(without_roles(statement))
        sql(f"""BEGIN;
         INSERT INTO trading.swap_fixed_sides(catalog_id,id,reference_catalog_id,reference_revision_id,quote_code,delivery_from,delivery_to,payment_at,fixed_price,implementation,publication_order,effective_at)
         VALUES('{manifest['catalogId']}','{uuid4()}','{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}','test_gas_outright','2021-01-01','2021-02-01','2021-02-18T00:00:00Z',3,'fixed_total_payment_v1',1,'2020-01-01T00:00:00Z');COMMIT;""")
        before=sql('SELECT row_to_json(s)::text FROM trading.swap_fixed_sides s;')
        sql((root/'db/migrations/0123_whole_term_swap_sides.sql').read_text(),expected='whole_term_swap_rebooking_required')
        assert sql('SELECT row_to_json(s)::text FROM trading.swap_fixed_sides s;')==before
        assert sql("SELECT to_regclass('trading.swap_side_periods') IS NULL;")=='t'
        print('PASS whole-term swap upgrade: retained old definitions refuse atomically pending explicit rebooking',flush=True)
    finally:
        query('DROP DATABASE '+database+' WITH(FORCE);')
