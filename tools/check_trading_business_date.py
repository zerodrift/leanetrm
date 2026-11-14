"""Explicit trading business dates in an owned, unmodified PostgreSQL cluster."""
from check_reference_editing import main
from preview_fixture import catalogue_fixture, trading_fixture
from check_futures_margin_adoption import successor_grants


def checks(query, manifest):
    catalog, tenant, workspace = (manifest[k] for k in ('catalogId', 'tenantId', 'workspaceId'))
    query(trading_fixture(catalog))
    query(catalogue_fixture(manifest, 'https://preview.invalid'))
    # A privileged fixture wrapper observes the private source gate under the
    # issued runtime identity; it is no evidence of ordinary write admission.
    query("CREATE FUNCTION public.synthetic_business_source(uuid,uuid,timestamptz,uuid) RETURNS void "
          "LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog AS $$ "
          "SELECT settlement_data.require_futures_margin_source($1,$2,$3,$4);$$;"
          "ALTER FUNCTION public.synthetic_business_source(uuid,uuid,timestamptz,uuid) OWNER TO openetrm_security_owner;"
          "REVOKE ALL ON FUNCTION public.synthetic_business_source(uuid,uuid,timestamptz,uuid) FROM PUBLIC;"
          "GRANT EXECUTE ON FUNCTION public.synthetic_business_source(uuid,uuid,timestamptz,uuid) TO openetrm_runtime;")

    def issue(profile='risk'):
        return query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
                     f"'https://preview.invalid','{profile}','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")

    def call(sql, profile='risk'):
        return query("SET SESSION AUTHORIZATION preview_runtime;BEGIN;"
                     f"SELECT security.use_context('{issue(profile)}');{sql}COMMIT;")

    context = call(f"SELECT security.capture_date_context('{catalog}','2020-12-20',NULL);")
    bind = f"SELECT security.use_market_context('{catalog}','{context}');"
    at = '2020-12-21T01:00:00Z'

    def reject(sql, code):
        try:
            call(bind + sql)
        except RuntimeError as error:
            assert code in str(error), str(error)
        else:
            raise AssertionError('Expected ' + code)

    reject(f"SELECT security.use_trading_context('{catalog}','{at}');", 'knowledge_coverage')
    accepted = call(bind + f"SELECT security.use_trading_context('{catalog}','{at}','2020-12-20'::date);"
                    f"SELECT public.synthetic_business_source('{catalog}','{context}','{at}','{manifest['revisionId']}');"
                    f"SELECT security.trading_context_day_matches('{catalog}','{context}','{at}');")
    assert accepted == 't', accepted
    reject(f"SELECT security.use_trading_context('{catalog}','{at}','2020-12-21'::date);", 'knowledge_coverage')
    reject(f"SELECT security.use_trading_context('{catalog}','{at}','2020-12-20'::date);"
           f"SELECT security.use_trading_context('{catalog}','{at}','2020-12-20'::date);", 'knowledge_coverage')
    assert call(bind + f"SELECT security.trading_context_day_matches('{catalog}','{context}','{at}');") == 'f'
    assert call(bind + "SELECT set_config('openetrm.trading_business_date','2020-12-20',true);"
                f"SELECT security.trading_context_day_matches('{catalog}','{context}','{at}');").splitlines()[-1] == 'f'
    assert call(bind + f"SELECT security.use_trading_context('{catalog}','2020-12-20T16:00:00Z');"
                f"SELECT security.trading_context_day_matches('{catalog}','{context}','2020-12-20T16:00:00Z');") == 't'
    print('PASS explicit trading business date: cross-midnight context, wrong date/rebind refusal, legacy UTC profile and unforgeable request scope', flush=True)
    admit = f"SELECT security.admit_trading_business_source('{catalog}','{context}','{at}','2020-12-20');"
    source = f"SELECT public.synthetic_business_source('{catalog}','{context}','{at}','{manifest['revisionId']}');"
    assert call(admit + source + f"SELECT security.trading_context_day_matches('{catalog}','{context}','{at}');").splitlines()[-1] == 't'
    assert call(f"SELECT security.admit_trading_business_source('{catalog}','{context}','{at}','2020-12-20')="
                f"security.admit_trading_business_source('{catalog}','{context}','{at}','2020-12-20');") == 't'
    # A source pin survives restoring a temporary market read without changing
    # the command's write scope, and cannot be overwritten by another source.
    assert call(admit + 'SAVEPOINT synthetic_read;' + bind +
                f"SELECT security.use_trading_context('{catalog}','{at}','2020-12-20');"
                'ROLLBACK TO SAVEPOINT synthetic_read;RELEASE SAVEPOINT synthetic_read;' + source +
                f"SELECT security.trading_context_day_matches('{catalog}','{context}','{at}');").splitlines()[-1] == 't'
    reject(admit + f"SELECT security.admit_trading_business_source('{catalog}','{context}','2020-12-21T02:00:00Z','2020-12-20');",
           'trading_source_conflict')
    reject(admit + f"SELECT security.require_admitted_business_source('{catalog}','{context}','{at}','2020-12-20','2090-01-01');",
           'trading_source_denied')
    with successor_grants(query, manifest, 'risk', 'ReadTrades'):
        try:
            call(admit + 'SELECT pg_sleep(6);' + admit)
        except RuntimeError as error:
            assert 'futures_margin_denied' in str(error), str(error)
        else:
            raise AssertionError('A successor grant extended the original source admission')
        assert call(admit + source + f"SELECT security.trading_context_day_matches('{catalog}','{context}','{at}');").splitlines()[-1] == 't'
    print('PASS immutable command source: exact tuple, retained deadline, savepoint restoration, conflict/forged deadline refusal and fresh retry', flush=True)


if __name__ == '__main__':
    main(checks=checks)
