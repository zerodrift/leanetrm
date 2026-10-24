"""Interval-curve HTTP checks against the launcher's disposable synthetic database."""
import json
from datetime import date, timedelta
from decimal import Decimal
from fractions import Fraction
from uuid import uuid4
from zoneinfo import ZoneInfo

from exact_values import fraction, instant, rounded
from interval_curve_fixture import mark, marked_hours
from interval_price_oracle import shaped_prices, compare as assert_oracle
from power_collection_fixture import Collector
from preview_client import PreviewClient

DAY_FROM, DAY_TO, DAYS_TO = '2031-11-02T04:00:00Z', '2031-11-03T05:00:00Z', '2031-11-04T05:00:00Z'
NOVEMBER, DECEMBER, JANUARY = '2031-11-01T04:00:00Z', '2031-12-01T05:00:00Z', '2032-01-01T05:00:00Z'
SHAPE_RANGES = [('2020-12-01T05:00:00Z', '2021-01-01T05:00:00Z'), ('2031-03-01T05:00:00Z', '2031-04-01T04:00:00Z'), (NOVEMBER, JANUARY)]
# The first fixture frame values at 16:00 UTC on 15 December 2020: the 14th is delivered, and of the 15th's hours from
# 15:00 UTC the first has ended, its actual due an hour later, and thirteen are live.
VALUED_AT = '2020-12-15T16:00:00Z'
DELIVERED_FROM, DELIVERED_TO = '2020-12-14T05:00:00Z', '2020-12-15T05:00:00Z'
ENDING_FROM, ENDING_TO = '2020-12-15T15:00:00Z', '2020-12-16T05:00:00Z'


def display(value):
    return str(rounded(value, 6))


def price(point):
    return fraction(point['price'])


def mean(points):
    """The exact duration-weighted mean price of the points."""
    hours = [Fraction(int((instant(p['interval']['end']) - instant(p['interval']['start'])).total_seconds()), 3600) for p in points]
    return sum(h * price(p) for h, p in zip(hours, points)) / sum(hours)


def segments(value):
    """One value per local hour of the shaped months, from its New York hour of day."""
    zone, result = ZoneInfo('America/New_York'), []
    for start, end in SHAPE_RANGES:
        first, last = instant(start), instant(end)
        hours = int((last - first).total_seconds() // 3600)
        result.append({'start': start, 'values': [value((first + timedelta(hours=i)).astimezone(zone).hour) for i in range(hours)]})
    return result


def run_checks(url, query, manifest):
    curves = manifest['intervalCurves']
    marked, observations = curves['marked'], curves['observations']
    ordinals = {start: n for n, (start, *_) in enumerate(marked_hours())}
    passed = 0
    oracle_counts=[]
    def compare_oracle(points,expected):
        assert_oracle(points,expected)
        oracle_counts.append(len(points))

    def check(name, condition):
        nonlocal passed
        assert condition, name
        passed += 1
        print('PASS interval curves http: ' + name, flush=True)

    def marks(start, end):
        """The fixture marks and mark observations of the marked curve's hours in [start, end)."""
        hours = [n for at, n in sorted(ordinals.items()) if instant(start) <= at < instant(end)]
        return [Fraction(mark(n)) for n in hours], [[observations[n]] for n in hours]

    anonymous = PreviewClient(url)
    anonymous.bootstrap()
    anonymous.call('interval-curves/catalog', {'snapshotId': '00000000-0000-0000-0000-000000000000'}, expected=403)
    check('a session without a profile is refused before any snapshot or read', True)

    client = PreviewClient(url)
    client.bootstrap()
    client.call('profile', {'profile': 'viewer'})
    first_frame = client.call('workspace')['frames'][0]
    frame = first_frame['id']

    def values(start=DAY_FROM, end=DAY_TO, code=marked, known_at=None, resolve=False, aggregate=None, snapshot=None, expected=200, shock=None):
        return client.call('interval-curves/values', {'snapshotId': snapshot or frame, 'curve': code, 'from': start, 'to': end,
                                                      'knownAt': known_at, 'resolve': resolve, 'aggregate': aggregate, 'shock':shock}, expected=expected)

    def refused(error, **request):
        return values(expected=422, **request)['code'] == error

    def period(snapshot):
        return client.call('period-curves/values',{'snapshotId':snapshot,'curve':curves['capacity'],'from':'2021-01-01T00:00:00Z','to':'2021-02-01T00:00:00Z'})
    before_fix=period(frame)['points']
    fixed_frame=next(f['id'] for f in client.call('workspace')['frames'] if instant(f['at'])>=instant('2020-12-16T12:00:00Z'))
    after_fix=period(fixed_frame)['points']
    check('capacity period uses its live mark before publication and its exact qualified published fixing afterward',
          len(before_fix)==len(after_fix)==1 and before_fix[0]['state']=='projected' and price(before_fix[0])==5
          and after_fix[0]['state']=='publishedfixing' and after_fix[0]['publication']=='published_fixing'
          and price(after_fix[0])==4 and after_fix[0]['observationIds']==[curves['capacityFixing']])
    missing_frame=next(f['id'] for f in client.call('workspace')['frames'] if instant(f['at'])==instant(curves['missingCapacityAt']))
    missing=client.call('period-curves/values',{'snapshotId':missing_frame,'curve':curves['capacity'],'from':'2021-01-01T00:00:00Z','to':'2021-02-01T00:00:00Z'},expected=422)
    check('a due absent capacity fixing returns a structured error before serialization',missing['code']=='index_fixing_missing')
    anonymous.call('period-curves/values',{'snapshotId':frame,'curve':curves['capacity'],'from':'2021-01-01T00:00:00Z','to':'2021-02-01T00:00:00Z'},expected=403)
    catalog = client.call('interval-curves/catalog', {'snapshotId': frame})
    entries = {c['code']: c for c in catalog['curves']}
    entry = entries[marked]
    check('the catalog lists the marked hourly curve, which resolves actuals, at the database clock',
          entry['kind'] == 'marked' and entry['intervalMinutes'] == 60 and entry['clock'] == 'synthetic_eastern'
          and entry['quote'] == 'test_power_outright' and entry['resolvable'] is True
          and instant(catalog['knownAt']) <= instant(client.call('interval-series')['knownAt']))
    uncovered = curves['uncovered']
    check('a curve whose definition cannot resolve is listed as unavailable with its reason, beside the others',
          set(entries) == {marked, curves['shaped'], curves['scaled'], uncovered}
          and entries[uncovered] == {'code': uncovered, 'kind': 'shaped',
                                     'unavailable': 'Calendar synthetic_eastern_holidays does not cover the whole delivery period.'}
          and refused('power_calendar', code=uncovered, start=JANUARY, end='2032-01-02T05:00:00Z', known_at=catalog['knownAt']))

    day = values()
    points = day['points']
    prices = [price(p) for p in points]
    expected, lineage = marks(DAY_FROM, DAY_TO)
    check('a 25-hour day returns 25 exact marked prices', len(points) == 25
          and prices == expected and all(p['price']['display'] == display(v) for p, v in zip(points, prices)))
    labels = [p['interval'] for p in points[1:3]]
    check('the repeated hour is two points told apart by occurrence',
          [(i['operatingDate'], i['hourLabel'], i['occurrence']) for i in labels] == [('2031-11-02', 1, 1), ('2031-11-02', 1, 2)]
          and labels[0]['start'] != labels[1]['start'])
    check('each price names the observation it came from', [p['observationIds'] for p in points] == lineage)
    check('a window returns only its intervals', [p['interval'] for p in values('2031-11-02T05:00:00Z', '2031-11-02T08:00:00Z')['points']]
          == [p['interval'] for p in points[1:4]])

    client.call('profile', {'profile': 'editor'})
    edited = client.call('corrections', {'snapshotId': frame, 'observationId': observations[3], 'value': '99.125'})
    corrected = values(snapshot=edited['frame']['id'])['points'][3]
    original = values()['points'][3]
    check('a correction reaches only the frame that selects it',
          (price(corrected), corrected['observationIds']) == (Fraction('99.125'), [edited['observationId']])
          and original['observationIds'] == [observations[3]] and price(original) == Fraction(mark(3)))

    check('misaligned, empty and oversized windows are refused',
          refused('interval_curve_window', start='2031-11-02T04:30:00Z') and refused('interval_curve_window', end=DAY_FROM)
          and refused('interval_curve_window', end='2036-11-03T05:00:00Z'))
    check('a window the curve does not cover throughout is refused', refused('interval_curve_coverage', end='2031-11-05T05:00:00Z'))
    check('a date-only curve is not an interval curve', refused('interval_contract', code='local_power'))

    navigation = client.call('workspace/navigation')
    evaluated = {instant(p['interval']['start']): p['interval'] for p in client.call('evaluate', {'snapshotId': frame, 'curve': marked})['points']}
    check('the classic market list omits interval curves while evaluate labels their intervals',
          all(c['code'] != marked for c in navigation['curves']) and all(evaluated[instant(p['interval']['start'])] == p['interval'] for p in points[:2]))
    live, _ = marks(VALUED_AT, ENDING_TO)
    check('the first frame selects only the intervals live at its valuation instant, and a plain read refuses delivered ones',
          instant(first_frame['at']) == instant(VALUED_AT)
          and sorted(at for at in evaluated if at < instant(DAY_FROM)) == [instant(VALUED_AT) + timedelta(hours=h) for h in range(13)]
          and [price(p) for p in values(VALUED_AT, ENDING_TO)['points']] == live
          and refused('interval_delivered', start=DELIVERED_FROM, end=DELIVERED_TO) and refused('interval_delivered', start=ENDING_FROM, end=ENDING_TO))

    past = client.call('interval-series')['knownAt']
    client.call('market/context', {'effectiveOn': '2020-12-18', 'knownAt': past, 'mode': 'at', 'selection': 'latest_correction'})
    historical = client.call('interval-curves/catalog', {'snapshotId': frame}, expected=422)['code'] == 'interval_curve_historical' \
        and refused('interval_curve_historical')
    client.call('market/current', {})
    check('a historical session refuses interval curve reads', historical)

    shaped, scaled, series = curves['shaped'], curves['scaled'], curves['shapeSeries']
    blocks = curves['blocks']
    client.call('profile', {'profile': 'editor'})

    def revise(series_id, expected, parts, reason):
        return client.call('interval-series/revise', {'requestId': str(uuid4()), 'seriesId': series_id, 'expectedRevision': expected,
                                                      'reason': reason, 'segments': parts})
    revise(series['additive'], 0, segments(lambda h: str(Decimal(h - 12) / 4)), 'Synthetic hourly offsets')
    revise(series['multiplicative'], 0, segments(lambda h: str(1 + Decimal(h) / 32)), 'Synthetic hourly factors')
    first_cut = client.call('interval-series')['knownAt']

    def curve(code, start, end, known_at, expected=200):
        return values(start, end, code, known_at, expected=expected)

    def months(points):
        return {(b, entry['contractId']): entry['mark'] for b, entries in blocks.items() for entry in entries}, \
               {key: [p for p in points if (p['block']['curve'], p['block']['contractId']) == key] for key in
                {(p['block']['curve'], p['block']['contractId']) for p in points}}

    check('the catalog lists both shaped curves with their method, blocks and shape series',
          entries[shaped]['kind'] == 'shaped' and entries[shaped]['method'] == 'additive' and entries[scaled]['method'] == 'multiplicative'
          and [(b['curve'], b['membership'], b['touProduct'], b['touVersion']) for b in entries[shaped]['blocks'] if b['curve'] in (curves['off'],curves['peak'])]
          == [(curves['off'], 'off', 'pjm', 1), (curves['peak'], 'peak', 'pjm', 1)]
          and entries[shaped]['shapeSeries'] == series['additive'] and entries[shaped]['resolvable'] is True)

    whole = curve(shaped, NOVEMBER, JANUARY, first_cut)
    marks_by_block, grouped = months(whole['points'])
    check('every November and December block averages exactly to its mark over its actual hours',
          len(whole['points']) == 721 + 744 and len(grouped) == 4
          and all(mean(points) == Fraction(marks_by_block[key]) for key, points in grouped.items()))
    oracle = shaped_prices(date(2031,11,1),date(2031,12,1),{'peak':60,'off':40},'additive',lambda _,h: Fraction(h-12,4),[date(2031,11,27)])
    oracle.update(shaped_prices(date(2031,12,1),date(2032,1,1),{'peak':70,'off':45},'additive',lambda _,h: Fraction(h-12,4)))
    compare_oracle(whole['points'],oracle)
    check('independent fractions oracle matches every additive HTTP cell across both months',True)
    by_start = {instant(p['interval']['start']): p for p in whole['points']}
    check('a synthetic weekday holiday prices its peak-window hours from the off-peak block',
          by_start[instant('2031-11-27T20:00:00Z')]['block']['curve'] == curves['off']
          and by_start[instant('2031-11-26T20:00:00Z')]['block']['curve'] == curves['peak'])
    check('each shaped price keeps its block mark observation and the shape revision that set it',
          all(p['block']['observationIds'] == [next(e['observationId'] for e in blocks[p['block']['curve']] if e['contractId'] == p['block']['contractId'])]
              and p['shapeRevision'] == 1 for p in whole['points']))
    check('each shaped display is the half-even six-place rounding of its exact price',
          all(p['price']['display'] == display(price(p)) for p in whole['points']))
    check('the response names its shape series, knowledge cutoff and the knowledge context it read',
          instant(whole['knownAt']) == instant(first_cut)
          and (whole['shapeSeries']['id'], whole['shapeSeries']['code']) == (series['additive'], 'test_power_hourly_shape')
          and len(whole['shapeSeries']['knowledgeContextIds']) == 1)
    cut = curve(shaped, '2031-11-10T05:00:00Z', '2031-11-12T05:00:00Z', first_cut)['points']
    check('a window cutting the month prices exactly as the whole month', len(cut) == 48 and all(p == by_start[instant(p['interval']['start'])] for p in cut))
    late=curve(shaped,'2032-01-01T04:00:00Z','2032-01-01T05:00:00Z',first_cut)['points']
    check('a late operating-month hour selects its local period even when its UTC date is next month',
          len(late)==1 and late[0]==by_start[instant('2032-01-01T04:00:00Z')])
    multiplied = curve(scaled, NOVEMBER, DECEMBER, first_cut)['points']
    marks_by_block, grouped = months(multiplied)
    check('multiplicative prices are exact fractions that average to each mark',
          any(p['price']['denominator'] != '1' for p in multiplied) and all(mean(points) == Fraction(marks_by_block[key]) for key, points in grouped.items()))

    compare_oracle(multiplied,shaped_prices(date(2031,11,1),date(2031,12,1),{'peak':60,'off':40},'multiplicative',lambda _,h: Fraction(32+h,32),[date(2031,11,27)]))
    check('independent fractions oracle matches every multiplicative HTTP cell',True)
    base_resolved=values(NOVEMBER,JANUARY,shaped,first_cut,resolve=True)['points']
    compare_oracle(base_resolved,oracle)
    target=next(b for b in blocks[curves['peak']] if b['month']=='2031-12')
    for up in (True,False):
        shock={'seriesId':target['seriesId'],'quote':'test_power_outright','size':'1','convention':'absolute_quote_input_symmetric_v1','up':up}
        shocked=values(NOVEMBER,JANUARY,shaped,first_cut,resolve=True,shock=shock)['points']
        expected=dict(oracle)
        expected.update(shaped_prices(date(2031,12,1),date(2032,1,1),{'peak':70,'off':45},'additive',lambda _,h:Fraction(h-12,4),bump={'peak':1 if up else -1}))
        compare_oracle(shocked,expected)
    invalid_shock={**shock,'size':None}
    check('an explicit null bump is a structured numeric refusal',values(NOVEMBER,JANUARY,shaped,first_cut,resolve=True,shock=invalid_shock,expected=422)['code']=='numeric_format')
    check('independent fractions oracle matches every base/up/down resolved HTTP cell',True)
    for final in curves['frozenObservations']:
        query(f"INSERT INTO market_data.observations VALUES('{manifest['catalogId']}','{final['id']}','{manifest['revisionId']}','{final['seriesId']}','{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}','fixture','synthetic_pre_delivery_{final['id']}','1','final_contract_settlement','final','{final['observedAt']}',clock_timestamp(),{final['value']},NULL);")
    frozen_frame=next(f['id'] for f in client.call('workspace')['frames'] if instant(f['at'])==instant(curves['frozenAt']))
    frozen_known=client.call('interval-series')['knownAt']
    march_from,march_to='2031-03-01T05:00:00Z','2031-04-01T04:00:00Z'
    modeled=values(march_from,march_to,shaped,frozen_known,resolve=True,snapshot=frozen_frame)['points']
    compare_oracle(modeled,shaped_prices(date(2031,3,1),date(2031,4,1),{'peak':58,'off':38},'additive',lambda _,h:Fraction(h-12,4)))
    check('explicit synthetic pre-delivery contract finals produce modeled cells with missing live market-risk coverage',
          len(modeled)==743 and all(p['state']=='modeled' and p['publication']=='final_settlement_anchor' and p['liveMarketRiskCoverage']=='frozen_anchor_only' and p['actual'] is None for p in modeled))
    check('the raw shaped endpoint refuses to hide frozen-anchor state',values(march_from,march_to,shaped,frozen_known,snapshot=frozen_frame,expected=422)['code']=='interval_delivered')
    check('a later-certified final is excluded by the earlier shape knowledge cutoff',values(march_from,march_to,shaped,first_cut,resolve=True,snapshot=frozen_frame,expected=422)['code']=='frozen_fact')
    revised = '2031-11-10T12:00:00Z'
    revise(series['additive'], 1, [{'start': revised, 'values': ['9', '9', '9']}], 'Synthetic partial correction')
    second_cut = client.call('interval-series')['knownAt']
    check('a knowledge cutoff keeps its shape after a later revision', curve(shaped, NOVEMBER, JANUARY, first_cut) == whole)
    mixed = curve(shaped, NOVEMBER, DECEMBER, second_cut)['points']
    marks_by_block, grouped = months(mixed)
    changed = [instant(p['interval']['start']) for p in mixed if p['shapeRevision'] == 2]
    check('a partial revision mixes shape revisions per interval and recalibrates to the same marks',
          changed == [instant(revised) + timedelta(hours=h) for h in range(3)]
          and all(mean(points) == Fraction(marks_by_block[key]) for key, points in grouped.items()))

    revise(series['additive'], 2, [{'start': '2031-11-29T05:00:00Z', 'values': [None]}], 'Synthetic withdrawal')
    third_cut = client.call('interval-series')['knownAt']
    check('a withdrawn shape interval refuses its block but not another month',
          curve(shaped, NOVEMBER, DECEMBER, third_cut, 422)['code'] == 'interval_shape_coverage'
          and len(curve(shaped, DECEMBER, JANUARY, third_cut)['points']) == 744)
    revise(series['additive'], 3, [{'start': '2031-11-29T05:00:00Z', 'values': ['-3']}], 'Synthetic restoration')
    check('restoring the withdrawn interval prices November again at a later cutoff',
          len(curve(shaped, NOVEMBER, DECEMBER, client.call('interval-series')['knownAt'])['points']) == 721)
    check('a shaped curve needs an explicit shape cutoff and a marked curve takes none',
          refused('interval_curve_cutoff', code=shaped, start=NOVEMBER, end=DECEMBER) and refused('interval_curve_cutoff', known_at=first_cut))
    check('a window outside every block is not partitioned',
          refused('interval_block_partition', code=shaped, start='2031-06-01T04:00:00Z', end='2031-06-02T04:00:00Z', known_at=first_cut))

    before = client.call('interval-series')['knownAt']
    collector = Collector(query, manifest, {'pjm': {'provider': 'PJM', 'sourceId': curves['source'], 'clock': 'synthetic_eastern'}})
    collector.publish('pjm', '2031-11-02', instant(DAY_FROM), 25, 60, [str(30 + n) for n in range(25)], 1)
    first = collector.publish('pjm', '2020-12-14', instant(DELIVERED_FROM), 24, 60, [str(30 + n) for n in range(24)], 1)
    collected = client.call('interval-series')['knownAt']

    def resolved(start, end, known_at, aggregate=None, code=marked, expected=200):
        return values(start, end, code, known_at, True, aggregate, expected=expected)

    ahead = resolved(DAY_FROM, DAYS_TO, collected)['points']
    expected, lineage = marks(DAY_FROM, DAYS_TO)
    check('an actual collected after the valuation instant never prices an interval still live then',
          [p['state'] for p in ahead] == ['projected'] * 49 and [price(p) for p in ahead] == expected
          and [p['forward']['observationIds'] for p in ahead] == lineage and all(p['actual'] is None for p in ahead))
    day = resolved(DELIVERED_FROM, DELIVERED_TO, collected)
    points = day['points']
    check('delivered hours resolve to the actuals known at the cutoff, each with its lineage and due time',
          [p['state'] for p in points] == ['actual'] * 24 and [price(p) for p in points] == [Fraction(30 + n) for n in range(24)]
          and all(p['actual'] == {'batchId': first['batchId'], 'providerRevision': 1, 'knownAt': p['actual']['knownAt']}
                  and instant(p['actual']['knownAt']) == instant(first['knownAt']) and p['forward'] is None for p in points)
          and all(instant(p['dueAt']) == instant(p['interval']['end']) + timedelta(hours=1) for p in points)
          and day['rule'] == {'anchor': 'interval_end', 'offsetMinutes': 60, 'version': 'interval_actual_due_v1'}
          and instant(day['valuationAt']) == instant(VALUED_AT) and len(day['knowledgeContextIds']) == 1)
    check('a delivered hour whose actual is due but not collected by the cutoff is refused',
          resolved(DELIVERED_FROM, DELIVERED_TO, before, expected=422)['code'] == 'interval_actual_missing')
    ending = resolved(ENDING_FROM, ENDING_TO, collected)['points']
    expected, lineage = marks(VALUED_AT, ENDING_TO)
    check('an hour ended before its actual is due is unresolved, and the live hours take their marks',
          [p['state'] for p in ending] == ['unresolved'] + ['projected'] * 13 and ending[0]['price'] is None
          and instant(ending[0]['dueAt']) == instant(VALUED_AT) + timedelta(hours=1)
          and [price(p) for p in ending[1:]] == expected and [p['forward']['observationIds'] for p in ending[1:]] == lineage)
    balance=resolved(ENDING_FROM,ENDING_TO,collected,code=shaped)['points']
    expected_balance=shaped_prices(date(2020,12,15),date(2021,1,1),{'peak':62,'off':42},'additive',lambda _,h:Fraction(h-12,4))
    compare_oracle(balance[1:],{at:value for at,value in expected_balance.items() if instant(VALUED_AT)<=at<instant(ENDING_TO)})
    check('selected live balance blocks cover every remaining current-day hour and retain an unresolved ended estimate',
          [p['state'] for p in balance]==['unresolved']+['projected']*13 and balance[0]['estimate'] is not None
          and all(p['anchor']['curve'] in [b['curve'] for b in curves['balances'].values()] for p in balance)
          and all(p['publication']=='mark' and p['liveMarketRiskCoverage']=='live_anchor' for p in balance[1:]))
    remainder_to='2021-01-01T05:00:00Z'
    full_balance=values(ENDING_FROM,remainder_to,shaped,collected,resolve=True)['points']
    compare_oracle(full_balance[1:],{at:value for at,value in expected_balance.items() if instant(VALUED_AT)<=at<instant(remainder_to)})
    for up in (True,False):
        target=curves['balances']['peak']
        shock={'seriesId':target['seriesId'],'quote':'test_power_outright','size':'1','convention':'absolute_quote_input_symmetric_v1','up':up}
        shocked=values(ENDING_FROM,remainder_to,shaped,collected,resolve=True,shock=shock)['points']
        oracle_balance=shaped_prices(date(2020,12,15),date(2021,1,1),{'peak':62,'off':42},'additive',lambda _,h:Fraction(h-12,4),bump={'peak':1 if up else -1})
        compare_oracle(shocked[1:],{at:value for at,value in oracle_balance.items() if instant(VALUED_AT)<=at<instant(remainder_to)})
    check('independent oracle matches every remaining-month balance cell in base/up/down scenarios',True)
    shaped_actuals=resolved(DELIVERED_FROM,DELIVERED_TO,collected,code=shaped)['points']
    check('shaped delivered actuals retain collected provenance and have no invented block anchor',
          [price(p) for p in shaped_actuals]==[Fraction(30+n) for n in range(24)]
          and all(p['state']=='actual' and p['publication']=='collected' and p['anchor'] is None for p in shaped_actuals))
    for up in (True,False):
        target=curves['balances']['peak']
        shock={'seriesId':target['seriesId'],'quote':'test_power_outright','size':'1','convention':'absolute_quote_input_symmetric_v1','up':up}
        actual_scenario=values(DELIVERED_FROM,DELIVERED_TO,shaped,collected,resolve=True,shock=shock)['points']
        assert actual_scenario==shaped_actuals
    check('collected shaped actuals remain exactly unchanged in both quote scenarios',True)
    second = collector.publish('pjm', '2020-12-14', instant(DELIVERED_FROM), 24, 60, [str(35 + n) for n in range(24)], 2)
    later = resolved(DELIVERED_FROM, DELIVERED_TO, client.call('interval-series')['knownAt'])['points']
    check('a corrected actual appears only at a later knowledge cutoff',
          resolved(DELIVERED_FROM, DELIVERED_TO, collected)['points'] == points and [price(p) for p in later] == [Fraction(35 + n) for n in range(24)]
          and all(p['actual']['batchId'] == second['batchId'] and p['actual']['providerRevision'] == 2 for p in later))

    days = resolved(DELIVERED_FROM, DELIVERED_TO, collected, 'day')['aggregates']
    hours = resolved(DELIVERED_FROM, DELIVERED_TO, collected, 'hour')['aggregates']
    check('day and hour aggregates are duration-weighted means that keep their native intervals',
          [(fraction(a['mean']), len(a['constituents'])) for a in days] == [(mean(points), 24)]
          and days[0]['policy'] == 'duration_weighted_price_mean_v1' and days[0]['constituents'] == [p['interval']['start'] for p in points]
          and [(fraction(a['mean']), len(a['constituents'])) for a in hours] == [(price(p), 1) for p in points])
    long_days = resolved(DAY_FROM, DAYS_TO, collected, 'day')['aggregates']
    check('the 25-hour day aggregates its repeated hour once per occurrence',
          [(fraction(a['mean']), len(a['constituents'])) for a in long_days] == [(mean(ahead[:25]), 25), (mean(ahead[25:]), 24)]
          and [len(a['constituents']) for a in resolved(DAY_FROM, DAYS_TO, collected, 'hour')['aggregates']] == [1] * 49)
    check('an hour aggregate has no mean while its interval is unresolved, and only whole operating days aggregate by day',
          [a['mean'] is None for a in resolved(ENDING_FROM, ENDING_TO, collected, 'hour')['aggregates']] == [True] + [False] * 13
          and resolved(ENDING_FROM, ENDING_TO, collected, 'day', expected=422)['code'] == 'interval_aggregate')
    check('resolution needs a knowledge cutoff, a due rule and an explicit aggregate, and every request member is required',
          refused('interval_curve_cutoff', end=DAYS_TO, resolve=True)
          and resolved(DAY_FROM, DAYS_TO, collected, code=scaled, expected=422)['code'] == 'interval_actual'
          and resolved(DAY_FROM, DAYS_TO, collected, 'week', expected=422)['code'] == 'interval_aggregate'
          and refused('interval_aggregate', end=DAYS_TO, aggregate='day')
          and client.call('interval-curves/values', {'snapshotId': frame, 'curve': marked, 'from': DAY_FROM, 'to': DAY_TO},
                          expected=400)['code'] == 'request_format')
    print("ORACLE "+json.dumps({"comparisons":len(oracle_counts),"exactCells":sum(oracle_counts),"cellsPerComparison":oracle_counts,"arithmetic":"Python fractions.Fraction","state":"all equal"}),flush=True)
    return passed
