"""Account-owned table layouts against a disposable synthetic preview."""
import json
from uuid import uuid4
from preview_client import PreviewClient


def run_checks(url, query):
    passed = 0
    def check(name, condition=True):
        nonlocal passed
        assert condition, name
        passed += 1
        print('PASS ' + name, flush=True)
    def client(profile):
        c = PreviewClient(url); c.bootstrap(); c.call('profile', {'profile': profile}); return c
    trader = client('trader'); other_browser = client('trader'); viewer = client('viewer')
    layout = dict(version=6, name='Account view', sidebar=False, mode='trades', rowGroup='book', columns=['book','quantity','unit'],
                  bookIds=[], portfolioIds=[], groupBy=[], measures=[], query='Jan', columnFilters={}, splitBy=[],
                  grid=dict(order=[], widths={}, sort=[]), panes=dict(layout=dict(kind='split', direction='column', ratio=0.06, children=[dict(kind='group', tabs=['summary'], active='summary'), dict(kind='split', direction='column', ratio=0.6, children=[dict(kind='group', tabs=['grid'], active='grid'), dict(kind='split', direction='column', ratio=0.5, children=[dict(kind='group', tabs=['details'], active='details'), dict(kind='group', tabs=['charts'], active='charts')])])]), collapsed=['details']), chart=dict(visible=False, measure='quantity', kind='bar'))
    check('account starts without saved layouts', trader.call('table-views/blotter') == [])
    saved = trader.call('table-views/blotter', {'layout': layout, 'expectedVersion': None})
    check('layout follows account into a separate browser session', other_browser.call('table-views/blotter') == [saved])
    trader.call('table-views/blotter', {'layout': {**layout, 'snapshotId': str(uuid4())}, 'expectedVersion': saved['version']}, expected=422)
    version4 = {key: value for key, value in layout.items() if key != 'sidebar'}
    trader.call('table-views/blotter', {'layout': {**version4, 'version': 4}, 'expectedVersion': None}, expected=422)
    earlier = {key: value for key, value in version4.items() if key != 'rowGroup'}
    trader.call('table-views/blotter', {'layout': {**earlier, 'version': 3}, 'expectedVersion': None}, expected=422)
    trader.call('table-views/blotter', {'layout': {**layout, 'rowGroup': 'portfolio'}, 'expectedVersion': saved['version']}, expected=422)
    check('only the current layout version is saved, with a desk grouping the Blotter offers', trader.call('table-views/blotter') == [saved])
    trader.call('table-views/blotter', {'layout': layout, 'expectedVersion': None}, expected=409)
    changed = trader.call('table-views/blotter', {'layout': {**layout,'query':'Feb'}, 'expectedVersion': saved['version']})
    other_browser.call('table-views/blotter', {'layout': layout, 'expectedVersion': saved['version']}, expected=409)
    other_browser.call('table-views/blotter/delete', {'name': layout['name'], 'expectedVersion': saved['version']}, expected=409)
    check('stale saves and deletes cannot overwrite a newer layout', other_browser.call('table-views/blotter') == [changed])
    flat = trader.call('table-views/blotter', {'layout': {**layout, 'name': 'Flat list', 'rowGroup': None}, 'expectedVersion': None})
    check('a flat list keeps its null grouping through storage', flat['layout']['rowGroup'] is None and
          [v['layout']['rowGroup'] for v in other_browser.call('table-views/blotter') if v['layout']['name'] == 'Flat list'] == [None])
    trader.call('table-views/blotter/delete', {'name': 'Flat list', 'expectedVersion': flat['version']})
    # A layout an earlier release stored cannot be written now, so the owner stores one as version 3 did: without a desk grouping.
    def store_earlier(name):
        stored = json.dumps({**{key: value for key, value in layout.items() if key != 'rowGroup'}, 'version': 3, 'name': name})
        return query("INSERT INTO presentation.table_views(tenant_id,workspace_id,principal_id,table_key,name,version,layout) "
                     f"SELECT tenant_id,workspace_id,principal_id,table_key,'{name}',gen_random_uuid(),'{stored}'::jsonb "
                     "FROM presentation.table_views WHERE name='Account view' RETURNING version;")
    earlier_version = store_earlier('Earlier view')
    listed = {v['layout']['name']: v for v in other_browser.call('table-views/blotter')}
    check('a layout an earlier release stored is listed exactly as it was stored',
          listed['Earlier view']['version'] == earlier_version and listed['Earlier view']['layout']['version'] == 3 and 'rowGroup' not in listed['Earlier view']['layout'])
    trader.call('table-views/blotter', {'layout': {**layout, 'name': 'Earlier view'}, 'expectedVersion': None}, expected=409)
    replaced = trader.call('table-views/blotter', {'layout': {**layout, 'name': 'Earlier view'}, 'expectedVersion': earlier_version})
    check('saving it again under the version it was stored with replaces it', replaced['version'] != earlier_version and replaced['layout']['version'] == 5 and
          replaced in other_browser.call('table-views/blotter'))
    trader.call('table-views/blotter/delete', {'name': 'Earlier view', 'expectedVersion': replaced['version']})
    discarded_version = store_earlier('Discarded view')
    trader.call('table-views/blotter/delete', {'name': 'Discarded view', 'expectedVersion': discarded_version})
    check('an earlier layout is deleted under the version it was stored with', [v['layout']['name'] for v in other_browser.call('table-views/blotter')] == ['Account view'])
    viewer.call('table-views/blotter', expected=403)
    viewer.call('table-views/blotter', {'layout': layout,'expectedVersion':None}, expected=403)
    check('market-only identity cannot read or change trading layouts')
    admin = client('admin')
    member = admin.call('admin/create', dict(operationId=str(uuid4()),login='layout_other',name='Synthetic layout user',kind='human',roles=['trader']))
    admin.call('profile', {'profile':'layout_other'})
    check('another trader has an independent layout namespace', admin.call('table-views/blotter') == [])
    admin.call('table-views/blotter/delete', {'name':layout['name'],'expectedVersion':changed['version']}, expected=409)
    own = admin.call('table-views/blotter', {'layout':layout,'expectedVersion':None})
    check('same layout name belongs independently to each account',own['version'] != changed['version'] and trader.call('table-views/blotter') == [changed])
    control = client('admin')
    disabled = control.call('admin/update',dict(operationId=str(uuid4()),id=member['id'],expectedVersion=member['version'],name=member['name'],enabled=False,roles=['trader']))
    admin.call('table-views/blotter', expected=403)
    admin.call('table-views/blotter', {'layout':layout,'expectedVersion':own['version']}, expected=403)
    check('current revocation blocks saved-layout reads and writes')
    control.call('admin/update',dict(operationId=str(uuid4()),id=member['id'],expectedVersion=disabled['version'],name=member['name'],enabled=True,roles=['trader']))
    check('restored account retains its own layout',admin.call('table-views/blotter') == [own])
    trader.call('table-views/blotter/delete', {'name':layout['name'],'expectedVersion':changed['version']})
    check('deleting a layout removes it across sessions', other_browser.call('table-views/blotter') == [])
    print(f'{passed} account table-layout checks passed.', flush=True)
