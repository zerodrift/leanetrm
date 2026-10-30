"""Named schedule/shortcut checks inside the newly owned preview HTTP suite."""


def run_checks(Client, check):
    c = Client()
    c.bootstrap()
    c.call('schedules', expected=403)
    check('schedule sources require a selected test persona')
    c.call('profile', {'profile': 'viewer'})
    library = c.call('schedules')
    check('shortcut date roles and source names are served, not spelled in the browser', [r['code'] for r in library['dateRoles']] == ['delivery_start', 'delivery_end', 'last_trading'] and all(s['name'] for s in library['sources']))
    source = library['sources'][0]
    check('source sequence has exact explicit first and last trading terms',
          source['curve'] == 'future' and len(source['entries']) == 6
          and all(e['eligibleFrom'] and e['lastTradingDate'] for e in source['entries']))
    draft = {'name': 'Gas prompt', 'symbol': 'oe.ng1', 'curve': 'future', 'position': 1,
             'dateRole': 'delivery_start', 'at': '2020-12-28T15:29:00Z'}
    before = c.call('schedules/preview', draft)
    at = c.call('schedules/preview', {**draft, 'at': '2020-12-28T15:30:00Z'})
    check('shortcut advances at exact expiry without creating a contract',
          before['date'] == '2021-01-01' and at['date'] == '2021-02-01'
          and before['contractId'] == source['entries'][0]['contractId']
          and at['contractId'] == source['entries'][1]['contractId'])
    check('resolution carries exact source and rule pins',
          all(at[k] for k in ['sequence', 'references', 'ruleVersion', 'dateRole', 'valuationAt']))
    observation_request = {'curve': 'future', 'selection': {'mode': 'prompt_offset', 'contract': None,
        'promptOffset': 0, 'roll': 'contract_expiry_roll_v1', 'boundaries': None}, 'observations': [
        {'ordinal': 0, 'from': '2021-01-01', 'to': '2021-01-02', 'fixingAt': draft['at']},
        {'ordinal': 1, 'from': '2021-01-02', 'to': '2021-01-03', 'fixingAt': '2020-12-28T15:30:00Z'}]}
    concrete = c.call('schedules/observation-contracts', observation_request)
    check('observation fixing instants resolve concrete dated contracts independently of delivery dates',
          [row['contract']['id'] for row in concrete] == [before['contractId'], at['contractId']]
          and concrete[1]['observation']['from'] == '2021-01-02' and concrete[1]['deliveryFrom'] == '2021-02-01')
    for missing in ['ordinal', 'from', 'to', 'fixingAt']:
        incomplete = {key: value for key, value in observation_request['observations'][0].items() if key != missing}
        c.call('schedules/observation-contracts', {**observation_request, 'observations': [incomplete]}, expected=400)
    check('every observation coordinate must be explicitly supplied')
    exact_request = {**observation_request, 'selection': {'mode': 'exact', 'contract': before['contractKey'],
        'promptOffset': None, 'roll': None, 'boundaries': None}}
    exact = c.call('schedules/observation-contracts', exact_request)
    check('an exact dated contract does not change when prompt rolls',
          all(row['contract'] == before['contractKey'] for row in exact))
    invalid = c.call('schedules/observation-contracts', {**observation_request,
        'selection': {**observation_request['selection'], 'roll': None}}, expected=422)
    check('observation contract resolution never supplies a default roll rule', invalid['code'] == 'contract_selection')
    c.call('schedules/observation-contracts', observation_request, headers={'X-Preview-CSRF': 'invalid'}, expected=400)
    check('observation contract resolution enforces the shared request boundary')
    second = c.call('schedules/preview', {**draft, 'symbol': 'oe.other', 'position': 2})
    check('symbol spelling never supplies an ordinal', second['contractId'] == at['contractId'])
    last = c.call('schedules/preview', {**draft, 'dateRole': 'last_trading'})
    check('date role selects supplied business date, not user timezone', last['date'] == '2020-12-28')
    c.call('schedules/preview', draft, headers={'X-Preview-CSRF': 'invalid'}, expected=400)
    check('shortcut preview enforces request token')
    c.call('schedules/preview', draft, headers={'Origin': 'https://untrusted.invalid'}, expected=403)
    check('shortcut preview enforces same origin')
    c.call('schedules/keep', draft, headers={'X-Preview-CSRF': 'invalid'}, expected=400)
    check('keeping a private shortcut enforces request token')
    market_before = c.call('workspace')
    kept = c.call('schedules/keep', draft)
    check('named shortcut is kept for reuse in this persona session',
          kept['symbol'] == 'oe.ng1' and len(c.call('schedules')['shortcuts']) == 1)
    check('identical keep is idempotent', c.call('schedules/keep', draft) == kept)
    resolved = c.call('schedules/resolve', {'symbol': 'oe.ng1', 'at': '2020-12-28T15:30:00Z'})
    check('shared resolver accepts symbolic reference and a new context', resolved == at)
    conflict = c.call('schedules/keep', {**draft, 'position': 2}, expected=409)
    check('symbol collision cannot mutate saved terms', conflict['code'] == 'shortcut_conflict')
    check('saved definition is unchanged after conflict', c.call('schedules/resolve', {'symbol': 'oe.ng1', 'at': draft['at']}) == before)
    c.call('profile', {'profile': 'editor'})
    check('another persona cannot see or resolve private shortcuts', c.call('schedules')['shortcuts'] == [])
    c.call('schedules/resolve', {'symbol': 'oe.ng1', 'at': draft['at']}, expected=422)
    c.call('profile', {'profile': 'viewer'})
    check('keeping shortcuts does not edit market data', c.call('workspace') == market_before)
    other = Client(); other.bootstrap(); other.call('profile', {'profile': 'viewer'})
    check('another browser session has a separate shortcut library', other.call('schedules')['shortcuts'] == [])
    for label, changes, code in [
        ('missing date role', {'dateRole': None}, 'shortcut_definition'),
        ('invalid symbol', {'symbol': 'ng1'}, 'shortcut_symbol'),
        ('unknown source', {'curve': 'absent'}, 'sequence_source'),
        ('non-UTC context', {'at': '2020-12-28T15:29:00+01:00'}, 'sequence_context'),
        ('uncovered context', {'at': '2020-11-01T00:00:00Z'}, 'sequence_coverage'),
        ('unavailable ordinal', {'position': 6, 'at': '2021-01-01T00:00:00Z'}, 'sequence_horizon'),
    ]:
        error = c.call('schedules/preview', {**draft, **changes}, expected=422)
        check('shortcut rejects ' + label, error['code'] == code)

    calendar = {'name': 'Synthetic weekdays', 'from': '2020-12-01', 'to': '2021-06-01',
                'weekendDays': [0, 6], 'closedDates': ['2021-01-01']}
    monthly = {'name': 'Delivery months', 'symbol': 'oe.ng', 'firstMonth': '2021-01',
               'lastMonth': '2021-04', 'anchor': 'day', 'day': 1, 'shortMonth': 'reject',
               'adjustment': 'unadjusted', 'calendar': calendar}
    c.call('schedules/dates/keep', monthly, headers={'X-Preview-CSRF': 'invalid'}, expected=400)
    check('date schedule retention enforces CSRF')
    dates = c.call('schedules/dates/preview', monthly)
    check('monthly occurrences retain anchor and adjusted dates',
          [d['date'] for d in dates['dates']] == ['2021-01-01', '2021-02-01', '2021-03-01', '2021-04-01']
          and all(d['date'] == d['unadjusted'] for d in dates['dates'])
          and 'businessDays' not in dates)
    c.call('schedules/dates/keep', monthly)
    check('date schedule keep is idempotent', c.call('schedules/dates/keep', monthly) == dates)
    error = c.call('schedules/dates/keep', {**monthly, 'adjustment': 'following'}, expected=409)
    check('saved monthly terms cannot be overwritten', error['code'] == 'date_definition_conflict')
    error = c.call('schedules/keep', {**draft, 'symbol': 'oe.ng'}, expected=409)
    check('rolling and generated date names share a collision boundary', error['code'] == 'date_definition_conflict')
    expr = {'expression': 'oe.ng[0] - 3bd', 'at': draft['at'], 'rollingCalendar': None}
    receipt = c.call('schedules/expression', expr)
    check('business-day movement uses bound closures and preserves source definition',
          receipt['date'] == '2020-12-29' and receipt['calendar'] == calendar
          and receipt['source'] == monthly and receipt['expression'] == expr['expression'])
    rolling = c.call('schedules/expression', {**expr, 'expression': 'oe.ng1 + 1bd', 'rollingCalendar': calendar})
    rolled = c.call('schedules/expression', {**expr, 'expression': 'oe.ng1 + 1bd', 'rollingCalendar': calendar, 'at': '2020-12-28T15:30:00Z'})
    check('rolling reference shifts its selected factor date under a new context',
          rolling['date'] == '2021-01-04' and rolled['date'] == '2021-02-02'
          and rolling['source']['contract'] != rolled['source']['contract'])
    for expression, changes, code in [
        ('oe.ng[0]+0bd', {}, 'date_expression'),
        ('oe.ng[4]', {}, 'date_index'),
        ('oe.ng', {}, 'date_index'),
        ('oe.ng[0]+10000bd', {}, 'no_business_day'),
        ('oe.ng1+1bd', {}, 'calendar_definition'),
    ]:
        error = c.call('schedules/expression', {**expr, 'expression': expression, **changes}, expected=422)
        check('date reference rejects explicit invalid case: ' + expression, error['code'] == code)
    own_calendar = c.call('schedules/expression', {**expr, 'rollingCalendar': calendar})
    check('rolling-reference calendar cannot override a generated schedule binding', own_calendar == receipt)
    contracts = [{'label': 'Month ' + str(i + 1), 'deliveryStart': f'oe.ng[{i}]',
                  'deliveryEnd': f'oe.ng[{i+1}]', 'lastTrading': f'oe.ng[{i}] - 3bd',
                  'cutoffUtc': '15:30'} for i in range(3)]
    curve = {'code': 'scheduled_gas', 'name': 'Synthetic scheduled gas', 'templateCurve': 'future',
             'at': draft['at'], 'rollingCalendar': None, 'contracts': contracts}
    built = c.call('schedules/contracts/preview', curve)
    check('curve contract definitions consume reusable date references',
          built['contracts'][0]['deliveryFrom'] == '2021-01-01'
          and built['contracts'][0]['deliveryTo'] == '2021-02-01'
          and built['contracts'][0]['expiry'] == '2020-12-29T15:30:00+00:00'
          and built['contracts'][0]['deliveryStart']['expression'] == 'oe.ng[0]')
    check('candidate curve keeps exact reference/specification pins',
          built['template'] == next(v for v in market_before['definitions']['curves'] if v['code'] == 'future')
          and built['definitionPin'] and built['referencePin'])
    mixed = c.call('schedules/contracts/preview', {**curve, 'code': 'mixed_dates',
        'rollingCalendar': calendar, 'at': '2020-12-28T15:29:00.1234567Z',
        'contracts': [{**contracts[0], 'lastTrading': 'oe.ng1 - 3bd'}]})
    check('mixed generated and rolling date fields retain calendar and precise context',
          mixed['contracts'][0]['lastTrading']['date'] == '2020-12-29'
          and mixed['rollingCalendar'] == calendar and mixed['at'] == '2020-12-28T15:29:00.1234567+00:00')
    c.call('schedules/contracts/keep', curve, headers={'Origin': 'https://untrusted.invalid'}, expected=403)
    check('draft curve retention enforces same origin')
    kept_curve = c.call('schedules/contracts/keep', curve)
    check('candidate curve retention is idempotent', c.call('schedules/contracts/keep', curve) == kept_curve)
    error = c.call('schedules/contracts/keep', {**curve, 'name': 'Other terms'}, expected=409)
    check('candidate curve cannot be silently overwritten', error['code'] == 'date_definition_conflict')
    for label, changes, code in [
        ('null contract', {'contracts': [None]}, 'contract_dates'),
        ('missing UTC time', {'contracts': [{**contracts[0], 'cutoffUtc': None}]}, 'contract_dates'),
        ('reversed delivery dates', {'contracts': [{**contracts[0], 'deliveryEnd': 'oe.ng[0]'}]}, 'contract_dates'),
        ('overlapping delivery periods', {'contracts': [contracts[0], {**contracts[1], 'deliveryStart': 'oe.ng[0]'}]}, 'contract_dates'),
        ('existing curve code', {'code': 'future'}, 'date_definition_conflict'),
        ('missing specification', {'templateCurve': None}, 'missing_curve'),
        ('missing context', {'at': None}, 'sequence_context'),
    ]:
        error = c.call('schedules/contracts/preview', {**curve, **changes}, expected=409 if code == 'date_definition_conflict' else 422)
        check('contract definitions reject ' + label, error['code'] == code)
    c.call('profile', {'profile': 'editor'})
    private = c.call('schedules')
    check('another persona cannot list date schedules or draft curves', private['dateSchedules'] == [] and private['draftCurves'] == [])
    c.call('schedules/contracts/preview', curve, expected=422)
    c.call('profile', {'profile': 'viewer'})
    check('retained candidate definitions survive explicit persona return', c.call('schedules')['draftCurves'] == [kept_curve])
    private = other.call('schedules')
    check('another browser has no candidate definitions', private['dateSchedules'] == [] and private['draftCurves'] == [])
    check('candidate curve operations leave sealed market inputs unchanged', c.call('workspace') == market_before)
