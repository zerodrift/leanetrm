"""Independent exact-bundle operation cases, including inaccessible book members."""
from copy import deepcopy
from uuid import uuid4
from preview_fixture import trade_booking
from check_ppa_bookings_http import profile_terms
from benchmark_ppa_http import terms as volume_terms
from exact_values import fraction, instant


def run_checks(url,query,manifest,fixture):
    client=fixture.client;frames=client.call('ppa/frames');bundle=manifest['ppaR3']['bundle']
    a=next(f for f in frames if not f.get('ppaBundle') and instant(f['at'])==instant(manifest['frames'][0]['at']))
    b=next(f for f in frames if f.get('ppaBundle')==bundle and f['at']==a['at'])
    later_a=next(f for f in frames if not f.get('ppaBundle') and instant(f['at'])==instant(manifest['ppaBookings']['overdueAt']))
    later_b=next(f for f in frames if f.get('ppaBundle')==bundle and f['at']==later_a['at'])
    catalog=manifest['catalogId'];books=[str(uuid4()),str(uuid4())];passed=0
    for book in books:
        query(f"INSERT INTO trading.books VALUES('{catalog}','{book}','d0000000-0000-0000-0000-000000000001','Synthetic revision check',1,'2020-01-01T00:00:00Z');")
    def check(name,condition):
        nonlocal passed
        assert condition,name
        passed+=1;print('PASS PPA R3 HTTP: '+name,flush=True)
    def cutoff():return fixture.cutoff()
    def book(t,frame,book_id,first,last):
        request=dict(operationId=str(uuid4()),snapshotId=frame['id'],tradeId=None,expectedRevision=None,bookId=book_id,boundaryAt=None,coverageFrom=first,coverageTo=last,quantity='80',reason='Synthetic exact revision admission',memo='',booking=trade_booking(),terms=t)
        return client.call('ppa/book',request),request
    def inspect(trade,frame=a,known=None):
        return client.call('ppa/editor',dict(snapshotId=frame['id'],tradeId=trade,knownAt=known or cutoff()))
    def preview(ids,frame=a,scope='trades',known=None,expected=200,**changes):
        selected=frame.get('ppaBundle') or dict(definitionRevisionId=manifest['revisionId'],referenceCatalogId=manifest['referenceCatalogId'],referenceRevisionId=manifest['referenceRevisionId'])
        body=dict(snapshotId=frame['id'],**selected,scope=scope,selectedIds=ids,explicitSubset=scope=='trades',terms=None,quantity=None,coverageFrom=None,coverageTo=None,knownAt=known or cutoff(),pendingActuals='estimate_until_actual_v1',detailMonths=[])
        body.update(changes);return client.call('ppa/preview',body,expected=expected)
    check('PPA bundle frames stay outside ordinary workspace and navigation inventory',all(not f.get('ppaBundle') for f in client.call('workspace')['frames']) and b['id'] not in client.call('workspace/navigation')['initialFrameIds'])
    original=profile_terms(manifest)
    t1,request=book(original,a,books[0],'2031-11-02T04:00:00Z','2031-11-03T05:00:00Z')
    old=cutoff();receipt=preview([t1['tradeId']],known=old)
    saved=inspect(t1['tradeId'],known=old)
    amended=deepcopy(request);amended.update(operationId=str(uuid4()),tradeId=t1['tradeId'],expectedRevision=1,boundaryAt='2031-11-02T06:00:00Z',coverageFrom='2031-11-02T06:00:00Z',quantity='60')
    client.call('ppa/amend',amended)
    now=inspect(t1['tradeId'])
    check('R3.1 same-bundle amendment preserves prefix MW and original full terms',len(now['bindings'])==2 and [str(x['quantity']) for x in now['bindings']]==['80','60'] and all(x['terms']==original for x in now['instruments']))
    for name,pins,code,reasons in [('definition',dict(definitionRevisionId=bundle['definitionRevisionId']),'ppa_definition_revision',['ppa_definition_revision','ppa_reference_revision']),('reference',dict(referenceCatalogId=bundle['referenceCatalogId'],referenceRevisionId=bundle['referenceRevisionId']),'ppa_reference_revision',['ppa_reference_revision']),('both',bundle,'ppa_definition_revision',['ppa_definition_revision','ppa_reference_revision'])]:
        bad=deepcopy(amended);bad.update(operationId=str(uuid4()),expectedRevision=2);bad['terms'].update(pins)
        refusal=client.call('ppa/amend',bad,expected=422)
        check('R3.2/3 '+name+' mismatch returns every reason and stores no partial revision',refusal['code']==code and refusal.get('affectedBindings') and all(x['reasons']==reasons for x in refusal['affectedBindings']) and inspect(t1['tradeId'])['head']['revision']==2 and client.call('ppa/status',{'operationId':bad['operationId']})['status']=='not_found')
    # Same authored original vintage/holding/payment policies under different
    # explicit definitions. Past delivery still has original obligations.
    retained=volume_terms(manifest,first='2020-12-01',last='2021-01-01');retained.update(bundle)
    t2,other=book(retained,b,books[0],'2020-12-19T05:00:00Z','2020-12-19T06:00:00Z')
    check('R3.4 independently published changed capacities do not rewrite earlier terms or receipt',all(inspect(t1['tradeId'],known=old)[key]==saved[key] for key in ('head','bindings','instruments')) and fraction(preview([t1['tradeId']],known=old)['valueExcludingOverdue'])==fraction(receipt['valueExcludingOverdue']))
    check('R3.5 mixed-book trades remain inspectable with original exact bundles',inspect(t1['tradeId'])['head']['definition_revision_id']==manifest['revisionId'] and inspect(t2['tradeId'])['head']['definition_revision_id']==bundle['definitionRevisionId'])
    for selected,affected in [(a,t2),(b,t1)]:
        refusal=preview([books[0]],selected,'books',expected=422)
        check('R3.5 complete mixed book refuses selected bundle '+selected['id'],refusal['code']=='ppa_definition_revision' and {x['tradeId'] for x in refusal['affectedBindings']}=={affected['tradeId']} and 'valueExcludingOverdue' not in refusal and all(x['reasons']==['ppa_definition_revision','ppa_reference_revision'] for x in refusal['affectedBindings']))
    t3,_=book(original,a,books[1],'2031-11-02T04:00:00Z','2031-11-03T05:00:00Z')
    refusal=preview(books,a,'books',expected=422)
    check('R3.6 multi-book request returns no successful partial total',refusal['code']=='ppa_definition_revision' and refusal['scope']['selectedIds']==books and 'completePv' not in refusal)
    subset=preview([t1['tradeId']])
    check('R3.7 explicit compatible subset identifies only requested trade and bindings',subset['scope']['scope']=='trades' and subset['scope']['explicitSubset'] and subset['scope']['selectedIds']==[t1['tradeId']] and {x['tradeId'] for x in subset['bindings']}=={t1['tradeId']})
    expired=preview([books[0]],later_a,'books',expected=422)
    held=inspect(t2['tradeId'],later_b)
    check('R3.8 ended delivery with retained certificates and original payment obligations remains incompatible',{x['tradeId'] for x in expired['affectedBindings']}=={t2['tradeId']} and held['instruments'][0]['terms']['memberships'][3]['side']['attribute']['disposition']=='none_recorded_v1' and held['bindings'][0]['coverage_to'].startswith('2020-12-19'))
    # Scope denial must precede both pins and values, despite prior successful
    # access and despite current RLS otherwise hiding the inaccessible member.
    query(f"DELETE FROM security.reference_catalog_scopes WHERE catalog_id='{bundle['referenceCatalogId']}';")
    try:
        denial=preview([books[0]],a,'books',expected=403)
        check('R3.9 inaccessible required reference refuses whole book before diagnostics',denial.get('code')=='access_denied' and 'affectedBindings' not in denial)
        check('R3.9 frame discovery hides revoked reference bundles',all(f.get('ppaBundle')!=bundle for f in client.call('ppa/frames')))
        check('R3.9 independently authorized compatible subset remains explicitly scoped',fraction(preview([t1['tradeId']])['valueExcludingOverdue'])==fraction(subset['valueExcludingOverdue']))
    finally:
        query(f"INSERT INTO security.reference_catalog_scopes VALUES('{bundle['referenceCatalogId']}','{manifest['tenantId']}','{manifest['workspaceId']}','owner');")
    # An old snapshot cannot be relabelled as the new bundle or vice versa.
    refusal=preview([t2['tradeId']],a,expected=422,**bundle)
    check('R3.10 exact compatible binding still refuses an incompatible selected market frame',refusal['code']=='ppa_definition_revision' and 'valueExcludingOverdue' not in refusal)
    # A stored compatible subset reuses the accepted meter-correction policy
    # while the other exact bundle remains present in the mixed book.
    config=manifest['ppaBookings']['workloads']['60']
    observed=volume_terms(manifest,first='2020-12-01',last='2021-01-01')
    t4,_=book(observed,a,books[0],'2020-12-15T17:00:00Z','2020-12-15T18:00:00Z')
    fixture.revise(config['meter'],'2020-12-15T17:00:00Z',['50'])
    old_knowledge=cutoff();old_result=preview([t4['tradeId']],later_a,known=old_knowledge)
    fixture.revise(config['meter'],'2020-12-15T17:00:00Z',['60'])
    corrected_result=preview([t4['tradeId']],later_a)
    check('R3.10 stored subset uses later eligible correction and old receipt replays exactly',fraction(old_result['valueExcludingOverdue'])==100 and fraction(corrected_result['valueExcludingOverdue'])==120 and fraction(preview([t4['tradeId']],later_a,known=old_knowledge)['valueExcludingOverdue'])==100)
    fixture.revise(config['meter'],'2020-12-15T17:00:00Z',[None])
    check('R3.10 missing compatible inputs refuse without fallback to the other bundle',preview([t4['tradeId']],later_a,expected=422)['code']=='volume_missing')
    print(f'PASS PPA R3 HTTP: {passed} checks',flush=True)
