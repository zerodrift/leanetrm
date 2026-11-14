"""Knowledge-only generated futures retain their accepted option causality."""
import json
from uuid import uuid4
from check_reference_editing import main
from check_lifecycle_actions import checks as action_checks, fixture


def checks(query, manifest):
    action_checks(query, manifest)
    catalog, tenant, workspace = (manifest[k] for k in ('catalogId', 'tenantId', 'workspaceId'))
    issuer = manifest['identityIssuer']
    event, trade, binding = query(f"SELECT e.id||'|'||e.futures_trade_id||'|'||a.binding_id FROM trading.futures_option_events e "
                                 f"JOIN settlement_data.futures_margin_adoptions a ON(a.catalog_id,a.trade_id,a.publication_order)="
                                 f"(e.catalog_id,e.futures_trade_id,e.publication_order) WHERE e.catalog_id='{catalog}' ORDER BY e.publication_order LIMIT 1;").split('|')

    def call(sql):
        token = query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
                      f"'{issuer}','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}COMMIT;")

    context = call(f"SELECT security.capture_date_context('{catalog}','2026-10-16',NULL);")
    at = '2026-10-16T15:30:00Z'

    def read(event_id=event, binding_id=binding, context_id=context, linked=True):
        name = 'read_futures_known_linked_history' if linked else 'read_futures_known_history'
        args = f"'{catalog}','{trade}','{binding_id}','{context_id}','{at}'" + (f",'{event_id}'" if linked else '')
        return call(f"SELECT security.use_market_context('{catalog}','{context_id}');"
                    f"SELECT security.use_trading_context('{catalog}','{at}','2026-10-16');"
                    f"SELECT settlement_data.{name}({args})::text;")

    result = json.loads(read())
    assert result['available'] and result['causalEventId'] == event and result['origin']['trade_id'] == trade
    assert result['asOf'].startswith('2026-10-16T15:30:00') and result['origin']['effective_at'] > result['asOf']
    assert result['heads'] == [] and result['cash'] == []
    for kwargs, code in ((dict(event_id=str(uuid4())), 'futures_known_history_context'),
                         (dict(binding_id=str(uuid4())), 'futures_known_history_context'),
                         (dict(linked=False), 'futures_margin_unavailable')):
        try:
            read(**kwargs)
        except RuntimeError as error:
            assert code in str(error), str(error)
        else:
            raise AssertionError('Expected ' + code)
    early = query(f"SELECT t.context_id FROM trading.contexts t WHERE t.catalog_id='{catalog}' AND t.known_through<"
                  f"(SELECT e.publication_order FROM trading.futures_option_events e WHERE(e.catalog_id,e.id)=('{catalog}','{event}')) "
                  "ORDER BY t.known_through DESC LIMIT 1;")
    assert early
    try:
        read(context_id=early)
    except RuntimeError as error:
        assert 'futures_known_history_context' in str(error), str(error)
    else:
        raise AssertionError('A pre-event source accepted a later generated future.')
    print('PASS source-known generated future: exact causal event and adoption, no opening holding, source cutoff and owner isolation', flush=True)


if __name__ == '__main__':
    main(checks=checks, fixture_factory=fixture)
