"""Owned stock-PostgreSQL proof for the prepared Henry synthetic forward artifact."""
from copy import deepcopy
from uuid import UUID, uuid5
from henry_hub_fixture import capture_forward_inputs
from henry_hub_forward_inputs import prepare


def run_checks(query, manifest):
    spec = manifest['henrySupport']['spec']
    captured, observations, metadata = capture_forward_inputs(query, spec, manifest)
    artifact = prepare(spec, captured, observations, metadata)
    cat, old, new = (spec[k] for k in ('catalog_id', 'source_market_revision_id', 'target_market_revision_id'))
    ref, new_ref = spec['reference_catalog_id'], spec['target_reference_revision_id']
    def fingerprint(revision):
        return query(f"SELECT md5(coalesce(string_agg(to_jsonb(o)::text,',' ORDER BY id),'')) FROM market_data.observations o WHERE(catalog_id,definition_revision_id)=('{cat}','{revision}');")
    retained = fingerprint(old)
    first = observations[0]
    target_id = str(uuid5(UUID(cat), 'synthetic_henry_forward_v1:' + new + ':' + first['id']))
    event = 'synthetic_henry_forward_v1:' + new + ':' + first['id']
    for conflict in ('value', 'identity'):
        other_id = target_id if conflict == 'value' else str(uuid5(UUID(cat), 'synthetic_conflict:' + target_id))
        wrong = f"""INSERT INTO market_data.observations SELECT catalog_id,'{other_id}','{new}',series_id,reference_catalog_id,'{new_ref}',
 source_code,'{event}','initial',role,publication_state,observed_at,clock_timestamp(),value{'+0.01' if conflict == 'value' else ''},NULL
 FROM market_data.observations WHERE(catalog_id,id)=('{cat}','{first['id']}');"""
        try:
            query(artifact['sql'].replace('BEGIN;', 'BEGIN;\n' + wrong, 1))
        except RuntimeError as error:
            assert 'henry_forward_replay_conflict' in str(error), str(error)
        else:
            raise AssertionError('Conflicting target observation was accepted: ' + conflict)
        assert query(f"SELECT count(*) FROM market_data.observations WHERE(catalog_id,definition_revision_id)=('{cat}','{new}');") == '0'
    query(artifact['sql'])
    assert query(f"SELECT count(*) FROM market_data.observations WHERE(catalog_id,definition_revision_id)=('{cat}','{new}');") == str(len(observations))
    copied = fingerprint(new)
    query(artifact['sql'])
    assert fingerprint(new) == copied, 'Replay changed original receipt audit metadata.'
    assert fingerprint(old) == retained, 'Original primary inputs changed.'
    changed = deepcopy(observations); changed[0]['value'] = '3.15'
    try:
        query(prepare(spec, captured, changed, metadata)['sql'])
    except RuntimeError as error:
        assert 'henry_forward_source_fact' in str(error), str(error)
    else:
        raise AssertionError('A supplied value differing from its captured source fact was accepted.')
    assert fingerprint(new) == copied and fingerprint(old) == retained
    print('PASS Henry forward inputs: captured synthetic pins, exact replay, conflict refusal and unchanged primary observations')
    return artifact['curveMarket']
