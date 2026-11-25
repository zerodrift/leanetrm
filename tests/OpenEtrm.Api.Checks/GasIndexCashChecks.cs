using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;

internal static class GasIndexCashChecks
{
    static JsonElement Row(object value) => JsonSerializer.SerializeToElement(value);
    static void Require(bool condition, string message) { if(!condition) throw new Exception(message); }
    internal static int Run()
    {
        var passed = 0; var f = new IndexFixture();
        void Check(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
        void Refused(string name, string code, Action action) => Check(name, () =>
        {
            try { action(); } catch(MarketDataException e) when(e.Code == code) { return; }
            throw new Exception("Expected " + code + ": " + name);
        });
        Check("named daily payments use their independent index and one exact contract per gas day", () =>
        {
            var side = GasNamedIndexCash.Resolve(f.Data(), f.Physical, f.Cash("daily_index", "named_index_daily_v1"), f.From, f.To);
            Require(side.Periods.Length == 3 && side.Periods.Select((p, n) => p.DeliveryFrom == f.From.AddDays(n) &&
                p.DeliveryTo == f.From.AddDays(n + 1) && p.Projections.Single().Contract.DeliveryFrom == p.DeliveryFrom &&
                p.Projections.Single().Contract.DeliveryTo == p.DeliveryTo && p.Projections.Single().Market.Curve.Code == "daily" &&
                p.Fixings.Single().Index.Code == "daily_index" && p.ObservationImplementation == FloatingSide.DeliveryIndexImplementation).All(v => v),
                "Daily rows must use the named index's exact daily forecast and fixing.");
        });
        Check("a retained cash-out overview exposes both ordinary index payment schedules",()=>
        {
            var cash=GasNamedIndexCash.Resolve(f.Data(),f.Physical,f.Cash("daily_index","named_index_daily_v1"),f.From,f.To);
            Side[] sides=[f.Physical,cash,cash];
            var programs=sides.Select((side,slot)=>{
                var program=BookedSideProgram.Compile(ScheduleDefinitionCompiler.Generate(side));
                return new RetainedSideProgram(slot,side.Id,Guid.NewGuid(),program.Identity,program);
            }).ToImmutableArray();
            var position=new GasPosition(Row(new{instrument_id=Guid.NewGuid()}),default,[Row(new{
                reference_catalog_id=cash.Quote.Quantity.Revision.CatalogId,reference_revision_id=cash.Quote.Quantity.Revision.RevisionId,
                quantity_code=cash.Quote.Quantity.Code,currency_code=cash.Quote.Currency.Code})]);
            ImmutableArray<JsonElement> members=[
                Row(new{slot=0,code="gas",role="physical",direction=1,ratio_n=1,ratio_d=1}),
                Row(new{slot=1,code="balance.to_us",role="imbalance_cashout",direction=1,ratio_n=1,ratio_d=1}),
                Row(new{slot=2,code="balance.by_us",role="imbalance_cashout",direction=-1,ratio_n=1,ratio_d=1})];
            var group=RetainedInstrumentSidePresentation.Gas(position,programs,members);
            Require(group.Sides.Length==3&&group.Sides[1].RoleLabel=="Imbalance cash-out payment"&&
                group.Sides[2].RoleLabel=="Imbalance cash-out payment"&&group.Sides[1].Slot==1&&group.Sides[2].Slot==2&&
                group.Sides[1].SideId==cash.Id&&group.Sides[2].SideId==cash.Id,
                "Both directions keep their own native membership of the reusable floating Side, without pricing the overview.");
        });
        Check("named monthly payments retain one shared monthly contract and fixing on daily rows", () =>
        {
            var side = GasNamedIndexCash.Resolve(f.Data(), f.Physical, f.Cash("monthly_index", "named_index_monthly_v1"), f.From, f.To);
            Require(side.Periods.Length == 3 && side.Periods.All(p => p.ObservationImplementation == FloatingSide.DeliveryIndexImplementation) &&
                side.Periods.Select(p => p.Projections.Single().Contract).Distinct().Single().DeliveryTo == f.From.AddMonths(1) &&
                side.Fixings.Distinct().Single().Index.Code == "monthly_index", "Every daily row must retain the same qualified monthly publication.");
        });
        Check("a point with publication periods also accepts a named independent index", () =>
        {
            var side = GasIndexedCash.Resolve(f.Data(), f.Physical, f.Cash("daily_index", "named_index_daily_v1"), f.Point);
            Require(side.Fixings.All(p => p.Index.Code == "daily_index"), "Point publications cannot override the named payment index.");
        });
        Check("named and point-publication payments cover only their own partial term", () =>
        {
            var from = f.From.AddDays(1);
            var named = GasNamedIndexCash.Resolve(f.Data(), f.Physical, f.Cash("daily_index", "named_index_daily_v1"), from, f.To);
            var point = GasPointIndexCash.Resolve(f.Data(), f.Physical, f.Cash(null, "point_publications_v1"), f.Point, from, f.To);
            Require(named.Periods.Length == 2 && point.Periods.Length == 2 && named.DeliveryFrom == from && point.DeliveryFrom == from &&
                named.Periods.All(p => p.Allocation == OpenEtrm.Shared.Numerics.ExactRatio.One) &&
                point.Fixings.All(p => p.Index.Code == "point_index"), "A payment excludes all source days outside its own term.");
        });
        Refused("new strict daily bookings are retired", "gas_index_terms", () =>
            GasIndexedCash.Resolve(f.Data(), f.Physical, f.Cash("point_index", "daily_published_index_v1"), f.Point));
        Refused("an unknown named index has no inferred curve", "gas_index_coverage", () =>
            GasNamedIndexCash.Resolve(f.Data(), f.Physical, f.Cash("unknown", "named_index_daily_v1"), f.From, f.To));
        Refused("two matching curves are ambiguous", "gas_index_coverage", () =>
            GasNamedIndexCash.Resolve(f.Data(duplicateCurve: true), f.Physical, f.Cash("daily_index", "named_index_daily_v1"), f.From, f.To));
        Refused("a missing daily contract refuses", "gas_index_coverage", () =>
            GasNamedIndexCash.Resolve(f.Data(missingContract: true), f.Physical, f.Cash("daily_index", "named_index_daily_v1"), f.From, f.To));
        Refused("a missing daily fixing refuses", "gas_index_coverage", () =>
            GasNamedIndexCash.Resolve(f.Data(missingFixing: true), f.Physical, f.Cash("daily_index", "named_index_daily_v1"), f.From, f.To));
        Refused("duplicate fixing definitions refuse", "gas_index_coverage", () =>
            GasNamedIndexCash.Resolve(f.Data(duplicateFixing: true), f.Physical, f.Cash("daily_index", "named_index_daily_v1"), f.From, f.To));
        foreach(var (index,convention) in new[]{("daily_index","named_index_daily_v1"),("monthly_index","named_index_monthly_v1")})
            Refused("a same-date fixing on a different contract refuses for "+index,"gas_index_coverage",()=>
                GasNamedIndexCash.Resolve(f.Data(differentFixingContract:index),f.Physical,f.Cash(index,convention),f.From,f.To));
        Refused("point publications require the exact projection contract too","gas_point_index_terms",()=>
            GasPointIndexCash.Resolve(f.Data(differentFixingContract:"point_index"),f.Physical,f.Cash(null,"point_publications_v1"),f.Point,f.From,f.To));
        Refused("point-publication term refusals retain their existing code", "gas_point_index_terms", () =>
        {
            var cash = f.Cash(null, "point_publications_v1");
            GasPointIndexCash.Resolve(f.Data(), f.Physical, cash with { Indexed = cash.Indexed! with { PaymentDay = 29 } }, f.Point, f.From, f.To);
        });
        return passed;
    }
    sealed class IndexFixture
    {
        readonly PreviewManifest manifest;
        readonly Dictionary<string, ImmutableArray<JsonElement>> definitions, references;
        readonly SideCalendarPolicy calendars = SyntheticSideCalendars.Dated(true);
        internal DateOnly From { get; } = new(2040, 2, 1);
        internal DateOnly To => From.AddDays(3);
        internal PhysicalGasSide Physical { get; }
        internal JsonElement Point { get; }
        internal IndexFixture()
        {
            var baseline = PreviewDataChecks.Fixture(0);
            manifest = baseline.Manifest; definitions = baseline.Definitions; references = baseline.References;
            var contracts = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
            var series = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
            (DateOnly From, DateOnly To) Period(int n) => n is 0 or 4 ? (From, From.AddMonths(1)) : (From.AddDays(n - 1), From.AddDays(n));
            string Index(int n) => n == 0 ? "point_index" : n == 4 ? "monthly_index" : "daily_index";
            definitions["contracts"] = [..contracts.Select((id, n) => Row(new { id, kind = "forward", delivery_from = Period(n).From, delivery_to = Period(n).To,
                expiry_at = "2040-04-01T00:00:00Z", futures_series_code = (string?)null, futures_option_series_code = (string?)null }))];
            definitions["curves"] = [..new[] { "point", "daily", "monthly" }.Select(code => Row(new { code, commodity_code = "gas", quote_code = "price",
                location_code = "hub", profile_code = "uniform", settlement_target_code = code, rule = "direct", direct_role = "trader_mark", direct_source_code = "marks" }))];
            definitions["curve_members"] = [..contracts.Select((id, n) => Row(new { curve_code = n == 0 ? "point" : n == 4 ? "monthly" : "daily", contract_id = id, ordinal = n }))];
            definitions["series"] = [..series.Select((id, n) => Row(new { id, contract_id = contracts[n], quote_code = "price", value_specification_code = (string?)null }))];
            definitions["index_fixing_observations"] = [..series.Select((id, n) => Row(new { index_code = Index(n), quote_code = "price",
                observation_from = Period(n).From, observation_to = Period(n).To, fixing_at = "2040-02-03T12:00:00Z", series_id = id, source_code = "publication",
                implementation = IndexFixing.SupportedImplementation, reference_catalog_id = manifest.ReferenceCatalogId, reference_revision_id = manifest.ReferenceRevisionId }))];
            references["settlement_targets"] = [..new[] { "point", "daily", "monthly" }.Select(code => Row(new { code, kind = "index", index_a_code = code + "_index", index_b_code = (string?)null }))];
            references["sources"] = [Row(new { code = "marks" }), Row(new { code = "publication" })];
            var data = Data(); var quote = data.Quotes.Single(); ReferenceKey R(string code) => new(data.ReferenceRevision, code);
            var clock = GasDayClock.Capture(R("synthetic_clock"), TimeZoneInfo.Utc, 360);
            var location = new GasDeliveryLocation(R("pipeline"), R("zone"), R("point"));
            var contract = data.Contracts.Single(c => c.Key.Id == contracts[0]);
            var selection = PriceSettlementSelection.CurveTarget(R("point_index"));
            var fixing = PriceSettlementInputs.Fixings(data, selection, quote, [contract]).Single();
            Physical = new(Guid.NewGuid(), location, R("gas"), new(clock, From, To, GasQuantityBasis.PerOperatingDay,
                new(ExactDecimal.Parse("1"), quote.Quantity), GasFuelConvention.None, default),
                [new(R("assignment"), location, R("gas"), contract.DeliveryFrom, contract.DeliveryTo, selection, quote,
                    new(new(new(data.DefinitionRevision, "point"), contract.Key), contract), fixing, new(default, quote))], SyntheticSideCalendars.Physical());
            Point = Row(new { forecast_source = "published_custom_v1", forecast_visible = true, forecast_curve_id = Guid.NewGuid(), forecast_version = "1",
                forecast_output_node = "out", forecast_selection = CustomCurveProjectionBinding.ContainingDeliverySelection,
                forecast_implementation = CustomCurveProjectionBinding.SupportedImplementation,
                settlement_periods = new[] { new { valid_from = From, valid_to = From.AddMonths(1), curve_code = "point", index_code = "point_index", observation_convention = "monthly_published_index_v1" } } });
        }
        internal PreviewData Data(bool duplicateCurve = false, bool missingContract = false, bool missingFixing = false, bool duplicateFixing = false,string? differentFixingContract=null)
        {
            var rows = new Dictionary<string, ImmutableArray<JsonElement>>(definitions);
            if(duplicateCurve)
            {
                rows["curves"] = [..rows["curves"], Row(new { code = "second_daily", commodity_code = "gas", quote_code = "price", location_code = "hub", profile_code = "uniform",
                    settlement_target_code = "daily", rule = "direct", direct_role = "trader_mark", direct_source_code = "marks" })];
                rows["curve_members"] = [..rows["curve_members"], ..rows["curve_members"].Where(r => r.Text("curve_code") == "daily")
                    .Select(r => Row(new { curve_code = "second_daily", contract_id = r.Id("contract_id"), ordinal = r.GetProperty("ordinal").GetInt32() }))];
            }
            if(missingContract) rows["curve_members"] = [..rows["curve_members"].Where(r => r.GetProperty("ordinal").GetInt32() != 2)];
            if(missingFixing) rows["index_fixing_observations"] = [..rows["index_fixing_observations"].Where(r => r.Text("observation_from") != From.AddDays(1).ToString("yyyy-MM-dd"))];
            if(duplicateFixing) rows["index_fixing_observations"] = rows["index_fixing_observations"].Add(rows["index_fixing_observations"][2]);
            if(differentFixingContract is not null)
            {
                var fixing=rows["index_fixing_observations"].First(r=>r.Text("index_code")==differentFixingContract);
                var series=rows["series"].Single(r=>r.Id("id")==fixing.Id("series_id"));
                var contract=rows["contracts"].Single(r=>r.Id("id")==series.Id("contract_id"));
                var replacement=Guid.NewGuid();
                rows["contracts"]=rows["contracts"].Add(Row(new{id=replacement,kind="forward",delivery_from=contract.Text("delivery_from"),delivery_to=contract.Text("delivery_to"),
                    expiry_at=contract.Text("expiry_at"),futures_series_code=(string?)null,futures_option_series_code=(string?)null}));
                rows["series"]=[..rows["series"].Select(r=>r.Id("id")==series.Id("id")?Row(new{id=r.Id("id"),contract_id=replacement,quote_code="price",value_specification_code=(string?)null}):r)];
                rows["series"]=rows["series"].Add(Row(new{id=Guid.NewGuid(),contract_id=contract.Id("id"),quote_code="price",value_specification_code=(string?)null}));
            }
            return new(manifest, rows, references, []);
        }
        internal GasCashRequest Cash(string? index, string convention)
        {
            CalendarBindingInput Binding(CalendarBinding b) => new(b.Applicable, b.Calendar?.Key);
            return new("payment", "indexed_consideration", "pay", "delivery", "delivery", null, null, null,
                new(index, convention, "next_month_day_utc_v1", 10, "0.05", null))
                { CalendarPolicy = new(SideCalendarPolicy.Implementation, Binding(calendars.Delivery), Binding(calendars.Observation), Binding(calendars.Payment)) { Retained = calendars } };
        }
    }
}
