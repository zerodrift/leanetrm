"""Synthetic whole-rights quotes over a separately retained gas entitlement."""
import json
from uuid import uuid4

from gas_transport_capacity_fixture import TransportCapacityFixture, fixture, literal


PROFILE = 'physical_gas_transport_direct_rights_v1'
RIGHTS_POLICY = 'signed_current_whole_remaining_rights_pv_excluding_fees_and_owned_gas_v1'
FEE_PROFILE = 'unconditional_one_off_fixed_amount_at_booking_v1'
EXPIRY_POLICY = 'fixed_window_capacity_rights_expire_at_through_v1'


class FinancialTransportFixture(TransportCapacityFixture):
    def __init__(self, query, manifest):
        super().__init__(query, manifest)
        self._install_financial_source()

    @classmethod
    def from_capacity(cls, operating):
        result = cls.__new__(cls)
        result.__dict__.update(operating.__dict__)
        result._install_financial_source()
        return result

    def _install_financial_source(self):
        self.query("DELETE FROM security.role_permissions WHERE role_id=(SELECT id FROM security.roles WHERE code='viewer') "
            "AND permission NOT IN('ReadMarketData','ReadTrades');"
            "INSERT INTO security.role_permissions SELECT id,'ReadTrades' FROM security.roles WHERE code='viewer' ON CONFLICT DO NOTHING;"
            "INSERT INTO security.role_permissions SELECT id,'RunScenario' FROM security.roles WHERE code='trader' ON CONFLICT DO NOTHING;")
        self.valuation_at = '2020-12-19T16:00:00Z'
        self.operational_definition = self.package_capacity('synthetic_financial_transport_capacity')
        # The genuine market contract and capacity package share this day-long
        # service window; no unrelated forward is used as a price proxy.
        self.operational_outcome = self.record(self.operational_definition)
        self.entitlement = self.operational_definition['entitlementId']
        self.capacity_version = self.operational_outcome['versionId']
        self.reference = str(uuid4())
        self.valuation_reference = str(uuid4())
        self.definition_revision, self.contract, self.series = (str(uuid4()) for _ in range(3))
        self.observation = str(uuid4())
        self.provider_contract, self.provider_series, self.provider_observation = (str(uuid4()) for _ in range(3))
        self.rate_series = [str(uuid4()) for _ in range(3)]
        self._install_package_quote()

    def package_capacity(self, key):
        definition = self.definition(key)
        definition['through'] = '2020-12-21T00:00:00Z'
        for scope in [definition, *definition['paths'], *definition['receipts'], *definition['deliveries']]:
            for limit in scope.get('limits', scope.get('agreement', [])):
                limit['through'] = definition['through']
        return definition

    def new_capacity(self, key, *, effective=None, name=None):
        original_book, book = self.book, str(uuid4())
        label = 'Synthetic independent financial package' if name is None else name
        self.query(f"BEGIN;INSERT INTO trading.publications SELECT '{self.catalog}',max(publication_order)+1,pg_current_xact_id() FROM trading.publications WHERE catalog_id='{self.catalog}';"
            f"INSERT INTO trading.books SELECT catalog_id,'{book}',portfolio_id,({literal(label)}#>>'{{}}'),"
            f"(SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{self.catalog}'),effective_at "
            f"FROM trading.books WHERE(catalog_id,id)=('{self.catalog}','{original_book}');COMMIT;")
        self.book = book
        try:
            definition = self.package_capacity(key)
            return book, definition, self.record(definition, effective=effective)
        finally:
            self.book = original_book

    def _install_package_quote(self):
        # The entitlement already exists under the original gas reference bundle.
        # This later bundle describes a real whole service package, never gas units.
        r = f"'{self.reference}','{self.valuation_reference}'"
        m = f"'{self.catalog}','{self.definition_revision}'"
        sql = f"""BEGIN;
SELECT pg_advisory_xact_lock(730849102);
INSERT INTO ref_data.catalogs VALUES('{self.reference}','synthetic_transport_valuation','Synthetic independent Transport valuation reference');
INSERT INTO ref_data.revisions(catalog_id,id,revision_key) VALUES({r},'synthetic_transport_financial_v1');
INSERT INTO ref_data.currencies VALUES({r},'usd','USD','Synthetic package currency',2);
INSERT INTO ref_data.dimensions VALUES({r},'whole_package',0,0,0,0,0,0,0);
INSERT INTO ref_data.units VALUES({r},'whole_package','package','whole_package');
INSERT INTO ref_data.commodities VALUES({r},'transport_rights','Synthetic Transport rights','whole_package');
INSERT INTO ref_data.commodity_units VALUES({r},'transport_rights','whole_package','whole_package');
INSERT INTO ref_data.measurement_bases VALUES({r},'whole_remaining_rights','transport_rights','whole_package');
INSERT INTO ref_data.quantity_specifications VALUES({r},'whole_transport_package','transport_rights','whole_remaining_rights','whole_package','whole_package');
INSERT INTO ref_data.quote_specifications VALUES({r},'signed_transport_pv','transport_rights','outright','whole_transport_package','usd',1,.01,.01,-999999999,999999999);
INSERT INTO ref_data.dimensions VALUES({r},'energy',1,2,-2,0,0,0,0);
INSERT INTO ref_data.units VALUES({r},'gas_energy','synthetic-energy','energy');
INSERT INTO ref_data.commodities VALUES({r},'ordinary_gas','Synthetic ordinary gas','energy');
INSERT INTO ref_data.commodity_units VALUES({r},'ordinary_gas','gas_energy','energy');
INSERT INTO ref_data.measurement_bases VALUES({r},'ordinary_gas_basis','ordinary_gas','energy');
INSERT INTO ref_data.quantity_specifications VALUES({r},'ordinary_gas_quantity','ordinary_gas','ordinary_gas_basis','gas_energy','energy');
INSERT INTO ref_data.quote_specifications VALUES({r},'ordinary_gas_quote','ordinary_gas','outright','ordinary_gas_quantity','usd',1,.01,.01,-999999999,999999999);
INSERT INTO ref_data.clock_rules(catalog_id,revision_id,code,kind,fixed_offset_seconds) VALUES({r},'utc','fixed_offset',0);
INSERT INTO ref_data.calendars VALUES({r},'every_day','utc',0,'1901-01-01','2200-01-01');
INSERT INTO ref_data.location_types VALUES({r},'service_scope','Synthetic service scope');
INSERT INTO ref_data.locations VALUES({r},'transport_package','Synthetic whole Transport package','service_scope','utc');
INSERT INTO ref_data.commodity_locations VALUES({r},'transport_rights','transport_package');
INSERT INTO ref_data.sources(catalog_id,revision_id,code,kind,locator,retrieved_at,license_reference,distribution_review)
 VALUES({r},'synthetic_transport_marks','synthetic','synthetic:transport-whole-rights-v1',clock_timestamp(),'Project-authored synthetic observations','redistributable');
INSERT INTO ref_data.price_index_definitions VALUES({r},'transport_package_index','transport_rights','transport_package','usd','whole_package',1,'every_day');
INSERT INTO ref_data.delivery_profiles VALUES({r},'package_service_window','uniform','utc','every_day','fixed_total',60);
INSERT INTO ref_data.settlement_targets VALUES({r},'package_reference','index','transport_package_index',NULL);
INSERT INTO ref_data.interest_rate_definitions VALUES({r},'package_zero_rate','usd','every_day','actual_365_fixed','simple','openetrm_rates_v1');
INSERT INTO ref_data.value_specifications VALUES({r},'package_decimal_rate','dimensionless',NULL,NULL,-.1,1);
UPDATE ref_data.revisions SET state='sealed' WHERE(catalog_id,id)=({r});
INSERT INTO market_data.revisions VALUES({m},{r},'draft',0);
INSERT INTO market_data.revision_periods VALUES({m},'1901-01-01','2199-12-31');
INSERT INTO market_data.contracts VALUES({m},'{self.contract}',{r},'forward','2020-12-20','2020-12-21','2020-12-21T00:00:00Z',NULL);
INSERT INTO market_data.curves VALUES({m},'transport_rights',{r},'transport_rights','transport_package','signed_transport_pv','package_service_window','package_reference','direct','synthetic_transport_marks','trader_mark','Synthetic whole Transport rights PV');
INSERT INTO market_data.series VALUES({m},'{self.series}','transport_rights',{r},'{self.contract}','signed_transport_pv',NULL);
INSERT INTO market_data.curve_members VALUES({m},'transport_rights','{self.contract}',0);
INSERT INTO market_data.contracts VALUES({m},'{self.provider_contract}',{r},'forward','2020-12-20','2020-12-21','2020-12-21T00:00:00Z',NULL);
INSERT INTO market_data.curves VALUES({m},'provider_transport_rights',{r},'transport_rights','transport_package','signed_transport_pv','package_service_window','package_reference','direct','synthetic_transport_marks','trader_mark','Synthetic independent provider rights PV');
INSERT INTO market_data.series VALUES({m},'{self.provider_series}','provider_transport_rights',{r},'{self.provider_contract}','signed_transport_pv',NULL);
INSERT INTO market_data.curve_members VALUES({m},'provider_transport_rights','{self.provider_contract}',0);
UPDATE market_data.revisions SET state='sealed' WHERE(catalog_id,id)=({m});
INSERT INTO market_data.observations(catalog_id,id,definition_revision_id,series_id,reference_catalog_id,reference_revision_id,source_code,source_event_id,source_revision_id,role,publication_state,observed_at,received_at,value)
 VALUES('{self.catalog}','{self.observation}','{self.definition_revision}','{self.series}',{r},'synthetic_transport_marks','synthetic_transport_customer_mark','1','trader_mark','final','{self.valuation_at}',clock_timestamp(),20);
INSERT INTO market_data.observations(catalog_id,id,definition_revision_id,series_id,reference_catalog_id,reference_revision_id,source_code,source_event_id,source_revision_id,role,publication_state,observed_at,received_at,value)
 VALUES('{self.catalog}','{self.provider_observation}','{self.definition_revision}','{self.provider_series}',{r},'synthetic_transport_marks','synthetic_transport_provider_mark','1','trader_mark','final','{self.valuation_at}',clock_timestamp(),-20);
COMMIT;"""
        from rate_curve_fixture import dated_curve, discount_default
        rate_definitions = '\n'.join(f"INSERT INTO market_data.series VALUES({m},'{series}','package_rate_{index}',{r},NULL,NULL,'package_decimal_rate');"
            for index, series in enumerate(self.rate_series))
        rate_definitions += dated_curve(self.catalog, self.definition_revision, r, 'transport_package_discount',
            'Synthetic explicit zero-rate package discount', ['2020-12-23', '2021-01-01', '2021-02-01'], self.rate_series,
            currency='usd', source='synthetic_transport_marks', interest_rate='package_zero_rate', specification='package_decimal_rate')
        marker = f"UPDATE market_data.revisions SET state='sealed' WHERE(catalog_id,id)=({m});"
        assert sql.count(marker) == 1
        sql = sql.replace(marker, rate_definitions + '\n' + marker)
        self.frames = []
        self.rate_observations = {}
        observations = []
        for when in [self.valuation_at, '2020-12-21T00:00:00Z', '2020-12-22T00:00:00Z', '2020-12-23T16:00:00Z']:
            pins = []
            for series in self.rate_series:
                observation = str(uuid4())
                pins.append(observation)
                observations.append(f"INSERT INTO market_data.observations(catalog_id,id,definition_revision_id,series_id,reference_catalog_id,reference_revision_id,source_code,source_event_id,source_revision_id,role,publication_state,observed_at,received_at,value) "
                    f"VALUES('{self.catalog}','{observation}','{self.definition_revision}','{series}',{r},'synthetic_transport_marks','{observation}','1','parameter','final','{when}',clock_timestamp(),0);")
            self.frames.append(dict(at=when, frozenObservationIds=[], observationIds=([self.observation, self.provider_observation] if when == self.valuation_at else []) + pins,
                curveContracts={'transport_rights': [self.contract], 'provider_transport_rights': [self.provider_contract]} if when == self.valuation_at else {}))
            self.rate_observations[when] = pins
        publication = f"(SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{self.catalog}')"
        observations.append(f"INSERT INTO trading.publications SELECT '{self.catalog}',coalesce(max(publication_order),0)+1,pg_current_xact_id() FROM trading.publications WHERE catalog_id='{self.catalog}';")
        observations.append(discount_default(self.catalog, str(uuid4()), self.definition_revision, r, 'usd', 'transport_package_discount', publication, '2020-01-01T00:00:00Z'))
        sql = sql.replace('COMMIT;', '\n'.join(observations) + '\nCOMMIT;')
        self.query(sql)
        self.query(f"INSERT INTO security.reference_catalog_scopes VALUES('{self.reference}','{self.manifest['tenantId']}','{self.manifest['workspaceId']}','owner');")
        self.manifest.setdefault('inputMarkets', []).append(dict(revisionId=self.definition_revision, frames=self.frames))

    def booking(self, *, key='synthetic_financial_transport', perspective='customer', fee='12.5', direction='pay'):
        return dict(operationId=str(uuid4()), snapshotId=str(uuid4()), contextId=self.capture(),
            definitionRevisionId=self.definition_revision, knowledgeMode='current', profile=PROFILE, businessKey=key,
            bookId=self.book, legalEntityId='d4000000-0000-0000-0000-000000000001', legalEntityRevision=1,
            counterpartyId='d5000000-0000-0000-0000-000000000001', counterpartyRevision=1,
            valuationReferenceCatalogId=self.reference, valuationReferenceRevisionId=self.valuation_reference,
            entitlementId=self.entitlement, capacityVersionId=self.capacity_version, perspective=perspective,
            packageQuantity='whole_transport_package', currency='usd', rightsValuationPolicy=RIGHTS_POLICY,
            feeProfile=FEE_PROFILE, fixedFeeAmount=fee, feeDirection=direction,
            feeDueAt=None if fee == '0' else '2020-12-23T16:00:00Z', expiryPolicy=EXPIRY_POLICY,
            evidence='Synthetic explicit whole Transport agreement')

    def book_financial(self, body):
        return json.loads(self.call(f"SELECT security.book_gas_transport_financial('{self.catalog}',"
            f"{literal(body)},'{self.valuation_at}')::text;"))

    def read_financial(self, trade, *, context=None, at=None, profile='trader', business_date=None):
        selected, when = context or self.capture(), at or self.valuation_at
        day = when[:10] if business_date is None else business_date
        return json.loads(self.call(f"SELECT security.use_market_context('{self.catalog}','{selected}');"
            f"SELECT security.use_trading_context('{self.catalog}','{when}','{day}');"
            f"SELECT security.read_gas_transport_financial('{self.catalog}','{trade}','{selected}','{when}')::text;", profile=profile))

    def mark(self, trade, state):
        return dict(kind='adopt_rights_mark', tradeId=trade, financialStateId=state, expectedPreviousMarkId=None,
            valuationAt=self.valuation_at, marketDefinitionRevisionId=self.definition_revision,
            curveCode='transport_rights', contractId=self.contract, seriesId=self.series,
            observationId=self.observation, source='synthetic_transport_marks', quote='signed_transport_pv',
            evidence='Synthetic exact signed package mark adoption')

    def record_financial(self, body, *, operation=None, effective=None, context=None, at=None):
        return json.loads(self.call(f"SELECT security.record_gas_transport_financial('{self.catalog}','{operation or uuid4()}',"
            f"{literal(body)},'{effective or self.valuation_at}'," + ('NULL' if context is False else f"'{context or self.capture()}'") +
            f",'{at or self.valuation_at}')::text;"))

    def expiry_policy(self):
        policy = str(uuid4())
        body = dict(operationId=str(uuid4()), policyId=policy, expectedRevision=0,
            action='gas.transport_financial.expire', mode='manual', fromAt='2020-01-01T00:00:00Z', untilAt='2022-01-01T00:00:00Z',
            implementation='explicit_lifecycle_action_policy_v1')
        self.call(f"SELECT lifecycle.save_policy('{self.catalog}',{literal(body)});")
        return policy

    def alternate_mark(self, mark):
        """A different genuine definition sharing the same exact reference."""
        definition, observation = str(uuid4()), str(uuid4())
        sql = ["BEGIN;SELECT pg_advisory_xact_lock(730849102);",
            f"INSERT INTO market_data.revisions VALUES('{self.catalog}','{definition}','{self.reference}','{self.valuation_reference}','draft',0);"]
        for table in ['revision_periods', 'contracts', 'curves', 'series', 'curve_members']:
            sql.append(f"INSERT INTO market_data.{table} SELECT(jsonb_populate_record(NULL::market_data.{table},"
                f"to_jsonb(r)||jsonb_build_object('revision_id','{definition}'))).* FROM market_data.{table} r "
                f"WHERE(catalog_id,revision_id)=('{self.catalog}','{self.definition_revision}');")
        sql.extend([f"UPDATE market_data.revisions SET state='sealed' WHERE(catalog_id,id)=('{self.catalog}','{definition}');",
            f"INSERT INTO market_data.observations SELECT(jsonb_populate_record(NULL::market_data.observations,"
            f"to_jsonb(o)||jsonb_build_object('id','{observation}','definition_revision_id','{definition}','source_event_id','{observation}'))).* "
            f"FROM market_data.observations o WHERE(catalog_id,id)=('{self.catalog}','{self.observation}');", 'COMMIT;'])
        self.query('\n'.join(sql))
        return {**mark, 'marketDefinitionRevisionId': definition, 'observationId': observation}

    def empty_pricing_definition(self):
        definition = str(uuid4())
        self.query(f"BEGIN;INSERT INTO market_data.revisions VALUES('{self.catalog}','{definition}','{self.reference}','{self.valuation_reference}','draft',0);"
            f"INSERT INTO market_data.revision_periods VALUES('{self.catalog}','{definition}','1901-01-01','2199-12-31');"
            f"UPDATE market_data.revisions SET state='sealed' WHERE(catalog_id,id)=('{self.catalog}','{definition}');COMMIT;")
        return definition
