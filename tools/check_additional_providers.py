"""New provider routing and exact billing through HTTP Test on the owned synthetic preview only."""
import json
from uuid import uuid4
from check_model_qualification import command, terms
from check_model_versions import capabilities
from fake_model_provider import event, stream


def recording(vendor, model, served=None):
    served = served or model
    if vendor == 'google':
        return event('message', {'modelVersion': served, 'candidates': [{'index': 0,
            'content': {'role': 'model', 'parts': [{'text': 'ready'}]}, 'finishReason': 'STOP'}],
            'usageMetadata': {'promptTokenCount': 100, 'cachedContentTokenCount': 20, 'candidatesTokenCount': 7,
                              'thoughtsTokenCount': 3, 'totalTokenCount': 110, 'serviceTier': 'standard'}})
    usage = {'input_tokens': 100, 'input_tokens_details': {'cached_tokens': 20}, 'output_tokens': 10,
             'output_tokens_details': {'reasoning_tokens': 3}, 'total_tokens': 110}
    if vendor == 'openai':
        usage['input_tokens_details']['cache_write_tokens'] = 10
        usage['input_tokens_details']['cache_write_tokens_details'] = {'ttl_30m_tokens': 10, 'ttl_12h_tokens': 0}
    return (event('response.created', {'type': 'response.created', 'response': {'id': 'resp_synthetic', 'model': model, 'status': 'in_progress'}})
            + event('response.output_text.delta', {'type': 'response.output_text.delta', 'delta': 'ready'})
            + event('response.completed', {'type': 'response.completed', 'response': {'id': 'resp_synthetic', 'model': served,
                'status': 'completed', 'service_tier': 'default', 'usage': usage,
                'output': [{'type': 'message', 'role': 'assistant', 'content': [{'type': 'output_text', 'text': 'ready'}]}]}}))


def run_checks(query, manifest, fake, trap, admin):
    for vendor, mapping in [('openai', 'openai_responses_v1'), ('google', 'generate_content_v1'),
                            ('xai', 'xai_responses_v1'), ('meta', 'meta_responses_v1')]:
        current = next(p for p in admin.call('settings/ai/providers')['providers'] if p['vendor'] == vendor)
        keyed = admin.call('settings/ai/providers/key', {'operationId': str(uuid4()), 'vendor': vendor,
                          'expectedVersion': current['version'], 'key': 'sk-synthetic-adapter-'+vendor+'-key'})
        recorded = admin.call('settings/ai/providers/terms', terms(vendor, keyed['version']))
        admin.call('settings/ai/providers/attest', command(vendor, recorded['version']))
        identifier = 'model-synthetic-adapter-'+vendor
        added = admin.call('settings/ai/models/add', {'operationId': str(uuid4()), 'vendor': vendor,
                          'identifier': identifier, 'displayName': 'Synthetic adapter '+vendor})
        initial = capabilities(added['id'], 1, outputLimit=1024, reasoningEfforts=['low'], acceptedServed=[])
        if vendor == 'xai':
            del initial['identifierPolicy']  # The original fixed-only HTTP contract.
        saved = admin.call('settings/ai/models/edit', initial)
        assert saved['qualified'] and saved['capability']['mapping'] == mapping
        if vendor == 'xai':
            # Alias approval is an immutable capability fact, not permission for arbitrary
            # served identifiers, new prices, or a different provider.
            legacy = saved
            alias = capabilities(added['id'], saved['version'], outputLimit=1024,
                                 reasoningEfforts=['low'], acceptedServed=['model-synthetic-snapshot'],
                                 identifierPolicy='alias')
            saved = admin.call('settings/ai/models/edit', alias)
            assert saved['qualified'] and saved['capability']['identifierPolicy'] == 'alias'
            assert legacy['capability']['identifierPolicy'] == 'fixed'
            assert admin.call('settings/ai/models/edit', alias) == saved
            assert admin.call('settings/ai/models/edit', initial) == legacy
            assert admin.call('settings/ai/models/edit', alias | {'hasIdentifierPolicy': False}, expected=400)['code'] == 'request_format'
            conflict = admin.call('settings/ai/models/edit', alias | {'identifierPolicy': 'fixed'}, expected=409)
            assert conflict['code'] == 'model_admin_conflict'
            for invalid in (None, '', 'wildcard'):
                bad = capabilities(added['id'], saved['version'], identifierPolicy=invalid)
                assert admin.call('settings/ai/models/edit', bad, expected=422)['code'] == 'model_admin_input'
            denied = admin.call('settings/ai/models/enable', {'operationId': str(uuid4()), 'modelId': added['id'],
                                'expectedVersion': saved['version']}, expected=409)
            assert denied['code'] == 'model_provider_disabled'
            current = next(p for p in admin.call('settings/ai/providers')['providers'] if p['vendor'] == vendor)
            admin.call('settings/ai/providers/enable', command(vendor, current['version']))
            saved = admin.call('settings/ai/models/enable', {'operationId': str(uuid4()), 'modelId': added['id'],
                               'expectedVersion': saved['version']})
            assert saved['enabled'] and saved['capability']['identifierPolicy'] == 'alias'
        # Deliberately synthetic rates: the expected costs below are independent decimal amounts.
        admin.call('settings/ai/models/price', {'operationId': str(uuid4()), 'modelId': added['id'], 'expectedSequence': 0,
            'currency': {'catalogId': manifest['referenceCatalogId'], 'revisionId': manifest['referenceRevisionId'], 'code': 'usd'},
            'rates': {'uncachedInput': '1', 'cacheRead': '2', 'cacheWrite': '3' if vendor == 'openai' else None, 'output': '4'},
            'tier': None, 'effectiveFrom': '2020-01-01T00:00:00Z', 'effectiveTo': None})
        body = {'operationId': str(uuid4()), 'modelId': added['id'], 'reasoning': 'low'}
        sent = len(fake.received)
        fake.respond(stream(recording(vendor, identifier)))
        result = admin.call('settings/ai/providers/test', body)
        assert result['outcome'] == 'succeeded' and result['mapping'] == mapping, result
        assert result['usage']['status'] == 'recognized'
        assert result['cost']['amount'] == ('0.00018' if vendor == 'openai' else '0.00016'), result
        request = fake.received[-1]
        expected_path = f'/v1beta/models/{identifier}:streamGenerateContent?alt=sse' if vendor == 'google' else '/v1/responses'
        assert request['path'] == expected_path and len(fake.received) == sent+1
        headers = dict(request['headers'])
        assert headers.get('x-goog-api-key') == 'sk-synthetic-adapter-google-key' if vendor == 'google' else headers['authorization'] == 'Bearer sk-synthetic-adapter-'+vendor+'-key'
        assert admin.call('settings/ai/providers/test', body) == result and len(fake.received) == sent+1
        if vendor == 'openai':
            unsupported = recording(vendor, identifier).replace('"ttl_30m_tokens":10,"ttl_12h_tokens":0',
                                                               '"ttl_30m_tokens":9,"ttl_12h_tokens":1')
            fake.respond(stream(unsupported))
            refused = admin.call('settings/ai/providers/test', body | {'operationId': str(uuid4())})
            assert refused['outcome'] == 'usage_unrecognized' and refused['cost']['status'] != 'priced', refused
        fake.respond(stream(recording(vendor, identifier, 'model-synthetic-wrong-served')))
        wrong = admin.call('settings/ai/providers/test', body | {'operationId': str(uuid4())})
        assert wrong['outcome'] == 'model_mismatch' and wrong['served'] == 'model-synthetic-wrong-served', wrong
        assert wrong['usage']['status'] == 'unrecognized' and wrong['cost']['status'] != 'priced'
        if vendor == 'xai':
            # Announce the approved snapshot in the first event, before any text.
            wire = recording(vendor, identifier).replace('"model":"'+identifier+'"',
                                                       '"model":"model-synthetic-snapshot"')
            fake.respond(stream(wire))
            snapshot = admin.call('settings/ai/providers/test', body | {'operationId': str(uuid4())})
            assert snapshot['outcome'] == 'succeeded' and snapshot['identifier'] == identifier
            assert snapshot['served'] == 'model-synthetic-snapshot' and snapshot['cost']['amount'] == '0.00016'
            assert snapshot['capabilityVersion'] == saved['capability']['version']
            assert query(f"SELECT identifier_policy FROM assistant.model_versions WHERE model_id='{added['id']}' ORDER BY version;") == 'fixed\nalias'
        assert query(f"SELECT count(*) FROM assistant.model_calls c LEFT JOIN assistant.model_call_outcomes o ON o.call_id=c.id WHERE c.model_id='{added['id']}' AND o.call_id IS NULL;") == '0'
        print('PASS '+vendor+' HTTP Test: admitted mapping, endpoint, credential header, exact cost, replay and durable mismatch close', flush=True)
    assert trap.connections == 0
