"""Synthetic TOU checks; only invoked against the runner's newly owned fixture."""
from decimal import Decimal


def run_checks(Client, check):
    c=Client(); c.bootstrap()
    c.call('power/catalog',{'snapshotId':'00000000-0000-0000-0000-000000000000'},expected=403)
    check('TOU catalog requires a current authorized identity')
    c.call('profile',{'profile':'viewer'})
    workspace=c.call('workspace'); frame=workspace['frames'][0]
    catalog=c.call('power/catalog',{'snapshotId':frame['id']})
    check('power final money defaults to three display places',catalog['display']['money']==3)
    check('index classification suggestions are served by the catalog',catalog['classifications']=={'priceTypes':['Test','Official'],'timings':['End of day','Intraday']})
    check('the power calculation offers the published TOU products with their clocks',
          {p['code'] for p in catalog['products']}=={'pjm','nyiso','isone','miso','spp','ercot','caiso'} and all(p['name'] and p['clock'] and p['version']==1 for p in catalog['products']))
    check('every power choice is served by the catalog',
          [b['code'] for b in catalog['blocks']]==['all','peak','off'] and catalog['intervals']==[60,30,15,5]
          and [h['code'] for h in catalog['hourLabels']]==['HE','HB'] and all(b['name'] for b in catalog['blocks']+catalog['hourLabels']))
    curve=next(v for v in catalog['curves'] if v['code']=='local_power_all_in')
    contract=next(k for k in curve['contracts'] if k['from']=='2021-03-01')
    check('holiday calendar is the settlement index calendar from reference data',contract['holidayCalendar']=='test_business'
          and any(r['code']=='test_business' for r in workspace['references']['calendars']))
    def request(product='pjm',block='all',minutes=60):
        p=next(p for p in catalog['presets'] if p['product']==product and p['block']==block and p['minutes']==minutes)
        return dict(snapshotId=frame['id'],curve=curve['code'],contractId=contract['id'],product=product,productVersion=1,block=block,minutes=minutes,hourLabels='HE',quantity={'value':'10','specification':next(q['specification'] for q in catalog['quantityChoices'] if q['basis']=='flat_power')},quantityBasis='flat_power',allocation=None,display=catalog['display'],priceType='Test',timing='End of day',nonHolidays=p['nonHolidays'],holidays=p['holidays'],numericalPolicy=catalog['numericalPolicy'])
    body=request(); result=c.call('power/preview',body)
    check('curve-linked prevailing March has 743 hours and 7430 MWh',result['summary']['hours']=='743' and result['summary']['energy']=='7430')
    check('generated fixings use the selected curve price and exact cash arithmetic',Decimal(result['summary']['amount'])==Decimal(result['mark'])*7430)
    check('preview is explicitly synthetic and unsaved with captured rules',result['synthetic'] and not result['saved'] and bool(result['clock']['rules']) and len(result['clock']['fingerprint'])==64)
    total_body={**body,'quantity':{'value':'1','specification':next(q['specification'] for q in catalog['quantityChoices'] if q['basis']=='total_energy')},'quantityBasis':'total_energy','allocation':'flat_rate'}
    total=c.call('power/preview',total_body)
    check('total MWh is retained exactly and is never multiplied by delivery hours', total['summary']['energy']=='1' and Decimal(total['summary']['amount'])==Decimal(total['mark']) and total['amounts']['input']['amount']=='1' and total['amounts']['energy']['numerator']=='1' and total['amounts']['energy']['denominator']=='1')
    from fractions import Fraction
    check('rational interval energy conserves total MWh before display rounding',sum((Fraction(int(r['energy']['numerator']),int(r['energy']['denominator'])) for r in total['rows']),Fraction(0))==1)
    c.call('power/preview',{**total_body,'allocation':None},expected=422)
    c.call('power/preview',{**body,'quantityBasis':'total_energy','allocation':'flat_rate'},expected=422)
    c.call('power/preview',{**total_body,'quantity':{**total_body['quantity'],'specification':{**total_body['quantity']['specification'],'code':'missing'}}},expected=422)
    check('missing total allocation, incompatible input units and unavailable exact specifications reject')
    fixed=c.call('power/preview',request('miso'))
    check('fixed EST comparison has 744 hours and 7440 MWh',fixed['summary']['hours']=='744' and fixed['summary']['energy']=='7440')
    fine=c.call('power/preview',request(minutes=5))
    check('5-minute integration preserves hourly economics',fine['summary']==result['summary'] and len(fine['rows'])==743*12)
    hb=c.call('power/preview',{**body,'hourLabels':'HB'})
    check('HB changes labels while preserving UTC identity and economics',hb['summary']==result['summary'] and [r['start'] for r in hb['rows']]==[r['start'] for r in result['rows']] and hb['rows'][0]['label']!=result['rows'][0]['label'])
    peak=c.call('power/preview',request(block='peak'))
    off=c.call('power/preview',request(block='off'))
    check('on-peak and off-peak partition the actual UTC intervals',set(r['start'] for r in peak['rows']).isdisjoint(r['start'] for r in off['rows']) and len(peak['rows'])+len(off['rows'])==len(result['rows']))
    check('reference calendar closure removes exactly one 16-hour peak weekday',peak['summary']['hours']=='352' and peak['holidays']==['2021-03-15'] and peak['holidayCalendar']=='test_business')
    c.call('power/preview',{**body,'holidayDates':['2021-03-16']},expected=400)
    check('clients cannot supply their own holiday list')
    custom=c.call('power/preview',{**body,'priceType':'Custom test','timing':'Custom timing'})
    check('independent classifications retain user-defined values',custom['summary']['index']['priceType']=='Custom test' and custom['summary']['index']['timing']=='Custom timing')
    c.call('power/preview',{**body,'nonHolidays':'1'},expected=422)
    c.call('power/preview',{**body,'priceType':''},expected=422)
    c.call('power/preview',{**body,'numericalPolicy':'invented'},expected=422)
    check('malformed masks, empty classifications and unknown rounding reject')
    c.call('power/preview',{**body,'curve':'local_gas'},expected=422)
    check('gas prices cannot masquerade as prices per MWh')
    c.call('profile',{'profile':'admin'})
    c.call('power/preview',body,expected=403)
    check('access administration does not grant TOU market calculations')
