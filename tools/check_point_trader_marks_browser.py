"""Publish an empty ordinary point through this tree's owned HTTP and browser."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import tempfile
from uuid import uuid4

from check_reference_editing import main as native_main
from close_session_fixture import sql_session
from gas_transport_financial_managed import value
from owned_lifecycle_browser import api_artifact, build_assets, run as run_browser
from point_trader_mark_fixture import PointTraderMarkFixture, fixture, literal
from preview_fixture import catalogue_fixture


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ['pg-bin', 'dotnet', 'browser']:
        parser.add_argument('--' + name, type=Path, required=True)
    parser.add_argument('--no-build', action='store_true')
    parser.add_argument('--api-bin', type=Path)
    args = parser.parse_args()
    for path in [args.pg_bin / 'psql', args.dotnet, args.browser]:
        if not path.is_file():
            parser.error('An installed executable is required: ' + str(path))
    if args.api_bin is not None:
        if not args.no_build:
            parser.error('--api-bin requires --no-build')
        try:
            args.api_bin = api_artifact(args.api_bin)
        except ValueError as error:
            parser.error(str(error))
    os.environ['DOTNET'] = str(args.dotnet.resolve())
    if not args.no_build:
        build_assets(args.dotnet.resolve())
    children = []

    def checks(query, manifest):
        f = PointTraderMarkFixture(query, manifest)
        old = json.loads(f.call(f"SELECT security.read_current_catalogue('{f.catalog}',NULL);", profile='approver'))
        query(catalogue_fixture(manifest, manifest['identityIssuer'], curve_sources=[f.definition_revision],
            expected_head=old['id'], request_id=str(uuid4())))
        current = json.loads(f.call(f"SELECT security.read_current_catalogue('{f.catalog}',NULL);", profile='approver'))
        assert current['roles'] == old['roles'], 'Adding the selected ordinary source replaced independent catalogue roles.'
        # A normal recorded close supplies a complete attested matrix, unlike
        # an unexpanded built-in seed. Its independent root is already valued;
        # the new target point remains genuinely absent from this selection.
        session = 'synthetic_point_publication_inputs'
        sql_session(query, manifest, session, 'every_day', '16:00:00', ['provider_transport_rights'],
            ['trader', 'viewer', 'editor'], revision=f.imported_definition, effective=('2020-01-01', '2022-01-01'))
        query("INSERT INTO security.role_permissions SELECT id,'PublishMarketData' FROM security.roles WHERE code='trader' ON CONFLICT DO NOTHING;")
        pins = [dict(definitionRevisionId=f.imported_definition, observationId=observation, inputUse='variable')
            for observation in [f.imported_observation] + f.imported_rate_observations[f.valuation_at]]
        # Use the genuine provider observation as the independent primary root.
        provider = query(f"SELECT id FROM market_data.observations WHERE(catalog_id,definition_revision_id,series_id,observed_at)="
            f"('{f.catalog}','{f.imported_definition}','{f.provider_series}','{f.valuation_at}');")
        pins[0]['observationId'] = provider
        pins += [dict(definitionRevisionId=f.definition_revision, observationId=observation, inputUse='variable')
            for observation in f.rate_observations[f.valuation_at]]
        # The ordinary close law freezes only genuine final settlements or
        # fixings. Parameters remain variable; no rate is relabeled frozen.
        frozen = str(uuid4())
        query("INSERT INTO market_data.observations SELECT(jsonb_populate_record(NULL::market_data.observations,to_jsonb(o)||"
            f"jsonb_build_object('id','{frozen}','role','final_contract_settlement','publication_state','final',"
            f"'source_event_id','synthetic_independent_final_settlement','source_revision_id','1','corrects_id',NULL,"
            f"'observed_at','{f.valuation_at}','received_at',clock_timestamp()))).* FROM market_data.observations o "
            f"WHERE(o.catalog_id,o.id)=('{f.catalog}','{manifest['frames'][0]['observationIds'][0]}');")
        pins += [dict(definitionRevisionId=manifest['revisionId'], observationId=frozen, inputUse='frozen')]
        for pin in pins:
            pin['seriesId'] = query(f"SELECT series_id FROM market_data.observations WHERE(catalog_id,id)=('{f.catalog}','{pin['observationId']}');")
        members = [dict(definitionRevisionId=f.imported_definition, curve='provider_transport_rights', contractId=f.provider_contract, ordinal=0, scope='root')]
        close = f.call(f"SELECT security.register_market_close('{f.catalog}','{f.imported_definition}','{uuid4()}',"
            f"'Synthetic complete original market inputs','{session}','{f.valuation_at[:10]}','{f.valuation_at}',{literal(pins)},{literal(members)});")
        booking = f.booking(key='synthetic_published_point_consumer')
        trade = f.book_financial(booking)
        assert f.read_financial(trade['tradeId'])['mark'] is None
        before_trading = query(f"SELECT count(*) FROM trading.publications WHERE catalog_id='{f.catalog}';")
        packet = dict(at=f.valuation_at, businessDate=f.valuation_at[:10], session=session, closeId=close,
            source=dict(definitionRevisionId=f.definition_revision, referenceCatalogId=f.reference, referenceRevisionId=f.valuation_reference),
            contractId=f.contract, policy='manual_final_direct_point_trader_mark_v1',
            noFrameContext=dict(contextId=f.market_context(), asOf='2020-12-19T16:00:00.000020Z', businessDate='2020-12-19', snapshotId=str(uuid4())))
        print('Point TraderMark browser fixture migrations loaded.', flush=True)
        artifacts = Path(tempfile.mkdtemp(prefix='openetrm-point-publication-artifacts-'))
        artifacts.chmod(0o700)
        owned_root = Path(query('SHOW unix_socket_directories;')).resolve()
        repository = Path(__file__).resolve().parents[1]
        inputs = [repository / 'db/migrations/0355_point_trader_marks.sql', Path(__file__),
            repository / 'tests/browser/point-trader-marks.mjs']
        if args.api_bin is not None:
            inputs += [args.api_bin / 'OpenEtrm.Api.dll']
        inputs += sorted((repository / 'web/dist').rglob('*.js'))
        hashes = {str(path): hashlib.sha256(path.read_bytes()).hexdigest() for path in inputs}
        try:
            run_browser(query, manifest, args.dotnet.resolve(), args.browser.resolve(), children, packet,
                'tests/browser/point-trader-marks.mjs', worker_enabled=False, timeout=600, api_bin=args.api_bin)
        finally:
            for name in ['point-trader-mark-mobile.png', 'point-trader-mark-recorded.png']:
                if (owned_root / name).is_file():
                    shutil.copyfile(owned_root / name, artifacts / name)
                    (artifacts / name).chmod(0o600)
            (artifacts / 'inputs.json').write_text(json.dumps(hashes, indent=2))
            (artifacts / 'inputs.json').chmod(0o600)
            print('Point publication private screenshots and input manifest: ' + str(artifacts), flush=True)
        assert hashes == {str(path): hashlib.sha256(path.read_bytes()).hexdigest() for path in inputs}, 'A qualified browser input changed during the run.'
        cuts = json.loads((owned_root / 'point-source-cuts.json').read_text())
        preview = cuts['previewOnly']['contextId']
        for table, key in [('market_data.temporal_contexts', 'id'), ('market_data.market_contexts', 'context_id'),
                           ('market_data.catalogue_contexts', 'context_id'), ('trading.contexts', 'context_id')]:
            assert query(f"SELECT to_regclass('{table}') IS NULL;") == 't', \
                f'Point setup, preview or refused publication persisted the preview-only source in {table}'
        selected = cuts['accepted']
        matched = query(f"""
            SELECT count(*) FROM market_data.point_trader_mark_commands cmd
            WHERE (cmd.catalog_id,cmd.operation_id)=('{f.catalog}','{cuts['operation']}')
              AND (cmd.source_context_id,cmd.source_business_on,cmd.source_known_at)=('{selected['contextId']}',
                   '{selected['effectiveOn']}','{selected['knownAt']}')
              AND cmd.source_system_id=(pg_control_system()).system_identifier::text
              AND cmd.source_timeline=(pg_control_checkpoint()).timeline_id;
        """)
        assert matched == '1', 'The accepted mark must own the exact displayed coordinates and original clock lineage.'
        print('PASS point reads and previews create no saved contexts; publication owns its exact inputs.', flush=True)
        assert query(f"SELECT count(*) FROM trading.publications WHERE catalog_id='{f.catalog}';") == before_trading
        assert f.read_financial(trade['tradeId'])['mark'] is None, 'Ordinary publication automatically adopted a financial mark.'
        heads = json.loads(query(f"SELECT jsonb_agg(jsonb_build_object('id',o.id,'value',o.value::text)) FROM market_data.observations o "
            f"WHERE(o.catalog_id,o.definition_revision_id,o.series_id,o.observed_at)=('{f.catalog}','{f.definition_revision}','{f.series}','{f.valuation_at}') "
            "AND NOT EXISTS(SELECT 1 FROM market_data.observations n WHERE n.catalog_id=o.catalog_id AND n.corrects_id=o.id);"))
        assert len(heads) == 1 and heads[0]['value'] == '20'
        counts = json.loads(query(f"SELECT jsonb_build_object('observations',count(*),'changedReceipts',"
            f"(SELECT count(*) FROM market_data.point_trader_mark_commands c WHERE(c.catalog_id,c.definition_revision_id,c.series_id,c.source_as_of)="
            f"('{f.catalog}','{f.definition_revision}','{f.series}','{f.valuation_at}') AND c.outcome->'changed'='true'::jsonb),"
            f"'acknowledgements',(SELECT count(*) FROM market_data.point_trader_mark_commands c WHERE(c.catalog_id,c.definition_revision_id,c.series_id,c.source_as_of)="
            f"('{f.catalog}','{f.definition_revision}','{f.series}','{f.valuation_at}') AND c.outcome->'changed'='false'::jsonb)) "
            f"FROM market_data.observations WHERE(catalog_id,definition_revision_id,series_id,observed_at)=('{f.catalog}','{f.definition_revision}','{f.series}','{f.valuation_at}');"))
        assert counts == dict(observations=3, changedReceipts=3, acknowledgements=1), counts
        f.observation = heads[0]['id']
        f.record_financial(f.mark(trade['tradeId'], booking['operationId']))
        source = f.read_financial(trade['tradeId'])
        value(f, trade['tradeId'], rate_ids=f.rate_observations[f.valuation_at], mark=source['mark'], expected='7.5')
        (artifacts / 'postconditions.json').write_text(json.dumps(dict(point=counts, financialValue='7.5', automaticFinancialAdoptions=0,
            tradingPublicationGrowthDuringPublication=0), indent=2))
        (artifacts / 'postconditions.json').chmod(0o600)
        print('PASS ordinary market publication creates no trading effect or automatic adoption; a separate explicit financial adoption consumes the genuine20 and independent12.5 fee as exact7.5.', flush=True)

    native_main(checks=checks, fixture_factory=fixture, arguments=['--pg-bin', str(args.pg_bin.resolve())], cleanup_ready=lambda: not children)


if __name__ == '__main__':
    main()
