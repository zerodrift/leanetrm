"""Whole-term gas Side booking, with independent dated quantity/price oracles."""
from copy import deepcopy
from datetime import date, timedelta
from fractions import Fraction
from uuid import uuid4
from preview_client import PreviewClient, initial_frame
from preview_fixture import trade_booking


def run_checks(url, query, manifest, run_worker):
    c=PreviewClient(url);c.bootstrap();c.call('profile',{'profile':'trader'})
    frame=initial_frame(c.call('workspace'),'2020-12-18')
    context=dict(snapshotId=frame['id'],convention='physical_gas_fixed_quantity_v4',knowledgeMode='current')
    catalog=c.call('gas/catalog',context)
    path=next(p for p in catalog['locations'] if p['deliveryPoint']=='synthetic_gas_point_a')
    first=None
    for start,end in [('2021-01-01','2021-03-01'),('2021-01-15','2021-03-01'),('2021-01-25','2021-02-10'),('2021-01-31','2021-02-02')]:
        jan=(date(2021,2,1)-date.fromisoformat(start)).days
        feb=(date.fromisoformat(end)-date(2021,2,1)).days
        for basis,quantity in [('per_operating_day','100'),('period_total_equal_days','1000')]:
            daily=Fraction(quantity) if basis=='per_operating_day' else Fraction(quantity)/(jan+feb)
            for direction,cash_direction,sign in [('receive','pay',1),('deliver','receive',-1)]:
                terms={**context,**trade_booking(),'operationId':str(uuid4()),'bookId':catalog['books'][0]['id'],'definitionRevisionId':path['definitionRevisionId'],
                    'gasSetupId':catalog['gasSetupId'],'discounting':False,'profile':'fixed_gas_composition_v1',
                    'physicalSides':[dict(code='gas',pipeline=path['pipeline'],zone=path['zone'],deliveryPoint=path['deliveryPoint'],product=path['product'],direction=direction,
                        deliveryFrom=start,deliveryTo=end,quantityBasis=basis,quantity=quantity,link=None)],
                    'cashSides':[dict(code='payment',role='consideration',direction=cash_direction,source='gas',stage='delivery',price='3',tariffCode=None,paymentAt='2021-03-02T00:00:00Z')]}
                expected=sign*daily*(jan*Fraction('0.2')+feb*Fraction('0.265'))
                value=c.call('gas/composition/preview',terms)
                assert Fraction(value['presentValue'])==expected,(start,end,basis,direction,value)
                assert len(value['sides'])==2 and len(value['sides'][0]['deliveryRows'])==jan+feb
                booked=c.call('gas/composition/book',terms)
                assert c.call('gas/composition/book',terms)==booked
                reopened=c.call('gas/composition/editor',dict(context=context,tradeId=booked['tradeId']))
                assert Fraction(reopened['valuation']['presentValue'])==expected
                assert reopened['terms']['physicalSides']==terms['physicalSides']
                rows=reopened['valuation']['sides'][0]['deliveryRows']
                assert len(rows)==jan+feb and all(Fraction(r['quantity']['numerator'])/Fraction(r['quantity']['denominator'])==daily for r in rows)
                assert rows[0]['day']==start and rows[-1]['day']==(date.fromisoformat(end)-timedelta(days=1)).isoformat()
                ticket=c.call('tickets/trade',dict(context=context,tradeId=booked['tradeId'],bindingId=booked['instrumentId'],revision=1))
                assert len(ticket['draft']['sides'])==2
                assert Fraction(ticket['schedule']['totals']['gas.quantity']['value'])==sign*daily*(jan+feb)
                operational=c.call('gas/quantities/read',dict(context=context,tradeId=booked['tradeId'],selectionPolicy='operational_projection_v1'))
                assert len(operational['rows'])==jan+feb
                assert all(Fraction(r['contractual']['numerator'])/Fraction(r['contractual']['denominator'])==daily for r in operational['rows'])
                if first is None:first=(terms,booked,expected)
    assert first is not None
    print('PASS whole-term gas booking: 16 partial/full-month, quantity-basis and direction cases retain one physical Side and one cash Side',flush=True)

    terms,booked,expected=first
    for days in (0,1201):
        invalid=deepcopy(terms);invalid['operationId']=str(uuid4())
        invalid['physicalSides'][0]['deliveryTo']=(date(2021,1,1)+timedelta(days=days)).isoformat()
        assert c.call('gas/composition/preview',invalid,expected=422)['code']=='gas_input'
    shared={**context,'convention':'trading_workspace_v1'}
    scope=dict(context=shared,bookIds=[],portfolioIds=[],tradeIds=[booked['tradeId']])
    facts=c.call('trading/positions',dict(context=shared,bookIds=[],portfolioIds=[],offset=0,limit=64))
    check_native_sources(query,manifest,booked,facts['context']['contextId'])
    blotter=c.call('trading/blotter',dict(context=shared,view=dict(mode='trades',bookIds=[],portfolioIds=[],groupBy=[],measures=[],pivotConvention=None)))
    assert Fraction(next(p['presentValue'] for p in blotter['holdings'] if p['tradeId']==booked['tradeId']))==expected
    c.call('profile',{'profile':'risk'})
    catalog=c.call('portfolio/risk/catalog',scope)
    assert len(catalog['factors'])==6
    request={**scope,'context':{**shared,'knowledgeMode':'retained','contextId':catalog['context']['contextId']},'detail':'holdings',
        'convention':catalog['convention'],'numericalPolicy':catalog['numericalPolicy'],'pricingPolicy':catalog['pricingPolicy'],'stress':[],
        'bumps':[dict(factor=f['factor'],size='0.01',convention=catalog['bumpConvention']) for f in catalog['factors']]}
    risk=c.call('portfolio/risk',request)
    assert not risk['excludedPositions'] and risk['work']['positionCount']==1
    for f in risk['factors']:
        values=f['positions'][0]['values'];assert Fraction(values['base'])==1362 and Fraction(values['gamma'])==0
        sensitivity=3100 if f['factor']['deliveryFrom']=='2021-01-01' else 2800
        assert Fraction(values['delta'])==sensitivity and Fraction(values['upPnl'])==Fraction(sensitivity,100)
    if run_worker is not None:
        job=c.call('portfolio/risk/jobs',dict(requestId=str(uuid4()),request=request));run_worker()
        result=c.call('portfolio/risk/jobs/'+job['id'])
        from check_portfolio_risk import financial
        assert result['state']=='completed' and financial(result['result'])==financial(risk)
        print('PASS whole-term gas: all59days in native ticket/operational reads, Blotter1362, six independent monthly factors and retained worker parity',flush=True)


def check_native_sources(query,manifest,booked,context_id):
    """Corrupt one exact native source at a time, then roll back every mutation/grant."""
    from check_booked_side_programs import runtime_query
    signature='trading.require_gas_program_shape(uuid,uuid,integer,date,date,timestamp with time zone,numeric,numeric,market_data.gas_valuation_assignments,ref_data.gas_delivery_conventions,timestamp with time zone,jsonb)'
    legacy=signature.removesuffix(',jsonb)')+')'
    schedule=query(f"SELECT h.id FROM trading.gas_memberships m JOIN trading.side_schedules h ON(h.catalog_id,h.side_id)=(m.catalog_id,m.side_id) WHERE m.instrument_id='{booked['instrumentId']}' AND m.slot=0;")
    rows=f"catalog_id='{manifest['catalogId']}' AND schedule_id='{schedule}'"
    january=f"(SELECT ordinal FROM trading.side_schedule_rows WHERE {rows} AND local_key='gas/0/market')"
    february=f"(SELECT ordinal FROM trading.side_schedule_rows WHERE {rows} AND local_key='gas/31/market')"
    assignment=f"SELECT x.* INTO STRICT a FROM market_data.gas_valuation_assignments x JOIN trading.gas_component_terms m ON(m.catalog_id,m.definition_revision_id,m.assignment_code)=(x.catalog_id,x.revision_id,x.code) WHERE m.instrument_id='{booked['instrumentId']}' AND m.slot=0;"
    setup=f"SELECT security.use_market_context('{manifest['catalogId']}','{context_id}');SELECT security.use_trading_context('{manifest['catalogId']}','2020-12-18T16:00:00Z');"
    validator=f"PERFORM trading.require_gas_program_shape('{manifest['catalogId']}','{schedule}',0,'2021-01-01','2021-03-01','2021-03-01',59,NULL,a,d,start_at,NULL);"
    declarations="DECLARE a market_data.gas_valuation_assignments;d ref_data.gas_delivery_conventions;start_at timestamptz;"
    initialize=assignment+f"SELECT * INTO STRICT d FROM ref_data.gas_delivery_conventions WHERE(catalog_id,revision_id,pipeline_code,zone_code,point_code,product_code)=(a.reference_catalog_id,a.reference_revision_id,a.pipeline_code,a.zone_code,a.point_code,a.product_code);SELECT min(origin) INTO start_at FROM trading.side_schedule_grids WHERE {rows};"
    grant=f"GRANT EXECUTE ON FUNCTION {signature} TO openetrm_runtime;"
    runtime_query(query,manifest,setup+f"DO $positive$ {declarations}BEGIN {initialize}{validator}END$positive$;",prefix=grant,commit=False)
    b_rows=rows.replace('catalog_id=', 'b.catalog_id=').replace('schedule_id=', 'b.schedule_id=')
    p_rows=rows.replace('catalog_id=', 'p.catalog_id=').replace('schedule_id=', 'p.schedule_id=')
    bindings='ALTER TABLE trading.side_schedule_bindings DISABLE TRIGGER USER;'
    native=f"(SELECT contract_id FROM trading.side_schedule_bindings WHERE {rows} AND row_ordinal={february} AND port='market')"
    mutations=[
        (bindings+f"UPDATE trading.side_schedule_bindings b SET contract_id=p.contract_id FROM trading.side_schedule_bindings p WHERE {b_rows} AND b.row_ordinal={february} AND b.port='market' AND {p_rows} AND p.row_ordinal={january} AND p.port='market';",'P0001','gas_schedule_sources'),
        (bindings+f"UPDATE trading.side_schedule_bindings b SET(series_id,source_code,fixing_index_code,fixing_from,fixing_to,quote_code,coordinate)=(p.series_id,p.source_code,p.fixing_index_code,p.fixing_from,p.fixing_to,p.quote_code,p.coordinate) FROM trading.side_schedule_bindings p WHERE {b_rows} AND b.row_ordinal={february} AND b.port='fixing' AND {p_rows} AND p.row_ordinal={january} AND p.port='fixing';",'P0001','gas_schedule_sources'),
        (bindings+f"UPDATE trading.side_schedule_bindings b SET coordinate=p.coordinate FROM trading.side_schedule_bindings p WHERE {b_rows} AND b.row_ordinal={february} AND b.kind='gas_assignment' AND {p_rows} AND p.row_ordinal={january} AND p.kind='gas_assignment';",'P0001','gas_schedule_assignment'),
        ('ALTER TABLE market_data.curve_members DISABLE TRIGGER ALL;'+f"DELETE FROM market_data.curve_members WHERE contract_id={native};",'P0002',None),
        ('ALTER TABLE market_data.contracts DISABLE TRIGGER USER;ALTER TABLE market_data.curve_members DISABLE TRIGGER USER;'+f"WITH copy AS(SELECT(jsonb_populate_record(NULL::market_data.contracts,to_jsonb(c)||jsonb_build_object('id',gen_random_uuid()))).* FROM market_data.contracts c WHERE c.id={native}),saved AS(INSERT INTO market_data.contracts SELECT * FROM copy RETURNING *) INSERT INTO market_data.curve_members SELECT m.catalog_id,m.revision_id,m.curve_code,s.id,(SELECT max(ordinal)+1 FROM market_data.curve_members WHERE(catalog_id,revision_id,curve_code)=(m.catalog_id,m.revision_id,m.curve_code)) FROM saved s JOIN market_data.curve_members m ON m.contract_id={native};",'P0003',None),
    ]
    for mutation,code,message in mutations:
        guard=f"SQLSTATE<>'{code}'"+(f" OR SQLERRM<>'{message}'" if message else '')
        runtime_query(query,manifest,setup+f"DO $negative$ {declarations}BEGIN {initialize}BEGIN {validator}RAISE EXCEPTION 'invalid_native_source_admitted';EXCEPTION WHEN OTHERS THEN IF {guard} THEN RAISE;END IF;END;END$negative$;",prefix=grant+mutation,commit=False)
    # The historical private physical ABI remains single-month; composition has
    # an explicit forecast argument and its fixed cash slot can span the term.
    runtime_query(query,manifest,setup+f"DO $legacy$ {declarations}BEGIN {initialize}BEGIN PERFORM trading.require_gas_program_shape('{manifest['catalogId']}','{schedule}',0,'2021-01-31','2021-02-02','2021-02-02',2,NULL,a,d,start_at);RAISE EXCEPTION 'legacy_scope_widened';EXCEPTION WHEN raise_exception THEN IF SQLERRM<>'gas_schedule_geometry' THEN RAISE;END IF;END;END$legacy$;",prefix=f"GRANT EXECUTE ON FUNCTION {legacy} TO openetrm_runtime;",commit=False)
    assert query(f"SELECT has_function_privilege('openetrm_runtime','{signature}','EXECUTE'),has_function_privilege('openetrm_runtime','{legacy}','EXECUTE');")=='f|f'
    print('PASS whole-term native admission: positive control, wrong-month contract/fixing/assignment, missing/duplicate monthly coverage and unchanged legacy physical boundary',flush=True)
