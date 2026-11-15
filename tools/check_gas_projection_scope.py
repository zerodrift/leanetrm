"""The managed scope boundary cannot recapture Gas authority after a base wait."""
import json
import os
from pathlib import Path
import subprocess
import sys
from uuid import uuid4

sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'tools'))
from check_reference_editing import main
from check_gas_owned_holdings import checks as baseline
from check_futures_margin_adoption import successor_grants
from check_lifecycle_economic_worker import worker_checks,upgrade_checks as worker_upgrade,children
from owned_process import stop_owned_groups
from preview_fixture import fixture as base_fixture


def fixture(root):
    reference,market,security,manifest=base_fixture(root)
    manifest['identityIssuer']='https://preview.invalid'
    return reference,market,security,manifest


def checks(query,manifest,*,probe='base'):
    assert probe in ('base','callback','both')
    baseline(query,manifest,economic_worker=worker_checks)
    host=Path(query('SHOW unix_socket_directories;'))
    # The existing baseline retains an exact source-known future account/book.
    # No opening holding or artificial market input is needed for scope admission.
    path=host/'gas-owned-managed.json'
    retained=json.loads(path.read_text())
    signature='settlement_data.futures_margin_preview_authority(uuid)'
    original=query(f"SELECT pg_get_functiondef('{signature}'::regprocedure);")
    needle='RETURN settlement_data.futures_margin_preview_authority_pre_risk(catalog);'
    assert original.count(needle)==1
    changed=original.replace('job risk.jobs;BEGIN','job risk.jobs;fixture_until timestamptz;BEGIN')
    assert changed!=original
    changed=changed.replace(needle,'fixture_until=settlement_data.futures_margin_preview_authority_pre_risk(catalog);'
        'PERFORM pg_sleep(4);RETURN fixture_until;')
    path=host/'gas-scope-authority.json'
    root=Path(__file__).resolve().parents[1]
    def verify(test_case,book_id=None,context_id=None):
        selected=retained|dict(testCase=test_case)
        if book_id is not None:selected['bookId']=book_id
        if context_id is not None:selected['contextId']=context_id
        path.write_text(json.dumps(selected));path.chmod(0o600)
        subprocess.run([os.environ.get('DOTNET','dotnet'),'run','--project','tests/OpenEtrm.Api.Checks','--no-build','--no-restore',
            '--','--gas-scope-authority',str(path)],cwd=root,check=True)
    verify('original')
    catalog,tenant,workspace=(manifest[k] for k in ('catalogId','tenantId','workspaceId'))
    issuer=manifest['identityIssuer']
    token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
        f"'{issuer}','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
    retained['otherContextId']=query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');"
        f"SELECT security.capture_date_context('{catalog}','2020-12-19',NULL);COMMIT;")
    verify('changed_source')
    # Give this control an explicitly empty book and a later captured source
    # that includes its own typed metadata publication. Existing fixture books
    # may contain source-known future ownership even with an empty opening.
    no_gas_book=str(uuid4())
    query("BEGIN;SELECT pg_advisory_xact_lock(730849102);"
        f"INSERT INTO trading.publications SELECT '{catalog}',coalesce(max(publication_order),0)+1,pg_current_xact_id() FROM trading.publications WHERE catalog_id='{catalog}';"
        f"INSERT INTO trading.books SELECT b.catalog_id,'{no_gas_book}',b.portfolio_id,'Synthetic scope without gas',p.publication_order,b.effective_at "
        "FROM trading.books b JOIN trading.publications p ON p.catalog_id=b.catalog_id AND p.publication_xid=pg_current_xact_id() "
        f"WHERE(b.catalog_id,b.id)=('{catalog}','{retained['bookId']}');COMMIT;")
    token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
        f"'{issuer}','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
    no_gas_context=query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');"
        f"SELECT security.capture_date_context('{catalog}','2020-12-19',NULL);COMMIT;")
    before=query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{manifest['catalogId']}';")
    if probe=='callback':
        from gas_scope_callback_authority import checks as callback_checks
        callback_checks(query,manifest,verify)
        assert query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{manifest['catalogId']}';")==before
        return
    try:
        query(changed)
        with successor_grants(query,manifest,'trader','ReadGasOwnedHoldings',issuer=manifest['identityIssuer']):
            verify('gas_successor')
            verify('original')
        assert query(f"SELECT EXISTS(SELECT 1 FROM trading.books WHERE(catalog_id,id)=('{catalog}','{no_gas_book}')) AND NOT EXISTS("
            "SELECT 1 FROM trading.gas_owned_positions p JOIN trading.gas_owned_accounts a ON(a.catalog_id,a.id)=(p.catalog_id,p.account_id) "
            f"WHERE(a.catalog_id,a.book_id)=('{catalog}','{no_gas_book}'));")=='t'
        with successor_grants(query,manifest,'trader','ReadGasOwnedHoldings',issuer=manifest['identityIssuer']):
            verify('no_gas',no_gas_book,no_gas_context)
    finally:query(original)
    assert query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{manifest['catalogId']}';")==before
    if probe=='both':
        from gas_scope_callback_authority import checks as callback_checks
        callback_checks(query,manifest,verify)
        assert query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{manifest['catalogId']}';")==before


if __name__=='__main__':
    from gas_projection_scope_upgrade import checks as scope_upgrade
    def upgrade(query,migration):
        worker_upgrade(query,migration);scope_upgrade(query,migration)
    try:
        main(checks=lambda query,manifest:checks(query,manifest,probe='both'),fixture_factory=fixture,before_migration=upgrade,
            cleanup_ready=lambda:not children)
    finally:stop_owned_groups(children)
