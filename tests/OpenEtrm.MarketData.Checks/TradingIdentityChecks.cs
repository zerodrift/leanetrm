using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Temporal;
using System.Collections.Immutable;

internal static class TradingIdentityChecks
{
    [EconomicDefinition("identity_fixture_v1")]
    private sealed record IdentityFixture([property: IdentityField] ExactDecimal Amount,
        [property: IdentityField] string? Optional, string Label);

    public static int Run()
    {
        var passed = 0;
        void Check(string name, Action test) { test(); passed++; Console.WriteLine("PASS trading identity: " + name); }
        void Assert(bool value) { if (!value) throw new Exception("Trading identity contract failed."); }
        void Reject(string code, Action test)
        { try { test(); } catch (MarketDataException e) when (e.Code == code) { return; } throw new Exception("Expected " + code); }
        var f = new Fixture(); var q = f.Quotes[0]; var k = f.Contracts[1];
        FixedSide Fixed(string price, DateTimeOffset? payment = null) => new(Guid.NewGuid(), new(ExactDecimal.Parse(price), q),
            new(ExactDecimal.Parse("1"), q.Quantity), k.DeliveryFrom, k.DeliveryTo,
            payment ?? Fixture.Utc("2041-02-02T00:00:00Z"), FixedSide.Implementation,q);
        FloatingSide Floating() => new(Guid.NewGuid(), PriceSettlementSelection.CurveTarget(f.R("index")), q, new(ExactDecimal.Parse("1"), q.Quantity),
            k.DeliveryFrom, k.DeliveryTo, Fixture.Utc("2040-12-19T00:00:00Z"), Fixture.Utc("2041-02-02T00:00:00Z"),
            FloatingAveraging.MonthlyIndexPrice, [new(new(f.C("base_forward"), k.Key), k)], FloatingSide.Implementation);
        FinancialSwapInstrument Swap(string price = "3", bool reverse = false, bool discounting = false) => new(Guid.NewGuid(),
            [new(Fixed(price), ExactDecimal.Parse(reverse ? "1" : "-1")), new(Floating(), ExactDecimal.Parse(reverse ? "-1" : "1"))],
            FinancialSwapInstrument.Implementation, discounting);
        Check("rebuilt definitions and normalized decimal spellings share identity", () =>
            Assert(Swap("3.00").EconomicIdentity == Swap("3").EconomicIdentity));
        Check("marked economics and membership direction distinguish instruments", () =>
        {
            Assert(Swap().EconomicIdentity != Swap("3.01").EconomicIdentity);
            Assert(Swap().EconomicIdentity != Swap(reverse: true).EconomicIdentity);
            Assert(DefinitionIdentity.For(Fixed("3")) != DefinitionIdentity.For(Fixed("3", Fixture.Utc("2041-02-03T00:00:00Z"))));
        });
        Check("discounting distinguishes reusable instruments while sides remain shared", () =>
        {
            var plain = Swap();
            var discounted = new FinancialSwapInstrument(Guid.NewGuid(), plain.Sides, FinancialSwapInstrument.Implementation, true);
            Assert(plain.EconomicIdentity != discounted.EconomicIdentity);
            Assert(DefinitionIdentity.For(plain.Fixed) == DefinitionIdentity.For(discounted.Fixed));
            var map = new InstrumentIdentityMap(f.Revision.CatalogId);
            Assert(!ReferenceEquals(map.Intern(plain), map.Intern(discounted)));
            Assert(map.Count == 2);
            Reject("instrument_identity", () => map.Intern(new FinancialSwapInstrument(plain.Id, plain.Sides,
                FinancialSwapInstrument.Implementation, true)));
        });
        Check("fixed-side quote reference is part of its persisted economic key", () =>
        {
            var a = Fixed("3");
            var b = new FixedSide(Guid.NewGuid(),((FlatPrice)a.Price).Price,a.UnitQuantity,a.DeliveryFrom,a.DeliveryTo,a.PaymentAt,FixedSide.Implementation,q with {Key=f.R("another_quote")});
            Assert(DefinitionIdentity.For(a) != DefinitionIdentity.For(b));
            Reject("swap_definition",()=>new FixedSide(Guid.NewGuid(),((FlatPrice)a.Price).Price,a.UnitQuantity,a.DeliveryFrom,a.DeliveryTo,a.PaymentAt,
                FixedSide.Implementation,q with {Quantity=f.R("wrong_quantity")}));
        });
        Check("a versioned golden encoding excludes unmarked labels and preserves null", () =>
        {
            var key = DefinitionIdentity.For(new IdentityFixture(ExactDecimal.Parse("3.00"), null, "Synthetic label A"));
            Assert(key.Canonical == "{\"contract\":\"identity_fixture_v1\",\"Amount\":\"3\",\"Optional\":null}");
            Assert(key.Digest == "f1cb39c0facb1bc4964caf96848a5c0ceef56714709dfb0402a3b9d70e2be0ae");
            Assert(key == DefinitionIdentity.For(new IdentityFixture(ExactDecimal.Parse("3"), null, "Synthetic label B")));
            Assert(key != DefinitionIdentity.For(new IdentityFixture(ExactDecimal.Parse("3"), "", "Synthetic label A")));
            Reject("definition_identity", () => DefinitionIdentity.For(new object()));
        });
        Check("operation-owned maps retain separate objects for identical authorized definitions", () =>
        {
            var map = new InstrumentIdentityMap(f.Revision.CatalogId);
            var a = map.Intern(Swap()); var b = map.Intern(Swap());
            Assert(ReferenceEquals(a, b)); Assert(map.Count == 1);
            Assert(!ReferenceEquals(a, new InstrumentIdentityMap(Guid.NewGuid()).Intern(Swap())));
        });
        Check("an exact instrument identity cannot be reinterpreted", () =>
        {
            var map = new InstrumentIdentityMap(f.Revision.CatalogId); var a = Swap(); map.Intern(a);
            Reject("instrument_identity", () => map.Intern(new FinancialSwapInstrument(a.Id, Swap("4").Sides, FinancialSwapInstrument.Implementation, false)));
            Reject("instrument_identity", () => map.Retain(Swap()));
        });
        Check("an exact side identity cannot be reinterpreted through another instrument", () =>
        {
            var map = new InstrumentIdentityMap(f.Revision.CatalogId); var a = Swap(); map.Intern(a);
            var changed = new FixedSide(a.Fixed.Id, new(ExactDecimal.Parse("4"), q), a.Fixed.UnitQuantity,
                a.Fixed.DeliveryFrom, a.Fixed.DeliveryTo, a.Fixed.PaymentAt, FixedSide.Implementation,q);
            Reject("side_identity", () => map.Intern(new FinancialSwapInstrument(Guid.NewGuid(),
                [new(changed, ExactDecimal.Parse("-1")), a.Sides[1]], FinancialSwapInstrument.Implementation, false)));
            Assert(map.Count == 1);
        });
        Check("equal payment dates do not merge distinct payment rules", () =>
        {
            var lag = new PaymentRule(PaymentFrequency.Monthly, PaymentDateRule.DaysAfterPeriodEnd, 18, null,
                PaymentDayBasis.CalendarDays, null, DateRoll.Unadjusted, null, PaymentRule.Implementation);
            var day = new PaymentRule(PaymentFrequency.Monthly, PaymentDateRule.DayOfFollowingMonth, null, 18,
                null, ShortMonth.Reject, DateRoll.Unadjusted, null, PaymentRule.Implementation);
            FixedSide Scheduled(PaymentRule rule) => new(Guid.NewGuid(), ((FlatPrice)Fixed("3").Price).Price, new(ExactDecimal.Parse("1"), q.Quantity),
                k.DeliveryFrom, k.DeliveryTo, rule, FixedSide.Implementation,q);
            Assert(Scheduled(lag).PaymentAt == Scheduled(day).PaymentAt);
            Assert(DefinitionIdentity.For(Scheduled(lag)) != DefinitionIdentity.For(Scheduled(day)));
        });
        Check("floating index, exact projections, weighting and calendars participate", () =>
        {
            FloatingSide Monthly(string index, string curve) => new(Guid.NewGuid(), PriceSettlementSelection.CurveTarget(f.R(index)), q, new(ExactDecimal.Parse("1"), q.Quantity),
                k.DeliveryFrom, k.DeliveryTo, Fixture.Utc("2040-12-19T00:00:00Z"), Fixture.Utc("2041-02-02T00:00:00Z"),
                FloatingAveraging.MonthlyIndexPrice, [new(new(f.C(curve), k.Key), k)], FloatingSide.Implementation);
            Assert(DefinitionIdentity.For(Monthly("index", "base_forward")) != DefinitionIdentity.For(Monthly("other_index", "base_forward")));
            Assert(DefinitionIdentity.For(Monthly("index", "base_forward")) != DefinitionIdentity.For(Monthly("index", "other_curve")));
            var dates = Enumerable.Range(0, 31).Select(n => k.DeliveryFrom.AddDays(n)).ToArray();
            var projections = dates.Select(date => { var contract = new MarketContract(new(f.Revision, Guid.NewGuid()),
                ContractKind.Forward, date, date.AddDays(1), Fixture.Utc("2041-02-02T00:00:00Z"), null);
                return new SwapProjection(new(f.C("daily"), contract.Key), contract); }).ToImmutableArray();
            FloatingSide Daily(FloatingAveraging method, string calendarCode = "calendar", string weight = "1") => new(Guid.NewGuid(), PriceSettlementSelection.CurveTarget(f.R("index")), q,
                new(ExactDecimal.Parse("1"), q.Quantity), k.DeliveryFrom, k.DeliveryTo, Fixture.Utc("2040-12-19T00:00:00Z"), Fixture.Utc("2041-02-02T00:00:00Z"),
                method, method == FloatingAveraging.QuantityWeighted ? projections.Select((p,n) => p with { AveragingQuantity = new(ExactDecimal.Parse(n == 0 ? weight : "1"), q.Quantity) }).ToImmutableArray() : projections,
                FloatingSide.Implementation, method == FloatingAveraging.ObservationArithmetic ? new(f.R(calendarCode), DateSet.Create(k.DeliveryFrom,k.DeliveryTo,dates)) : null);
            Assert(DefinitionIdentity.For(Daily(FloatingAveraging.CalendarDayArithmetic)) != DefinitionIdentity.For(Daily(FloatingAveraging.QuantityWeighted)));
            Assert(DefinitionIdentity.For(Daily(FloatingAveraging.QuantityWeighted)) != DefinitionIdentity.For(Daily(FloatingAveraging.QuantityWeighted, weight:"2")));
            Assert(DefinitionIdentity.For(Daily(FloatingAveraging.ObservationArithmetic)) != DefinitionIdentity.For(Daily(FloatingAveraging.ObservationArithmetic,"other_calendar")));
        });
        Check("floating contractual identity excludes resolved constituent aliases but pricing identity retains them", () =>
        {
            var a = Floating();
            var alias = k with {Key = new(k.Key.Revision,Guid.NewGuid())};
            var b = new FloatingSide(Guid.NewGuid(),a.Settlement,a.Quote,a.UnitQuantity,a.DeliveryFrom,a.DeliveryTo,
                a.FirstFixingAt,a.PaymentAt,a.Averaging,[new(new(a.Projections[0].Market.Curve,alias.Key),alias)],FloatingSide.Implementation);
            Assert(DefinitionIdentity.For(a) == DefinitionIdentity.For(b));
            Assert(DefinitionIdentity.For(a.PricingBinding) != DefinitionIdentity.For(b.PricingBinding));
            var map = new InstrumentIdentityMap(f.Revision.CatalogId);
            map.Intern(new FinancialSwapInstrument(Guid.NewGuid(),[new(Fixed("3"),ExactDecimal.Parse("-1")),new(a,ExactDecimal.Parse("1"))],FinancialSwapInstrument.Implementation,false));
            Reject("valuation_binding",()=>map.Intern(new FinancialSwapInstrument(Guid.NewGuid(),[new(Fixed("3"),ExactDecimal.Parse("-1")),new(b,ExactDecimal.Parse("1"))],FinancialSwapInstrument.Implementation,false)));
        });
        Check("futures dated contracts, lot economics and exact versions distinguish definitions", () =>
        {
            var contract = f.Contracts[0];
            var series = new FuturesSeriesSpecification(f.R("future_jan"), q.Quantity, q.Currency, q.Multiplier, q.TradingTick,
                f.R("location"), f.R("uniform"), contract.DeliveryFrom, contract.DeliveryTo, contract.Expiry,
                q.Quantity, ExactDecimal.Parse("40"), LotBasis.FixedTotal, Fixture.Utc("2040-01-01T00:00:00Z"),"cash");
            FuturesMarketBinding Future(MarketContract c, FuturesSeriesSpecification s, CurveRequest market) => new(Guid.NewGuid(),market,c,s,q,"cash",FuturesMarketBinding.Implementation);
            var request = new CurveRequest(f.C("future"), contract.Key); var a = Future(contract, series, request);
            Assert(a.Instrument.EconomicIdentity == Future(contract, series, request).Instrument.EconomicIdentity);
            Assert(a.Instrument.EconomicIdentity == Future(contract, series, request with { Curve = f.C("other_future") }).Instrument.EconomicIdentity);
            var revisedSeries = series with { Key = new(new(f.Reference.CatalogId,Guid.NewGuid()),"future_jan") };
            Assert(a.Instrument.Id != ContractIdentity.Futures(revisedSeries.Key));
            var map = new InstrumentIdentityMap(f.Revision.CatalogId);
            map.Intern(a.Instrument);
            Reject("instrument_identity", () => map.Intern(Future(contract, series with { ContractSize = ExactDecimal.Parse("50") }, request).Instrument));
        });
        Check("parties retain exact versions and reject incomplete or cross-catalog bindings", () =>
        {
            var catalog = f.Revision.CatalogId;
            var entity = new LegalEntity(new(catalog, Guid.NewGuid(), 1), "entity_a", "Synthetic entity A");
            var counterparty = new Counterparty(new(catalog, Guid.NewGuid(), 2), "party_a", "Synthetic party A");
            var terms = new TradeBookingTerms(new(2040, 12, 18), entity, counterparty);
            Assert(terms.LegalEntity == entity && terms.Counterparty.Key.Revision == 2);
            Reject("trade_party", () => new LegalEntity(new(catalog, Guid.Empty, 1), "entity_a", "Synthetic entity A"));
            Reject("trade_party", () => new Counterparty(new(catalog, Guid.NewGuid(), 0), "party_a", "Synthetic party A"));
            Reject("trade_party", () => new TradeBookingTerms(new(2040, 12, 18), entity,
                new(new(Guid.NewGuid(), Guid.NewGuid(), 1), "party_b", "Synthetic party B")));
        });
        Check("party name bounds count Unicode scalars consistently with retained storage", () =>
        {
            var key = new PartyKey(f.Revision.CatalogId, Guid.NewGuid(), 1);
            foreach (var name in new[] { new string('n', 80), string.Concat(Enumerable.Repeat("\U0001F50C", 80)), new string('n', 79) + "\U0001F50C" })
            {
                Assert(new LegalEntity(key, "entity_a", name).Name == name);
                Assert(new Counterparty(key, "party_a", name).Name == name);
            }
            foreach (var name in new[] { new string('n', 81), string.Concat(Enumerable.Repeat("\U0001F50C", 81)), "", "\u00A0\u2003", "name\0" })
            {
                Reject("trade_party", () => new LegalEntity(key, "entity_a", name));
                Reject("trade_party", () => new Counterparty(key, "party_a", name));
            }
        });
        return passed;
    }
}
