"""Synthetic daily delivery facts and independently specified selection oracles."""
from copy import deepcopy
from uuid import uuid4
from preview_client import PreviewClient,initial_frame
from preview_fixture import trade_booking


def assert_batch_equivalence(query,manifest,trade,context,at,include_history=True):
    from check_booked_side_programs import runtime_query
    bind=f"SELECT security.use_market_context('{manifest['catalogId']}','{context}');SELECT security.use_trading_context('{manifest['catalogId']}','{at}');"
    equivalence=f"""DO $test$ DECLARE expected jsonb;actual jsonb;BEGIN
      SELECT coalesce(jsonb_agg(to_jsonb(x) ORDER BY x.obligation_id,x.kind,x.day),'[]'::jsonb) INTO expected FROM(
       SELECT DISTINCT ON(s.obligation_id,s.kind,r.day) r.*,s.obligation_id,s.kind,s.stage,s.source
       FROM trading.gas_quantity_revisions r JOIN trading.gas_quantity_series s ON(s.catalog_id,s.id)=(r.catalog_id,r.series_id)
       JOIN trading.gas_delivery_obligations o ON(o.catalog_id,o.id)=(s.catalog_id,s.obligation_id)
       WHERE(o.catalog_id,o.trade_id)=('{manifest['catalogId']}','{trade}') ORDER BY s.obligation_id,s.kind,r.day,r.revision DESC)x;
      actual=security.read_gas_quantity_rows('{manifest['catalogId']}','{trade}','heads');IF actual IS DISTINCT FROM expected THEN RAISE EXCEPTION 'batch_heads_differ_from_rls';END IF;
      SELECT coalesce(jsonb_agg(to_jsonb(x) ORDER BY x.publication_order DESC),'[]'::jsonb) INTO expected FROM(
       SELECT r.*,s.kind,o.id obligation_id,m.code side_code FROM trading.gas_quantity_revisions r
       JOIN trading.gas_quantity_series s ON(s.catalog_id,s.id)=(r.catalog_id,r.series_id)
       JOIN trading.gas_delivery_obligations o ON(o.catalog_id,o.id)=(s.catalog_id,s.obligation_id)
       JOIN trading.gas_component_terms m ON(m.catalog_id,m.instrument_id,m.slot)=(o.catalog_id,o.instrument_id,o.slot)
       WHERE(o.catalog_id,o.trade_id)=('{manifest['catalogId']}','{trade}') ORDER BY r.publication_order DESC LIMIT 100)x;
      actual=security.read_gas_quantity_rows('{manifest['catalogId']}','{trade}','history');IF actual IS DISTINCT FROM expected THEN RAISE EXCEPTION 'batch_history_differs_from_rls';END IF;
      END$test$;"""
    if not include_history:
        equivalence=equivalence[:equivalence.index('SELECT coalesce(jsonb_agg(to_jsonb(x) ORDER BY x.publication_order')]+"END$test$;"
    runtime_query(query,manifest,bind+equivalence)


def run_checks(url,query=None,manifest=None):
    c=PreviewClient(url);c.bootstrap();c.call('profile',{'profile':'trader'});workspace=c.call('workspace')
    before={'snapshotId':initial_frame(workspace,'2020-12-18')['id'],'convention':'physical_gas_fixed_quantity_v4','knowledgeMode':'current'}
    after={**before,'snapshotId':next(f['id'] for f in workspace['frames'] if f['at'].startswith('2021-01-15T08:'))}
    cat=c.call('gas/catalog',before);point=next(p for p in cat['locations'] if p['deliveryPoint']=='synthetic_gas_point_a')
    terms={**before,**trade_booking(),'operationId':str(uuid4()),'bookId':cat['books'][0]['id'],'definitionRevisionId':point['definitionRevisionId'],'gasSetupId':cat['gasSetupId'],'discounting':False,'profile':'fixed_gas_composition_v1',
      'physicalSides':[{'code':'receipt','pipeline':point['pipeline'],'zone':point['zone'],'deliveryPoint':point['deliveryPoint'],'product':point['product'],'direction':'receive','deliveryFrom':'2021-01-01','deliveryTo':'2021-01-02','quantityBasis':'period_total_equal_days','quantity':'1000','link':None}],
      'cashSides':[{'code':'payment','role':'consideration','direction':'pay','source':'receipt','stage':'delivery','price':'3','tariffCode':None,'paymentAt':'2021-02-02T00:00:00Z'}]}
    prebooking=c.call('gas/catalog',after)['context']['contextId']
    booked=c.call('gas/composition/book',terms)
    def read(context=after,policy='operational_projection_v1'):
        return c.call('gas/quantities/read',{'context':context,'tradeId':booked['tradeId'],'selectionPolicy':policy})
    original=read();assert original['rows'][0]['contractual']=={'numerator':'1000','denominator':'1'}
    retained={**after,'knowledgeMode':'retained','contextId':original['context']['contextId']}
    obligation=original['rows'][0]['obligationId']
    def command(kind,amount,status='provisional',previous=None,context=after):
        return {'context':context,'tradeId':booked['tradeId'],'expectedRevision':1,'operationId':str(uuid4()),'obligationId':obligation,'kind':kind,'status':status,'day':'2021-01-01','quantity':amount,'evidence':'Synthetic daily delivery statement','expectedPreviousId':previous}
    nominated=command('nominated','990')
    n=c.call('gas/quantities/record',nominated)
    assert c.call('gas/quantities/record',nominated)==n
    assert c.call('gas/quantities/status',{'operationId':nominated['operationId']})==n
    assert read()['rows'][0]['selected']=={'numerator':'990','denominator':'1'}
    scheduled=c.call('gas/quantities/record',command('scheduled','985'))
    assert read()['rows'][0]['selected']=={'numerator':'985','denominator':'1'}
    c.call('gas/quantities/read',{'context':after,'tradeId':booked['tradeId'],'selectionPolicy':'final_allocated_billing_v1'},expected=422)
    allocation=command('allocated','980','final');a=c.call('gas/quantities/record',allocation)
    allocation_context=read()['context']['contextId']
    selected=read(policy='final_allocated_billing_v1')['rows'][0]
    assert selected['selected']=={'numerator':'980','denominator':'1'} and selected['normalizedInput']=={'numerator':'49','denominator':'50'}
    assert selected['revisionId']==a['revisionId']
    corrected=command('allocated','990','final',a['revisionId']);correction=c.call('gas/quantities/record',corrected)
    assert c.call('gas/quantities/record',allocation)==a
    same=c.call('gas/quantities/record',{**corrected,'operationId':str(uuid4())})
    assert same['unchanged'] and same['revisionId']==correction['revisionId']
    c.call('gas/quantities/record',command('allocated','995','final',a['revisionId']),expected=409)
    c.call('gas/quantities/record',{**corrected,'quantity':'997'},expected=409)
    assert read(retained)['rows'][0]['selected']=={'numerator':'1000','denominator':'1'}
    assert len(read()['history'])==4
    lean=c.call('gas/quantities/read',{'context':after,'tradeId':booked['tradeId'],'selectionPolicy':'operational_projection_v1','includeHistory':False})
    assert lean['history']==[] and lean['rows']==read()['rows']
    journal=c.call('gas/quantities/history',{'context':after,'tradeId':booked['tradeId']})
    assert journal['history']==read()['history']
    assert c.call('gas/quantities/history',{'context':retained,'tradeId':booked['tradeId']})['history']==[]

    zero=c.call('gas/quantities/record',command('allocated','0','final',correction['revisionId']))
    assert read(policy='final_allocated_billing_v1')['rows'][0]['selected']=={'numerator':'0','denominator':'1'}
    c.call('gas/quantities/record',command('allocated',None,'withdrawn',zero['revisionId']))
    assert read()['rows'][0]['selected']=={'numerator':'985','denominator':'1'}
    for mutation in ['interval','future','negative','obligation','revision','status']:
        bad=command('allocated','980','final')
        if mutation=='interval':bad['day']='2021-01-03'
        if mutation=='future':bad['context']=before
        if mutation=='negative':bad['quantity']='-1'
        if mutation=='obligation':bad['obligationId']=str(uuid4())
        if mutation=='revision':bad['expectedRevision']=2
        if mutation=='status':bad.update(kind='nominated',status='final')
        c.call('gas/quantities/record',bad,expected=409 if mutation=='revision' else 422)
    unchanged=c.call('gas/composition/editor',{'context':before,'tradeId':booked['tradeId']})
    assert unchanged['terms']['physicalSides']==terms['physicalSides'] and unchanged['valuation']['presentValue']=='200'
    c.call('gas/quantities/record',command('scheduled','980',previous=scheduled['revisionId'],context=retained),expected=422)
    c.call('gas/quantities/record',command('scheduled','985',previous=scheduled['revisionId'],context=before),expected=409)
    from concurrent.futures import ThreadPoolExecutor
    clients=[PreviewClient(url),PreviewClient(url)];attempts=[]
    for client,amount in zip(clients,['981','982']):
        client.bootstrap();client.call('profile',{'profile':'trader'});own=client.call('workspace')
        ctx={**after,'snapshotId':next(f['id'] for f in own['frames'] if f['at'].startswith('2021-01-15T08:'))}
        attempts.append((client,command('scheduled',amount,previous=scheduled['revisionId'],context=ctx)))
    def record(attempt):
        client,body=attempt
        try:return client.call('gas/quantities/record',body)
        except AssertionError as error:
            if 'got 409:' not in str(error):raise
            return None
    with ThreadPoolExecutor(max_workers=2) as pool:outcomes=list(pool.map(record,attempts))
    assert sum(o is not None for o in outcomes)==1

    two_day=deepcopy(terms);two_day['operationId']=str(uuid4());two_day['physicalSides'][0].update(deliveryTo='2021-01-03',quantity='2000')
    other=c.call('gas/composition/book',two_day)
    other_grid=c.call('gas/quantities/read',{'context':after,'tradeId':other['tradeId'],'selectionPolicy':'operational_projection_v1'})
    first=command('nominated','990');first.update(tradeId=other['tradeId'],obligationId=other_grid['rows'][0]['obligationId'])
    c.call('gas/quantities/record',first)
    earlier={**after,'snapshotId':next(f['id'] for f in workspace['frames'] if f['at'].startswith('2021-01-01T08:'))}
    second={**first,'operationId':str(uuid4()),'day':'2021-01-02','context':earlier}
    c.call('gas/quantities/record',second,expected=409)
    assert other_grid['rows'][0]['contractual']=={'numerator':'1000','denominator':'1'}

    clone=deepcopy(terms);clone['operationId']=str(uuid4());clone_trade=c.call('gas/composition/book',clone)
    clone_grid=c.call('gas/quantities/read',{'context':after,'tradeId':clone_trade['tradeId'],'selectionPolicy':'operational_projection_v1'})
    clone_command=command('nominated','777');clone_command.update(tradeId=clone_trade['tradeId'],obligationId=clone_grid['rows'][0]['obligationId'])
    c.call('gas/quantities/record',clone_command)
    assert read()['rows'][0]['selected']['numerator'] in ('981','982')
    assert c.call('gas/quantities/read',{'context':after,'tradeId':clone_trade['tradeId'],'selectionPolicy':'operational_projection_v1'})['rows'][0]['selected']=={'numerator':'777','denominator':'1'}
    earlier_effective_context=read(before)['context']['contextId']
    assert read(before)['rows'][0]['selected']=={'numerator':'1000','denominator':'1'}
    latest_context=read()['context']['contextId']
    c.call('profile',{'profile':'risk'});c.call('gas/quantities/record',command('scheduled','980'),expected=403)
    c.call('profile',{'profile':'viewer'});c.call('gas/quantities/history',{'context':after,'tradeId':booked['tradeId']},expected=403);c.call('gas/quantities/read',{'context':after,'tradeId':booked['tradeId'],'selectionPolicy':'operational_projection_v1'},expected=403)
    if query:
        from check_booked_side_programs import runtime_query
        import json
        invalid=command('confirmed','980');encoded=json.dumps(invalid).replace("'","''")
        runtime_query(query,manifest,f"DO $$BEGIN BEGIN PERFORM security.record_gas_daily_quantity('{manifest['catalogId']}','{encoded}'::jsonb,'2021-01-15T08:00:00Z','{prebooking}');RAISE EXCEPTION 'prebooking_context_accepted';EXCEPTION WHEN raise_exception THEN IF SQLERRM<>'gas_quantity_context' THEN RAISE;END IF;END;END$$;")
        tables=['gas_delivery_obligations','gas_quantity_series','gas_quantity_revisions']
        policies=''.join(f'CREATE POLICY daily_test_wide ON trading.{t} FOR SELECT TO openetrm_runtime USING(true);' for t in tables)
        empty=' OR '.join(f'EXISTS(SELECT 1 FROM trading.{t})' for t in tables)
        runtime_query(query,manifest,f"DO $$BEGIN IF {empty} THEN RAISE EXCEPTION 'daily_unbound_scope_bypassed';END IF;END$$;",profile='viewer',prefix=policies,commit=False)
        bind=f"SELECT security.use_market_context('{manifest['catalogId']}','{original['context']['contextId']}');SELECT security.use_trading_context('{manifest['catalogId']}','2021-01-15T08:00:00Z');"
        runtime_query(query,manifest,bind+"DO $$BEGIN IF EXISTS(SELECT 1 FROM trading.gas_quantity_revisions) OR EXISTS(SELECT 1 FROM trading.gas_quantity_series) THEN RAISE EXCEPTION 'daily_cutoff_bypassed';END IF;END$$;",prefix=policies,commit=False)
        runtime_query(query,manifest,f"DO $$BEGIN BEGIN PERFORM security.read_gas_quantity_rows('{manifest['catalogId']}','{booked['tradeId']}','heads');RAISE EXCEPTION 'unbound_batch_read_accepted';EXCEPTION WHEN raise_exception THEN IF SQLERRM<>'gas_quantity_context' THEN RAISE;END IF;END;END$$;")
        runtime_query(query,manifest,bind+f"DO $$BEGIN IF security.read_gas_quantity_rows('{manifest['catalogId']}','{booked['tradeId']}','heads')<>'[]'::jsonb OR security.read_gas_quantity_rows('{manifest['catalogId']}','{booked['tradeId']}','history')<>'[]'::jsonb THEN RAISE EXCEPTION 'batch_cutoff_bypassed';END IF;END$$;",prefix=policies,commit=False)
        current_bind=f"SELECT security.use_market_context('{manifest['catalogId']}','{latest_context}');SELECT security.use_trading_context('{manifest['catalogId']}','2021-01-15T08:00:00Z');"
        for context,at in [(latest_context,'2021-01-15T08:00:00Z'),(allocation_context,'2021-01-15T08:00:00Z'),(earlier_effective_context,'2020-12-18T16:00:00Z')]:
            assert_batch_equivalence(query,manifest,booked['tradeId'],context,at)
        runtime_query(query,manifest,f"DO $$BEGIN BEGIN PERFORM security.read_gas_quantity_rows('{manifest['catalogId']}','{booked['tradeId']}','heads');RAISE EXCEPTION 'unauthorized_batch_read_accepted';EXCEPTION WHEN insufficient_privilege THEN NULL;END;END$$;",profile='viewer')
        for invalid_trade,invalid_mode,error_code in [(str(uuid4()),'heads','gas_quantity_trade'),(booked['tradeId'],'unknown','gas_quantity_context')]:
            runtime_query(query,manifest,current_bind+f"DO $$BEGIN BEGIN PERFORM security.read_gas_quantity_rows('{manifest['catalogId']}','{invalid_trade}','{invalid_mode}');RAISE EXCEPTION 'invalid_batch_read_accepted';EXCEPTION WHEN raise_exception THEN IF SQLERRM<>'{error_code}' THEN RAISE;END IF;END;END$$;")
        query("DO $$BEGIN UPDATE trading.gas_quantity_revisions SET quantity=42;RAISE EXCEPTION 'Expected immutable facts';EXCEPTION WHEN SQLSTATE '55000' THEN NULL;END$$;")
    print('PASS daily gas quantities: retained day binding, contract unchanged, absolute/native normalization, selection, finality, zero, withdrawal, correction, certified history, replay and authority',flush=True)
