"""Account-owned dashboard layouts against a disposable synthetic preview."""
from copy import deepcopy
from uuid import uuid4


def widget(kind, config, x, y):
    return dict(id=str(uuid4()), kind=kind, config=config, board=dict(x=x, y=y, w=4, h=3),
                morning=dict(section='prices_positions', order=x + y))


def run_checks(url, query, browsers):
    """browsers: two sessions of the preview, which these checks switch to their own users."""
    passed = 0
    def check(name, condition=True):
        nonlocal passed
        assert condition, name
        passed += 1
        print('PASS ' + name, flush=True)
    trader, other_browser = browsers
    for session in browsers:
        session.call('profile', {'profile': 'trader'})
    layout = dict(version=1, arrangement='board', widgets=[
        widget('latest_levels', dict(curves=['ng_henry_hub', 'power_ercot_north']), 0, 0),
        widget('price_chart', dict(curve='ng_henry_hub', contractId=str(uuid4())), 4, 0),
        widget('positions', dict(definitionId=str(uuid4())), 8, 0)])
    def save(session, body, request_id=None, expected=None, status=200):
        return session.call('dashboard/layout', dict(requestId=request_id or str(uuid4()), layout=body, expectedVersion=expected), expected=status)
    def refused(body, code='dashboard_layout'):
        response = trader.call('dashboard/layout', dict(requestId=str(uuid4()), layout=body, expectedVersion=None), expected=422)
        return response['code'] == code

    check('account starts without a dashboard', trader.call('dashboard/layout') == dict(version=None, layout=None))
    request_id = str(uuid4())
    saved = save(trader, layout, request_id)
    check('layout follows the account into another browser session', other_browser.call('dashboard/layout') == saved and saved['layout'] == layout)

    retried = save(trader, layout, request_id)
    check('a retried save returns its first outcome', retried == saved and other_browser.call('dashboard/layout') == saved)
    changed = {**layout, 'arrangement': 'morning'}
    conflict = save(trader, changed, request_id, status=409)
    check('a request id reused for a different layout is a conflict', conflict['code'] == 'dashboard_request_conflict' and other_browser.call('dashboard/layout') == saved)

    stale = save(other_browser, changed, expected=None, status=409)
    stale_version = save(other_browser, changed, expected=str(uuid4()), status=409)
    check('a stale expected version is refused and loses nothing',
          stale['code'] == stale_version['code'] == 'dashboard_layout_conflict' and trader.call('dashboard/layout') == saved)

    unknown_kind = deepcopy(layout); unknown_kind['widgets'][0]['kind'] = 'news'
    unknown_section = deepcopy(layout); unknown_section['widgets'][0]['morning']['section'] = 'lunch'
    unknown_field = deepcopy(layout); unknown_field['widgets'][0]['title'] = 'Gas'
    unknown_layout_field = {**layout, 'asOf': '2026-10-30'}
    duplicate_id = deepcopy(layout); duplicate_id['widgets'][1]['id'] = duplicate_id['widgets'][0]['id']
    no_curves = deepcopy(layout); no_curves['widgets'][0]['config'] = dict(curves=[])
    repeated_curve = deepcopy(layout); repeated_curve['widgets'][0]['config'] = dict(curves=['ng_henry_hub', 'ng_henry_hub'])
    off_board = deepcopy(layout); off_board['widgets'][0]['board']['x'] = 12
    crowded = {**layout, 'widgets': [widget('positions', dict(definitionId=str(uuid4())), i % 12, i // 12) for i in range(33)]}
    oversize = {**layout, 'widgets': [widget('latest_levels', dict(curves=[f'oversize_curve_{n:02d}_' + 'x' * 46 for n in range(12)]), i % 12, i // 12) for i in range(32)]}
    check('unknown kinds, sections, fields and oversize layouts are refused',
          all(refused(body) for body in [unknown_kind, unknown_section, unknown_field, unknown_layout_field, duplicate_id, no_curves, repeated_curve, off_board, crowded, oversize, {**layout, 'version': 2}, {**layout, 'arrangement': 'grid'}])
          and trader.call('dashboard/layout') == saved)

    morning = save(trader, changed, expected=saved['version'])
    check('switching arrangement keeps every widget',
          [(w['id'], w['config']) for w in morning['layout']['widgets']] == [(w['id'], w['config']) for w in layout['widgets']]
          and morning['layout']['arrangement'] == 'morning' and morning['version'] != saved['version'] and other_browser.call('dashboard/layout') == morning)

    other_browser.call('profile', {'profile': 'admin'})
    other_browser.call('dashboard/layout', expected=403)
    save(other_browser, layout, status=403)
    check('an account without ReadMarketData cannot read or save')
    print(f'{passed} account dashboard-layout checks passed.', flush=True)
