"""Exact primary trading pins; supplemental markets resolve corrections only in certified contexts (D172)."""
import json
from decimal import Decimal
from datetime import datetime
from uuid import UUID, uuid4
from preview_client import initial_frame


VIEW = {'mode': 'trades', 'bookIds': [], 'portfolioIds': [], 'groupBy': [],
        'measures': [], 'pivotConvention': None}


def trading_context(frame, retained=None):
    return {'snapshotId': frame['id'], 'convention': 'trading_workspace_v1',
            'knowledgeMode': 'retained' if retained else 'current',
            **({'contextId': retained['contextId']} if retained else {})}


def trading_workspace(c, context):
    return c.call('trading/workspace', {'context': context, 'view': VIEW,
                                       'bookingCatalog': 'priced_contracts'})


def legacy_trading_reads(c, context, convention):
    """What a trading workspace read that asks for no summaries combines."""
    return {'catalog': c.call('futures/catalog', {**context, 'convention': convention}),
            'blotter': c.call('trading/blotter', {'context': context, 'view': VIEW}), 'summaries': []}


def _changed_value(observation, quote):
    amount = Decimal(str(observation['value']))
    step = Decimal(str(quote['clearing_increment'] if observation['role'] in ('daily_settlement', 'final_contract_settlement') else quote['trading_tick']))
    low, high = Decimal(str(quote['minimum'])), Decimal(str(quote['maximum']))
    changed = amount + step if amount + step <= high else amount - step
    assert step > 0 and low <= changed <= high and changed != amount
    return format(changed, 'f')


def run_checks(c, check, query, manifest):
    c.call('profile', {'profile': 'editor'}); c.call('market/current', {})
    data = c.call('workspace')
    frame = initial_frame(data, '2020-12-18')
    corrected_ids = {o.get('corrects_id') for o in data['observations']}
    future_ids = {r['id'] for r in data['definitions']['contracts'] if r['kind'] == 'future'}
    series = next(s for s in data['definitions']['series'] if s.get('contract_id') in future_ids
                  and any(o['series_id'] == s['id'] and o['id'] in frame['observationIds'] and o['id'] not in corrected_ids for o in data['observations']))
    original = next(o for o in data['observations'] if o['series_id'] == series['id'] and o['id'] in frame['observationIds'])
    quote = next(q for q in data['references']['quote_specifications'] if q['code'] == series['quote_code'])
    changed_value = _changed_value(original, quote)
    c.call('profile', {'profile': 'trader'})
    before = trading_workspace(c, trading_context(frame))
    old_cut = before['catalog']['context']
    before = trading_workspace(c, trading_context(frame, old_cut))
    c.call('profile', {'profile': 'editor'})
    changed = c.call('corrections', {'snapshotId': frame['id'], 'observationId': original['id'],
                                   'value': changed_value})
    successor = str(UUID(changed['observationId']))
    try:
        c.call('profile', {'profile': 'trader'})
        old_now = trading_workspace(c, trading_context(frame))
        cut = old_now['catalog']['context']
        old_pin, new_pin = trading_context(frame, cut), trading_context(changed['frame'], cut)
        old_result, new_result = trading_workspace(c, old_pin), trading_workspace(c, new_pin)
        check('priced trading retains the explicitly selected original observation after a correction',
              old_result == legacy_trading_reads(c, old_pin, data['futuresConvention']))
        check('priced trading retains the explicitly selected corrected observation',
              new_result == legacy_trading_reads(c, new_pin, data['futuresConvention'])
              and old_result['catalog']['contracts'] != new_result['catalog']['contracts'])
        check('priced trading replays the original cutoff after later correction publication',
              trading_workspace(c, trading_context(frame, old_cut)) == before)
        c.call('trading/workspace', {'context': trading_context(changed['frame'], old_cut),
               'view': VIEW, 'bookingCatalog': 'priced_contracts'}, expected=422)
        check('a later corrected pin cannot enter an earlier trading cutoff')
        # A read-only fault makes inspecting an unselected successor observable.
        # The full standalone route remains the control for its broader contract.
        query(f"""CREATE FUNCTION public.synthetic_unselected_trading_pin(id uuid) RETURNS boolean
          LANGUAGE plpgsql VOLATILE AS $$BEGIN
          IF id='{successor}'::uuid THEN RAISE EXCEPTION 'Synthetic unselected correction read' USING ERRCODE='42501';END IF;
          RETURN true;END$$;
          CREATE POLICY synthetic_unselected_trading_pin ON market_data.observations AS RESTRICTIVE
          FOR SELECT TO openetrm_runtime USING(public.synthetic_unselected_trading_pin(id));""")
        try:
            c.call('futures/catalog', {**old_pin, 'convention': data['futuresConvention']}, expected=403)
            check('standalone full trading reads still inspect same-fact correction history')
            check('priced workspace does not inspect unselected primary correction rows',
                  trading_workspace(c, old_pin) == old_result)
        finally:
            query('DROP POLICY synthetic_unselected_trading_pin ON market_data.observations;DROP FUNCTION public.synthetic_unselected_trading_pin(uuid);')
        anchor = str(UUID(original['id']))
        query(f"CREATE POLICY synthetic_hidden_trading_pin ON market_data.observations AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(id<>'{anchor}'::uuid);")
        try:
            c.call('trading/workspace', {'context': old_pin, 'view': VIEW,
                   'bookingCatalog': 'priced_contracts'}, expected=422)
            check('a hidden exact anchor is not replaced by its visible successor')
        finally:
            query('DROP POLICY synthetic_hidden_trading_pin ON market_data.observations;')
    finally:
        # Append a same-value restoration; never rewrite or delete a financial fact.
        c.call('profile', {'profile': 'editor'}); c.call('market/current', {})
        c.call('corrections', {'snapshotId': changed['frame']['id'], 'observationId': successor,
                               'value': original['value']})
        c.call('profile', {'profile': 'viewer'})


def run_supplemental_checks(c, check, query, manifest):
    """Invoke after the existing swap fixture has booked supplemental daily trades."""
    c.call('profile', {'profile': 'trader'}); c.call('market/current', {})
    data = c.call('workspace')
    frame = initial_frame(data, '2020-12-18')
    before = trading_workspace(c, trading_context(frame)); retained = trading_context(frame, before['catalog']['context'])
    before = trading_workspace(c, retained)
    held = next(h for h in before['blotter']['holdings'] if h['instrumentType'] == 'financial_swap'
                and h['definitionRevisionId'] != manifest['revisionId'] and h['curve'] == 'daily_gas' and h['fixedPrice'] is not None)
    market = next(m for m in manifest['swapMarkets'] if m['revisionId'] == held['definitionRevisionId'])
    seed = next(f for f in market['frames'] if datetime.fromisoformat(f['at']) == datetime.fromisoformat(frame['at']))
    selected = ','.join("'" + str(UUID(i)) + "'::uuid" for i in seed['observationIds'])
    selected_facts = json.loads(query(f"SELECT json_agg(json_build_object('observation',row_to_json(o),'quote',row_to_json(q)) ORDER BY o.id)::text FROM market_data.observations o JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.id)=(o.catalog_id,o.definition_revision_id,o.series_id) JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(s.reference_catalog_id,s.reference_revision_id,s.quote_code) WHERE o.id=ANY(ARRAY[{selected}]) AND EXISTS(SELECT 1 FROM market_data.curve_members m WHERE(m.catalog_id,m.revision_id,m.contract_id,m.curve_code)=(s.catalog_id,s.revision_id,s.contract_id,'daily_gas'));"), parse_float=Decimal)
    # Move every selected daily fact by the same declared grid step. A single
    # daily tick can produce a non-terminating monthly mean; no rounding is added.
    corrections = [(fact['observation'], str(uuid4()), _changed_value(fact['observation'], fact['quote']))
                   for fact in selected_facts]
    assert len(corrections) > 1 and len({Decimal(value) - Decimal(str(original['value']))
                                       for original, _, value in corrections}) == 1
    revision, catalog = str(UUID(market['revisionId'])), str(UUID(manifest['catalogId']))
    def append(rows):
        statements = []
        for identifier, predecessor, value in rows:
            identifier, predecessor = str(UUID(identifier)), str(UUID(predecessor))
            value = format(Decimal(str(value)), 'f')
            statements.append(f"""INSERT INTO market_data.observations(catalog_id,id,definition_revision_id,series_id,reference_catalog_id,reference_revision_id,source_code,source_event_id,source_revision_id,role,publication_state,observed_at,received_at,value,corrects_id)
              SELECT catalog_id,'{identifier}'::uuid,definition_revision_id,series_id,reference_catalog_id,reference_revision_id,source_code,source_event_id,'{identifier}',role,publication_state,observed_at,clock_timestamp(),'{value}'::numeric,'{predecessor}'::uuid
              FROM market_data.observations WHERE catalog_id='{catalog}' AND definition_revision_id='{revision}' AND id='{predecessor}';""")
        query('BEGIN;' + ''.join(statements) + 'COMMIT;')
    append([(successor, original['id'], value) for original, successor, value in corrections])
    def held_value(read):
        return next(h for h in read['blotter']['holdings'] if h['tradeId'] == held['tradeId'])['presentValue']
    try:
        current = trading_workspace(c, trading_context(frame)); cut = trading_context(frame, current['catalog']['context'])
        current = trading_workspace(c, cut)
        check("supplemental swap markets keep their snapshot's exact pins after a later correction (D172)",
              current == legacy_trading_reads(c, cut, data['futuresConvention']) and held_value(current) == held['presentValue'])
        check('supplemental swap correction does not change retained prior PV or provenance',
              trading_workspace(c, retained) == before)
        # A certified context resolves the latest correction known at its cutoff in every bundle.
        c.call('market/context', {'effectiveOn': '2020-12-18', 'knownAt': current['catalog']['context']['knownAt'],
                                  'mode': 'at', 'selection': 'latest_correction'})
        historical = c.call('workspace')
        alias = next(f for f in historical['frames'] if f['at'] == frame['at'] and 'originCloseId' not in f)
        certified = {'snapshotId': alias['id'], 'convention': 'trading_workspace_v1', 'knowledgeMode': 'retained',
                     'contextId': historical['context']['contextId']}
        resolved = trading_workspace(c, certified)
        check('a certified context after a supplemental correction resolves it identically through scoped and legacy reads',
              resolved == legacy_trading_reads(c, certified, data['futuresConvention']) and held_value(resolved) != held['presentValue'])
    finally:
        append([(str(uuid4()), successor, original['value']) for original, successor, _ in corrections])
        c.call('market/current', {})
        c.call('profile', {'profile': 'viewer'})
