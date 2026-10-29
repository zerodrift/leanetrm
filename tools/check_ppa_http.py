"""Independent exact-Fraction acceptance checks for synthetic PPA quantity reads."""
from fractions import Fraction
from uuid import uuid4
from exact_values import fraction, instant
from preview_client import PreviewClient, initial_frame


def run_checks(url, query, manifest):
    fixture=manifest['ppa']; passed=0
    def check(name,condition):
        nonlocal passed
        assert condition,name
        passed+=1;print('PASS PPA http: '+name,flush=True)
    client=PreviewClient(url);client.bootstrap();client.call('profile',{'profile':'editor'})
    frame=initial_frame(client.call('workspace'),'2020-12-15')['id']
    def cutoff():return client.call('interval-series')['knownAt']
    heads={}
    def revise(series,start,values):
        result=client.call('interval-series/revise',{'requestId':str(uuid4()),'seriesId':series,'expectedRevision':heads.get(series,0),'reason':'Synthetic PPA acceptance input','segments':[{'start':start,'values':values}]})
        heads[series]=result['revision'];return result
    def inputs(minutes,start,end,meter=False,values=('0.25','0.30')):
        n=int((instant(end)-instant(start)).total_seconds()/60/minutes)
        for asset,value in zip(fixture['assets'],values):
            source=fixture['series'][f'{asset}_{minutes}'];revise(source['meter' if meter else 'forecast'],start,[value]*n)
    def quantity(start,end,minutes=60,stage='delivered',policy='estimate_until_actual_v1',known=None,expected=200,**changes):
        body={'snapshotId':frame,'assets':fixture['assets'],'sources':[f'{a}_{minutes}' for a in fixture['assets']],
              'energy':'test_power_quantity','stage':stage,'from':start,'to':end,'knownAt':known or cutoff(),
              'pendingActuals':policy,'contractMw':'80','attribute':'energy'}
        body.update(changes)
        return client.call('ppa/quantities',body,expected=expected)
    annual=client.call('ppa/profile',{'snapshotId':frame,'clock':'synthetic_eastern','intervalMinutes':60,'capacity':'test_capacity_quantity','energy':'test_power_quantity','from':'2031-01-01T05:00:00Z','to':'2032-01-01T05:00:00Z','membership':'all','touProduct':None,'touVersion':None,'calendar':'synthetic_eastern_holidays','contractMw':'80'})
    check('a full 365-day profile compiles all 8760 native hours',len(annual['rows'])==8760 and all(fraction(r['quantity'])==80 for r in annual['rows']))
    start,end='2031-11-02T04:00:00Z','2031-11-03T05:00:00Z'
    inputs(60,start,end)
    rows=quantity(start,end)['rows']
    # Entire asset MWh, adjusted only once, divided by the shared basis; 80MW applied once.
    expected=(Fraction(200)*Fraction('0.25')+Fraction(100)*Fraction('0.30'))*Fraction('.98')*Fraction('.98')/300*80
    check('25-hour DST forecast has exact multi-asset shared-capacity quantity',len(rows)==25 and all(fraction(r['quantity'])==expected for r in rows))
    check('every row retains both contribution identities, exact versions and certified knowledge',all(len(r['contributions'])==2 and all(c['knownAt'] and c['contextId'] and c['revision']==1 for c in r['contributions']) for r in rows))
    check('location assignment is consumed with exact adjustment and selected curve',all(r['assignments'][0]['code']=='test_location_energy_0' and r['assignments'][0]['adjustment']=='1.25' for r in rows))
    inputs(15,start,end)
    quarter=quantity(start,end,15)['rows']
    check('100 quarter-hour rows equal the independently computed duration-scaled quantity',len(quarter)==100 and all(fraction(r['quantity'])==expected/4 for r in quarter) and sum(fraction(r['quantity']) for r in rows)==sum(fraction(r['quantity']) for r in quarter))
    future='2031-11-15T05:00:00Z';future_end='2031-11-15T06:00:00Z';inputs(60,future,future_end)
    version=quantity(future,future_end)['rows'][0]
    check('asset versions and location assignment switch at delivery equality',fraction(version['capacityBasis'])==150 and all(instant(c['assetVersion']['from'])==instant(fixture['switch']) for c in version['contributions']) and version['assignments'][0]['code']=='test_location_energy_1')
    live,live_end='2020-12-15T16:00:00Z','2020-12-15T17:00:00Z';inputs(60,live,live_end);inputs(60,live,live_end,True,('999','999'))
    live_row=quantity(live,live_end)['rows'][0]
    check('a live interval ignores an available meter row',live_row['state']=='forecast' and fraction(live_row['quantity'])==expected)
    pending,pending_end='2020-12-15T15:00:00Z','2020-12-15T16:00:00Z';inputs(60,pending,pending_end)
    check('delivered pre-deadline forecast is explicitly estimated',quantity(pending,pending_end)['rows'][0]['state']=='estimated')
    check('required actual before deadline is a structured refusal',quantity(pending,pending_end,policy='require_actuals_v1',expected=422)['code']=='actuals_required')
    due,due_end='2020-12-15T14:00:00Z','2020-12-15T15:00:00Z';inputs(60,due,due_end)
    check('deadline equality refuses missing actual for both policies',all(quantity(due,due_end,policy=p,expected=422)['code']=='meter_actual_missing' for p in ['require_actuals_v1','estimate_until_actual_v1']))
    inputs(60,pending,pending_end,True,('50','30'));old=cutoff()
    metered=quantity(pending,pending_end,known=old)['rows'][0]
    check('delivered meter learned after valuation is allowed by certified known cutoff and only loss applies',metered['state']=='metered' and fraction(metered['quantity'])==Fraction(80)*Fraction('.98')/300*80 and all(instant(c['knownAt'])>instant('2020-12-15T16:00:00Z') for c in metered['contributions']))
    a=fixture['series']['test_asset_a_60']['meter'];revise(a,pending,['0']);new=cutoff()
    check('zero meter retains the same capacity denominator',fraction(quantity(pending,pending_end,known=new)['rows'][0]['quantity'])==Fraction(30)*Fraction('.98')/300*80)
    check('earlier knowledge cutoff retains pre-correction actual',fraction(quantity(pending,pending_end,known=old)['rows'][0]['quantity'])==fraction(metered['quantity']))
    revise(a,pending,[None]);missing=cutoff()
    check('withdrawn meter cannot silently shrink the denominator',quantity(pending,pending_end,policy='require_actuals_v1',known=missing,expected=422)['code']=='actuals_required')
    check('duplicate assets refuse instead of double counting',quantity(start,end,assets=[fixture['assets'][0]]*2,expected=422)['code']=='asset_set')
    check('unmatched sources refuse instead of dropping an asset',quantity(start,end,sources=['test_asset_a_60'],expected=422)['code']=='asset_set')
    check('unknown financial members are malformed requests',quantity(start,end,valuationAt='2032-01-01T00:00:00Z',expected=400)['code']=='request_format')
    capacity=client.call('ppa/capacity',{'snapshotId':frame,'assets':fixture['assets'],'operatingDay':'2031-11-02','knownAt':cutoff(),'contractMw':'80'})
    check('daily accredited capacity consumes exact versions and capacity location assignment',fraction(capacity['quantity'])==64 and fraction(capacity['capacityBasis'])==300 and all(a['curve']==fixture['capacityCurve'] for a in capacity['assignments']))
    profile=client.call('ppa/profile',{'snapshotId':frame,'clock':'synthetic_eastern','intervalMinutes':15,'capacity':'test_capacity_quantity','energy':'test_power_quantity','from':start,'to':end,'membership':'all','touProduct':None,'touVersion':None,'calendar':'synthetic_eastern_holidays','contractMw':'80'})
    check('contract profile preserves 100 native DST cells and exact 1MW duration then scales once',len(profile['rows'])==100 and all(fraction(r['quantity'])==20 and r['state']=='contractual' for r in profile['rows']))
    def selected_profile(membership,first,last,tou=manifest['intervalCurves']['pjm'],version=1,expected=200):
        return client.call('ppa/profile',{'snapshotId':frame,'clock':'synthetic_eastern','intervalMinutes':60,'capacity':'test_capacity_quantity','energy':'test_power_quantity','from':first,'to':last,'membership':membership,'touProduct':tou,'touVersion':version,'calendar':'synthetic_eastern_holidays','contractMw':'80'},expected=expected)
    peak=selected_profile('peak','2031-11-03T05:00:00Z','2031-11-04T05:00:00Z')['rows']
    check('published peak profile independently selects the declared weekday hours',len(peak)==16 and [r['interval']['hourLabel'] for r in peak]==list(range(7,23)) and all(fraction(r['quantity'])==80 for r in peak))
    check('published holiday moves all hours into off-peak',not selected_profile('peak','2031-11-27T05:00:00Z','2031-11-28T05:00:00Z')['rows'] and len(selected_profile('off','2031-11-27T05:00:00Z','2031-11-28T05:00:00Z')['rows'])==24)
    check('peak membership requires an exact published product version',selected_profile('peak',start,end,version=999,expected=422)['code']=='ppa_definition')
    certificate=quantity(start,end,attribute='certificate')['rows']
    check('certificate location assignments consume the explicit synthetic REC vintage',all(a['curve']==fixture['certificateCurve'] and a['unit']['quantity']['code']=='test_rec_quantity' and a['adjustment']=='0' for r in certificate for a in r['assignments']))
    check('generated quantities use exact capacity normalization without delivered loss',fraction(quantity(pending,pending_end,stage='generated',known=old)['rows'][0]['quantity'])==Fraction(64,3))
    check('policy is versioned and response retains its selected convention',quantity(start,end)['pendingActuals']=='estimate_until_actual_v1' and quantity(start,end)['normalization']=='contract_mw_of_capacity_in_force_v1' and quantity(start,end,policy='estimate_until_actual',expected=422)['code']=='ppa_definition')
    check('submicrosecond knowledge and null financial input are structured refusals',quantity(start,end,known='2020-12-15T16:00:00.0000001Z',expected=422)['code']=='interval_series_instant' and quantity(start,end,contractMw=None,expected=422)['code']=='numeric_format')
    check('unknown snapshot pins refuse before calculation',quantity(start,end,snapshotId=str(uuid4()),expected=422)['code']=='snapshot_missing')
    anonymous=PreviewClient(url);anonymous.bootstrap()
    anonymous.call('ppa/capacity',{'snapshotId':frame,'assets':fixture['assets'],'operatingDay':'2031-11-02','knownAt':cutoff(),'contractMw':'80'},expected=403)
    check('a profile-less session cannot read quantity definitions',True)
    past=cutoff();client.call('market/context',{'effectiveOn':'2020-12-18','knownAt':past,'mode':'at','selection':'latest_correction'})
    check('historical sessions refuse current delivery-input reads',quantity(start,end,known=past,expected=422)['code']=='interval_curve_historical')
    client.call('market/current',{})
    for table,catalog in [('market_catalog_scopes',manifest['catalogId']),('reference_catalog_scopes',manifest['referenceCatalogId'])]:
        where=f"catalog_id='{catalog}' AND tenant_id='{manifest['tenantId']}' AND workspace_id='{manifest['workspaceId']}'"
        saved=query(f"SELECT row_to_json(s)::text FROM security.{table} s WHERE {where};")
        assert saved
        retained=cutoff()
        try:
            query(f"DELETE FROM security.{table} WHERE {where};")
            status,code=(403,'access_denied') if table=='market_catalog_scopes' else (422,'ambiguous_reference')
            check('current '+table+' revocation refuses already selected inputs',quantity(start,end,known=retained,expected=status)['code']==code)
        finally:
            encoded=saved.replace("'","''")
            query(f"INSERT INTO security.{table} SELECT(json_populate_record(NULL::security.{table},'{encoded}'::json)).*;")
    invalid_day=client.call('ppa/capacity',{'snapshotId':frame,'assets':fixture['assets'],'operatingDay':'9999-12-31','knownAt':cutoff(),'contractMw':'80'},expected=422)
    check('unsupported capacity operating dates return a structured refusal',invalid_day['code']=='ppa_definition')
    print(f'{passed} PPA HTTP checks passed',flush=True)
