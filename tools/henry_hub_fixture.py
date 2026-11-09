"""Original synthetic Henry Hub inputs for owned SQL and HTTP qualification, before source sealing."""
from copy import deepcopy
from uuid import UUID, uuid5


def prepare(reference, market, manifest):
    """Return reference/market/manifest with six extra native curves and explicit support/seed specifications."""
    manifest = deepcopy(manifest)
    cat, rev, ref, refrev = [manifest[key] for key in ('catalogId', 'revisionId', 'referenceCatalogId', 'referenceRevisionId')]
    key = lambda name: str(uuid5(UUID(cat), 'henry_support_fixture_v1:' + name))
    rr, mr = f"'{ref}','{refrev}'", f"'{cat}','{rev}'"
    refs = [f"INSERT INTO ref_data.commodities VALUES({rr},'synthetic_crude','Synthetic crude','volume'),"
            f"({rr},'synthetic_ngl','Synthetic NGL','volume'),({rr},'synthetic_lng','Synthetic LNG','energy');"]
    definitions, observations, fixings, direct_series, members = [], [], [], [], {}
    for family, future, basis, location, template, start, end, expiry in (
        ('daily', 'hhd_daily', 'hhd_daily_basis', 'hhd_location', 'test_gas_202101', '2021-01-01', '2021-01-02', '2020-12-31T20:00:00Z'),
        ('monthly', 'h_monthly', 'h_monthly_basis', 'h_location', 'test_gas_202102', '2021-02-01', '2021-03-01', '2021-01-28T20:00:00Z')):
        series_code = 'synthetic_henry_' + family
        refs.append(f"INSERT INTO ref_data.futures_series SELECT(jsonb_populate_record(NULL::ref_data.futures_series,to_jsonb(x)||"
                    f"jsonb_build_object('code','{series_code}','series_label','Synthetic {family}','delivery_from','{start}','delivery_to','{end}'))).* "
                    f"FROM ref_data.futures_series x WHERE(catalog_id,revision_id,code)=({rr},'{template}');")
        for table in ('futures_milestones', 'futures_execution_policies'):
            refs.append(f"INSERT INTO ref_data.{table} SELECT(jsonb_populate_record(NULL::ref_data.{table},to_jsonb(x)||"
                        f"jsonb_build_object('series_code','{series_code}'))).* FROM ref_data.{table} x "
                        f"WHERE(catalog_id,revision_id,series_code)=({rr},'{template}');")
        ids = {}
        for code, kind, quote, rule, target, role in (
            (future, 'future', 'test_gas_outright', 'direct', 'test_future_target', 'daily_settlement'),
            (basis, 'forward', 'test_gas_basis', 'direct', 'test_basis_target', 'trader_mark'),
            (location, 'forward', 'test_gas_outright', 'add', 'test_index_target', None)):
            contract, series = key('contract:' + code), key('series:' + code)
            ids[code] = contract
            members[code] = [contract]
            expiry_sql = (f"(SELECT event_at FROM ref_data.futures_milestones WHERE(catalog_id,revision_id,series_code,milestone)="
                          f"({rr},'{series_code}','last_trading'))" if kind == 'future' else f"'{end}T00:00:00Z'")
            definitions.append(f"INSERT INTO market_data.contracts VALUES({mr},'{contract}',{rr},'{kind}','{start}','{end}',{expiry_sql},"
                               + (f"'{series_code}');" if kind == 'future' else "NULL);"))
            direct = f"'fixture','{role}'" if role else 'NULL,NULL'
            definitions += [f"INSERT INTO market_data.curves(catalog_id,revision_id,code,reference_catalog_id,reference_revision_id,commodity_code,"
                            f"location_code,quote_code,profile_code,settlement_target_code,rule,direct_source_code,direct_role,name) VALUES"
                            f"({mr},'{code}',{rr},'test_gas','test_hub_a','{quote}','test_uniform','{target}','{rule}',{direct},'Synthetic support {code}');",
                            f"INSERT INTO market_data.curve_members VALUES({mr},'{code}','{contract}',0);"]
            # The daily calculated target already has a compatible series; the monthly target deliberately has none.
            if role or family == 'daily':
                definitions.append(f"INSERT INTO market_data.series VALUES({mr},'{series}','synthetic_{code}',{rr},'{contract}','{quote}',NULL);")
            if role:
                direct_series.append(dict(curve=code, series_id=series, role=role, value='3.10' if kind == 'future' else '0.20'))
        for slot, source, policy in (('base', future, 'freeze_final_future'), ('spread', basis, 'require_live')):
            definitions += [f"INSERT INTO market_data.curve_inputs(catalog_id,revision_id,curve_code,slot,source_curve_code,expiry_rule) "
                            f"VALUES({mr},'{location}','{slot}','{source}','{policy}');",
                            f"INSERT INTO market_data.constituent_mappings VALUES({mr},'{location}','{slot}','{ids[location]}','{source}','{ids[source]}');"]
        fixings.append(dict(curve=location, contract_id=ids[location], **{'from': start, 'to': end}, expiry_at=expiry, fixing_at=expiry))
        if family == 'daily':
            # Retained legacy evidence belongs to the primary bundle; the new publication bundle must not alias it.
            definitions.append(f"INSERT INTO market_data.index_fixing_observations VALUES({mr},'test_price','{start}','{end}',{rr},"
                               f"'test_gas_outright','{key('series:' + location)}','fixture','{expiry}','published_index_observations_v1');")
    for frame in manifest['frames']:
        frame['curveContracts'] = {**frame.get('curveContracts', {}), **members}
        for item in direct_series:
            observation = key('observation:' + item['curve'] + ':' + frame['at'])
            observations.append(f"INSERT INTO market_data.observations(catalog_id,id,definition_revision_id,series_id,reference_catalog_id,reference_revision_id,"
                                f"source_code,source_event_id,source_revision_id,role,publication_state,observed_at,received_at,value) VALUES"
                                f"('{cat}','{observation}','{rev}','{item['series_id']}',{rr},'fixture','{observation}','1','{item['role']}',"
                                f"'final','{frame['at']}',clock_timestamp(),{item['value']});")
            frame['observationIds'] = [*frame['observationIds'], observation]
    for marker in ('UPDATE ref_data.revisions SET', 'UPDATE market_data.revisions SET'):
        assert market.count(marker) == 1
    market = market.replace('UPDATE ref_data.revisions SET', '\n'.join(refs) + '\nUPDATE ref_data.revisions SET', 1)
    market = market.replace('UPDATE market_data.revisions SET', '\n'.join(definitions) + '\nUPDATE market_data.revisions SET', 1)
    head, tail = market.rsplit('COMMIT;', 1)
    market = head + '\n'.join(observations) + '\nCOMMIT;' + tail
    spec = dict(catalog_id=cat, reference_catalog_id=ref, source_reference_revision_id=refrev, source_market_revision_id=rev,
                target_reference_revision_id=key('target-reference'), target_market_revision_id=key('target-market'),
                revision_key='synthetic_henry_support_v1', commodity_code='test_gas', location_code='test_hub_a', quote_code='test_gas_outright',
                calendar_code='test_business', clock_code='test_utc', source_code='fixture',
                daily_index_code='synthetic_henry_hub_gas_daily', monthly_index_code='synthetic_henry_hub_iferc_monthly',
                daily_target_code='synthetic_henry_hub_daily_target', monthly_target_code='synthetic_henry_hub_monthly_target', fixings=fixings)
    bindings = {name: dict(commodity=commodity, calendar='test_business', clock='test_utc') for name, commodity in
                [('gas_daily', 'test_gas'), ('gas_monthly', 'test_gas'), ('argus_crude', 'synthetic_crude'), ('opis_crude', 'synthetic_crude'),
                 ('opis_ngl', 'synthetic_ngl'), ('iso_power', 'test_power'), ('platts_lng', 'synthetic_lng')]}
    bindings['gas_daily'].update(index=spec['daily_index_code'], quote=spec['quote_code'])
    bindings['gas_monthly'].update(index=spec['monthly_index_code'], quote=spec['quote_code'])
    manifest['henrySupport'] = dict(spec=spec, publicationBindings=bindings, directSeries=direct_series, curveContracts=members)
    return reference, market, manifest


def qualify(query, manifest):
    """Apply the prepared artifact only to this owned fixture; prove exact replay and retained-source immutability."""
    from henry_hub_support import prepare_sql
    spec = manifest['henrySupport']['spec']
    cat, old, new = spec['catalog_id'], spec['source_market_revision_id'], spec['target_market_revision_id']
    def retained():
        return query(f"""SELECT md5(string_agg(value,',' ORDER BY value)) FROM(
 SELECT to_jsonb(x)::text value FROM market_data.contracts x WHERE(catalog_id,revision_id)=('{cat}','{old}') UNION ALL
 SELECT to_jsonb(x)::text FROM market_data.curves x WHERE(catalog_id,revision_id)=('{cat}','{old}') UNION ALL
 SELECT to_jsonb(x)::text FROM market_data.series x WHERE(catalog_id,revision_id)=('{cat}','{old}') UNION ALL
 SELECT to_jsonb(x)::text FROM market_data.index_fixing_observations x WHERE(catalog_id,revision_id)=('{cat}','{old}') UNION ALL
 SELECT to_jsonb(x)::text FROM market_data.observations x WHERE(catalog_id,definition_revision_id)=('{cat}','{old}'))facts;""")
    before = retained()
    sql = prepare_sql(spec)
    query(sql)
    counts = query(f"""SELECT(SELECT count(*) FROM market_data.curves WHERE(catalog_id,revision_id)=('{cat}','{new}')),
 (SELECT count(*) FROM market_data.index_fixing_observations WHERE(catalog_id,revision_id)=('{cat}','{new}')),
 (SELECT count(*) FROM market_data.observations WHERE(catalog_id,definition_revision_id)=('{cat}','{new}'));""")
    assert counts == '6|2|0', counts
    assert query(f"SELECT count(*) FROM market_data.series WHERE(catalog_id,revision_id)=('{cat}','{new}');") == '6'
    query(sql)
    assert retained() == before
    changed = deepcopy(spec)
    changed['fixings'][0]['fixing_at'] = '2020-12-31T20:00:01Z'
    try:
        query(prepare_sql(changed))
    except RuntimeError as error:
        assert 'henry_support_replay_conflict' in str(error), str(error)
    else:
        raise AssertionError('Changed support terms reused sealed target identities.')
    assert retained() == before
    print('PASS Henry support: exact native identities, distinct daily/monthly fixings, missing series, replay refusal and unchanged primary inputs')
    return spec


def capture_forward_inputs(query, spec, source_manifest):
    """Read exact source rows for the primary manifest's existing raw pins; no price or expiry selection is inferred."""
    import json
    from henry_hub_support import validate_spec
    validate_spec(spec)
    catalog, revision = spec['catalog_id'], spec['source_market_revision_id']
    metadata = json.loads(query(f"""SELECT coalesce(jsonb_agg(jsonb_build_object(
 'catalog_id',c.catalog_id,'revision_id',c.revision_id,'reference_catalog_id',c.reference_catalog_id,'reference_revision_id',c.reference_revision_id,
 'curve_code',c.code,'contract_id',m.contract_id,'series_id',s.id,'quote_code',s.quote_code,'ordinal',m.ordinal,
 'rule',c.rule,'direct_source_code',c.direct_source_code,'direct_role',c.direct_role,'source_kind',p.kind) ORDER BY c.code,m.ordinal),'[]')
 FROM market_data.curves c JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.curve_code)=(c.catalog_id,c.revision_id,c.code)
 JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.contract_id,s.quote_code)=(c.catalog_id,c.revision_id,m.contract_id,c.quote_code)
 JOIN ref_data.sources p ON(p.catalog_id,p.revision_id,p.code)=(c.reference_catalog_id,c.reference_revision_id,c.direct_source_code)
 WHERE(c.catalog_id,c.revision_id)=('{catalog}','{revision}') AND c.code IN('hhd_daily','hhd_daily_basis','h_monthly','h_monthly_basis');"""))
    pins = {str(UUID(key)) for frame in source_manifest['frames'] for key in frame['observationIds'] + (frame.get('frozenObservationIds') or [])}
    series = [row['series_id'] for row in metadata]
    literal = lambda value: "'" + json.dumps(value).replace("'", "''") + "'::jsonb"
    observations = json.loads(query(f"""SELECT coalesce(jsonb_agg(to_jsonb(o)||jsonb_build_object('value',o.value::text) ORDER BY o.id),'[]')
 FROM market_data.observations o WHERE(catalog_id,definition_revision_id)=('{catalog}','{revision}')
 AND id IN(SELECT value::uuid FROM jsonb_array_elements_text({literal(sorted(pins))}))
 AND series_id IN(SELECT value::uuid FROM jsonb_array_elements_text({literal(series)}));"""))
    captured = deepcopy(source_manifest)
    for frame in captured['frames']:
        selected = set(frame['observationIds'] + (frame.get('frozenObservationIds') or []))
        frame['requiredSeries'] = sorted({row['series_id'] for row in observations if row['id'] in selected})
    return captured, observations, metadata
