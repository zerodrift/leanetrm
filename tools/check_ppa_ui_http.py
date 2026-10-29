"""Bounded PPA authoring, directory and retained schedule inspection checks."""
from copy import deepcopy
import json
from uuid import uuid4
from preview_client import PreviewClient
from preview_fixture import trade_booking
from check_ppa_bookings_http import profile_terms
from exact_values import fraction


def run_checks(url, query, manifest, **_):
    client=PreviewClient(url);client.bootstrap();client.call('profile',{'profile':'trader'});client.bootstrap()
    frames=client.call('ppa/frames');frame=next(f for f in frames if not f.get('ppaBundle'))['id']
    catalog=client.call('ppa/catalog',{'snapshotId':frame})
    assert catalog.get('knownAt'), 'catalog must supply database known-at for explicit authoring context'
    assert catalog['legalEntities'] and catalog['counterparties'], 'authorized authoring catalog must expose the seeded exact party choices'
    assert len(catalog['compositionTemplates'])==5
    assert all(catalog['references'][key] for key in ('currencies','quantitySpecs','units','clocks','calendars','locations'))
    assert catalog['definitionsMetadata']['priceCurves'] and 'calendar_days' not in catalog['references']
    metadata=catalog['definitionsMetadata']
    for rows,fields in ((metadata['assetVersions'],('capacity_basis','accredited_capacity')),
                        (metadata['volumeSources'],('loss','availability','curtailment','degradation')),
                        ([curve['quote'] for curve in metadata['priceCurves']],('multiplier','trading_tick','clearing_increment','minimum','maximum'))):
        for row in rows:
            for field in fields:
                assert row[field] is None or isinstance(row[field],str), f'financial catalog field {field} must retain exact text'
    for row in metadata['assetVersions']:
        stored=query(f"SELECT capacity_basis::text FROM market_data.generation_asset_versions WHERE catalog_id='{row['catalog_id']}' AND revision_id='{row['revision_id']}' AND asset_code='{row['asset_code']}' AND effective_from='{row['effective_from']}';").strip()
        assert row['capacity_basis']==stored, 'capacity basis must preserve the database numeric lexeme'
    print('PASS PPA UI: exact authoring catalog and database cutoff',flush=True)
    def cutoff():return client.call('ppa/catalog',{'snapshotId':frame})['knownAt']
    def directory(books,expected=200,**changes):
        body=dict(snapshotId=frame,bookIds=books,knownAt=cutoff(),offset=0,limit=1);body.update(changes)
        return client.call('ppa/directory',body,expected=expected)
    assert directory([])['total']==0
    assert directory([],expected=422,limit=65)['code']=='ppa_limit'
    books=[str(uuid4()),str(uuid4())]
    for book in books:query(f"INSERT INTO trading.books VALUES('{manifest['catalogId']}','{book}','d0000000-0000-0000-0000-000000000001','Synthetic authoring check',1,'2020-01-01T00:00:00Z');")
    terms=profile_terms(manifest);body=dict(operationId=str(uuid4()),snapshotId=frame,tradeId=None,expectedRevision=None,bookId=books[0],boundaryAt=None,coverageFrom='2031-11-02T04:00:00Z',coverageTo='2031-11-03T05:00:00Z',quantity='80',reason='Synthetic inspection booking',memo='',booking=trade_booking(),terms=terms)
    booked=client.call('ppa/book',body);trade=booked['tradeId']
    body2=deepcopy(body);body2['operationId']=str(uuid4());second=client.call('ppa/book',body2)
    page=directory(books)
    assert page['total']==2 and len(page['heads'])==1 and len(page['bindings'])==1
    assert directory(books,offset=1)['heads'][0]['trade_id']!=page['heads'][0]['trade_id']
    assert directory(books,offset=2)['heads']==[]
    assert directory([str(uuid4())],expected=403)['code']=='access_denied'
    print('PASS PPA UI: complete book admission and bounded directory pages',flush=True)
    reader=PreviewClient(url);reader.bootstrap();reader.call('profile',{'profile':'risk'});reader.bootstrap()
    reader_frame=next(f['id'] for f in reader.call('ppa/frames') if not f.get('ppaBundle') and f['at']==next(f for f in frames if f['id']==frame)['at'])
    reader_cutoff=cutoff()
    reader_editor=reader.call('ppa/editor',dict(snapshotId=reader_frame,tradeId=trade,knownAt=reader_cutoff))
    assert reader_editor['revisionBindings'][0]['bindings'][0]['bindingId']==booked['bindings'][0]['bindingId']
    assert reader.call('ppa/status',dict(operationId=body['operationId']))['status']=='not_found'
    editor=client.call('ppa/editor',dict(snapshotId=frame,tradeId=trade,knownAt=cutoff()))
    assert len(editor['revisionBindings'])==1
    binding=editor['bindings'][0]['binding_id'];side=editor['instruments'][0]['sideIds'][0]['sideId']
    preview=dict(snapshotId=frame,definitionRevisionId=manifest['revisionId'],referenceCatalogId=manifest['referenceCatalogId'],referenceRevisionId=manifest['referenceRevisionId'],scope='trades',selectedIds=[trade],explicitSubset=True,terms=None,quantity=None,coverageFrom=None,coverageTo=None,knownAt=cutoff(),pendingActuals='require_actuals_v1',detailMonths=['2031-11-01'])
    def inspect(expected=200,**changes):
        request=dict(preview=preview,bindingId=binding,sideId=side,offset=0,limit=7);request.update(changes)
        return client.call('ppa/inspect',request,expected=expected)
    assert inspect(expected=422,limit=129)['code']=='ppa_limit'
    assert inspect(expected=422,sideId=str(uuid4()))['code']=='ppa_scope'
    assert inspect(expected=422,bindingId=str(uuid4()))['code']=='ppa_scope'
    # The profile's physical side requires the shape series used by the owned fixture.
    editor_client=PreviewClient(url);editor_client.bootstrap();editor_client.call('profile',{'profile':'editor'});editor_client.bootstrap()
    shape=manifest['intervalCurves']['shapeSeries']['additive']
    shape_head=next(s['head'] for s in editor_client.call('interval-series')['series'] if s['id']==shape)
    editor_client.call('interval-series/revise',dict(requestId=str(uuid4()),seriesId=shape,expectedRevision=shape_head,reason='Synthetic native schedule inspection',segments=[dict(start='2031-11-01T04:00:00Z',values=['0']*721)]))
    preview['knownAt']=cutoff()
    pages=[inspect(offset=offset) for offset in (0,7,14,21)]
    assert all(page.get('currency',{}).get('code')=='usd' for page in pages), 'stored valuation must label exact currency'
    assert all(page.get('contractQuantitySpecification',{}).get('code')=='test_capacity_quantity' for page in pages), 'stored valuation must label exact contract quantity'
    summary=client.call('ppa/preview',{**preview,'detailMonths':[]})
    assert summary['currency']==pages[0]['currency'] and summary['contractQuantitySpecification']==pages[0]['contractQuantitySpecification']
    assert summary['contractQuantitySpecifications']==[summary['contractQuantitySpecification']]
    periods=[period for binding_value in summary['bindings'] for side_value in binding_value['sides'] for period in side_value['measures']['periods']]
    assert periods, 'summary must retain payment and delivery periods without detailed rows'
    for period in periods:
        assert period.get('month')=='2031-11-01', 'period projection must expose readable month'
        assert period.get('kind') in ('Delivery','Payment')
        assert isinstance(period.get('quantityState'),str) and isinstance(period.get('economicClass'),str)
        assert isinstance(period.get('coverage'),str) and period.get('valueAt')==period['key']['valueAt']
        assert period.get('quantitySpecification')=={'revision':{'catalogId':manifest['referenceCatalogId'],'revisionId':manifest['referenceRevisionId']},'code':'test_power_quantity'}
    draft=client.call('ppa/preview',{**preview,'scope':'draft','selectedIds':[],'explicitSubset':False,'terms':terms,'quantity':'80','coverageFrom':body['coverageFrom'],'coverageTo':body['coverageTo']})
    draft_sides=draft['bindings'][0]['sides']
    draft_rows=[row for side_value in draft_sides for row in side_value['measures']['rows']]
    assert len(draft_rows)==50
    assert all(row.get('priceComponents') and row.get('quantitySpecification',{}).get('code')=='test_power_quantity' for row in draft_rows), 'draft detail must expose exact native prices and quantity units before booking'
    assert {(side_value.get('role'),side_value.get('ordinal')) for side_value in draft_sides}=={('energy_delivery',0),('strike_payment',0)}, 'side preview must label explicit membership roles'
    strike_rows=next(side_value['measures']['rows'] for side_value in draft_sides if side_value['role']=='strike_payment')
    assert all(fraction(row['priceComponents'][0]['price'])==40 for row in strike_rows)
    assert all(row['priceComponents'][0]['price']['unit']['quantity']['code']=='test_power_quantity' for row in strike_rows)
    rows=[row for page in pages for row in page['detail']['rows']]
    assert len(rows)==25 and all(page['detail']['total']==25 for page in pages) and not pages[-1]['detail']['hasMore']
    assert len({row['interval']['start'] for row in rows})==25
    assert len([row for row in rows if row['interval']['hourBeginning']==1])==2
    assert {row['interval']['occurrence'] for row in rows if row['interval']['hourBeginning']==1}=={1,2}
    assert all(fraction(row['quantity'])==80 and row['priceComponents'] for row in rows)
    assert all(sum(fraction(p['quantity'])*fraction(p['price']) for p in row['priceComponents'])==fraction(row['amount']) for row in rows)
    assert all(fraction(row['amount'])==3300 for row in rows)
    assert all(row['priceComponents'][0]['price']['unit']['quantity']['code']=='test_power_quantity' for row in rows)
    assert all(not side['measures']['rows'] for page in pages for binding in page['bindings'] for side in binding['sides'])
    assert all(page['valueExcludingOverdue']==pages[0]['valueExcludingOverdue'] for page in pages)
    assert inspect(offset=25)['detail']['rows']==[]
    print('PASS PPA UI: exact DST schedule detail pages, units and price provenance',flush=True)
    old=cutoff();change=deepcopy(body);change.update(operationId=str(uuid4()),tradeId=trade,expectedRevision=1,boundaryAt='2031-11-02T06:00:00Z',coverageFrom='2031-11-02T06:00:00Z',quantity='60');client.call('ppa/amend',change)
    latest=client.call('ppa/editor',dict(snapshotId=frame,tradeId=trade,knownAt=cutoff()))
    prior=client.call('ppa/editor',dict(snapshotId=frame,tradeId=trade,knownAt=old))
    assert [len(r['bindings']) for r in latest['revisionBindings']]==[1,2] and len(prior['revisionBindings'])==1
    assert latest['revisionBindings'][0]['bindings'][0]['coverageTo'].startswith('2031-11-03')
    reader_history=reader.call('ppa/editor',dict(snapshotId=reader_frame,tradeId=trade,knownAt=cutoff()))
    assert reader_history['revisionBindings']==latest['revisionBindings']
    assert reader.call('ppa/editor',dict(snapshotId=reader_frame,tradeId=trade,knownAt=old))['revisionBindings']==prior['revisionBindings']
    print('PASS PPA UI: original revision and retained-prefix binding inspection',flush=True)
    bundle=manifest['ppaR3']['bundle'];other_frame=next(f for f in frames if f.get('ppaBundle')==bundle and f['at']==next(f for f in frames if f['id']==frame)['at'])['id']
    other_catalog=client.call('ppa/catalog',dict(snapshotId=other_frame))
    assert other_catalog['definitions']['revisionId']==bundle['definitionRevisionId']
    assert all(r['revision_id']==bundle['referenceRevisionId'] for r in other_catalog['references']['quantitySpecs'])
    alternate=deepcopy(body);alternate.update(operationId=str(uuid4()),snapshotId=other_frame);alternate['terms'].update(bundle)
    other_trade=client.call('ppa/book',alternate)['tradeId']
    mixed=directory(books,limit=64)
    assert mixed['total']==3 and {h['definition_revision_id'] for h in mixed['heads']}=={manifest['revisionId'],bundle['definitionRevisionId']}
    query(f"DELETE FROM security.reference_catalog_scopes WHERE catalog_id='{bundle['referenceCatalogId']}';")
    try:
        assert directory(books,expected=403)['code']=='access_denied'
    finally:query(f"INSERT INTO security.reference_catalog_scopes VALUES('{bundle['referenceCatalogId']}','{manifest['tenantId']}','{manifest['workspaceId']}','owner');")
    # The actual cutoff must follow booking; authoring's earlier database cutoff is immutable.
    known=client.call('ppa/catalog',dict(snapshotId=other_frame))['knownAt']
    query(f"DELETE FROM security.reference_catalog_scopes WHERE catalog_id='{manifest['referenceCatalogId']}';")
    try:
        assert client.call('ppa/catalog',dict(snapshotId=other_frame))['reference']['catalogId']==bundle['referenceCatalogId']
        assert client.call('ppa/editor',dict(snapshotId=other_frame,tradeId=other_trade,knownAt=known))['head']['reference_catalog_id']==bundle['referenceCatalogId']
        assert client.call('ppa/legs',dict(snapshotId=other_frame,tradeId=other_trade,knownAt=known,offset=0,limit=1))['total']==1
    finally:query(f"INSERT INTO security.reference_catalog_scopes VALUES('{manifest['referenceCatalogId']}','{manifest['tenantId']}','{manifest['workspaceId']}','owner');")
    print('PASS PPA UI: mixed bundle directory and selected-reference inspection authorization',flush=True)
    from benchmark_ppa_http import HttpFixture, terms as volume_terms
    volume_fixture=HttpFixture(url,manifest,frame)
    volume_fixture.heads={s['id']:s['head'] for s in volume_fixture.editor.call('interval-series')['series']}
    volume_fixture.load(60,'2031-11-01','2031-12-01')
    volume=deepcopy(body);volume.update(operationId=str(uuid4()),terms=volume_terms(manifest))
    volume_trade=client.call('ppa/book',volume)['tradeId'];preview['selectedIds']=[volume_trade];preview['knownAt']=cutoff()
    volume_editor=client.call('ppa/editor',dict(snapshotId=frame,tradeId=volume_trade,knownAt=preview['knownAt']))
    binding=volume_editor['bindings'][0]['binding_id'];side=volume_editor['instruments'][0]['sideIds'][0]['sideId']
    detailed=inspect(limit=1)['detail']['rows'][0]
    assert fraction(detailed['quantity'])==40 and detailed['quantityLineage']
    lineage=detailed['quantityLineage'][0]
    assert lineage['series']==manifest['ppaBookings']['workloads']['60']['forecast'] and lineage['revision']>0 and lineage['contextId'] and lineage['assetVersion']
    assert lineage['quantitySpecification']['code']=='test_power_quantity'
    assert detailed['priceComponents'][0]['resolution']['forwardLineage']
    print('PASS PPA UI: exact asset quantity lineage and native price provenance',flush=True)
    precise_mw='80.123456789012345678'
    precise_booking=deepcopy(body);precise_booking.update(operationId=str(uuid4()),bookId=books[1],quantity=precise_mw)
    precise_trade=client.call('ppa/book',precise_booking)['tradeId']
    precise_known=cutoff()
    precise_editor=client.call('ppa/editor',dict(snapshotId=frame,tradeId=precise_trade,knownAt=precise_known))
    precise_legs=client.call('ppa/legs',dict(snapshotId=frame,tradeId=precise_trade,knownAt=precise_known,offset=0,limit=1))
    precise_directory=directory([books[1]],limit=64)
    assert precise_editor['bindings'][0]['quantity']==precise_mw, 'editable current binding must retain all 18 decimal places as text'
    assert precise_legs['bindings'][0]['quantity']==precise_mw and precise_directory['bindings'][0]['quantity']==precise_mw
    assert precise_editor['revisionBindings'][0]['bindings'][0]['quantity']==precise_mw
    print('PASS PPA UI: editable binding MW stays exact text across current/history/directory/legs',flush=True)
    # Retained overdue balances and certificate holdings for the browser's read-only inspection.
    from power_collection_fixture import Collector
    from exact_values import instant
    source=manifest['intervalCurves']['source']
    collector=Collector(query,manifest,{'energy':{'provider':'PJM','sourceId':source,'clock':'synthetic_eastern'}})
    price_revision=int(query(f"SELECT coalesce(max(provider_revision),0)+1 FROM power_data.prices WHERE source_id='{source}';"))
    collector.publish('energy','2020-12-15',instant('2020-12-15T05:00:00Z'),24,60,['40']*24,price_revision)
    config=manifest['ppaBookings']['workloads']['60']
    volume_fixture.revise(config['forecast'],'2020-12-15T17:00:00Z',['0.25','0.25'])
    volume_fixture.revise(config['meter'],'2020-12-15T17:00:00Z',['50',None])
    overdue_book=str(uuid4())
    query(f"INSERT INTO trading.books VALUES('{manifest['catalogId']}','{overdue_book}','d0000000-0000-0000-0000-000000000001','Synthetic overdue inspection',1,'2020-01-01T00:00:00Z');")
    past=volume_terms(manifest,first='2020-12-01',last='2021-01-01')
    past_booking=deepcopy(body);past_booking.update(operationId=str(uuid4()),bookId=overdue_book,terms=past,snapshotId=next(f['id'] for f in frames if not f.get('ppaBundle') and instant(f['at'])==instant('2020-12-15T16:00:00Z')),coverageFrom='2020-12-15T17:00:00Z',coverageTo='2020-12-15T19:00:00Z')
    past_trade=client.call('ppa/book',past_booking)['tradeId']
    overdue_frame=next(f for f in frames if not f.get('ppaBundle') and instant(f['at'])==instant(manifest['ppaBookings']['overdueAt']))
    overdue_known=cutoff()
    overdue=client.call('ppa/preview',{**preview,'snapshotId':overdue_frame['id'],'selectedIds':[past_trade],'knownAt':overdue_known,'pendingActuals':'estimate_until_actual_v1','detailMonths':['2020-12-01']})
    assert fraction(overdue['valueExcludingOverdue'])==200 and overdue['completePv'] is None
    assert fraction(overdue['overdueKnownBalance'])==-860 and fraction(overdue['overdueEstimated'])==-860
    print('PASS PPA UI: retained holdings and separate overdue known/estimated browser fixture',flush=True)
    mixed_sources=deepcopy(volume)
    mixed_sources.update(operationId=str(uuid4()),bookId=books[1])
    mixed_sources['terms']['memberships'][0]['side']['quantity']=deepcopy(profile_terms(manifest)['memberships'][0]['side']['quantity'])
    independent_sources_trade=client.call('ppa/book',mixed_sources)['tradeId']
    check_unpriced_blotter(client,query,manifest,frame)
    return {'overdue':{'tradeId':past_trade,'bookId':overdue_book,'frameId':overdue_frame['id'],'frameAt':overdue_frame['at'],'knownAt':overdue_known},
            'independentSources':{'tradeId':independent_sources_trade,'bookId':books[1]},'baseFrameId':frame,'baseFrameAt':next(f['at'] for f in frames if f['id']==frame),
            'alternateFrameId':other_frame,'alternateBundle':bundle,'mixedBookIds':books,'mixedTradeIds':[trade,second['tradeId'],other_trade,volume_trade]}


def check_unpriced_blotter(client,query,manifest,snapshot):
    """Missing native price coverage lists the whole trade unvalued without changing other trades."""
    # April has no declared native shaped blocks. Book an explicit valid profile schedule
    # there, so this check does not depend on another suite leaving an unpriceable trade.
    refused=str(uuid4())
    query(f"INSERT INTO trading.books VALUES('{manifest['catalogId']}','{refused}','d0000000-0000-0000-0000-000000000001','Synthetic uncovered delivery',1,'2020-01-01T00:00:00Z');")
    terms=profile_terms(manifest)
    terms['delivery'].update({'from':'2031-04-01','to':'2031-05-01'})
    request=dict(operationId=str(uuid4()),snapshotId=snapshot,tradeId=None,expectedRevision=None,bookId=refused,boundaryAt=None,
                 coverageFrom='2031-04-02T04:00:00Z',coverageTo='2031-04-03T04:00:00Z',quantity='80',
                 reason='Synthetic uncovered native price delivery',memo='',booking=trade_booking(),terms=terms)
    booked=client.call('ppa/book',request)
    # Two retained bindings must still count as one unvalued position.
    amendment={**request,'operationId':str(uuid4()),'tradeId':booked['tradeId'],'expectedRevision':1,
               'boundaryAt':'2031-04-02T06:00:00Z','coverageFrom':'2031-04-02T06:00:00Z','quantity':'60'}
    client.call('ppa/amend',amendment)
    catalog=client.call('ppa/catalog',{'snapshotId':snapshot})
    preview=dict(snapshotId=snapshot,definitionRevisionId=catalog['definitions']['revisionId'],referenceCatalogId=catalog['reference']['catalogId'],
                 referenceRevisionId=catalog['reference']['revisionId'],scope='books',selectedIds=[refused],explicitSubset=False,terms=None,quantity=None,
                 coverageFrom=None,coverageTo=None,knownAt=catalog['knownAt'],pendingActuals='estimate_until_actual_v1',detailMonths=[])
    # The engine itself must name the missing block, before Blotter isolation runs.
    assert client.call('ppa/preview',preview,expected=422)['code']=='interval_block_coverage'
    print('PASS PPA UI: explicit uncovered native delivery refuses with interval_block_coverage',flush=True)
    def blotter(bookIds):
        view={'mode':'trades','bookIds':bookIds,'portfolioIds':[],'groupBy':[],'measures':[],'pivotConvention':None}
        read=client.call('trading/workspace',{'context':{'snapshotId':snapshot,'convention':'trading_workspace_v1','knowledgeMode':'current'},
                                               'view':view,'bookingCatalog':'directory'})['blotter']
        return read,{(r['tradeId'],r['bindingId']):r for r in read['holdings']}
    everything,rows=blotter([])
    listed=[r for r in rows.values() if r['bookId']==refused and r['instrumentType']=='ppa']
    assert any(r['instrumentType']=='ppa' and r['presentValue'] is not None for r in rows.values() if r['bookId']!=refused), 'another PPA must still be valued'
    assert len(listed)==2 and {r['tradeId'] for r in listed}=={booked['tradeId']}, listed
    assert all(r['unavailableReason']=='unpriced_delivery' and r['presentValue'] is None and r['completePv'] is None for r in listed), listed
    without,rest=blotter([b for b in json.loads(query("SELECT json_agg(id ORDER BY id) FROM trading.books;")) if b!=refused])
    assert {k:v for k,v in rows.items() if v['bookId']!=refused}==rest, 'the other books must read as they do without the refused one'
    # unvaluedPositions counts trades.
    assert everything['unvaluedPositions']==without['unvaluedPositions']+len({r['tradeId'] for r in listed}), (everything['unvaluedPositions'],without['unvaluedPositions'])
    print('PASS PPA UI: a PPA the market data cannot price is listed unvalued with its reason, and the rest read as they do without it',flush=True)
