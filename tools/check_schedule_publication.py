"""Independent schedule publication and context checks using owned test clients."""


def run_checks(Client, check):
    editor = Client(); editor.bootstrap(); editor.call('profile', {'profile': 'editor'})
    approver = Client(); approver.bootstrap(); approver.call('profile', {'profile': 'approver'})
    viewer = Client(); viewer.bootstrap(); viewer.call('profile', {'profile': 'viewer'})
    definition = {'name': 'Monthly dates', 'symbol': 'oe.ng', 'firstMonth': '2021-01', 'lastMonth': '2021-04',
                  'anchor': 'day', 'day': 1, 'shortMonth': 'reject', 'adjustment': 'unadjusted', 'calendar': None}
    library = viewer.call('date-schedules')
    check('schedule rule choices are served by the validating backend', [c['code'] for c in library['choices']['anchors']] == ['day', 'month_end'] and [c['code'] for c in library['choices']['selections']] == ['context_month', 'absolute_index'] and len(library['choices']['adjustments']) == 5)
    business = next(c for c in library['calendars'] if c['reference']['code'] == 'test_business')
    check('schedule calendars are listed from reference data with ISO weekend days',
          business['weekendDays'] == ['Saturday', 'Sunday'] and business['coverageFrom'] == '2019-01-01' and '2021-01-01' in business['closedDates'])
    calendar = business['reference']
    save = {'id': None, 'version': None, 'generation': None, 'definition': definition, 'calendar': calendar,
            'selection': 'context_month', 'effectiveFrom': '2021-01-01', 'effectiveTo': '2021-05-01'}
    inline = {**definition, 'calendar': {'name': 'Typed', 'from': '2020-12-01', 'to': '2021-06-01', 'weekendDays': [0, 6], 'closedDates': []}}
    for changed, code in [({**save, 'definition': inline}, 'calendar_binding'), ({**save, 'calendar': None}, 'calendar_reference'),
                          ({**save, 'calendar': {**calendar, 'code': 'absent_calendar'}}, 'calendar_reference'),
                          ({**save, 'calendar': {**calendar, 'revisionId': '00000000-0000-0000-0000-000000000000'}}, 'calendar_reference')]:
        error = editor.call('date-schedules/save', changed, expected=422)
        check('schedule calendar must be one exact visible reference version: ' + code, error['code'] == code)
    preview = viewer.call('date-schedules/preview', {k: save[k] for k in ['definition', 'calendar']})
    check('preview compiles against the reference calendar', [d['date'] for d in preview['dates']] == ['2021-01-01', '2021-02-01', '2021-03-01', '2021-04-01'])
    viewer.call('date-schedules/save', save, expected=403)
    check('readers cannot save shared schedule definitions')
    editor.call('date-schedules/save', save, headers={'X-Preview-CSRF': 'invalid'}, expected=400)
    check('schedule save requires CSRF token')
    for changed in [{**save, 'definition': None}, {k: v for k, v in save.items() if k != 'definition'}]:
        error = editor.call('date-schedules/save', changed, expected=422)
        check('missing schedule definition fails explicitly', error['code'] == 'schedule_definition')
    error = viewer.call('date-schedules/resolve', {'expression': '2021-01-01', 'context': {'effectiveOn': '2021-01-15'}}, expected=422)
    check('missing knowledge view cannot silently become zero even for a literal', error['code'] == 'schedule_context')
    draft = editor.call('date-schedules/save', save)
    check('independent schedule saved without a curve specification', draft['state'] == 'draft' and draft['version'] == 1)
    check('saved schedule binds the exact calendar version under the reference engine', draft['calendar'] == calendar and draft['engineVersion'] == 'date_schedule_v2')
    latest = approver.call('date-schedules')
    check('saved schedule survives a separate authorized browser session', any(v['id'] == draft['id'] for v in latest['versions']))
    check('readers receive published versions only, not drafts', all(v['state'] == 'published' for v in viewer.call('date-schedules')['versions']))
    body = {**save, 'id': draft['id'], 'version': draft['version'], 'generation': draft['generation'], 'expectedState': 'draft',
            'definition': {**definition, 'name': 'Renamed dates'}}
    renamed = editor.call('date-schedules/save', body)
    check('draft rename advances generation without changing identity or version', renamed['id'] == draft['id'] and renamed['version'] == 1 and renamed['generation'] == 2)
    editor.call('date-schedules/save', body, expected=409)
    check('stale draft save cannot overwrite newer edits')
    publish = {'id': renamed['id'], 'version': 1, 'generation': 2}
    editor.call('date-schedules/publish', publish, expected=403)
    check('editor cannot publish without current publishing permission')
    context = {'effectiveOn': '2021-01-15', 'knownThrough': 0}
    viewer.call('date-schedules/resolve', {'expression': 'oe.ng[0]', 'context': context}, expected=422)
    check('unpublished schedule cannot supply contract dates')
    published = approver.call('date-schedules/publish', publish)
    head1 = viewer.call('date-schedules')['knownThrough']
    check('publication is persisted and version immutable in application workflow', published['state'] == 'published' and head1 == published['publicationOrder'])
    editor.call('date-schedules/save', {**body, 'generation': published['generation']}, expected=409)
    check('a draft that was published cannot be silently edited using its old draft token')
    check('repeat publication is idempotent', approver.call('date-schedules/publish', publish) == published)
    context['knownThrough'] = head1
    first = viewer.call('date-schedules/resolve', {'expression': 'oe.ng[0]', 'context': context})
    february = viewer.call('date-schedules/resolve', {'expression': 'oe.ng[0]', 'context': {**context, 'effectiveOn': '2021-02-15'}})
    check('context month changes underlying calendar date deterministically', first['date'] == '2021-01-01' and february['date'] == '2021-02-01')
    check('resolved reference retains exact published identity engine and context', first['source']['scheduleId'] == published['id'] and first['source']['version'] == 1 and first['source']['context'] == context and first['source']['engineVersion'] == 'date_schedule_v2')
    check('literal date is usable in place of a reference', viewer.call('date-schedules/resolve', {'expression': '2021-01-20', 'context': context})['date'] == '2021-01-20')
    for expression, ctx, code in [('#ng[0]', context, 'date_expression'), ('oe.ng[9]', context, 'date_index'),
                                  ('oe.ng[0]', {**context, 'knownThrough': head1+1}, 'schedule_context'),
                                  ('oe.ng[0]', None, 'schedule_context'),
                                  ('oe.ng[0]', {**context, 'effectiveOn': '2020-12-01'}, 'schedule_unpublished')]:
        error = viewer.call('date-schedules/resolve', {'expression': expression, 'context': ctx}, expected=422)
        check('invalid explicit publication context/reference raises ' + code, error['code'] == code)
    next_draft = editor.call('date-schedules/save', {**body, 'expectedState': 'published', 'generation': published['generation'], 'definition': {**definition, 'name': 'Monthly dates revised', 'day': 2}})
    check('editing published version creates a separate next draft', next_draft['version'] == 2 and next_draft['state'] == 'draft' and next_draft['id'] == published['id'])
    after_draft = viewer.call('date-schedules/resolve', {'expression': 'oe.ng[0]', 'context': context})
    check('unpublished edits cannot alter published resolution', after_draft == first)
    approver.call('date-schedules/publish', {'id': next_draft['id'], 'version': 2, 'generation': next_draft['generation']})
    head2 = viewer.call('date-schedules')['knownThrough']
    revised = viewer.call('date-schedules/resolve', {'expression': 'oe.ng[0]', 'context': {**context, 'knownThrough': head2}})
    check('new publication changes only explicitly newer publication views', head2 > head1 and revised['date'] == '2021-01-02' and revised['source']['version'] == 2 and viewer.call('date-schedules/resolve', {'expression': 'oe.ng[0]', 'context': context}) == first)
    stored = viewer.call('date-schedules')['versions']
    check('prior published label and terms remain unchanged', next(v for v in stored if v['id'] == published['id'] and v['version'] == 1) == published)
    spec = {'code': 'context_contracts', 'name': 'Context contract example', 'context': context,
            'contracts': [{'label': 'Month 1', 'start': 'oe.ng[0]', 'end': 'oe.ng[1]', 'lastTrading': 'oe.ng[0] - 3bd', 'cutoffUtc': '15:30'},
                          {'label': 'Month 2', 'start': 'oe.ng[1]', 'end': 'oe.ng[2]', 'lastTrading': 'oe.ng[1] - 3bd', 'cutoffUtc': '15:30'}]}
    contracts = viewer.call('date-schedules/contracts/preview', spec)
    check('contract start/end consume previously published schedules without existing curve specs', contracts['contracts'][0]['start'] == '2021-01-01' and contracts['contracts'][0]['end'] == '2021-02-01' and contracts['contracts'][0]['lastTradingAt'] == '2020-12-29T15:30:00+00:00')
    later = viewer.call('date-schedules/contracts/preview', {**spec, 'context': {**context, 'effectiveOn': '2021-02-15'}})
    check('same contract expressions resolve under a different user context', later['contracts'][0]['start'] == '2021-02-01' and later['contracts'][0]['end'] == '2021-03-01')
    literal = {**spec, 'contracts': [{**spec['contracts'][0], 'start': '2021-01-03'}]}
    check('contract date fields accept literal dates and references together', viewer.call('date-schedules/contracts/preview', literal)['contracts'][0]['startReference']['source']['kind'] == 'literal')
    viewer.call('date-schedules/contracts/save', {**spec,'requestId':str(__import__('uuid').uuid4())}, expected=403)
    check('shared contract-date save requires authoring permission, while preview creates no stored definition', approver.call('date-schedules')['contractDefinitions'] == [])
