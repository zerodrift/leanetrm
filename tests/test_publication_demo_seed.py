"""The synthetic publication loader uses exact caller choices and never overwrites retained definitions."""
from copy import deepcopy
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
from publication_demo_seed import EXAMPLES, plan, seed


def fixture():
    references = {'catalogId': '10000000-0000-0000-0000-000000000001',
                  'revisionId': '20000000-0000-0000-0000-000000000001'}
    def ref(code): return {'revision': deepcopy(references), 'code': code}
    def choice(code): return {'reference': ref(code), 'name': 'Synthetic ' + code}
    catalog = {'catalogId': '40000000-0000-0000-0000-000000000001', 'references': references,
               'canEdit': True, 'publishers': [], 'publications': [],
               'commodities': [choice(code) for code in ('gas', 'crude', 'ngl', 'power', 'lng')],
               'calendars': [choice('business')], 'clocks': [choice('utc')],
               'implementations': ['gas_daily_explicit_v1', 'gas_monthly_explicit_v1',
                                   'crude_placeholder_v1', 'ngl_placeholder_v1', 'power_placeholder_v1', 'lng_placeholder_v1'],
               'indices': [{'key': ref(code), 'commodity': ref('gas'), 'location': ref('henry_hub'),
                            'quote': ref(quote), 'unit': {'currency': ref('usd'), 'quantity': ref('mmbtu'), 'multiplier': '1'},
                            'name': 'Synthetic ' + code} for code, quote in
                           [('gas_daily', 'gas_price'), ('iferc', 'gas_price'), ('iferc', 'other_quote')]]}
    commodities = dict(gas_daily='gas', gas_monthly='gas', argus_crude='crude', opis_crude='crude',
                       opis_ngl='ngl', iso_power='power', platts_lng='lng')
    bindings = {key: dict(commodity=commodity, calendar='business', clock='utc') for key, commodity in commodities.items()}
    bindings['gas_daily'].update(index='gas_daily', quote='gas_price')
    bindings['gas_monthly'].update(index='iferc', quote='gas_price')
    return catalog, bindings


class Api:
    def __init__(self, catalog):
        self.catalog = deepcopy(catalog)
        self.reads = []
        self.writes = []
        self.lose_response = False

    def call(self, path, body=None):
        if body is None:
            assert path.startswith('price-publications?snapshotId=')
            self.reads.append(path)
            return deepcopy(self.catalog)
        assert path == 'price-publications/save'
        assert body['expectedVersion'] == 0
        self.writes.append(deepcopy(body))
        saved = deepcopy(body['definition'])
        assert not any(p['key']['id'] == saved['key']['id'] for p in self.catalog['publications'])
        self.catalog['publications'].append(saved)
        if saved['publisher'] not in self.catalog['publishers']:
            self.catalog['publishers'].append(deepcopy(saved['publisher']))
        if self.lose_response:
            self.lose_response = False
            raise ConnectionError('Synthetic lost response after commit')
        return saved


class PublicationDemoSeedChecks(unittest.TestCase):
    def test_exact_plan_has_distinct_gas_indices_and_nonexecutable_placeholders(self):
        catalog, bindings = fixture()
        values = plan(catalog, bindings)
        self.assertEqual(set(values), set(bindings))
        self.assertEqual(values['gas_daily']['indices'][0]['key']['code'], 'gas_daily')
        self.assertEqual(values['gas_monthly']['indices'][0]['key']['code'], 'iferc')
        self.assertEqual(values['gas_monthly']['indices'][0]['quote']['code'], 'gas_price')
        self.assertEqual(len({v['publisher']['key']['id'] for v in values.values()}), 4)
        for key, value in values.items():
            self.assertIn('Synthetic', value['name'])
            self.assertIn('Synthetic', value['publisher']['name'])
            self.assertEqual(value['commodities'][0]['code'], bindings[key]['commodity'])
            if key not in ('gas_daily', 'gas_monthly'):
                self.assertTrue(value['implementation'].endswith('_placeholder_v1'))
                self.assertEqual(value['indices'], [])

    def test_repeat_and_new_snapshot_do_not_write_or_duplicate(self):
        catalog, bindings = fixture()
        api = Api(catalog)
        first = seed(api, 'snapshot-one', bindings)
        self.assertEqual(len(api.writes), len(EXAMPLES))
        self.assertTrue(all('marketRevisionId' not in body for body in api.writes))
        self.assertEqual(seed(api, 'snapshot-two', bindings), first)
        self.assertEqual(len(api.writes), len(EXAMPLES))

    def test_explicit_market_is_correlated_and_retained_in_every_command(self):
        catalog, bindings = fixture()
        market = '60000000-0000-0000-0000-000000000001'
        catalog['marketRevisionId'] = market
        api = Api(catalog)
        first = seed(api, 'snapshot-one', bindings, market_revision_id=market)
        self.assertEqual(api.reads, ['price-publications?snapshotId=snapshot-one&marketRevisionId=' + market])
        self.assertEqual({body.get('marketRevisionId') for body in api.writes}, {market})
        self.assertEqual(seed(api, 'snapshot-two', bindings, market_revision_id=market), first)
        self.assertEqual(len(api.writes), len(EXAMPLES))
        for returned in (None, '60000000-0000-0000-0000-000000000002'):
            catalog['marketRevisionId'] = returned
            other = Api(catalog)
            with self.assertRaises(ValueError): seed(other, 'snapshot-one', bindings, market_revision_id=market)
            self.assertEqual(other.writes, [])
    def test_lost_response_resumes_only_missing_definitions(self):
        catalog, bindings = fixture()
        api = Api(catalog)
        api.lose_response = True
        with self.assertRaises(ConnectionError): seed(api, 'snapshot-one', bindings)
        saved = seed(api, 'snapshot-two', bindings)
        self.assertEqual(len(saved), len(EXAMPLES))
        self.assertEqual(len(api.writes), len(EXAMPLES))

    def test_missing_or_mismatched_reference_refuses_before_any_write(self):
        for mutation in ('missing_commodity', 'wrong_index_commodity', 'missing_quote', 'incomplete_plan'):
            with self.subTest(mutation=mutation):
                catalog, bindings = fixture()
                if mutation == 'missing_commodity': catalog['commodities'] = catalog['commodities'][:-1]
                if mutation == 'wrong_index_commodity': bindings['gas_daily']['commodity'] = 'lng'
                if mutation == 'missing_quote': bindings['gas_monthly']['quote'] = 'unavailable'
                if mutation == 'incomplete_plan': del bindings['opis_ngl']
                api = Api(catalog)
                with self.assertRaises(ValueError): seed(api, 'snapshot-one', bindings)
                self.assertEqual(api.writes, [])

    def test_changed_existing_seed_refuses_without_overwriting_or_partial_seed(self):
        catalog, bindings = fixture()
        existing = plan(catalog, bindings)['platts_lng']
        existing['clock']['code'] = 'another_clock'
        catalog['publications'] = [existing]
        api = Api(catalog)
        with self.assertRaises(ValueError): seed(api, 'snapshot-one', bindings)
        self.assertEqual(api.writes, [])

    def test_existing_same_name_at_another_identity_is_not_duplicated(self):
        catalog, bindings = fixture()
        existing = plan(catalog, bindings)['gas_monthly']
        existing['key']['id'] = '50000000-0000-0000-0000-000000000001'
        catalog['publications'] = [existing]
        api = Api(catalog)
        with self.assertRaises(ValueError): seed(api, 'snapshot-one', bindings)
        self.assertEqual(api.writes, [])

    def test_ambiguous_exact_index_row_and_wrong_revision_refuse_before_writes(self):
        for mutation in ('duplicate', 'wrong_revision'):
            with self.subTest(mutation=mutation):
                catalog, bindings = fixture()
                if mutation == 'duplicate': catalog['indices'].append(deepcopy(catalog['indices'][0]))
                else: catalog['clocks'][0]['reference']['revision']['revisionId'] = 'different_revision'
                api = Api(catalog)
                with self.assertRaises(ValueError): seed(api, 'snapshot-one', bindings)
                self.assertEqual(api.writes, [])

    def test_malformed_accepted_receipt_stops_then_rereads_actual_saved_fact(self):
        catalog, bindings = fixture()
        api = Api(catalog)
        original = api.call
        def corrupted(path, body=None):
            reply = original(path, body)
            return {} if body else reply
        api.call = corrupted
        with self.assertRaises(ValueError): seed(api, 'snapshot-one', bindings)
        self.assertEqual(len(api.writes), 1)
        api.call = original
        self.assertEqual(len(seed(api, 'snapshot-two', bindings)), len(EXAMPLES))
        self.assertEqual(len(api.writes), len(EXAMPLES))

    def test_pure_plan_rejects_changed_retained_publication_terms(self):
        catalog, bindings = fixture()
        retained = list(plan(catalog, bindings).values())
        retained[0]['clock']['code'] = 'changed_retained_clock'
        catalog['publications'] = retained
        with self.assertRaises(ValueError): plan(catalog, bindings)

    def test_readonly_account_can_inspect_completed_seed_but_cannot_create(self):
        catalog, bindings = fixture()
        catalog['canEdit'] = False
        api = Api(catalog)
        with self.assertRaises(ValueError): seed(api, 'snapshot-one', bindings)
        self.assertEqual(api.writes, [])
        catalog['publications'] = list(plan(catalog, bindings).values())
        self.assertEqual(len(seed(Api(catalog), 'snapshot-one', bindings)), len(EXAMPLES))


if __name__ == '__main__':
    unittest.main()
