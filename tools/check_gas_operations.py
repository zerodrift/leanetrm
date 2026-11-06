from check_physical_operations import january_delivery_frame
"""Synthetic manual gas scheduling and independent statement imbalance lifecycle."""
from uuid import uuid4
from preview_client import PreviewClient, initial_frame
from preview_fixture import trade_booking


def run_checks(url, query, manifest):
    c=PreviewClient(url);c.bootstrap();c.call('profile',{'profile':'trader'})
    principal=query("SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='trader';")
    for role in ['gas_operator','gas_service_admin']:
        query(f"INSERT INTO security.role_grants SELECT '{uuid4()}','{principal}',id,'{manifest['tenantId']}','{manifest['workspaceId']}',clock_timestamp()-interval '1 day',clock_timestamp()+interval '1 hour',false FROM security.roles WHERE code='{role}';")
    workspace=c.call('workspace')
    before={'snapshotId':initial_frame(workspace,'2020-12-18')['id'],'convention':'physical_gas_fixed_quantity_v4','knowledgeMode':'current'}
    after={**before,'snapshotId':january_delivery_frame(workspace)}
    catalog=c.call('gas/catalog',before);point=next(p for p in catalog['locations'] if p['deliveryPoint']=='synthetic_gas_point_a')
    terms={**before,**trade_booking(),'operationId':str(uuid4()),'bookId':catalog['books'][0]['id'],'definitionRevisionId':point['definitionRevisionId'],'gasSetupId':catalog['gasSetupId'],'discounting':False,'profile':'fixed_gas_composition_v1',
      'physicalSides':[{'code':'receipt','pipeline':point['pipeline'],'zone':point['zone'],'deliveryPoint':point['deliveryPoint'],'product':point['product'],'direction':'receive','deliveryFrom':'2021-01-01','deliveryTo':'2021-01-03','quantityBasis':'per_operating_day','quantity':'100','link':None}],
      'cashSides':[{'code':'payment','role':'consideration','direction':'pay','source':'receipt','stage':'delivery','price':'3','tariffCode':None,'paymentAt':'2021-02-02T00:00:00Z'}]}
    booked=[c.call('gas/composition/book',{**terms,'operationId':str(uuid4())}) for _ in range(2)]
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
    retained=state()['context'];historic={**after,'knowledgeMode':'retained','contextId':retained['contextId']}
    # A newer nominated request prevents applying stale external scheduling evidence.
    later,_=write(nomination('synthetic-new-request','80'))
    c.call('gas/operations/preview',apply,expected=409)
    assert c.call('gas/operations/record',{**ncommand,'operationId':str(uuid4())})['id']==n['id']
    alias={**ncommand,'operationId':str(uuid4()),'preview':{**ncommand['preview'],'details':{**ncommand['preview']['details'],'quantity':'100.000','submittedAt':'2020-12-31T12:00:00+00:00','accountId':accounts[0]['id'].upper()}}}
    assert c.call('gas/operations/record',alias)['id']==n['id']
    assert next(h for h in state()['state']['heads'] if h['kind']=='nominated')['quantity']=='80'
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
    certificate=c.call('gas/operations/preview',transfer)['context']['contextId']
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
    c.call('gas/operations/read',dict(context=public_context,tradeId=trades[0],day=day),expected=403)
    c.call('profile',{'profile':'trader'})
    current_account=next(a for a in state()['state']['accounts'] if a['id']==accounts[0]['id'])
    write({**service,'details':{**service['details'],'expectedPreviousId':current_account['version_id'],'active':True,'evidence':'Synthetic reviewed service reactivation'}})
    from check_physical_operations import directory_checks
    directory_checks(c,after,query,trades[0],manifest)
    print('PASS gas operations: service scope, before/intra/retro, scheduling cuts, explicit adoption, stale heads, statement coverage, paired transfer, cash, concurrency, revisions, history and recovery')
    return dict(tradeId=trades[0],bindingId=booked[0]['bindingId'] if 'bindingId' in booked[0] else query(f"SELECT instrument_id FROM trading.gas_trade_revisions WHERE trade_id='{trades[0]}' AND revision=1;"),context=after,accountId=accounts[0]['id'])
