from check_physical_operations import january_delivery_frame
"""Synthetic manual gas scheduling and independent statement imbalance lifecycle."""
from contextlib import contextmanager
from uuid import uuid4
from preview_client import PreviewClient, initial_frame
from preview_fixture import trade_booking
from synthetic_side_calendars import side_calendar_policy


def check_confirmation_agreements(client, query, manifest, booking):
    import base64
    import json
    from copy import deepcopy
    from check_booked_side_programs import runtime_query
    from preview_fixture import blank_agreement_pdf

    command = deepcopy(booking)
    command['operationId'] = str(uuid4())
    catalog = manifest['catalogId']
    parties = trade_booking()
    parties['counterpartyId'] = query(f"SELECT id FROM trading.counterparties WHERE catalog_id='{catalog}' AND code='party_2' AND revision=1")
    for leg in command['tickets']:
        leg['draft']['trade']['counterparty'] = parties['counterpartyId'] + '@1'
    for permission in ['ReadTrades', 'ManageAgreements']:
        query(f"INSERT INTO security.role_permissions SELECT id,'{permission}' FROM security.roles WHERE code='market_data_editor' ON CONFLICT DO NOTHING")
    agreement = dict(**parties, kind='agreement', code='gas_location_master', name='Synthetic gas location master',
        agreementType='gas_master', products=['physical_gas'], effectiveAt='2020-12-01T00:00:00Z', validTo=None,
        status='executed', procedure='written', provisions='Synthetic delivery terms', filename='terms.pdf',
        pdf=base64.b64encode(blank_agreement_pdf()).decode(), expectedRevision=0)
    added = json.loads(runtime_query(query, manifest, f"SELECT desk.record('{catalog}','{uuid4()}','{json.dumps(agreement)}'::jsonb);", profile='editor'))['id']
    applied = {}
    for confirmation in ['purchase', 'sale']:
        refusal = client.call('tickets/gas-locations/book', command, expected=422)
        assert refusal['code'] == 'desk_agreement_ambiguous' and refusal.get('confirmation') == confirmation, refusal
        choice = refusal['agreementSelection']
        assert len(choice['choices']) == 2
        applied[confirmation] = next(a['id'] for a in choice['choices'] if (a['id'] == added) == (confirmation == 'sale'))
        selection = {'operationId': choice['operationId'], 'agreementId': applied[confirmation]}
        changed = client.call('desk/choose', {**selection, 'expectedProfile': 'risk'}, expected=409)
        assert changed['code'] == 'desk_profile_changed'
        assert query(f"SELECT count(*) FROM desk.booking_choices WHERE catalog_id='{catalog}' AND operation_id='{choice['operationId']}'") == '0'
        client.call('desk/choose', {**selection, 'expectedProfile': 'trader'})
        assert client.call('tickets/gas-locations/status', {'requestId': command['operationId']}) == {'found': False}
    receipt = client.call('tickets/gas-locations/book', command)
    assert client.call('tickets/gas-locations/book', command) == receipt
    for side, agreement_id in applied.items():
        trade_id = receipt[side + 'TradeId']
        assert query(f"SELECT agreement_id FROM desk.admissions WHERE catalog_id='{catalog}' AND trade_id='{trade_id}' AND revision=1") == agreement_id
    print('PASS separate confirmations retain their chosen agreements and recover one group receipt', flush=True)


@contextmanager
def counted_revision_reads(query):
    """Counts the gas trade revisions on which runtime reads evaluate row security, across sessions."""
    query("""CREATE SEQUENCE public.synthetic_gas_revision_reads;
        CREATE FUNCTION public.synthetic_gas_revision_read() RETURNS boolean LANGUAGE plpgsql VOLATILE SECURITY DEFINER
        SET search_path=pg_catalog AS $count$BEGIN PERFORM nextval('public.synthetic_gas_revision_reads');RETURN true;END$count$;
        CREATE POLICY synthetic_gas_revision_read ON trading.gas_trade_revisions AS RESTRICTIVE FOR SELECT TO openetrm_runtime
        USING(public.synthetic_gas_revision_read());""")
    try:
        yield lambda: int(query("SELECT CASE WHEN is_called THEN last_value ELSE 0 END FROM public.synthetic_gas_revision_reads;"))
    finally:
        query("""DROP POLICY synthetic_gas_revision_read ON trading.gas_trade_revisions;
            DROP FUNCTION public.synthetic_gas_revision_read();DROP SEQUENCE public.synthetic_gas_revision_reads;""")


def run_checks(url, query, manifest):
    c=PreviewClient(url);c.bootstrap();c.call('profile',{'profile':'trader'})
    principal=query("SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='trader';")
    for role in ['gas_operator','gas_service_admin']:
        query(f"INSERT INTO security.role_grants SELECT '{uuid4()}','{principal}',id,'{manifest['tenantId']}','{manifest['workspaceId']}',clock_timestamp()-interval '1 day',clock_timestamp()+interval '1 hour',false FROM security.roles WHERE code='{role}';")
    workspace=c.call('workspace')
    before={'snapshotId':initial_frame(workspace,'2020-12-18')['id'],'convention':'physical_gas_fixed_quantity_v4','knowledgeMode':'current'}
    after={**before,'snapshotId':january_delivery_frame(workspace)}
    catalog=c.call('gas/catalog',before);point=next(p for p in catalog['locations'] if p['deliveryPoint']=='synthetic_gas_point_a')
    reference=next(item['reference'] for item in catalog['sideCalendars'] if item['code']=='test_utc_every_day')
    def details(trade_id):
        instrument=query(f"SELECT instrument_id FROM trading.gas_trade_revisions WHERE trade_id='{trade_id}' AND revision=1;")
        return c.call('trading/gas',{'context':{**before,'convention':'trading_workspace_v1'},'tradeId':trade_id,'bindingId':instrument,'revision':1,'instrumentType':'physical_gas'})

    # Both confirmation structures use native gas tickets and one retained group receipt.
    from copy import deepcopy
    other=next(p for p in catalog['locations'] if p['deliveryPoint']!=point['deliveryPoint'])
    parties=trade_booking()
    for agreements in ['one_agreement','separate_confirmations']:
        selection={'agreements':agreements,'purchasePrice':'fixed','salePrice':'fixed'}
        drafts=c.call('tickets/gas-locations/start',{'context':before,**selection})
        for draft in drafts:
            draft['trade']={'book':catalog['books'][0]['id'],'legal_entity':parties['legalEntityId']+'@1','counterparty':parties['counterpartyId']+'@1'}
            if catalog['gasSetupId']:draft['trade']['gas_setup']=catalog['gasSetupId']
            for side in draft['sides']:
                physical=side['kind']=='physical'
                side['calendarPolicy']=side_calendar_policy(reference,delivery=True,observation=False,payment=not physical)
                if physical:
                    location=point if side['code']=='purchase' else other
                    side['values'].update(pipeline=location['pipeline'],zone=location['zone'],delivery_point=location['deliveryPoint'],
                        product=location['product'],delivery_from='2021-01-10',delivery_to='2021-01-12',quantity_basis='per_operating_day',quantity='25')
                else:
                    side['values'].update(cash_share='1',cash_part='whole',cash_start='2021-01-10',cash_end='2021-01-12',cash_stage='delivery',cash_price='3',cash_payment_at='2021-02-02T00:00:00Z')
        legs=[{'draft':d,'definitionRevisionId':c.call('tickets/preview',{'context':before,'draft':d})['source']['definitionRevisionId']} for d in drafts]
        command={'context':before,'operationId':str(uuid4()),'tickets':legs,**selection}
        if agreements=='separate_confirmations':
            # Fail during the second native publication in this disposable database.
            import hashlib
            from uuid import UUID
            children=[str(UUID(hex=hashlib.sha256(f"gas_location_booking_child_v1:{command['operationId']}:{i}".encode()).hexdigest()[:32])) for i in range(2)]
            query(f'''CREATE FUNCTION trading.test_location_second_refusal() RETURNS trigger LANGUAGE plpgsql AS $test$
                BEGIN RAISE EXCEPTION 'gas_conflict';END$test$;
                CREATE TRIGGER test_location_second_refusal BEFORE INSERT ON trading.gas_trade_revisions
                FOR EACH ROW WHEN(NEW.operation_id='{children[1]}') EXECUTE FUNCTION trading.test_location_second_refusal();''')
            try:
                c.call('tickets/gas-locations/book',command,expected=409)
                assert query(f"SELECT count(*) FROM trading.gas_trade_revisions WHERE operation_id IN('{children[0]}','{children[1]}')")=='0'
            finally:
                query('DROP TRIGGER test_location_second_refusal ON trading.gas_trade_revisions;DROP FUNCTION trading.test_location_second_refusal()')
        receipt=c.call('tickets/gas-locations/book',command)
        assert (receipt['purchaseTradeId']==receipt['saleTradeId'])==(agreements=='one_agreement')
        assert receipt==c.call('tickets/gas-locations/book',command)==c.call('tickets/gas-locations/status',{'requestId':command['operationId']})
        another=PreviewClient(url);another.bootstrap();another.call('profile',{'profile':'risk'})
        assert another.call('tickets/gas-locations/status',{'requestId':command['operationId']})=={'found':False}
        for trade_id in set((receipt['purchaseTradeId'],receipt['saleTradeId'])):
            [link]=details(trade_id)['confirmations']
            assert link['purchaseTradeId']==receipt['purchaseTradeId'] and link['saleTradeId']==receipt['saleTradeId']
        amended=deepcopy(command)
        amended['tickets'][0]['draft']['sides'][0]['values']['quantity']='26'
        c.call('tickets/gas-locations/book',amended,expected=409)
        malformed=deepcopy(command);malformed['operationId']=str(uuid4())
        next(s for ticket in malformed['tickets'] for s in ticket['draft']['sides'] if s['code']=='sale_payment')['values']['cash_source']='purchase'
        c.call('tickets/gas-locations/book',malformed,expected=422)
        assert c.call('tickets/gas-locations/status',{'requestId':malformed['operationId']})=={'found':False}
        if agreements=='separate_confirmations':
            check_confirmation_agreements(c,query,manifest,command)
    terms={**before,**trade_booking(),'operationId':str(uuid4()),'bookId':catalog['books'][0]['id'],'definitionRevisionId':point['definitionRevisionId'],'gasSetupId':catalog['gasSetupId'],'discounting':False,'profile':'fixed_gas_composition_v1',
      'physicalSides':[{'code':'receipt','pipeline':point['pipeline'],'zone':point['zone'],'deliveryPoint':point['deliveryPoint'],'product':point['product'],'direction':'receive','deliveryFrom':'2021-01-01','deliveryTo':'2021-01-03','quantityBasis':'per_operating_day','quantity':'100','link':None,'calendarPolicy':side_calendar_policy(reference,delivery=True,observation=False,payment=False)}],
      'cashSides':[{'code':'payment','role':'consideration','share':'1','direction':'pay','source':'receipt','stage':'delivery','price':'3','tariffCode':None,'paymentAt':'2021-02-02T00:00:00Z','calendarPolicy':side_calendar_policy(reference,delivery=True,observation=False,payment=True)}]}
    # A position's details show its own location booking. Trades outside that booking add no revision reads.
    with counted_revision_reads(query) as revisions_read:
        def linked_details():
            started=revisions_read();shown=details(receipt['saleTradeId'])['confirmations']
            return shown,revisions_read()-started
        confirmations,reads=linked_details()
        booked=[c.call('gas/composition/book',{**terms,'operationId':str(uuid4())}) for _ in range(2)]
        unchanged,reads_after=linked_details()
        assert unchanged==confirmations and reads_after<=reads,(reads,reads_after)
    print(f'PASS a linked gas position reads {reads_after} trade revisions for its details; two unrelated bookings add none',flush=True)
    trades=[b['tradeId'] for b in booked];day='2021-01-01'
    obligations=[c.call('gas/quantities/read',dict(context=after,tradeId=t,selectionPolicy='operational_projection_v1'))['rows'][0]['obligationId'] for t in trades]
    def state(trade=trades[0],context=after):
        return c.call('gas/operations/read',dict(context=context,tradeId=trade,day=day))
    def request(kind,details,trade=trades[0],context=after):
        return dict(context=context,tradeId=trade,expectedRevision=1,day=day,kind=kind,details=details)
    def reviewed(p):
        review=c.call('gas/operations/preview',p)
        return dict(preview=p,operationId=str(uuid4()),previewHash=review['previewHash'])
    def write(p):
        body=reviewed(p);path='gas/operations/configure' if p['kind']=='service' else 'gas/operations/record'
        result=c.call(path,body)
        assert c.call(path,body)==result
        return result,body
    accounts=[]
    for i,holder in enumerate(['legal_entity','counterparty']):
        result,_=write(request('service',dict(obligationId=obligations[i],holderRole=holder,externalReference='synthetic-service-'+str(i),expectedPreviousId=None,validFrom='2021-01-01',validTo='2021-02-01',active=True,statementPolicy='final_statement_balance_v1',termsReference='Synthetic manual service agreement',evidence='Synthetic reviewed service scope'),trades[i]))
        accounts.append(next(a for a in state(trades[i])['state']['accounts'] if a['id']==result['accountId']))
    def nomination(ref,quantity='100',submitted='2020-12-31T12:00:00Z'):
        return request('nomination',dict(accountId=accounts[0]['id'],serviceVersionId=accounts[0]['version_id'],externalReference=ref,expectedPreviousId=None,quantity=quantity,submittedAt=submitted,channel='portal',reason='Synthetic scheduling request',evidence='Synthetic manual submission evidence'))
    for ref,submitted,phase in [('before','2020-12-31T12:00:00Z','before_flow'),('during','2021-01-01T12:00:00Z','intraday'),('retro','2021-01-03T12:00:00Z','retro')]:
        p=nomination('synthetic-'+ref,submitted=submitted);assert c.call('gas/operations/preview',p)['review']['phase']==phase
    n,ncommand=write(nomination('synthetic-nomination'))
    assert c.call('gas/operations/status',dict(operationId=ncommand['operationId']))==n
    def response(parent,kind,side,quantity,decision='approved',previous=None):
        return request('response',dict(nominationId=parent if kind=='nomination' else None,imbalanceId=parent if kind=='imbalance' else None,side=side,decision=decision,quantity=quantity,externalReference='synthetic-'+side,respondedAt='2021-01-10T00:00:00Z',expectedPreviousId=previous,evidence='Synthetic '+side+' response'))
    apply=request('nomination_apply',dict(nominationId=n['id'],counterpartyResponseId=None,pipelineResponseId=None,expectedPreviousId=None,profile='manual_confirmed_ceiling_v1',evidence='Synthetic scheduling adoption'))
    c.call('gas/operations/preview',apply,expected=422)
    cp,_=write(response(n['id'],'nomination','counterparty','95'))
    pipe,_=write(response(n['id'],'nomination','pipeline','90'))
    # Received acknowledgments have not changed confirmed/scheduled tracks.
    heads=state()['state']['heads'];assert not any(h['kind']=='scheduled' for h in heads)
    apply['details'].update(counterpartyResponseId=cp['id'],pipelineResponseId=pipe['id'])
    applied,_=write(apply)
    heads=state()['state']['heads'];assert next(h for h in heads if h['kind']=='scheduled')['quantity']=='90'
    operating={'effectiveOn':'2021-01-31','knowledgeMode':'current','retained':None,'convention':'facility_business_date_end_utc_v1'}
    def report(client=c,context=None):
        return client.call('gas/pipeline-outlook',{'context':context or operating,'from':day,'to':'2021-01-02','period':'day'})
    def outlook():
        return report()['rows']
    original=report()
    before_renomination=original['context']['knownAt']
    waiting=next(b for b in original['balances'] if b['accountId']==accounts[0]['id'])
    assert waiting['amount'] is None and 'Awaiting' in waiting['reason']
    assert any(s['scheduled'] is None for s in original['sources'] if s['tradeId']==trades[1])
    submitted_sources=[s for s in original['sources'] if s['submitted'] is not None]
    assert len(submitted_sources)==1 and submitted_sources[0]['tradeId']==trades[0],submitted_sources
    assert [(r['externalReference'],r['accountId']) for r in submitted_sources[0]['requests']]==[('synthetic-nomination',accounts[0]['id'])]
    assert all(s['requests'] is None for s in original['sources'] if s['tradeId']!=trades[0])
    row=next(r for r in outlook() if r['path']['point']==point['deliveryPoint'] and r['path']['direction']=='receive')
    assert row['scheduled']['reportedQuantity']=='90' and row['nominated']['reportedQuantity']=='100' and row['submitted']['reportedQuantity']=='100',row
    retained=state()['context'];historic={**after,'knowledgeMode':'retained','contextId':retained['contextId']}
    # A newer nominated request prevents applying stale external scheduling evidence.
    later,_=write(nomination('synthetic-new-request','80'))
    c.call('gas/operations/preview',apply,expected=409)
    assert c.call('gas/operations/record',{**ncommand,'operationId':str(uuid4())})['id']==n['id']
    alias={**ncommand,'operationId':str(uuid4()),'preview':{**ncommand['preview'],'details':{**ncommand['preview']['details'],'quantity':'100.000','submittedAt':'2020-12-31T12:00:00+00:00','accountId':accounts[0]['id'].upper()}}}
    assert c.call('gas/operations/record',alias)['id']==n['id']
    assert next(h for h in state()['state']['heads'] if h['kind']=='nominated')['quantity']=='80'
    row=next(r for r in outlook() if r['path']['point']==point['deliveryPoint'] and r['path']['direction']=='receive')
    assert row['nominated']['reportedQuantity']=='80' and row['scheduled']['reportedQuantity']=='90' and row['submitted']['reportedQuantity']=='80',row
    frozen_client=PreviewClient(url);frozen_client.bootstrap();frozen_client.call('profile',{'profile':'trader'})
    frozen=frozen_client.call('market/context',{'effectiveOn':operating['effectiveOn'],'knownAt':before_renomination,'mode':'at','selection':'latest_correction'})['context']
    frozen_row=next(r for r in report(frozen_client,{**operating,'knowledgeMode':'retained','retained':frozen})['rows']
                    if r['path']==row['path'])
    assert frozen_row['submitted']['reportedQuantity']=='100' and frozen_row['nominated']['reportedQuantity']=='100' and frozen_row['scheduled']['reportedQuantity']=='90'
    before_booking=report(context={**operating,'effectiveOn':'2020-12-01'})
    assert not any(s['tradeId'] in trades for s in before_booking['sources'])
    statements=[]
    def statement(i,balance,previous=None,included=None):
        return request('statement',dict(accountId=accounts[i]['id'],serviceVersionId=accounts[i]['version_id'],externalReference='synthetic-statement-'+str(i),expectedPreviousId=previous,balance=balance,status='final',reportedAt='2021-01-12T00:00:00Z',includedMovementId=included,evidence='Synthetic signed operator statement'),trades[i])
    for i,amount in enumerate(['-200','250']):
        result,_=write(statement(i,amount));statements.append(result['id'])
    agreement=request('imbalance',dict(buyerAccountId=accounts[0]['id'],buyerVersionId=accounts[0]['version_id'],sellerAccountId=accounts[1]['id'],sellerVersionId=accounts[1]['version_id'],externalReference='synthetic-imbalance-agreement',quantity='200',price='3',agreedAt='2021-01-09T00:00:00Z',dueAt='2021-02-02T00:00:00Z',decimalPlaces=2,roundingPolicy='currency_minor_units_to_even_v1',profile='paired_offsetting_imbalance_v1',evidence='Synthetic agreed fixed-price imbalance transfer'))
    proposed=c.call('gas/operations/preview',agreement);assert proposed['review']['cash']=='-600'
    t,_=write(agreement)
    write(response(t['id'],'imbalance','counterparty','200'));write(response(t['id'],'imbalance','pipeline','200'))
    transfer=request('imbalance_apply',dict(imbalanceId=t['id'],evidence='Synthetic accepted paired transfer'))
    command=reviewed(transfer)
    import json
    from check_booked_side_programs import runtime_query
    from native_read_transport import select_context_inputs
    certificate_inputs=c.call('gas/operations/preview',transfer)['context']
    certificate=certificate_inputs['contextId']
    runtime_query(query,manifest,select_context_inputs(manifest['catalogId'],certificate_inputs))
    snapshot=state()['state'];acks=snapshot['responses']
    basis=dict(counterpartyId=next(r['id'] for r in acks if r['imbalance_id']==t['id'] and r['side']=='counterparty'),pipelineId=next(r['id'] for r in acks if r['imbalance_id']==t['id'] and r['side']=='pipeline'),buyerStatementId=statements[0],sellerStatementId=statements[1],buyerMovementId=None,sellerMovementId=None,cash='-600',numerator='-600',denominator='1')
    encoded=json.dumps(command).replace("'","''")
    for numerator,denominator in [('NaN','NaN'),('Infinity','Infinity'),('1.5','1'),('1','1e121')]:
        calc=json.dumps({**basis,'numerator':numerator,'denominator':denominator}).replace("'","''")
        runtime_query(query,manifest,f"DO $$BEGIN BEGIN PERFORM security.record_gas_operations('{manifest['catalogId']}','{encoded}'::jsonb,'2021-01-15T08:00:00Z','{certificate}','{calc}'::jsonb);RAISE EXCEPTION 'invalid_exact_cash_accepted';EXCEPTION WHEN raise_exception THEN IF SQLERRM<>'gas_operations_amount' THEN RAISE;END IF;END;END$$;")
    historical={**command,'preview':{**transfer,'context':historic}}
    raw=json.dumps(historical).replace("'","''")
    runtime_query(query,manifest,f"DO $$BEGIN BEGIN PERFORM security.record_gas_operations('{manifest['catalogId']}','{raw}'::jsonb,'2021-01-15T08:00:00Z','{certificate}','{{}}');RAISE EXCEPTION 'historical_write_accepted';EXCEPTION WHEN raise_exception THEN IF SQLERRM<>'historical_read_only' THEN RAISE;END IF;END;END$$;")
    from check_gas_operations_authority import check as check_authority
    check_authority(query,manifest,ncommand,command,basis,certificate)
    from concurrent.futures import ThreadPoolExecutor
    other=PreviewClient(url);other.bootstrap();other.call('profile',{'profile':'trader'})
    other_context={**after,'snapshotId':january_delivery_frame(other.call('workspace'))}
    other_preview={**transfer,'context':other_context}
    other_review=other.call('gas/operations/preview',other_preview)
    other_command={**command,'operationId':str(uuid4()),'preview':other_preview,'previewHash':other_review['previewHash']}
    with ThreadPoolExecutor(max_workers=2) as pool:
        results=list(pool.map(lambda pair:pair[0].call('gas/operations/record',pair[1]),[(c,command),(other,other_command)]))
    accepted=results[0];assert results[1]['id']==accepted['id']
    assert accepted['cash']=='-600' and accepted['buyerBalance']=='0' and accepted['sellerBalance']=='50'
    assert len(state()['state']['imbalanceApplications'])==1
    assert len(state()['state']['movements'])==2
    seller_move=next(m for m in state()['state']['movements'] if m['account_id']==accounts[1]['id'])
    buyer_move=next(m for m in state()['state']['movements'] if m['account_id']==accounts[0]['id'])
    c.call('gas/operations/preview',statement(1,'50',statements[1],buyer_move['id']),expected=422)
    # A net statement with explicit included prefix does not double-count the transfer.
    net,_=write(statement(1,'50',statements[1],seller_move['id']))
    assert next(b for b in state()['balances'] if b['accountId']==accounts[1]['id'])['amount']=='50'
    # Correct an earlier pre-transfer number: accepted movements remain consumed.
    write(statement(1,'150',net['id']))
    assert next(b for b in state()['balances'] if b['accountId']==accounts[1]['id'])['amount']=='-50'
    assert next(b for b in report()['balances'] if b['accountId']==accounts[1]['id'])['amount']=='-50'
    c.call('gas/operations/preview',transfer,expected=422)
    assert c.call('gas/operations/record',command)['id']==accepted['id']
    assert len(state(context=historic)['state']['nominations'])==1
    c.call('gas/operations/record',{**command,'operationId':str(uuid4()),'preview':{**transfer,'context':historic}},expected=422)
    service=request('service',dict(obligationId=obligations[0],holderRole='legal_entity',externalReference='synthetic-service-0',expectedPreviousId=accounts[0]['version_id'],validFrom='2021-01-01',validTo='2021-02-01',active=False,statementPolicy='final_statement_balance_v1',termsReference='Synthetic retired service terms',evidence='Synthetic reviewed retirement'))
    write(service)
    original_cp=next(r for r in state()['state']['responses'] if r['imbalance_id']==t['id'] and r['side']=='counterparty')
    write(response(t['id'],'imbalance','counterparty',None,'rejected',original_cp['id']))
    assert state()['state']['imbalanceApplications'][0]['id']==accepted['id']
    runtime_query(query,manifest,f"DO $$BEGIN IF EXISTS(SELECT 1 FROM trading.gas_service_accounts) OR EXISTS(SELECT 1 FROM trading.gas_balance_statements) OR EXISTS(SELECT 1 FROM trading.gas_account_movements) THEN RAISE EXCEPTION 'private_gas_operations_leaked';END IF;END$$;",profile='risk',prefix="CREATE POLICY gas_ops_test_wide ON trading.gas_balance_statements FOR SELECT TO openetrm_runtime USING(true);",commit=False)
    runtime_query(query,manifest,f"DO $$BEGIN IF security.trading_access('{manifest['catalogId']}','ReadTrades') IS NOT TRUE THEN RAISE EXCEPTION 'ordinary_trade_reader_required';END IF;IF security.gas_movement_visible('{manifest['catalogId']}','{accepted['id']}','{accounts[0]['id']}') IS NOT FALSE THEN RAISE EXCEPTION 'private_movement_metadata_leaked';END IF;END$$;",profile='risk')
    # Immutable facts, exact prefix FK and independent permissions are exercised on stock PG.
    query("DO $$BEGIN UPDATE trading.gas_balance_statements SET balance=42;RAISE EXCEPTION 'mutable statement';EXCEPTION WHEN SQLSTATE '55000' THEN NULL;END$$;")
    c.call('profile',{'profile':'risk'})
    public_context={**after,'snapshotId':january_delivery_frame(c.call('workspace'))}
    public=c.call('gas/quantities/read',dict(context=public_context,tradeId=trades[0],selectionPolicy='operational_projection_v1'))
    assert all('synthetic-service' not in h['evidence'] and 'Synthetic pipeline response' not in h['evidence'] for row in public['rows'] for h in row['heads'])
    refused=c.call('gas/operations/read',dict(context=public_context,tradeId=trades[0],day=day),expected=403)
    assert refused['code']=='access_denied' and 'gas operations' in refused['message'] and 'service terms' not in refused['message'],refused
    c.call('profile',{'profile':'trader'})
    current_account=next(a for a in state()['state']['accounts'] if a['id']==accounts[0]['id'])
    write({**service,'details':{**service['details'],'expectedPreviousId':current_account['version_id'],'active':True,'evidence':'Synthetic reviewed service reactivation'}})
    from check_physical_operations import directory_checks
    directory_checks(c,after,query,trades[0],manifest)
    print('PASS gas operations: service scope, before/intra/retro, scheduling cuts, explicit adoption, stale heads, statement coverage, paired transfer, cash, concurrency, revisions, history and recovery')
    return dict(tradeId=trades[0],bindingId=booked[0]['bindingId'] if 'bindingId' in booked[0] else query(f"SELECT instrument_id FROM trading.gas_trade_revisions WHERE trade_id='{trades[0]}' AND revision=1;"),context=after,accountId=accounts[0]['id'])
