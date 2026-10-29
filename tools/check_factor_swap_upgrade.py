"""Retained scheduled commands survive the factor-version schema upgrade."""
import json
import subprocess
from uuid import uuid4
from preview_fixture import fixture,trading_fixture,trade_booking


def run_checks(query,psql,environment,root):
    name='openetrm_factor_upgrade_'+uuid4().hex
    target=list(psql);target[target.index('-d')+1]=name
    def sql(statement):
        result=subprocess.run([str(p) for p in target],input=statement,text=True,capture_output=True,env=environment,cwd=root,timeout=30)
        if result.returncode:raise AssertionError(result.stderr)
        return result.stdout.strip()
    def without_roles(statement):return '\n'.join(line for line in statement.splitlines() if not line.startswith('CREATE ROLE '))
    query('CREATE DATABASE '+name+' TEMPLATE template0;')
    try:
        migrations=sorted((root/'db/migrations').glob('*.sql'))
        for migration in migrations:
            if int(migration.name[:4])<114:sql(without_roles(migration.read_text()))
        reference,market,security,manifest=fixture(root,fixing_frames=True)
        for statement in [reference,market,security,trading_fixture(manifest['catalogId'])]:sql(without_roles(statement))
        catalog,tenant,workspace=(manifest[k] for k in ('catalogId','tenantId','workspaceId'))
        def runtime(statement):
            token=sql(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
            return sql(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{statement} COMMIT;")
        quantity=sql(f"SELECT quantity_code FROM ref_data.quote_specifications WHERE(catalog_id,revision_id,code)=('{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}','test_gas_outright');")
        rule=dict(frequency='monthly',rule='days_after_period_end',days=18,dayOfMonth=None,dayBasis='calendar_days',shortMonth=None,adjustment='unadjusted',calendar=None,implementation='monthly_payment_date_rules_v1')
        ref=dict(revision=dict(catalogId=manifest['referenceCatalogId'],revisionId=manifest['referenceRevisionId']),code=quantity)
        fixed=dict(price='3',currency='usd',deliveryFrom='2021-01-01',deliveryTo='2021-02-01',paymentSchedule=rule)
        floating=dict(curve='base_forward',index='test_price',currency='usd',deliveryFrom='2021-01-01',deliveryTo='2021-02-01',paymentSchedule=rule,resetFrequency='monthly',averagingMethod='none',spread='0')
        def side(fixed_terms,float_terms):return dict(basis='period_total',quantity=ref,amount=None,allocation=None,priceApplication='period_average_times_quantity_v1',power=None,periods=[dict(amount='100',fixed=fixed_terms,floating=float_terms)])
        # This isolated database uses the 0106 side-composition contract, before 0115 adds factors.
        body=dict(operationId=str(uuid4()),knowledgeMode='current',convention='independent_side_financial_swap_v2',bookId='d1000000-0000-0000-0000-000000000001',definitionRevisionId=manifest['revisionId'],commodity='test_gas',swapType='scheduled_financial_swap',quantity=None,fixed=None,floating=None,firstFloating=None,fixedCoefficient=None,firstCoefficient='-1',discounting=False,scheduled=dict(implementation='independent_scheduled_financial_swap_v1',first=side(fixed,None),second=side(None,floating)),**trade_booking())
        compiled=[dict(relativeNumerator='1',relativeDenominator='1',clock=None,periods=[dict(numerator='1',denominator='1',fixingPins=[],power=None)])]*2
        def literal(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
        call=f"SELECT security.save_scheduled_swap('{catalog}',{literal(body)},{literal(compiled)},100,'2020-12-18T16:00:00Z',{literal(trade_booking())},'{{}}'::uuid[]);"
        receipt=json.loads(runtime(call));trade=receipt['tradeId']
        def rows():
            return {table:sql(f"SELECT(to_jsonb(t)-'quantity_factor_id')::text FROM trading.{table} t ORDER BY(to_jsonb(t)-'quantity_factor_id')::text;") for table in ('swap_instruments','swap_scheduled_sides','swap_scheduled_periods','swap_quantity_authoring','swap_quantity_amounts','swap_trade_revisions','publications')}
        before=rows()
        instrument=sql(f"SELECT instrument_id FROM trading.swap_trade_revisions WHERE trade_id='{trade}';")
        terms=f"SELECT trading.scheduled_swap_terms('{catalog}','{trade}',1,'{instrument}');"
        original=json.loads(sql(terms))
        sql((root/'db/migrations/0115_factor_scheduled_swaps.sql').read_text())
        assert rows()==before
        assert json.loads(runtime(call))==receipt
        assert json.loads(sql(terms))==original
        assert sql('SELECT count(*) FROM trading.swap_quantity_factors;')=='0'
        print('PASS factor upgrade: pre-upgrade scheduled rows, exact command replay and normalized retained terms remain unchanged',flush=True)
    finally:
        query('DROP DATABASE '+name+' WITH(FORCE);')
