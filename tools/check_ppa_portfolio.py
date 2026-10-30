"""Whole-book consumers cannot publish a futures/swap subset of a PPA book.

The Blotter is a report, not a whole-book consumer (D246): it lists a PPA booked under other reference or
definition versions than the selected close as a position it cannot value, and leaves it out of valuation totals.
"""
import json
from pathlib import Path
import subprocess
from uuid import uuid4
from preview_client import PreviewClient
from preview_fixture import trade_booking

TRADES = {'mode': 'trades', 'bookIds': [], 'portfolioIds': [], 'groupBy': [], 'measures': [], 'pivotConvention': None}
MONEY_BY_BOOK = {**TRADES, 'mode': 'pivot', 'groupBy': ['book'], 'measures': ['presentValue', 'executionPnl', 'tradeCount'],
                 'pivotConvention': 'trading_blotter_money_totals_v1'}


def check(name, condition, detail=None):
    assert condition, f'{name}: {detail}'
    print('PASS', name, flush=True)


def check_blotter(client, query, catalog, frame, book, future, ppa, bundle):
    """The Blotter lists the other-version PPA unvalued, values the rest, and leaves the PPA out of valuation totals."""
    context = {'snapshotId': frame['id'], 'convention': 'trading_workspace_v1', 'knowledgeMode': 'current'}
    scoped = {**TRADES, 'bookIds': [book]}
    read = client.call('trading/workspace', {'context': context, 'view': scoped, 'bookingCatalog': 'directory',
                                             'summaries': [{**MONEY_BY_BOOK, 'bookIds': [book]}]})
    rows = {r['tradeId']: r for r in read['blotter']['holdings']}
    check('a book holding a PPA booked under other versions than the close still reads', set(rows) == {future, ppa}, sorted(rows))
    held = json.loads(query(f"SELECT json_build_object('bindingId',binding_id,'quantity',quantity::text,'revision',revision::text) "
                            f"FROM trading.ppa_revision_bindings WHERE catalog_id='{catalog}' AND trade_id='{ppa}';"))
    row = rows[ppa]
    check('the PPA is listed as not valued, with the reason, and no value', row['unavailableReason'] == 'other_definition_versions'
          and row['presentValue'] is None and row['completePv'] is None and row['instrumentType'] == 'ppa', row)
    check('the PPA keeps its booked facts under the versions it was booked with',
          (row['bindingId'], row['quantity'], row['revision']) == (held['bindingId'], held['quantity'], held['revision'])
          and row['definitionRevisionId'] == bundle['definitionRevisionId']
          and all(r['revision'] == {'catalogId': bundle['referenceCatalogId'], 'revisionId': bundle['referenceRevisionId']}
                  for r in (row['quantitySpecification'], row['currencyReference'])), row)
    check('the PPA is its own market, named by its instrument, so no other position shares its group',
          row['marketKey'] == f"ppa_instrument:{row['instrumentId']}" and row['marketLabel'] == f"PPA · {row['instrumentReference']}", row)
    check('the future in the same book is still valued', rows[future]['unavailableReason'] is None and rows[future]['executionPnl'] is not None)
    check('the read counts the PPA among the positions it leaves out', read['blotter']['unvaluedPositions'] == 1)
    groups = {g['instrumentType']: g for g in read['summaries'][0]['groups']}
    check('the money totals leave the PPA out and say so, and still count its trade',
          groups['ppa']['presentValue'] is None and groups['ppa']['tradeCount'] == 1 and groups['ppa']['unvaluedPositions'] == 1
          and groups['future']['unvaluedPositions'] == 0 and groups['future']['executionPnl'] == rows[future]['executionPnl'], groups)
    return context, scoped


def run_checks(url, query, manifest, dotnet, owned_root, environment):
    catalog, tenant, workspace = [manifest[k] for k in ('catalogId', 'tenantId', 'workspaceId')]
    book = str(uuid4())
    query(f"INSERT INTO trading.books VALUES('{catalog}','{book}','d0000000-0000-0000-0000-000000000001',"
          "'Synthetic complete-scope check',1,'2020-01-01T00:00:00Z');")
    client = PreviewClient(url); client.bootstrap(); client.call('profile', {'profile': 'trader'})
    frame = next(f for f in client.call('workspace')['frames'] if f['at'].startswith('2020-12-15') and not f.get('ppaBundle'))
    context = {'snapshotId': frame['id'], 'convention': 'contract_lot_execution_pnl_v2', 'knowledgeMode': 'current'}
    contract = client.call('futures/catalog', context)['contracts'][0]
    future = client.call('futures/book', {**context, **trade_booking(), 'operationId': str(uuid4()),
        'bookId': book, 'curve': contract['curve'], 'contractId': contract['id'], 'lots': '1', 'executionPrice': '2.9'})

    def runtime(sql):
        token = query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
            f"'https://preview.invalid','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',120);")
        return 'BEGIN;SET LOCAL SESSION AUTHORIZATION preview_runtime;' + f"SELECT security.use_context('{token}');" + sql + 'COMMIT;'

    def capture():
        identity = query(runtime(f"SELECT security.capture_date_context('{catalog}','{frame['at'][:10]}',NULL);"))
        return json.loads(query("SELECT jsonb_build_object('contextId',id,'effectiveOn',effective_on,'knownAt',known_at,'knownThrough',known_through) "
            f"FROM market_data.temporal_contexts WHERE id='{identity}';"))

    before = capture()
    from ppa_demo_terms import profile_terms
    bundle=manifest['ppaR3']['bundle']
    other=next(f for f in client.call('ppa/frames') if f.get('ppaBundle')==bundle and f['at']==frame['at'])
    terms=profile_terms(manifest);terms.update(bundle)
    terms['delivery'].update({'from':'2020-12-01','to':'2021-01-01'})
    payload=dict(operationId=str(uuid4()),snapshotId=other['id'],tradeId=None,expectedRevision=None,
        bookId=book,boundaryAt=None,coverageFrom='2020-12-19T05:00:00Z',coverageTo='2020-12-19T06:00:00Z',
        quantity='80',reason='Synthetic whole-book refusal check',memo='',booking=trade_booking(),terms=terms)
    ppa=client.call('ppa/book',payload)['tradeId']
    fixture = Path(owned_root) / 'ppa-portfolio.json'
    fixture.write_text(json.dumps({'frame': frame, 'bookId': book, 'futureTradeId': future['tradeId'], 'before': before, 'after': capture()}))
    def managed(mode):
        subprocess.run([str(dotnet), 'run', '--project', 'tests/OpenEtrm.Api.Checks', '--', mode, str(fixture)],
            cwd=Path(__file__).resolve().parents[1], env=environment, check=True, timeout=90)
    managed('--ppa-portfolio')
    context, scoped = check_blotter(client, query, catalog, frame, book, future['tradeId'], ppa, manifest['ppaR3']['bundle'])
    reference = manifest['ppaR3']['bundle']['referenceCatalogId']
    where = f"catalog_id='{reference}' AND tenant_id='{tenant}' AND workspace_id='{workspace}'"
    saved = query(f"SELECT row_to_json(s) FROM security.reference_catalog_scopes s WHERE {where};")
    assert saved
    try:
        query(f"DELETE FROM security.reference_catalog_scopes WHERE {where};")
        managed('--ppa-portfolio-denied')
        denied = client.call('trading/workspace', {'context': context, 'view': scoped, 'bookingCatalog': 'directory'}, expected=403)
        check('without access to the PPA\'s reference catalog the Blotter read is refused, not shown without it', denied['code'] == 'access_denied', denied)
    finally:
        encoded_scope = saved.replace("'", "''")
        query("INSERT INTO security.reference_catalog_scopes SELECT(json_populate_record(NULL::security.reference_catalog_scopes,"
              f"'{encoded_scope}'::json)).*;")
