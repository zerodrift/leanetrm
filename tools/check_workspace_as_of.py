"""Workspace selection stays tab-local. Only an expressly saved mapping retains its inputs."""
from datetime import datetime
import json
from urllib.request import HTTPCookieProcessor, build_opener
from uuid import uuid4

from check_booked_side_programs import capture_context
from preview_client import PreviewClient


def open_tab(account, identifier=None):
    """One account cookie and CSRF identity, independent page headers for GET and POST."""
    tab = PreviewClient(account.url)
    tab.jar = account.jar
    tab.session = account.session
    tab.opener = build_opener(HTTPCookieProcessor(tab.jar))
    tab.opener.addheaders = [('X-OpenETRM-View', str(identifier or uuid4()))]
    tab.bootstrap()
    return tab


def prepare(tab, day, mode='current', at=None):
    return tab.call('date-schedules/context', {'effectiveOn': day, 'mode': mode, 'knownAt': at})['context']


def select(tab, context):
    result = tab.call('market/context', {'effectiveOn': context['effectiveOn'], 'mode': 'at',
        'knownAt': context['knownAt'], 'selection': 'latest_correction'})
    assert result['context'] == context, 'Applying a displayed cutoff changed its coordinates or identity'


def priced(tab, day):
    navigation = tab.call('workspace/navigation')
    matching = [row for row in navigation['frames'] if row.get('close') and row['businessDate'] == day
                and row['close']['session'] == 'demo_close_1600']
    assert len(matching) == 1, f"Expected one {day} close in this tab, got {len(matching)} at {navigation['context']}"
    close = matching[0]
    answer = tab.call('evaluate', {'snapshotId': close['id'], 'curve': 'ng_monthly'})
    assert answer['snapshotId'] == close['id'] and answer['points']
    return navigation['context'], close['originCloseId'], [(point['contractId'], point['value']) for point in answer['points']]


def run_checks(url, query, manifest):
    catalog = manifest['catalogId']
    def counts():
        return history_counts(query, catalog)

    # Seed a compatible legacy receipt after market setup, with certified knowledge.
    early_day, late_day = '2020-12-17', '2020-12-18'
    stored_day = late_day
    stored_id = capture_context(query, manifest, stored_day)
    stored_time = query(f"SELECT known_at FROM market_data.temporal_contexts WHERE catalog_id='{catalog}' AND id='{stored_id}';")
    started = counts()
    legacy = PreviewClient(url)
    legacy.bootstrap()
    legacy.call('profile', {'profile': 'editor'})
    first_id = uuid4()
    first = open_tab(legacy, first_id)
    second = open_tab(legacy)
    original = priced(first, early_day)
    alternate = priced(second, late_day)
    assert original[0] is None and alternate[0] is None and alternate[1] != original[1]

    # Reuse a genuine retained selection already present before opening either tab.
    old = prepare(first, stored_day, 'at', datetime.fromisoformat(stored_time).isoformat())
    assert old['contextId'] == stored_id and datetime.fromisoformat(old['knownAt']) == datetime.fromisoformat(stored_time)

    early = prepare(first, early_day)
    late = prepare(second, late_day)
    assert early['contextId'] != late['contextId']
    select(first, early)
    first.call('market/context', {'effectiveOn': early_day, 'knownAt': early['knownAt'], 'mode': 'at',
        'selection': 'latest_correction', 'restoreKey': 'different-certified-history'}, expected=422)
    current = priced(first, early_day)
    assert current == (early, *original[1:]), 'The applied cut did not reproduce the selected close'
    assert priced(second, late_day) == alternate, 'As of changed another tab'
    assert legacy.call('workspace/navigation')['context'] is None, 'First tab leaked into the headerless workspace'
    legacy.call('market/current', {})
    assert priced(first, early_day) == current, 'A headerless reset modified the first tab'
    assert counts() == started, 'Opening or applying a workspace retained temporal rows'

    select(second, late)
    # Back re-applies the original exact selection. It must not capture "now" again.
    select(first, prepare(first, late_day, 'at', early['knownAt']))
    select(first, early)
    assert priced(first, early_day) == current
    assert priced(second, late_day) == (late, *alternate[1:])
    legacy.call('market/context/retained', {'contextId': stored_id, 'selection': 'latest_correction'})
    assert priced(first, early_day) == current and priced(second, late_day)[0] == late
    first.call('market/current', {})
    assert priced(first, early_day) == original
    assert legacy.call('workspace/navigation')['context']['contextId'] == stored_id
    assert counts() == started, 'Switching, restoring or returning to current retained temporal rows'

    # A tab identifier conveys no access across account cookies.
    outsider = PreviewClient(url)
    outsider.bootstrap()
    outsider.call('profile', {'profile': 'editor'})
    outsider.opener.addheaders = [('X-OpenETRM-View', str(first_id))]
    outsider.call('workspace/navigation', expected=403)
    print('PASS tab and headerless views stay independent and Back, initialization and exact legacy replay add zero retained rows', flush=True)

    saved_mapping(first, second, late_day, query, catalog)


def history_counts(query, catalog):
    tables = ('market_data.temporal_contexts', 'market_data.market_contexts', 'market_data.catalogue_contexts', 'trading.contexts')
    columns = [f"(SELECT count(*) FROM {table} WHERE catalog_id='{catalog}')" for table in tables]
    return tuple(map(int, query('SELECT ' + ','.join(columns)).split('|')))


def read_trading_workspace(query, runtime, catalog, as_of, statement, known_at=None):
    """Read populated trade facts in a fresh transaction at an unretained workspace cutoff."""
    before = history_counts(query, catalog)
    day = as_of[:10]
    knowledge = 'NULL' if known_at is None else f"'{known_at}'"
    cut = runtime(f"""SELECT id,known_at,known_through,system_id,timeline,stored
        FROM security.workspace_read_cut('{catalog}','{day}',{knowledge},false);""", 'trader')
    identifier, cutoff, prefix, system, timeline, stored = cut.split('|')
    assert stored == 'f', 'This fixture must exercise a workspace cutoff without a stored receipt'
    answer = runtime(f"""SELECT security.bind_workspace_read('{catalog}','{identifier}','{day}','{cutoff}',{prefix},'{system}',{timeline});
        SELECT security.use_trading_context('{catalog}','{as_of}');
        {statement}
        SELECT security.finish_workspace_read();""", 'trader')
    assert history_counts(query, catalog) == before, 'Reading a trading workspace stored its cutoff'
    return json.loads(answer)


def saved_mapping(first, second, late_day, query, catalog):
    def counts():
        return history_counts(query, catalog)

    started = counts()
    # An author explicitly saves the mapping they previewed in Working data.
    selected = prepare(first, late_day)
    definition = {'code': 'workspace_dates_proof', 'name': 'Synthetic workspace dates', 'context': selected,
                  'contracts': None, 'dates': [{'label': 'Delivery', 'date': '2021-02-01'}]}
    preview = first.call('date-schedules/date-lists/preview', definition)
    assert preview['context'] == selected and preview['dates'][0]['date'] == '2021-02-01'
    assert counts() == started
    save_path = 'date-schedules/contracts/save'
    # A refused date-ordering change must leave its ephemeral cut untouched.
    refused = first.call(save_path, {**definition, 'requestId': str(uuid4()),
        'dates': definition['dates'] + [{'label': 'Earlier', 'date': '2021-01-01'}]}, expected=422)
    assert refused['code'] == 'date_list_order'
    assert counts() == started
    command = {**definition, 'requestId': str(uuid4())}
    receipt = first.call(save_path, command)
    after = counts()
    assert after == tuple(n+1 for n in started), 'Saving the reviewed mapping must retain its exact financial inputs once'
    assert query(f"""SELECT validation_context_id FROM market_data.contract_date_versions
        WHERE catalog_id='{catalog}' AND definition_id='{receipt['id']}' AND version={receipt['version']};""") == selected['contextId']
    # Replay from a separate tab which never prepared this cut and read its saved source.
    first.call('market/current', {})
    second.call('market/current', {})
    assert second.call(save_path, command) == receipt
    saved, = [row for row in second.call('date-schedules')['contractDefinitions'] if row['id'] == receipt['id']]
    assert saved['definition']['context'] == selected
    assert prepare(second, late_day, 'at', selected['knownAt']) == selected and counts() == after
    print('PASS an explicitly saved date mapping retains the reviewed coordinates and retries once from another tab', flush=True)
