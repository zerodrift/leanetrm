"""Authoring an existing definition from an open editor uses its current version."""
from uuid import uuid4

from assistant_context import Contexts
from native_read_transport import select_context_inputs
from preview_client import PreviewClient, initial_frame


def run_checks(url, query, manifest):
    editor, publisher = PreviewClient(url), PreviewClient(url)
    for client, profile in ((editor, 'editor'), (publisher, 'approver')):
        client.bootstrap()
        client.call('profile', {'profile': profile})

    dates = editor.call('date-schedules')
    calendar = next(c['reference'] for c in dates['calendars'] if c['reference']['code'] == 'test_business')
    schedule = {
        'definition': {'name': 'Synthetic schedule', 'symbol': 'oe.head_dates', 'firstMonth': '2021-01',
                       'lastMonth': '2021-04', 'anchor': 'day', 'day': 1, 'shortMonth': 'reject',
                       'adjustment': 'unadjusted', 'calendar': None},
        'calendar': calendar, 'selection': 'context_month', 'effectiveFrom': '2021-01-01', 'effectiveTo': '2021-05-01',
    }
    product = next(p for p in editor.call('tou-products')['versions'] if p['code'] == 'pjm')
    tou = {k: product[k] for k in ('clock', 'calendar', 'holidayTreatment', 'peakStartHour', 'peakEndHour', 'peakSaturday')}
    tou.update(code='synthetic_head_peak', name='Synthetic peak')
    context = editor.call('date-schedules/context', {'effectiveOn': '2021-01-15', 'knownAt': None, 'mode': 'current'})['context']
    mapping = {'name': 'Synthetic period', 'code': 'synthetic_head_mapping', 'context': context,
               'contracts': [{'label': 'January', 'start': '2021-01-01', 'end': '2021-02-01',
                              'lastTrading': '2020-12-29', 'cutoffUtc': '15:30'}]}
    frame = initial_frame(editor.call('workspace'), '2020-12-18')
    template = next(t for t in editor.call('custom-curves')['templates'] if t['code'] == 'ng_prompt')
    curve = {'definition': dict(template['definition'], code='synthetic_head_curve', name='Synthetic curve'),
             'layout': template.get('layout', {}), 'snapshotId': frame['id']}

    failures = []
    headers = []
    for path, body, library, field, conflict in (
        ('date-schedules', schedule, 'date-schedules', 'versions', 'schedule_conflict'),
        ('tou-products', tou, 'tou-products', 'versions', 'tou_conflict'),
        ('date-schedules/contracts', mapping, 'date-schedules', 'contractDefinitions', 'contract_date_conflict'),
        ('custom-curves', curve, 'custom-curves', 'recipes', 'custom_curve_conflict'),
    ):
        has_request_id = path in ('date-schedules/contracts', 'custom-curves')

        def command(body):
            return dict(body, requestId=str(uuid4())) if has_request_id else body

        def publish_request(draft):
            terms = {k: draft[k] for k in ('id', 'version', 'generation')}
            if path == 'custom-curves':
                terms['snapshotId'] = frame['id']
            return command(terms)

        def edit(old, name):
            changes = {'definition': dict(body['definition'], name=name)} if 'definition' in body else {'name': name}
            return command(dict(body, **changes, id=old['id'], version=old['version'], generation=old['generation'],
                                expectedState=old['state']))

        create = command(body)
        first_publish = publish_request(editor.call(path + '/save', create))
        first = publisher.call(path + '/publish', first_publish)
        successor = edit(first, 'Synthetic second version')
        second_draft = editor.call(path + '/save', successor)
        second = publisher.call(path + '/publish', publish_request(second_draft))
        assert first['version'] == 1 and second['version'] == 2 and first['id'] == second['id'], path
        assert publisher.call(path + '/publish', first_publish) == first, path
        if has_request_id:
            assert editor.call(path + '/save', successor) == second_draft, path

        response = editor.call(path + '/save', edit(first, 'Edit from the earlier version'), expected=(200, 409))
        if editor.last_status != 409 or response.get('code') != conflict:
            failures.append((path, editor.last_status, response.get('code')))
            continue
        versions = [v for v in editor.call(library)[field] if v['id'] == first['id']]
        assert {(v['version'], v['state']) for v in versions} == {(1, 'published'), (2, 'published')}, path
        if has_request_id:
            headers.append((path, first['id'], conflict, publish_request(dict(second, version=4, generation=1))))

    assert not failures, f'Expected version conflicts for an edit based on the earlier publication, got {failures}'
    # A sparse legacy history can already contain a publication beyond the next available number.
    # Seed that version with the editor's original inputs, then publish it through its normal endpoint.
    native = Contexts(query, manifest)
    for path, identity, conflict, publish in headers:
        if path == 'date-schedules/contracts':
            table, key = 'contract_date_versions', 'definition_id'
            children = ('contract_date_contracts', 'contract_date_fields')
            inputs = select_context_inputs(manifest['catalogId'], context)
        else:
            table, key = 'custom_curve_versions', 'curve_id'
            children = ('custom_curve_nodes', 'custom_curve_edges', 'custom_curve_node_sources')
            inputs = ''
        source = f"catalog_id = '{manifest['catalogId']}' AND {key} = '{identity}' AND version = 2"

        def draft(version):
            return f"""INSERT INTO market_data.{table}
                SELECT (jsonb_populate_record(v, jsonb_build_object('version', {version}, 'state', 'draft',
                    'generation', 1, 'publication_order', NULL, 'published_at', NULL, 'publication_xid', NULL))).*
                FROM market_data.{table} v WHERE {source};"""

        contents = ''.join(f"""INSERT INTO market_data.{child}
            SELECT (jsonb_populate_record(v, jsonb_build_object('version', 4))).*
            FROM market_data.{child} v WHERE {source};""" for child in children)
        native.runtime('editor', inputs + draft(4) + contents)
        assert publisher.call(path + '/publish', publish)['version'] == 4
        native.runtime('editor', inputs + f"""
            DO $$ BEGIN
              {draft(3)}
              RAISE EXCEPTION 'Expected the earlier draft to conflict';
            EXCEPTION WHEN raise_exception THEN
              IF SQLERRM <> '{conflict}' THEN RAISE; END IF;
            END $$;
        """)
    print('PASS earlier definition versions remain readable and new edits require the current version', flush=True)
