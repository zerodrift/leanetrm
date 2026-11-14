"""The synthetic indexed crude formula curve, saved, published and previewed over HTTP in an owned preview.

The output for 2027-01 is the business-day average of the prompt future over January, plus the 2:1 weighted December
roll spreads of F against G and H, plus the 2027-01 differential. Each context's price is stated literally."""
import json
import subprocess
from copy import deepcopy
from fractions import Fraction
from pathlib import Path
from uuid import uuid4
from preview_client import PreviewClient, initial_frame
from crude_formula_fixture import CALENDAR, CONTRACT, CURVE, DIFFERENTIAL, LATE_SETTLEMENT, observation

TERMS=[{'ordinal':0,'group':'average','weight':'1','position':1,'lessPosition':None,'period':'calendar_month_of_delivery_v1'},
       {'ordinal':1,'group':'roll','weight':'2','position':1,'lessPosition':2,'period':'trade_month_of_delivery_contract_v1'},
       {'ordinal':2,'group':'roll','weight':'1','position':1,'lessPosition':3,'period':'trade_month_of_delivery_contract_v1'}]
POLICY={'sampling':'context_utc_time','corrections':'exact_context_frame_history_latest_visible'}
OBSERVED={'implementation':'contract_observation_terms_v1','targetFrom':'2027-01-01','targetCount':'1','days':'business_days_v1',
          'calendar':CALENDAR,'roll':'contract_expiry_roll_v1',**POLICY}
DEFINITION={'code':'synthetic_crude_formula','name':'Synthetic indexed crude','output':'output','nodes':[
    {'id':'source','kind':'source','parameters':{'curve':CURVE},'inputs':{}},
    {'id':'crude','kind':'contract_observations','parameters':OBSERVED,'inputs':{'source':'source'},'observationTerms':TERMS},
    {'id':'differential','kind':'source','parameters':{'curve':DIFFERENTIAL},'inputs':{}},
    {'id':'select','kind':'select','parameters':{'mode':'positions','first':'1','count':'1','binding':'context'},'inputs':{'source':'differential'}},
    {'id':'dates','kind':'dates','parameters':{'mode':'context',**POLICY},'inputs':{}},
    {'id':'sample','kind':'sample','parameters':{},'inputs':{'selection':'select','dates':'dates'}},
    {'id':'total','kind':'weighted_panels','parameters':{'aggregation':'signed_sum_v1','alignment':'exact_delivery_intervals_v1'},
     'inputs':{'formula':'crude','differential':'sample'},'panelWeights':[{'sourcePort':'formula','weight':'1'},{'sourcePort':'differential','weight':'1'}]},
    {'id':'output','kind':'output','parameters':{},'inputs':{'values':'total'}}]}


def observed(definition):
    return next(node for node in definition['nodes'] if node['id']=='crude')


def run_checks(url,query,manifest,dotnet,owned_root,environment):
    catalog,tenant,workspace=(manifest[key] for key in ('catalogId','tenantId','workspaceId'))
    client=PreviewClient(url)
    client.bootstrap()
    client.call('profile',{'profile':'viewer'})
    # The catalog offers the operator with its admitted periods and day rules, so no client states them.
    operator=next(o for o in client.call('custom-curves')['operators'] if o['kind']=='contract_observations')
    fields={field['key']:field for field in operator['fields']}
    assert (operator['label'],operator['outputType'],[p['name'] for p in operator['inputPorts']])==('Contract observations','panel',['source']),operator
    assert [o['value'] for o in fields['days']['options']]==['calendar_days_v1','business_days_v1'],fields['days']
    assert fields['calendar']['when']=={'key':'days','values':['business_days_v1']},fields['calendar']
    assert [o['value'] for o in fields['observationTerms']['options']]==['calendar_month_of_delivery_v1','trade_month_of_delivery_contract_v1'],fields['observationTerms']
    assert all(o['label'] for field in fields.values() for o in field['options'])

    client.call('profile',{'profile':'editor'})
    def frame(day):
        return initial_frame(client.call('workspace'),day)
    saved=client.call('custom-curves/save',{'requestId':str(uuid4()),'snapshotId':frame('2026-12-01')['id'],'definition':deepcopy(DEFINITION),'layout':{}})
    curve=saved['id']
    assert saved['kind']=='observation_formula' and observed(saved['definition'])['parameters']==OBSERVED,saved
    assert query(f"SELECT calendar_code FROM market_data.custom_curve_node_calendars WHERE(catalog_id,curve_id,version,node_id)=('{catalog}','{curve}',1,'crude');")==CALENDAR
    client.call('profile',{'profile':'approver'})
    published=client.call('custom-curves/publish',{'requestId':str(uuid4()),'id':curve,'version':1,'generation':1})
    assert published['state']=='published'

    client.call('profile',{'profile':'viewer'})
    def preview(day,definition=None,expected=200,snapshot=None):
        body={'definition':definition or published['definition'],'snapshotId':snapshot or frame(day)['id']}
        if definition is None:body|={'id':curve,'version':1}
        return client.call('custom-curves/preview',body,expected=expected)
    def price(result,expected):
        # The point's exact rational is the stated decimal; the row presents the same value.
        point=result['points'][0]['price']
        assert len(result['rows'])==1 and Fraction(int(point['numerator']),int(point['denominator']))==Fraction(expected),(point,result['rows'])
        assert Fraction(result['rows'][0][1])==Fraction(expected),result['rows']
    def live(result):
        return {d['contract']['id'] for d in result['dependencies']}
    def known():
        return client.call('date-schedules/context',{'effectiveOn':'2027-01-25','mode':'current','knownAt':None})['context']['knownAt']
    def historical(knownAt):
        client.call('market/context',{'effectiveOn':'2027-01-25','knownAt':knownAt,'mode':'at','selection':'latest_correction'})
        return initial_frame(client.call('workspace/navigation'),'2027-01-25')['id']

    # Review focus 2: until G's 2027-01-19 settlement is recorded, the finished average refuses rather than averaging four days.
    missing=preview('2027-01-25',expected=422)
    assert missing['code']=='recipe_input' and '2027-01-19' in missing['message'],missing
    unsettled=known()
    day,contract,value=LATE_SETTLEMENT
    late=str(uuid4())
    query(observation(late,contract,day,value))

    # Before the pricing period every observation is projected at today's mark of the contract it selects.
    before=preview('2026-12-01')
    price(before,'68.46')
    assert live(before)=={CONTRACT[c] for c in 'FGHD'},before['dependencies']
    # The December roll window is settled history; January is still projected.
    settled=preview('2026-12-22')
    price(settled,'68.46')
    assert live(settled)=={CONTRACT[c] for c in 'GHD'},settled['dependencies']
    # Inside January: two settled days, the context day frozen at its own close, and two later days at today's mark of H.
    # G's only remaining observation is today's, so G is no longer live.
    during=preview('2027-01-20')
    price(during,'68.52')
    assert live(during)=={CONTRACT[c] for c in 'HD'},during['dependencies']
    # A context frame without the context day's own observation of its selected contract refuses; no other mark is taken,
    # not even the 2027-01-22 settlement of H read as history at other contexts.
    incomplete=preview('2027-01-22',expected=422)
    assert incomplete['code']=='recipe_input' and '2027-01-22' in incomplete['message'],incomplete
    # After the pricing period the formula is fixed; only the differential remains live.
    after=preview('2027-01-25')
    price(after,'68.50')
    assert live(after)=={CONTRACT['D']},after['dependencies']
    # A consumer reads the published curve through a published source and prices the same cell.
    consumer={'code':'synthetic_crude_consumer','name':'Synthetic crude consumer','output':'output','nodes':[
        {'id':'formula','kind':'published_source','parameters':{'selection':'all_output_points_v1'},'inputs':{},
         'publishedSource':{'catalogId':catalog,'curveId':curve,'version':1,'outputNode':'output'}},
        {'id':'output','kind':'output','parameters':{},'inputs':{'values':'formula'}}]}
    for day,expected in (('2026-12-01','68.46'),('2026-12-22','68.46'),('2027-01-20','68.52'),('2027-01-25','68.50')):
        price(preview(day,consumer),expected)
    # The consumer's preview prepares the published curve with the same frozen context day and the same live inputs.
    consumed=preview('2027-01-20',consumer)
    assert live(consumed)=={CONTRACT[c] for c in 'HD'},consumed['dependencies']
    # Calendar availability plans the consumer's published input at the sampling instant of every observation day, past
    # days included, so every close of the fixture prices both curves.
    closes={'2026-12-01','2026-12-22','2027-01-20','2027-01-25'}
    for definition in (published['definition'],consumer):
        dates=client.call('market/dates',{'curve':None,'timeZone':'UTC','session':frame('2026-12-01')['close']['session'],'definition':definition})
        assert closes<={row['date'] for row in dates['dates']},(definition['code'],dates)
    # The knowledge view before the settlement was recorded still refuses.
    unknown=preview('2027-01-25',snapshot=historical(unsettled),expected=422)
    assert (unknown['code'],unknown['message'])==(missing['code'],missing['message']),unknown
    client.call('market/current',{})
    projections(client,manifest,frame('2026-12-01'),curve,consumer,dotnet,owned_root,environment)
    print('PASS contract observation curve: projected, settling and settled formula prices, live inputs by context, a published-source consumer, calendar availability, a refused incomplete history and a refused incomplete context frame',flush=True)

    # Review focus 5: a correction of a settlement already read is a new revision with a later knowledge time.
    recorded=known()
    correction=str(uuid4())
    query(f"""INSERT INTO market_data.observations SELECT catalog_id,'{correction}',definition_revision_id,series_id,reference_catalog_id,reference_revision_id,
        source_code,source_event_id,'{correction}',role,publication_state,observed_at,greatest(observed_at,clock_timestamp()),69.55,id
        FROM market_data.observations WHERE catalog_id='{catalog}' AND id='{late}';""")
    price(preview('2027-01-25'),'68.51')
    price(preview('2027-01-25',consumer),'68.51')
    price(preview('2027-01-25',snapshot=historical(recorded)),'68.50')
    client.call('market/current',{})
    print('PASS contract observation correction: the latest view averages the corrected settlement and the earlier knowledge view reproduces the original',flush=True)

    # Refusals: each required setting, the business-day calendar, and a body that differs from the saved version only in a weight.
    context=frame('2027-01-20')['id']
    for parameter in [p for p in OBSERVED if p!='calendar']:
        draft=deepcopy(DEFINITION);del observed(draft)['parameters'][parameter]
        assert preview(None,draft,422,context)['code']=='recipe_definition',parameter
    # A term whose position is blank reaches validation, which names the term and its position.
    draft=deepcopy(DEFINITION);observed(draft)['observationTerms'][1]['position']=None
    refused=preview(None,draft,422,context)
    assert refused['code']=='recipe_definition' and refused['message'].startswith('Observation term 2 position:'),refused
    # A position typed as anything but a whole number is refused for the term, by preview and by save; an absent member is a
    # malformed request.
    def answer(path,body):
        try:return {'status':422,**{key:value for key,value in client.call(path,body,expected=422).items() if key in ('code','message')}}
        except AssertionError as error:return str(error)
    typed={}
    for member,value in (('lessPosition','2e'),('position',1.5),('position',3000000000)):
        draft=deepcopy(DEFINITION);observed(draft)['observationTerms'][1][member]=value
        typed[member,'preview']=answer('custom-curves/preview',{'definition':draft,'snapshotId':context})
        client.call('profile',{'profile':'editor'})
        typed[member,'save']=answer('custom-curves/save',{'requestId':str(uuid4()),'snapshotId':context,'definition':draft,'layout':{}})
        client.call('profile',{'profile':'viewer'})
    whole={'status':422,'code':'recipe_definition','message':'An observation term position must be a whole number from 1 to 120.'}
    assert all(answer==whole for answer in typed.values()),typed
    draft=deepcopy(DEFINITION);del observed(draft)['observationTerms'][1]['position']
    assert client.call('custom-curves/preview',{'definition':draft,'snapshotId':context},expected=400)['code']=='request_format'
    draft=deepcopy(DEFINITION);del observed(draft)['parameters']['calendar']
    refused=preview(None,draft,422,context)
    assert (refused['code'],refused['message'])==('custom_curve_definition','Business-day windows require an exact reference calendar.'),refused
    reweighted=deepcopy(published['definition']);observed(reweighted)['observationTerms'][1]['weight']='3'
    refused=client.call('custom-curves/preview',{'definition':reweighted,'snapshotId':context,'id':curve,'version':1},expected=422)
    assert (refused['code'],refused['message'])==('custom_curve_definition','The selected graph or exact references differ from its retained version.'),refused
    # A reader without access to the market catalog is denied.
    scope=query(f"DELETE FROM security.market_catalog_scopes WHERE catalog_id='{catalog}' RETURNING tenant_id||','||workspace_id;")
    try:
        assert client.call('custom-curves/preview',{'definition':published['definition'],'snapshotId':context,'id':curve,'version':1},expected=403)['code']=='access_denied'
    finally:
        query(f"INSERT INTO security.market_catalog_scopes VALUES('{catalog}','{tenant}','{workspace}');")
    assert scope==f'{tenant},{workspace}',scope
    price(preview('2027-01-25'),'68.51')
    print('PASS contract observation refusals: every required setting, a blank term position, a position typed as text, a fraction or beyond the 32-bit range, an absent position member, the business-day calendar, a reweighted saved version and a reader without market access',flush=True)


def projections(client,manifest,working,formula,consumer,dotnet,owned_root,environment):
    """D391: a published curve that reads contract observation terms, directly or through a published source, is no trade
    projection. The consumer and an ordinary differential curve are published beside the formula curve; the managed check
    asks projection choices and projection admission for all three under the trading read of the working frame."""
    ordinary={'code':'synthetic_crude_mark','name':'Synthetic crude differential mark','output':'output','nodes':[
        {'id':'differential','kind':'source','parameters':{'curve':DIFFERENTIAL},'inputs':{}},
        {'id':'select','kind':'select','parameters':{'mode':'positions','first':'1','count':'1','binding':'context'},'inputs':{'source':'differential'}},
        {'id':'dates','kind':'dates','parameters':{'mode':'context',**POLICY},'inputs':{}},
        {'id':'sample','kind':'sample','parameters':{},'inputs':{'selection':'select','dates':'dates'}},
        {'id':'output','kind':'output','parameters':{},'inputs':{'values':'sample'}}]}
    def publish(definition,prior=None):
        client.call('profile',{'profile':'editor'})
        saved=client.call('custom-curves/save',{'requestId':str(uuid4()),'snapshotId':working['id'],'definition':deepcopy(definition),'layout':{},
            **({'id':prior['id'],'version':prior['version'],'generation':prior['generation'],'expectedState':prior['state']} if prior else {})})
        client.call('profile',{'profile':'approver'})
        published=client.call('custom-curves/publish',{'requestId':str(uuid4()),'id':saved['id'],'version':saved['version'],'generation':saved['generation']})
        assert published['state']=='published',published
        return published
    first=publish(ordinary)
    # The ordinary curve's next version reads the formula curve: its first version stays a projection, but is not the version
    # in force under the context.
    reading={**ordinary,'nodes':[{'id':'formula','kind':'published_source','parameters':{'selection':'all_output_points_v1'},'inputs':{},
        'publishedSource':{'catalogId':manifest['catalogId'],'curveId':formula,'version':1,'outputNode':'output'}},
        {'id':'output','kind':'output','parameters':{},'inputs':{'values':'formula'}}]}
    assert publish(reading,first)['version']==2
    curves={'observation':formula,'consumer':publish(consumer)['id'],'ordinary':first['id']}
    client.call('profile',{'profile':'viewer'})
    path=Path(owned_root)/'contract-observation-projections.json'
    path.write_text(json.dumps({'frame':working,**curves}))
    subprocess.run([str(dotnet),'run','--project','tests/OpenEtrm.Api.Checks','--','--contract-observation-projections',str(path)],
                   cwd=Path(__file__).resolve().parents[1],env=environment,check=True,timeout=180)
    # A swap names its projection in the request, never through the choices: a forged reference is refused by swap observation
    # options, preview, booking, draft pricing and scheduled pricing before any other term is read.
    client.call('profile',{'profile':'trader'})
    context={'snapshotId':working['id'],'convention':'independent_side_financial_swap_v2','knowledgeMode':'current'}
    delivery={'deliveryFrom':'2027-01-01','deliveryTo':'2027-02-01'}
    refused={'code':'custom_projection','message':'A curve that reads contract observation terms has no qualified trade projection.'}
    def attempt(route,body):
        try:return client.call(route,body,expected=422)
        except AssertionError as error:return str(error)
    answers={}
    for name in ('observation','consumer'):
        projection={'catalogId':manifest['catalogId'],'curveId':curves[name],'version':1,'outputNode':'output','position':None,
                    'selection':'containing_delivery_interval_v1','implementation':'published_custom_curve_projection_v1'}
        floating={'curve':DIFFERENTIAL,'index':'synthetic_crude_differential_index','currency':'usd',**delivery,'resetFrequency':'monthly',
                  'averagingMethod':'none','spread':'0','settlement':{'mode':'curve_target','index':None,'projectionPolicy':None},'customProjection':projection}
        fixed={'price':'70','currency':'usd',**delivery}
        terms={**context,'operationId':str(uuid4()),'bookId':str(uuid4()),'quantity':'1000','fixedCoefficient':'-1','discounting':False,
               'fixed':fixed,'floating':floating,'definitionRevisionId':manifest['revisionId'],'commodity':'synthetic_crude'}
        quantity={'revision':{'catalogId':manifest['referenceCatalogId'],'revisionId':manifest['referenceRevisionId']},'code':'synthetic_crude_quantity'}
        side={'basis':'per_period','quantity':quantity,'amount':None,'allocation':None,'priceApplication':'contract_quantity','power':None}
        scheduled={**terms,'floating':None,'fixed':None,'scheduled':{'implementation':'scheduled_financial_swap_v1',
            'first':{**side,'periods':[{'amount':'1000','fixed':fixed,'floating':None}]},'second':{**side,'periods':[{'amount':'1000','fixed':None,'floating':floating}]}}}
        draft={'context':context,'definitionRevisionId':manifest['revisionId'],'commodity':'synthetic_crude','quantity':{'value':'1000','specification':quantity},
               'fixedCoefficient':'-1','discounting':False,'fixed':fixed,'floating':floating}
        options={'context':context,'definitionRevisionId':manifest['revisionId'],'commodity':'synthetic_crude','curve':DIFFERENTIAL,
                 'index':'synthetic_crude_differential_index','resetFrequency':'monthly','averagingMethod':'none',**delivery,
                 'settlement':{'mode':'curve_target','index':None,'projectionPolicy':None},'customProjection':projection}
        for route,body in (('swaps/observation-options',options),('swaps/preview',terms),('swaps/book',terms),('swaps/price',draft),('swaps/scheduled-price',{'terms':scheduled,'priceMode':'agreed','parRatePolicy':None})):
            answers[name,route]=attempt(route,body)
    assert all(answer==refused for answer in answers.values()),answers
    print('PASS contract observation swap admission: swap observation options, preview, booking, draft pricing and scheduled pricing refuse a forged projection of the observation curve and of its published-source consumer',flush=True)
    client.call('profile',{'profile':'viewer'})
