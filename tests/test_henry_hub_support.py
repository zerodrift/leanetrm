"""The Henry Hub operator artifact requires explicit identities and timing, never edits its retained source."""
from copy import deepcopy
from pathlib import Path
import sys
import unittest
from uuid import UUID, uuid5

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
from henry_hub_support import prepare_sql, validate_spec


def specification():
    uid = lambda key: str(uuid5(UUID('10000000-0000-0000-0000-000000000001'), key))
    return dict(catalog_id=uid('market'), reference_catalog_id=uid('reference'),
                source_reference_revision_id=uid('source_ref'), source_market_revision_id=uid('source_market'),
                target_reference_revision_id=uid('target_ref'), target_market_revision_id=uid('target_market'),
                revision_key='synthetic_henry_publications_v1', commodity_code='gas', location_code='henry_hub',
                quote_code='nymex_ng_quote', calendar_code='gas_calendar', clock_code='synthetic_central', source_code='synthetic_demo_close',
                daily_index_code='synthetic_henry_hub_gas_daily', monthly_index_code='synthetic_henry_hub_iferc_monthly',
                daily_target_code='synthetic_henry_hub_daily_target', monthly_target_code='synthetic_henry_hub_monthly_target',
                fixings=[dict(curve='hhd_location', contract_id=uid('daily'), **{'from':'2026-09-01','to':'2026-09-02'},
                              expiry_at='2026-08-31T20:00:00Z', fixing_at='2026-08-31T20:00:00Z'),
                         dict(curve='h_location', contract_id=uid('monthly'), **{'from':'2026-12-01','to':'2027-01-01'},
                              expiry_at='2026-11-25T20:00:00Z', fixing_at='2026-11-25T20:00:00Z')])


class HenryHubSupportChecks(unittest.TestCase):
    def test_spec_is_explicit_and_preparation_does_not_mutate_it(self):
        spec = specification()
        before = deepcopy(spec)
        validate_spec(spec)
        self.assertEqual(prepare_sql(spec), prepare_sql(spec))
        self.assertEqual(spec, before)

    def test_same_revision_missing_timings_and_nonutc_instants_refuse(self):
        for key in ('same_revision', 'missing_expiry', 'offset_time', 'unknown_curve', 'duplicate_contract'):
            with self.subTest(key=key):
                spec = specification()
                if key == 'same_revision': spec['target_reference_revision_id'] = spec['source_reference_revision_id']
                if key == 'missing_expiry': del spec['fixings'][0]['expiry_at']
                if key == 'offset_time': spec['fixings'][0]['fixing_at'] = '2026-08-31T20:00:00+01:00'
                if key == 'unknown_curve': spec['fixings'][0]['curve'] = 'h_monthly'
                if key == 'duplicate_contract': spec['fixings'][1]['contract_id'] = spec['fixings'][0]['contract_id']
                with self.assertRaises(ValueError): prepare_sql(spec)

    def test_operator_sql_keeps_observations_and_activation_outside_its_scope(self):
        sql = prepare_sql(specification())
        self.assertNotIn('INSERT INTO market_data.observations', sql)
        self.assertNotIn('DISABLE TRIGGER', sql)
        self.assertNotIn('session_replication_role', sql)
        self.assertIn('security.reference_display_tables()', sql)
        self.assertIn('henry_support_replay_conflict', sql)
        self.assertNotIn('INSERT INTO market_data.rate_curves', sql)
        self.assertNotIn('UPDATE market_data.curve_definition_versions', sql)


if __name__ == '__main__':
    unittest.main()
