"""Synthetic empty direct points and independent rate pins for publication checks."""
import hashlib
import json
from datetime import datetime
from uuid import uuid4

from gas_transport_financial_fixture import FinancialTransportFixture, fixture, literal


POLICY = 'manual_final_direct_point_trader_mark_v1'


def instant(value):
    return datetime.fromisoformat(value.replace('Z', '+00:00')).isoformat(timespec='microseconds').replace('+00:00', 'Z')


def identity(domain, fields):
    payload = b''.join(str(len(value.encode('utf-8'))).encode('ascii') + b':' + value.encode('utf-8')
        for value in [domain, *fields])
    return domain + ':' + hashlib.sha256(payload).hexdigest()


class PointTraderMarkFixture(FinancialTransportFixture):
    def __init__(self, query, manifest):
        super().__init__(query, manifest)
        original, selected = self.definition_revision, str(uuid4())
        self.imported_definition, self.imported_observation = original, self.observation
        self.imported_rate_observations = self.rate_observations
        rows = ['BEGIN;SELECT pg_advisory_xact_lock(730849102);',
            f"INSERT INTO market_data.revisions VALUES('{self.catalog}','{selected}','{self.reference}','{self.valuation_reference}','draft',0);"]
        for table in ['revision_periods', 'contracts', 'curves', 'series', 'curve_members', 'rate_curves', 'rate_curve_tenors']:
            rows.append(f"INSERT INTO market_data.{table} SELECT(jsonb_populate_record(NULL::market_data.{table},"
                f"to_jsonb(r)||jsonb_build_object('revision_id','{selected}'))).* FROM market_data.{table} r "
                f"WHERE(catalog_id,revision_id)=('{self.catalog}','{original}');")
        rows.append(f"INSERT INTO market_data.curves SELECT(jsonb_populate_record(NULL::market_data.curves,to_jsonb(c)||"
            f"jsonb_build_object('code','transport_rights_alias','name','Synthetic exact point alias'))).* FROM market_data.curves c "
            f"WHERE(catalog_id,revision_id,code)=('{self.catalog}','{selected}','transport_rights');")
        rows.append(f"INSERT INTO market_data.curve_members SELECT(jsonb_populate_record(NULL::market_data.curve_members,to_jsonb(m)||"
            f"jsonb_build_object('curve_code','transport_rights_alias'))).* FROM market_data.curve_members m "
            f"WHERE(catalog_id,revision_id,curve_code)=('{self.catalog}','{selected}','transport_rights');")
        rows.append(f"UPDATE market_data.revisions SET state='sealed' WHERE(catalog_id,id)=('{self.catalog}','{selected}');")
        selected_rates = {}
        for at, observations in self.rate_observations.items():
            pins = []
            for old in observations:
                observation = str(uuid4())
                pins.append(observation)
                rows.append(f"INSERT INTO market_data.observations SELECT(jsonb_populate_record(NULL::market_data.observations,to_jsonb(o)||"
                    f"jsonb_build_object('id','{observation}','definition_revision_id','{selected}','source_event_id','{observation}',"
                    f"'source_revision_id','1','corrects_id',NULL,'received_at',clock_timestamp()))).* FROM market_data.observations o "
                    f"WHERE(catalog_id,id)=('{self.catalog}','{old}');")
            selected_rates[at] = pins
        rows.append(f"INSERT INTO trading.publications SELECT '{self.catalog}',coalesce(max(publication_order),0)+1,pg_current_xact_id() FROM trading.publications WHERE catalog_id='{self.catalog}';")
        from rate_curve_fixture import discount_default
        rows.append(discount_default(self.catalog, str(uuid4()), selected, f"'{self.reference}','{self.valuation_reference}'",
            'usd', 'transport_package_discount', f"(SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{self.catalog}')", '2020-01-01T00:00:00Z'))
        rows.append('COMMIT;')
        query('\n'.join(rows))
        self.definition_revision = selected
        self.rate_observations = selected_rates
        self.observation = self.provider_observation = None
        self.frames = [dict(at=at, observationIds=pins, frozenObservationIds=[], curveContracts={})
            for at, pins in selected_rates.items()]
        manifest.setdefault('inputMarkets', []).append(dict(revisionId=selected, frames=self.frames))
        # The publisher persona has only its ordinary market permissions. No
        # financial/operating authority is borrowed from the fixture's trader.
        query("DELETE FROM security.role_permissions WHERE role_id=(SELECT id FROM security.roles WHERE code='market_data_editor') "
            "AND permission NOT IN('ReadMarketData','ImportObservations');")

    def point_target(self, *, previous=None, event=None, curve='transport_rights', contract=None, series=None, at=None):
        at = instant(at or self.valuation_at)
        series = series or self.series
        point = [self.catalog, self.definition_revision, series, 'synthetic_transport_marks', 'trader_mark', at]
        event = event if event is not None else identity('manual_point_event_v1', point)
        return dict(definitionRevisionId=self.definition_revision, referenceCatalogId=self.reference,
            referenceRevisionId=self.valuation_reference, curveCode=curve, contractId=contract or self.contract,
            seriesId=series, source='synthetic_transport_marks', quote='signed_transport_pv', observedAt=at,
            sourceEventId=event, sourceRevisionToken=identity('manual_point_revision_v1', [*point, event, previous or '']),
            expectedPreviousObservationId=previous)

    def command(self, value, *, target=None, evidence='Synthetic explicitly entered signed point value'):
        return dict(policy=POLICY, target=target or self.point_target(), value=value, evidence=evidence, originalInputs=None)

    def market_context(self, *, profile='editor', day=None):
        return self.call(f"SELECT security.resolve_operation_context('{self.catalog}','{day or self.valuation_at[:10]}',NULL);", profile=profile)

    def publish(self, body, *, operation=None, context=None, profile='editor', commit=True):
        operation = operation or str(uuid4())
        context = context or self.market_context(profile=profile)
        return json.loads(self.call(f"SELECT security.record_point_trader_mark('{self.catalog}','{operation}',{literal(body)},"
            f"'{context}','{self.valuation_at}')::text;", profile=profile, commit=commit))

    def publication_heads(self):
        return self.query(f"SELECT jsonb_build_array((SELECT count(*) FROM market_data.observations WHERE catalog_id='{self.catalog}'),"
            f"(SELECT count(*) FROM market_data.knowledge_events WHERE catalog_id='{self.catalog}'),"
            f"(SELECT count(*) FROM trading.publications WHERE catalog_id='{self.catalog}'));")
