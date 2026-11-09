"""Synthetic forward copies retain exact captured pins and never manufacture observations."""
from copy import deepcopy
import json
from pathlib import Path
import sys
import unittest
from uuid import UUID, uuid5

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
from henry_hub_forward_inputs import prepare
from henry_hub_fixture import capture_forward_inputs
from test_henry_hub_support import specification


def fixture():
    spec = specification()
    uid = lambda text: str(uuid5(UUID(spec['catalog_id']), text))
    raw = ('hhd_daily', 'hhd_daily_basis', 'h_monthly', 'h_monthly_basis')
    members = {curve: [uid('contract:' + curve)] for curve in raw}
    members.update({row['curve']: [row['contract_id']] for row in spec['fixings']})
    metadata = [dict(catalog_id=spec['catalog_id'], revision_id=spec['source_market_revision_id'],
                     reference_catalog_id=spec['reference_catalog_id'], reference_revision_id=spec['source_reference_revision_id'],
                     curve_code=curve, contract_id=members[curve][0], series_id=uid('series:' + curve), ordinal=0,
                     quote_code='synthetic_basis' if curve.endswith('basis') else spec['quote_code'], rule='direct',
                     direct_source_code=spec['source_code'], direct_role='trader_mark' if curve.endswith('basis') else 'daily_settlement',
                     source_kind='synthetic') for curve in raw]
    observations, frames = [], []
    for at in ('2026-08-01T20:00:00Z', '2026-08-02T20:00:00Z'):
        pins = []
        for series in metadata:
            observation = dict(catalog_id=spec['catalog_id'], id=uid(series['series_id'] + at), definition_revision_id=spec['source_market_revision_id'],
                               series_id=series['series_id'], reference_catalog_id=spec['reference_catalog_id'], reference_revision_id=spec['source_reference_revision_id'],
                               source_code=series['direct_source_code'], source_event_id='synthetic_event:' + at + ':' + series['curve_code'], source_revision_id='1',
                               role=series['direct_role'], publication_state='final', observed_at=at, received_at=at, value='3.1400', corrects_id=None)
            observations.append(observation); pins.append(observation['id'])
        frames.append(dict(at=at, observationIds=pins, curveContracts=deepcopy(members)))
    manifest = dict(catalogId=spec['catalog_id'], revisionId=spec['source_market_revision_id'],
                    referenceCatalogId=spec['reference_catalog_id'], referenceRevisionId=spec['source_reference_revision_id'], frames=frames)
    return spec, manifest, observations, metadata


class HenryForwardInputChecks(unittest.TestCase):
    def test_exact_captured_values_and_frame_membership_are_preserved(self):
        args = fixture(); before = deepcopy(args)
        result = prepare(*args)
        self.assertEqual(result, prepare(*args)); self.assertEqual(args, before)
        self.assertEqual(result['curveMarket']['revisionId'], args[0]['target_market_revision_id'])
        for old, new in zip(args[1]['frames'], result['curveMarket']['frames']):
            self.assertEqual(old['at'], new['at']); self.assertEqual(old['curveContracts'], new['curveContracts'])
            self.assertEqual(len(old['observationIds']), len(new['observationIds']))
            self.assertFalse(set(old['observationIds']) & set(new['observationIds']))
        self.assertIn('synthetic_henry_forward_v1:', result['sql'])
        self.assertNotIn('UPDATE market_data.observations', result['sql'])
        self.assertNotIn('DISABLE TRIGGER', result['sql'])

    def test_missing_ambiguous_unpinned_or_nonsynthetic_inputs_refuse(self):
        for mutation in ('missing', 'duplicate', 'preliminary', 'licensed', 'wrong_revision', 'unmatched_frame', 'unmatched_row', 'wrong_role', 'wrong_membership', 'float_value'):
            with self.subTest(mutation=mutation):
                spec, manifest, rows, metadata = fixture()
                if mutation == 'missing': rows.pop()
                if mutation == 'duplicate': rows.append(deepcopy(rows[0]))
                if mutation == 'preliminary': rows[0]['publication_state'] = 'preliminary'
                if mutation == 'licensed': metadata[0]['source_kind'] = 'licensed_feed'
                if mutation == 'wrong_revision': rows[0]['definition_revision_id'] = spec['target_market_revision_id']
                if mutation == 'unmatched_frame': manifest['frames'][0]['observationIds'] = []
                if mutation == 'unmatched_row': manifest['frames'] = manifest['frames'][:1]
                if mutation == 'wrong_role': rows[0]['role'] = 'index_fixing'
                if mutation == 'wrong_membership': manifest['frames'][0]['curveContracts']['hhd_daily'] = [spec['fixings'][0]['contract_id']]
                if mutation == 'float_value': rows[0]['value'] = 3.14
                with self.assertRaises(ValueError): prepare(spec, manifest, rows, metadata)

    def test_explicit_frozen_final_is_retained_without_selecting_a_new_price(self):
        spec, manifest, rows, metadata = fixture()
        selected = rows[0]; selected['role'] = 'final_contract_settlement'
        manifest['frames'][0]['observationIds'].remove(selected['id'])
        manifest['frames'][0]['frozenObservationIds'] = [selected['id']]
        frame = prepare(spec, manifest, rows, metadata)['curveMarket']['frames'][0]
        self.assertEqual(len(frame['frozenObservationIds']), 1)
        self.assertEqual(len(frame['observationIds']), 3)

    def test_partial_raw_coverage_requires_explicit_frame_expectations(self):
        spec, manifest, rows, metadata = fixture()
        removed = rows.pop(0)
        manifest['frames'][0]['observationIds'].remove(removed['id'])
        with self.assertRaises(ValueError): prepare(spec, manifest, rows, metadata)
        manifest['frames'][0]['requiredSeries'] = [row['series_id'] for row in metadata if row['series_id'] != removed['series_id']]
        result = prepare(spec, manifest, rows, metadata)
        self.assertEqual(len(result['curveMarket']['frames'][0]['observationIds']), 3)
        self.assertEqual(result['curveMarket']['frames'][0]['curveContracts'], manifest['frames'][0]['curveContracts'])

    def test_nullable_frozen_selection_captures_no_frozen_inputs(self):
        spec, manifest, rows, metadata = fixture()
        for frame in manifest['frames']: frame['frozenObservationIds'] = None
        replies = iter([json.dumps(metadata), json.dumps(rows)])
        captured, observations, exact_metadata = capture_forward_inputs(lambda _: next(replies), spec, manifest)
        result = prepare(spec, captured, observations, exact_metadata)
        self.assertEqual(observations, rows)
        self.assertTrue(all('frozenObservationIds' not in frame for frame in result['curveMarket']['frames']))
        self.assertTrue(all(frame['frozenObservationIds'] is None for frame in manifest['frames']))


if __name__ == '__main__':
    unittest.main()
