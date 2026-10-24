using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Temporal;

// Independent expectations come from policy examples and ppa_expected_values.py.
// This suite does not use a production pricing routine to construct expected values.
internal static class PpaAcceptanceChecks
{
    private static readonly Dictionary<string,object?> OracleValues=[];
    private static void Export(string path,ExactMoney value)=>OracleValues[path]=new{numerator=value.Numerator,denominator=value.Denominator};
    private static void Export(string path,ExactQuantity value)=>OracleValues[path]=new{numerator=value.Numerator,denominator=value.Denominator};
    private static ExactDecimal D(string value)=>ExactDecimal.Parse(value);
    private static void Equal<T>(T actual,T expected)
    {
        if(!EqualityComparer<T>.Default.Equals(actual,expected))
            throw new Exception($"Expected {expected}; got {actual}.");
    }
    private static string Reject(string code,Action action)
    {
        try{action();}
        catch(MarketDataException error)when(error.Code==code){return error.Code;}
        throw new Exception("Expected "+code);
    }
    private static void Price(ExactUnitPrice actual,string numerator,string denominator="1")
    {Equal(actual.Numerator,numerator);Equal(actual.Denominator,denominator);}
    private static void Money(ExactMoney actual,string numerator,string denominator="1")
    {Equal(actual.Numerator,numerator);Equal(actual.Denominator,denominator);}
    private static void Quantity(ExactQuantity actual,string numerator,string denominator="1")
    {Equal(actual.Numerator,numerator);Equal(actual.Denominator,denominator);}

    public static int Run()
    {
        OracleValues.Clear();
        var count=0;
        void Check(string label,Action action){action();count++;Console.WriteLine("PASS PPA independent acceptance: "+label);}
        var f=new Fixture();
        Check("anniversary strike retains original base across a later segment",()=>{
            var price=new EscalatedPrice(new(D("40"),f.EnergyPrice),D("0.025"),new(2030,3,15),ContractYearBasis.AnniversaryStep);
            Price(price.At(new(2030,3,15)),"40");
            Price(price.At(new(2031,3,14)),"40");
            Price(price.At(new(2031,3,15)),"41");
            Price(price.At(new(2031,6,1)),"41");
            Price(price.At(new(2032,3,15)),"1681","40");
            var dates=new[]{new DateOnly(2030,3,15),new(2031,3,14),new(2031,3,15),new(2031,6,1),new(2032,3,15)};
            for(var n=0;n<dates.Length;n++){var actual=price.At(dates[n]);OracleValues[$"plan_escalation/price_{n}"]=new{numerator=actual.Numerator,denominator=actual.Denominator};}
        });
        Check("stepped strike changes on its selected operating date",()=>{
            var price=new SteppedPrice([new(new(2031,11,1),new(D("40"),f.EnergyPrice)),new(new(2031,11,15),new(D("50"),f.EnergyPrice))]);
            Price(price.At(new(2031,11,14)),"40");Price(price.At(new(2031,11,15)),"50");
            Reject("ppa_price_rule",()=>price.At(new(2031,10,31)));
            Reject("ppa_price_rule",()=>new SteppedPrice([new(new(2031,11,1),new(D("40"),f.EnergyPrice)),new(new(2031,11,1),new(D("50"),f.EnergyPrice))]));
        });
        Check("original November payment date survives energy segmentation",()=>{
            Equal(f.Payment.Generate(new(2031,11,1),new(2031,12,1)).Date,new DateOnly(2031,12,10));
            // Shortening the payment period is refused; a binding must select rows
            // inside the original schedule instead of generating a new short month.
            Reject("payment_period",()=>f.Payment.Generate(new(2031,11,1),new(2031,11,15)));
        });
        Check("amendment partitions reject overlaps gaps and boundaries before delivered economics",()=>{
            var start=PpaAcceptanceFixture.At("2031-11-15T04:00:00Z");var boundary=start.AddHours(1);var end=start.AddHours(2);
            var whole=new DeliveryCoverage(start,end);
            DeliveryCoverage.ValidatePartition(whole,[new(start,boundary),new(boundary,end)]);
            OracleValues["02_coverage_partition/overlap"]=Reject("ppa_segment_coverage",()=>DeliveryCoverage.ValidatePartition(whole,[new(start,boundary.AddHours(1)),new(boundary,end)]));
            OracleValues["02_coverage_partition/gap"]=Reject("ppa_segment_coverage",()=>DeliveryCoverage.ValidatePartition(whole,[new(start,boundary.AddMinutes(-15)),new(boundary,end)]));
            Reject("ppa_segment_coverage",()=>DeliveryCoverage.ValidatePartition(whole,[]));
            var clock=PowerClock.Capture(TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
            DeliveryCoverage.ValidateForwardBoundary(boundary,boundary,clock,60);
            DeliveryCoverage.ValidateForwardBoundary(end,boundary.AddMinutes(30),clock,60);
            Reject("ppa_segment_boundary",()=>DeliveryCoverage.ValidateForwardBoundary(boundary.AddMinutes(30),boundary,clock,60));
            Reject("ppa_segment_boundary",()=>DeliveryCoverage.ValidateForwardBoundary(start,boundary,clock,60));
            Reject("ppa_segment_boundary",()=>DeliveryCoverage.ValidateForwardBoundary(boundary,boundary.AddMinutes(30),clock,60));
        });
        Check("asset-free fixed physical composition has explicit opposing memberships",()=>{
            var physical=new PowerPhysicalSide(Guid.NewGuid(),f.Terms(f.Profile),f.R("synthetic_node"));
            var strike=new PpaFixedSide(Guid.NewGuid(),f.Terms(new SlotQuantity([new(PpaRole.EnergyDelivery)])),new FlatPrice(new(D("40"),f.EnergyPrice)));
            var instrument=new PpaInstrument(Guid.NewGuid(),f.Definitions,f.Reference,
                [new(new(PpaRole.EnergyDelivery),physical,D("1")),new(new(PpaRole.StrikePayment),strike,D("-1"))],[],false,"ppa_instrument_v1");
            Equal(instrument.Currency,f.Currency);Equal(instrument.UnitQuantity,f.UnitMw);
        });
        Check("two strike payments cannot silently consume one delivery twice",()=>{
            var physical=new PowerPhysicalSide(Guid.NewGuid(),f.Terms(f.Profile),f.R("synthetic_node"));
            PpaFixedSide Strike(string price)=>new(Guid.NewGuid(),f.Terms(new SlotQuantity([new(PpaRole.EnergyDelivery)])),new FlatPrice(new(D(price),f.EnergyPrice)));
            Reject("ppa_composition",()=>new PpaInstrument(Guid.NewGuid(),f.Definitions,f.Reference,
                [new(new(PpaRole.EnergyDelivery),physical,D("1")),new(new(PpaRole.StrikePayment),Strike("40"),D("-1")),new(new(PpaRole.StrikePayment,1),Strike("50"),D("-1"))],[],false,"ppa_instrument_v1"));
        });
        Check("an explicit fixed charge may retain identical terms to a distinct strike side",()=>{
            var physical=new PowerPhysicalSide(Guid.NewGuid(),f.Terms(f.Profile),f.R("synthetic_node"));
            PpaFixedSide Cash()=>new(Guid.NewGuid(),f.Terms(new SlotQuantity([new(PpaRole.EnergyDelivery)])),new FlatPrice(new(D("40"),f.EnergyPrice)));
            var instrument=new PpaInstrument(Guid.NewGuid(),f.Definitions,f.Reference,
                [new(new(PpaRole.EnergyDelivery),physical,D("1")),new(new(PpaRole.StrikePayment),Cash(),D("-1")),new(new(PpaRole.FixedCharge),Cash(),D("1"))],[],false,"ppa_instrument_v1");
            Equal(instrument.Memberships.Length,3);
        });
        Check("a reusable payment slot cannot become its own quantity source",()=>{
            var physical=new PowerPhysicalSide(Guid.NewGuid(),f.Terms(f.Profile),f.R("synthetic_node"));
            Reject("ppa_quantity_reference",()=>{
                var strike=new PpaFixedSide(Guid.NewGuid(),f.Terms(new SlotQuantity([new(PpaRole.StrikePayment)])),new FlatPrice(new(D("40"),f.EnergyPrice)));
                _=new PpaInstrument(Guid.NewGuid(),f.Definitions,f.Reference,
                    [new(new(PpaRole.EnergyDelivery),physical,D("1")),new(new(PpaRole.StrikePayment),strike,D("-1"))],[],false,"ppa_instrument_v1");
            });
        });
        Check("fixed physical strike must bind its energy delivery rather than silently select another source",()=>{
            var physical=new PowerPhysicalSide(Guid.NewGuid(),f.Terms(f.Profile),f.R("synthetic_node"));
            var strike=new PpaFixedSide(Guid.NewGuid(),f.Terms(f.Profile),new FlatPrice(new(D("40"),f.EnergyPrice)));
            Reject("ppa_composition",()=>new PpaInstrument(Guid.NewGuid(),f.Definitions,f.Reference,
                [new(new(PpaRole.EnergyDelivery),physical,D("1")),new(new(PpaRole.StrikePayment),strike,D("-1"))],[],false,"ppa_instrument_v1"));
        });
        count+=ValuationChecks()+LifecycleChecks()+PriceAndContextChecks();
        var output=Environment.GetEnvironmentVariable("OPENETRM_PPA_ORACLE_OUTPUT");
        if(!string.IsNullOrEmpty(output))File.WriteAllText(output,JsonSerializer.Serialize(OracleValues,new JsonSerializerOptions{WriteIndented=true}));
        return count;
    }

    private static int ValuationChecks()
    {
        var count=0;
        void Check(string label,Action action){action();count++;Console.WriteLine("PASS PPA independent acceptance: "+label);}
        var f=new PpaAcceptanceFixture();
        var at=PpaAcceptanceFixture.At("2031-10-30T00:00:00Z");
        var market=f.Market(at);
        var discount=new ExplicitDiscounting(D("1"),DiscountFactorSource.Undiscounted);
        var hour=new DeliveryCoverage(f.From,f.From.AddHours(1));
        Check("physical and strike retain independent gross rows and one external MW scale",()=>{
            var instrument=f.Physical();var inputs=f.Prepare(market,[instrument]);
            var measures=ValueMeasures.From(inputs.Session(market.Build).Value(instrument,hour,discount)).Scale(D("100"));
            Money(measures.ValueExcludingOverdue,"200");
            Equal(measures.Rows.Length,2);
            var physical=measures.Rows.Single(r=>r.Kind==ComponentKind.Delivery);
            var payment=measures.Rows.Single(r=>r.Kind==ComponentKind.Payment);
            Money(physical.Amount,"4200");Money(payment.Amount,"-4000");
            Export("plan_physical/physical",physical.Amount);Export("plan_physical/strike",payment.Amount);Export("plan_physical/primary",measures.ValueExcludingOverdue);
            Quantity(physical.Quantity,"100");Quantity(payment.Quantity,"100");
            Equal(physical.Lifecycle,null);Equal(payment.Lifecycle,PaymentLifecycle.RemainingProjected);
            Equal(physical.Coefficient,D("1"));Equal(payment.Coefficient,D("-1"));
        });
        Check("future-only binding does not require uncovered historical meters",()=>{
            var valuation=PpaAcceptanceFixture.At("2031-11-15T05:00:00Z");
            var selectedMarket=f.Market(valuation,actuals:h=>h.End<=valuation?"40":null);
            var instrument=f.Physical(volume:true);
            var start=PpaAcceptanceFixture.At("2031-11-20T05:00:00Z");
            var selected=new DeliveryCoverage(start,start.AddHours(1));
            var inputs=f.Prepare(selectedMarket,[instrument],pending:PendingActualsPolicy.RequireActuals,
                coverage:ImmutableDictionary<Guid,ImmutableArray<DeliveryCoverage>>.Empty.Add(instrument.Id,[selected]));
            var measures=ValueMeasures.From(inputs.Session(selectedMarket.Build).Value(instrument,selected,discount)).Scale(D("80"));
            Money(measures.ValueExcludingOverdue,"40");
            Equal(measures.Rows.Length,2);
            Equal(measures.Rows.Single(r=>r.Kind==ComponentKind.Payment).ValueAt.Date,new DateTime(2031,12,10));
            Reject("ppa_segment_coverage",()=>inputs.Session(selectedMarket.Build).Value(instrument,new(start.AddHours(1),start.AddHours(2)),discount));
            // A second consumer requests disjoint cells from the same raw source.
            // Neither earlier historical meters nor historical price actuals are inputs.
            var second=f.Physical("50",volume:true);var later=new DeliveryCoverage(start.AddHours(2),start.AddHours(3));
            var sparseMarket=f.Market(valuation,priceSpans:[(selected.From,selected.To),(later.From,later.To)]);
            var scopes=ImmutableDictionary<Guid,ImmutableArray<DeliveryCoverage>>.Empty.Add(instrument.Id,[selected]).Add(second.Id,[later]);
            var shared=f.Prepare(sparseMarket,[instrument,second],pending:PendingActualsPolicy.RequireActuals,coverage:scopes);
            var session=shared.Session(sparseMarket.Build);
            Money(ValueMeasures.From(session.Value(instrument,selected,discount)).Scale(D("80")).ValueExcludingOverdue,"40");
            Money(ValueMeasures.From(session.Value(second,later,discount)).Scale(D("80")).ValueExcludingOverdue,"-160");
            Equal(shared.VolumeComputations,2);
            Equal(shared.Receipt.RequestedCoverage,scopes);

        });
        Check("asset generation divides by the capacity basis before contract MW scaling",()=>{
            var instrument=f.Physical(volume:true);var inputs=f.Prepare(market,[instrument]);
            var measures=ValueMeasures.From(inputs.Session(market.Build).Value(instrument,hour,discount)).Scale(D("80"));
            var physical=measures.Rows.Single(r=>r.Kind==ComponentKind.Delivery);
            Quantity(physical.Quantity,"20");Money(physical.Amount,"840");Money(measures.ValueExcludingOverdue,"40");
            Export("10_shared_work/physical_0",physical.Amount);Export("10_shared_work/combined_quantity_0_strike_0",measures.ValueExcludingOverdue);
        });
        Check("meter normalization and explicit fractional source adjustments retain exact external quantities",()=>{
            var instrument=f.Physical(volume:true);var deliveredMarket=f.Market(PpaAcceptanceFixture.At("2031-12-10T00:00:00Z"),actuals:_=>"40");
            var actual=f.Prepare(deliveredMarket,[instrument],f.Meter(h=>h.Start==f.From?"50":"0"));
            var metered=ValueMeasures.From(actual.Session(deliveredMarket.Build).Value(instrument,hour,discount)).Scale(D("80")).Rows.Single(r=>r.Kind==ComponentKind.Delivery);
            Equal(metered.QuantityState,VolumeState.Metered);Quantity(metered.Quantity,"20");Export("plan_normalization/metered_mwh",metered.Quantity);
            var adjusted=f.Source with {Availability=new(D("0.98"),false),Curtailment=new(D("0.02"),false)};
            var inputs=f.Prepare(market,[instrument],sourceOverride:adjusted);
            var quantity=ValueMeasures.From(inputs.Session(market.Build).Value(instrument,hour,discount)).Scale(D("80")).Rows.Single(r=>r.Kind==ComponentKind.Delivery).Quantity;
            Quantity(quantity,"2401","125");Export("plan_normalization/degraded_mwh",quantity);
        });
        Check("whole November capacity sums thirty operating dates including the repeated DST hour",()=>{
            var instrument=f.CapacityAgreement();var inputs=f.Prepare(market,[instrument]);
            var measures=ValueMeasures.From(inputs.Session(market.Build).Value(instrument,new(f.From,f.To),discount)).Scale(D("80"));
            var capacity=measures.Rows.Single(r=>r.Kind==ComponentKind.Delivery);
            Quantity(capacity.Quantity,"1920");Money(capacity.Amount,"7680");Money(measures.ValueExcludingOverdue,"7680");
            Export("07_capacity_month/mw_day",capacity.Quantity);Export("07_capacity_month/amount",capacity.Amount);
            OracleValues["07_capacity_month/monthly_rows"]=measures.Rows.Count(r=>r.Kind==ComponentKind.Delivery);
            OracleValues["07_capacity_month/operating_days"]=capacity.CapacityDays.Select(d=>d.OperatingDay).Distinct().Count();
            var dst=capacity.CapacityDays.Single(d=>d.OperatingDay==new DateOnly(2031,11,2)).PerUnit.Scale(D("80"));
            Export("07_capacity_month/allocated_mw",dst);Export("07_capacity_month/dst_day_mw_day",dst);
            Equal(measures.Rows.Length,2);
            Equal(capacity.Interval.Start,f.From);Equal(capacity.Interval.End,f.To);
        });
        Check("effective accreditation changes affect daily allocation without creating partial-month entitlement",()=>{
            var instrument=f.CapacityAgreement();
            var change=f.Clock.StartOfDay(new(2031,11,16)).ToUniversalTime();
            var asset=f.Asset with {Versions=[new(f.From,change,D("200"),D("160")),new(change,f.To,D("200"),D("120"))]};
            var inputs=f.Prepare(market,[instrument],asset:asset);
            var measures=ValueMeasures.From(inputs.Session(market.Build).Value(instrument,new(f.From,f.To),discount)).Scale(D("80"));
            Quantity(measures.Rows.Single(r=>r.Kind==ComponentKind.Delivery).Quantity,"1680");Money(measures.ValueExcludingOverdue,"6720");
            Export("07_capacity_month/changed_mw_day",measures.Rows.Single(r=>r.Kind==ComponentKind.Delivery).Quantity);
            Export("07_capacity_month/changed_amount",measures.ValueExcludingOverdue);
            Reject("ppa_segment_boundary",()=>inputs.Session(market.Build).Value(instrument,new(f.From,change),discount));
        });
        Check("capacity boundaries and missing daily accreditation refuse rather than prorate or omit",()=>{
            var instrument=f.CapacityAgreement();var inputs=f.Prepare(market,[instrument]);
            OracleValues["08_capacity_components/interior_boundary"]=Reject("ppa_segment_boundary",()=>inputs.Session(market.Build).Value(instrument,new(f.From,f.From.AddDays(14)),discount));
            var change=f.Clock.StartOfDay(new(2031,11,16)).ToUniversalTime();
            var asset=f.Asset with {Versions=[new(f.From,change,D("200"),D("160")),new(change,f.To,D("200"),null)]};
            Reject("asset_version",()=>f.Prepare(market,[instrument],asset:asset).Session(market.Build).Value(instrument,new(f.From,f.To),discount));
        });
        Check("monthly capacity rejects energy and unconverted MW price denominators",()=>{
            foreach(var (label,unit) in new[]{("mwh",f.EnergyPrice),("mw",new PriceUnit(f.R("synthetic_usd"),f.Capacity.Key,D("1")))})
            {
                var original=f.CapacityAgreement();var cash=new PpaFixedSide(Guid.NewGuid(),original.Memberships[1].Side.Terms,new FlatPrice(new(D("4"),unit)));
                var invalid=new PpaInstrument(Guid.NewGuid(),f.Definitions,f.Reference,[original.Memberships[0],new(new(PpaRole.CapacityPayment),cash,D("-1"))],[f.Asset.Key],false,"ppa_instrument_v1");
                OracleValues[$"08_capacity_components/{label}_quote"]=Reject("ppa_quantity_unit",()=>f.Prepare(market,[invalid]));
            }
            var capacity=f.CapacityAgreement();var middle=f.Clock.StartOfDay(new(2031,11,15)).ToUniversalTime();
            var originalAssignment=f.Assignments.Single(a=>a.Attribute==ValuedAttribute.Capacity);
            var split=f.Assignments.Where(a=>a.Attribute!=ValuedAttribute.Capacity).Append(originalAssignment with {To=middle})
                .Append(originalAssignment with {Code="synthetic_reassigned_capacity",From=middle,Curve=f.C("synthetic_capacity_other")}).ToImmutableArray();
            Reject("location_assignment",()=>f.Prepare(market,[capacity],assignments:split));
        });
        Check("capacity assets retain their own normalized monthly components and location prices",()=>{
            var first=f.Asset with {Versions=[new(f.From,f.To,D("200"),D("60"))]};
            var second=new GenerationAsset(new(f.Definitions,"synthetic_second_generator"),f.R("synthetic_second_node"),f.Capacity,f.R("synthetic_clock"),f.Clock,[new(f.From,f.To,D("100"),D("40"))]);
            var physical=new CapacityDeliverySide(Guid.NewGuid(),f.Terms(new AccreditedQuantity([first.Key,second.Key])),f.CapacityDays.Key,"mw_day_per_operating_day_v1");
            var cash=new PpaFixedSide(Guid.NewGuid(),f.Terms(new SlotQuantity([new(PpaRole.CapacityDelivery)])),new FlatPrice(new(D("0"),f.CapacityPrice)));
            var instrument=new PpaInstrument(Guid.NewGuid(),f.Definitions,f.Reference,[new(new(PpaRole.CapacityDelivery),physical,D("1")),new(new(PpaRole.CapacityPayment),cash,D("-1"))],
                [first.Key,second.Key],false,"ppa_instrument_v1");
            var assignments=f.Assignments.Add(new("synthetic_other_capacity",second.Location,ValuedAttribute.Capacity,f.From,f.To,f.C("synthetic_capacity_other"),f.CapacityPrice,D("0")));
            var inputs=f.Prepare(market,[instrument],asset:first,extraAssets:[second],assignments:assignments);
            var value=ValueMeasures.From(inputs.Session(market.Build).Value(instrument,new(f.From,f.To),discount)).Scale(D("100"));
            var row=value.Rows.Single(r=>r.Kind==ComponentKind.Delivery);Quantity(row.Quantity,"1000");Money(row.Amount,"4400");
            Equal(row.PriceComponents.Length,2);
            Quantity(row.PriceComponents.Single(c=>c.Curve==f.C("synthetic_capacity")).Quantity,"600");
            Quantity(row.PriceComponents.Single(c=>c.Curve==f.C("synthetic_capacity_other")).Quantity,"400");
            Equal(row.CapacityDays.Length,60);Export("08_capacity_components/amount",row.Amount);
        });
        Check("capacity uses its qualified published fixing and preserves marked versus realised knowledge",()=>{
            var asset=f.Asset with {Versions=[new(f.From,f.To,D("50"),D("50"))]};var instrument=f.CapacityAgreement();
            var published=f.Market(PpaAcceptanceFixture.At("2031-12-10T00:00:00Z"),actuals:_=>"40",capacityFixing:true);
            var fixedInputs=f.Prepare(published,[instrument],asset:asset);
            var realised=ValueMeasures.From(fixedInputs.Session(published.Build).Value(instrument,new(f.From,f.To),discount)).Scale(D("50"));
            var row=realised.Rows.Single(r=>r.Kind==ComponentKind.Delivery);Money(row.Amount,"6000");Equal(row.Class,EconomicClass.Realised);
            Equal(row.PriceComponents.Single().State,RowPriceState.PublishedFixing);Export("plan_capacity/fixed_amount",row.Amount);
            var missing=published.Snapshot with {Id=Guid.NewGuid(),Selections=[..published.Snapshot.Selections.Where(s=>s.Input.Use!=InputUse.FrozenFact)]};
            var incomplete=published with {Snapshot=missing,Build=published.Model.Build(missing,[..published.Build.Values.Select(v=>v.Request)])};
            Reject("index_fixing_missing",()=>f.Prepare(incomplete,[instrument],asset:asset).Session(incomplete.Build).Value(instrument,new(f.From,f.To),discount));
            var marked=f.Market(at,capacity:"3.5");var markInputs=f.Prepare(marked,[instrument],asset:asset);
            var future=ValueMeasures.From(markInputs.Session(marked.Build).Value(instrument,new(f.From,f.To),discount)).Scale(D("50"));
            var futureRow=future.Rows.Single(r=>r.Kind==ComponentKind.Delivery);Money(futureRow.Amount,"5250");Equal(futureRow.Class,EconomicClass.Unrealised);
            Export("plan_capacity/marked_amount",futureRow.Amount);
            var ended=f.Market(PpaAcceptanceFixture.At("2031-12-10T00:00:00Z"),capacity:"3.5",actuals:_=>"40");
            var pending=f.Prepare(ended,[instrument],asset:asset);var estimate=ValueMeasures.From(pending.Session(ended.Build).Value(instrument,new(f.From,f.To),discount)).Scale(D("50"));
            var estimatedRow=estimate.Rows.Single(r=>r.Kind==ComponentKind.Delivery);Money(estimatedRow.Amount,"5250");Equal(estimatedRow.Class,EconomicClass.Estimated);Money(estimate.RealisedDeliveryValue,"0");
            Reject("actuals_required",()=>f.Prepare(ended,[instrument],asset:asset,pending:PendingActualsPolicy.RequireActuals).Session(ended.Build).Value(instrument,new(f.From,f.To),discount));
        });
        Check("segmented November payments keep the full-month date and exact separate MW sizes",()=>{
            var old=f.Physical("40",volume:true,discounting:true);var amended=f.Physical("50",volume:true,discounting:true);
            var boundary=PpaAcceptanceFixture.At("2031-11-15T05:00:00Z");
            var inputs=f.Prepare(market,[old,amended]);var session=inputs.Session(market.Build);
            var df=new ExplicitDiscounting(D("0.9"),DiscountFactorSource.Explicit);
            var oldMeasures=ValueMeasures.From(session.Value(old,new(boundary.AddHours(-1),boundary),df)).Scale(D("80"));
            var newMeasures=ValueMeasures.From(session.Value(amended,new(boundary,boundary.AddHours(1)),df)).Scale(D("60"));
            var oldCash=oldMeasures.Rows.Single(r=>r.Kind==ComponentKind.Payment);var newCash=newMeasures.Rows.Single(r=>r.Kind==ComponentKind.Payment);
            Money(oldCash.Amount,"-800");Money(newCash.Amount,"-750");Money(oldCash.PresentValue!,"-720");Money(newCash.PresentValue!,"-675");
            var total=oldCash.PresentValue!.Add(newCash.PresentValue!);Money(total,"-1395");
            Equal(oldCash.ValueAt,PpaAcceptanceFixture.At("2031-12-10T00:00:00Z"));Equal(newCash.ValueAt,oldCash.ValueAt);
            Export("01_segmented_payment/amount_0",oldCash.Amount);Export("01_segmented_payment/amount_1",newCash.Amount);
            Export("01_segmented_payment/pv_0",oldCash.PresentValue!);Export("01_segmented_payment/pv_1",newCash.PresentValue!);Export("01_segmented_payment/primary",total);
            Export("01_segmented_payment/termination_retained_amount",oldCash.Amount);
            OracleValues["01_segmented_payment/payment_0"]=oldCash.ValueAt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
            OracleValues["01_segmented_payment/payment_1"]=newCash.ValueAt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        });
        Check("coverage partitions repeated DST hours by UTC identity without loss or overlap",()=>{
            var instrument=f.Physical(volume:true);var inputs=f.Prepare(market,[instrument]);var session=inputs.Session(market.Build);
            var from=PpaAcceptanceFixture.At("2031-11-02T04:00:00Z");var middle=from.AddHours(2);var to=from.AddHours(4);
            ValueMeasureSet Value(DateTimeOffset a,DateTimeOffset b)=>ValueMeasures.From(session.Value(instrument,new(a,b),discount)).Scale(D("80"));
            var whole=Value(from,to);var first=Value(from,middle);var second=Value(middle,to);
            var wholeRows=whole.Rows.Where(r=>r.Kind==ComponentKind.Payment).ToArray();
            var partition=first.Rows.Concat(second.Rows).Where(r=>r.Kind==ComponentKind.Payment).ToArray();
            Equal(wholeRows.Length,4);Equal(partition.Length,4);Equal(partition.Select(r=>r.Interval.Start).Distinct().Count(),4);
            Equal(partition.Select(r=>r.Interval).ToHashSet().SetEquals(wholeRows.Select(r=>r.Interval)),true);
            ExactMoney Sum(IEnumerable<ScheduleRowValue> rows)=>rows.Select(r=>r.Amount).Aggregate((a,b)=>a.Add(b));
            var total=Sum(wholeRows);var left=Sum(first.Rows.Where(r=>r.Kind==ComponentKind.Payment));var right=Sum(second.Rows.Where(r=>r.Kind==ComponentKind.Payment));
            Money(total,"-3200");Equal(left.Add(right),total);
            Export("02_coverage_partition/full_amount",total);Export("02_coverage_partition/first_amount",left);Export("02_coverage_partition/second_amount",right);
            OracleValues["02_coverage_partition/row_count"]=wholeRows.Length;
            OracleValues["02_coverage_partition/repeated_hour_count"]=wholeRows.Count(r=>r.Interval.HourBeginning==1);
            OracleValues["02_coverage_partition/invalid_boundary"]=Reject("ppa_segment_boundary",()=>session.Value(instrument,new(from,from.AddMinutes(90)),discount));
        });
        Check("known generated certificates survive their delivery coverage as current vintage holdings",()=>{
            var instrument=f.WithCertificate();var valueAt=PpaAcceptanceFixture.At("2031-12-10T00:00:00Z");
            var observed=f.Market(valueAt,actuals:_=>"40");
            var meter=f.Meter(h=>h.Start==f.From?"50":"0");var inputs=f.Prepare(observed,[instrument],meter);
            var session=inputs.Session(observed.Build);
            var measures=ValueMeasures.From(session.Value(instrument,hour,discount)).Scale(D("400"));
            var holding=measures.Rows.Single(r=>r.Slot.Role==PpaRole.CertificateDelivery);
            Quantity(holding.Quantity,"100");Money(holding.Amount,"500");Money(measures.ValueExcludingOverdue,"500");
            Equal(holding.Class,EconomicClass.Held);Equal(holding.Coverage,ValueCoverage.HoldingValue);Equal(holding.Discount,null);Equal(holding.Lifecycle,null);
            var cash=measures.Rows.Single(r=>r.Slot.Role==PpaRole.CertificatePayment);
            Money(cash.Amount,"-300");Equal(cash.Coverage,ValueCoverage.OverdueBalance);Equal(cash.PresentValue,null);
            Equal(measures.CompletePv,null);Money(measures.OverdueKnownBalance,"-300");
            Equal(inputs.VolumeComputations,1);
            Export("03_retained_certificates/primary",measures.ValueExcludingOverdue);Export("03_retained_certificates/overdue_known",measures.OverdueKnownBalance);
            OracleValues["03_retained_certificates/complete_pv"]=measures.CompletePv;
            OracleValues["03_retained_certificates/holding_class"]=holding.Class.ToString();OracleValues["03_retained_certificates/holding_coverage"]=holding.Coverage.ToString();
            OracleValues["03_retained_certificates/holding_cash_rows"]=measures.Rows.Count(r=>r.Slot.Role==PpaRole.CertificateDelivery && r.Kind==ComponentKind.Payment);
            var scenarios=observed.Model.Scenarios(observed.Build,[observed.Bumps["synthetic_certificate"]]).Single();
            var up=ValueMeasures.From(inputs.Session(scenarios.Up,session).Value(instrument,hour,discount)).Scale(D("400"));
            var down=ValueMeasures.From(inputs.Session(scenarios.Down,session).Value(instrument,hour,discount)).Scale(D("400"));
            Money(up.ValueExcludingOverdue,"600");Money(down.ValueExcludingOverdue,"400");
            Money(up.OverdueKnownBalance,"-300");Money(down.OverdueKnownBalance,"-300");
            Export("03_retained_certificates/up",up.ValueExcludingOverdue);Export("03_retained_certificates/down",down.ValueExcludingOverdue);
        });
        Check("estimated holdings correct under later knowledge while original valuation and cutoff replay",()=>{
            var instrument=f.WithCertificate(cash:false);var valuation=PpaAcceptanceFixture.At("2031-12-10T00:00:00Z");
            var correctedAt=valuation.AddDays(1);
            var firstMeter=f.Meter(h=>h.Start==f.From?null:"0");
            ValueMeasureSet Value(DateTimeOffset at,DateTimeOffset cutoff,QuantitySeries meter)
            {
                var observed=f.Market(at,cutoff,actuals:_=>"40");var inputs=f.Prepare(observed,[instrument],meter,knownAt:cutoff);
                return ValueMeasures.From(inputs.Session(observed.Build).Value(instrument,hour,discount)).Scale(D("400"));
            }
            var first=Value(valuation,valuation,firstMeter);Money(first.ValueExcludingOverdue,"500");Money(first.EstimatedHoldingValue,"500");
            Equal(first.Rows.Single(r=>r.Slot.Role==PpaRole.CertificateDelivery).Class,EconomicClass.Estimated);
            var correction=firstMeter with {Cells=firstMeter.Cells.Add(new(f.From,f.From.AddHours(1),D("45"),2,correctedAt,f.QuantityKnowledge))};
            var revised=Value(valuation,correctedAt,correction);Money(revised.ValueExcludingOverdue,"450");
            Equal(revised.Rows.Single(r=>r.Slot.Role==PpaRole.CertificateDelivery).Class,EconomicClass.Held);
            var replay=Value(valuation,valuation,correction);Money(replay.ValueExcludingOverdue,"500");
            Export("04_estimated_holdings/original",first.ValueExcludingOverdue);Export("04_estimated_holdings/corrected",revised.ValueExcludingOverdue);Export("04_estimated_holdings/original_replay",replay.ValueExcludingOverdue);
            OracleValues["04_estimated_holdings/original_class"]=first.Rows.Single(r=>r.Slot.Role==PpaRole.CertificateDelivery).Class.ToString();
            var due=f.From.AddHours(1).AddMinutes(f.Source.MeterDue.OffsetMinutes);
            OracleValues["04_estimated_holdings/deadline_missing"]=Reject("meter_actual_missing",()=>Value(due,valuation,firstMeter));
        });
        Check("request unit cache distinguishes coverage while applying each binding MW once",()=>{
            foreach(var segmented in new[]{false,true})
            {
                var instrument=f.Physical();var inputs=f.Prepare(market,[instrument]);
                var middle=f.Clock.StartOfDay(new(2031,11,15)).ToUniversalTime();
                var scopes=segmented?new[]{new DeliveryCoverage(f.From,f.To),new(f.From,middle),new(middle,f.To)}:[new DeliveryCoverage(f.From,f.To)];
                var book=Guid.NewGuid();var positions=ImmutableArray.CreateBuilder<RiskPosition>();
                foreach(var scope in scopes)
                    for(var n=1;n<=100;n++)
                        positions.Add(new(Guid.NewGuid(),Guid.NewGuid(),book,instrument.Id,new(D(n.ToString()),f.Capacity.Key),
                            new(new Money(default,instrument.Currency),f.Capacity.Key),new(default,instrument.Currency)){Coverage=scope});
                var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,discount){Interval=inputs};
                var portfolio=new ValuationPortfolio([instrument],positions.ToImmutable(),setup);
                var prepared=RiskEngine.Prepare(market.Model,market.Snapshot,[portfolio]);
                var run=RiskEngine.CalculateExact(prepared,[],RiskEngine.ExactNumericalPolicy);
                Equal(run.Work.UnitPricerCalls,segmented?3:1);Equal(run.Work.UniqueValuations,segmented?3:1);
                Equal(run.Work.RepeatedScopeReferences,segmented?297:99);Equal(run.Work.UniqueSideCalculations,segmented?6:2);
                // 1+...+100=5050 MW; November has721 elapsed operating hours.
                Money(run.Groups.Single().BaseValues.Single().Value,segmented?"14564200":"7282100");
                var label=segmented?"segmented":"full";
                OracleValues[$"09_unit_reuse/{label}_unit_calculations"]=run.Work.UnitPricerCalls;
                OracleValues[$"09_unit_reuse/{label}_unit_reuses"]=run.Work.RepeatedScopeReferences;
                OracleValues[$"09_unit_reuse/{label}_side_calculations"]=run.Work.UniqueSideCalculations;
            }
        });
        Check("independent physical and cash dates discount gross amounts before summing and reverse once",()=>{
            var source=f.Market(at,energy:"1");
            var physical=new PowerPhysicalSide(Guid.NewGuid(),f.Terms(f.Profile,10),f.Asset.Location);
            var cash=new PpaFixedSide(Guid.NewGuid(),f.Terms(new SlotQuantity([new(PpaRole.EnergyDelivery)]),20),new FlatPrice(new(D("1"),f.EnergyPrice)));
            var members=ImmutableArray.Create(new PpaMembership(new(PpaRole.EnergyDelivery),physical,D("1")),new(new(PpaRole.StrikePayment),cash,D("-1")));
            var instrument=new PpaInstrument(Guid.NewGuid(),f.Definitions,f.Reference,members,[],true,"ppa_instrument_v1");
            var reverse=new PpaInstrument(Guid.NewGuid(),f.Definitions,f.Reference,[..members.Select(m=>m with {Coefficient=D("-1")*m.Coefficient})],[],true,"ppa_instrument_v1");
            var inputs=f.Prepare(source,[instrument,reverse]);var session=inputs.Session(source.Build);var dated=new DatedDiscounting();
            var value=ValueMeasures.From(session.Value(instrument,hour,dated)).Scale(D("100"));
            var reversed=ValueMeasures.From(session.Value(reverse,hour,dated)).Scale(D("100"));
            Money(value.ValueExcludingOverdue,"10");Money(reversed.ValueExcludingOverdue,"-10");
            Money(value.Rows.Single(r=>r.Kind==ComponentKind.Delivery).PresentValue!,"90");Money(value.Rows.Single(r=>r.Kind==ComponentKind.Payment).PresentValue!,"-80");
            Export("11_independent_discounting/primary",value.ValueExcludingOverdue);Export("11_independent_discounting/reversed",reversed.ValueExcludingOverdue);
            var unit=ValueMeasures.From(session.Value(instrument,hour,dated));
            Equal(unit.Scale(D("80")).ValueExcludingOverdue.Add(unit.Scale(D("20")).ValueExcludingOverdue),unit.Scale(D("100")).ValueExcludingOverdue);
        });
        Check("a reused provider object cannot conceal changed discount factors from the unit cache",()=>{
            var instrument=f.Physical(discounting:true);var inputs=f.Prepare(market,[instrument]);var session=inputs.Session(market.Build);
            var provider=new MutableDiscounting{Factor=D("0.9")};
            var first=ValueMeasures.From(session.Value(instrument,hour,provider)).Scale(D("100"));Money(first.ValueExcludingOverdue,"180");
            provider.Factor=D("0.8");
            var changed=ValueMeasures.From(session.Value(instrument,hour,provider)).Scale(D("100"));Money(changed.ValueExcludingOverdue,"160");
            Money(first.ValueExcludingOverdue,"180");
        });
        return count;
    }

    private sealed class DatedDiscounting:ISideDiscounting
    {
        public ImmutableArray<SidePaymentFactor> Resolve(ImmutableArray<SidePaymentKey> payments,MarketSnapshot snapshot)=>
            [..payments.Select(p=>new SidePaymentFactor(p,D(p.PaymentAt.Day==10?"0.9":"0.8"),snapshot,DiscountFactorSource.Explicit))];
    }
    private sealed class MutableDiscounting:ISideDiscounting
    {
        public ExactDecimal Factor { get; set; }
        public ImmutableArray<SidePaymentFactor> Resolve(ImmutableArray<SidePaymentKey> payments,MarketSnapshot snapshot)=>
            [..payments.Select(p=>new SidePaymentFactor(p,Factor,snapshot,DiscountFactorSource.Explicit))];
    }

    private static int LifecycleChecks()
    {
        var count=0;
        void Check(string label,Action action){action();count++;Console.WriteLine("PASS PPA independent acceptance: "+label);}
        var f=new PpaAcceptanceFixture();var at=PpaAcceptanceFixture.At("2031-12-10T00:00:00Z");
        var certificateSource=f.Source with {Key=new(f.Definitions,"synthetic_certificate_generation"),ForecastSeries=Guid.NewGuid(),MeteredSeries=Guid.NewGuid()};
        var certificateForecast=f.Forecast() with {Id=certificateSource.ForecastSeries};
        var certificateMeter=f.Meter(h=>h.Start==f.From?"250":"0") with {Id=certificateSource.MeteredSeries};
        var forecast=f.Forecast() with {Cells=[..f.Forecast().Cells.Select(c=>c.Start==f.From.AddHours(1)?c with {Value=D("0.125")}:c)]};
        var meter=f.Meter(h=>h.Start==f.From?"50":h.Start==f.From.AddHours(1)?null:"0");
        var indexed=new PpaFloatingSide(Guid.NewGuid(),f.Terms(f.Volume),new(f.C("synthetic_energy"),D("1"),new(D("0"),f.EnergyPrice)));
        var future=new PpaFixedSide(Guid.NewGuid(),f.Terms(f.Profile,20),new FlatPrice(new(D("0.625"),f.EnergyPrice)));
        var certificate=new CertificateDeliverySide(Guid.NewGuid(),f.Terms(new VolumeQuantity([certificateSource.Key],QuantityStage.Generated)),f.Certificates.Key,D("1"),
            "asset_generation_at_stage_v1","held_at_current_vintage_mark_v1","none_recorded_v1");
        var members=ImmutableArray.Create(new PpaMembership(new(PpaRole.IndexedPayment),indexed,D("1")),new(new(PpaRole.StrikePayment),future,D("1")),new(new(PpaRole.CertificateDelivery),certificate,D("1")));
        PpaInstrument Instrument(bool discounting)=>new(Guid.NewGuid(),f.Definitions,f.Reference,members,[f.Asset.Key],discounting,"ppa_instrument_v1");
        var instrument=Instrument(true);var coverage=new DeliveryCoverage(f.From,f.From.AddHours(2));
        IntervalValuationInputs Inputs(PpaAcceptanceFixture.MarketInputs market,PpaInstrument selected,QuantitySeries selectedMeter,DateTimeOffset cutoff)=>
            f.Prepare(market,[selected],selectedMeter,knownAt:cutoff,forecastOverride:forecast,extraSources:[certificateSource],extraSeries:[certificateForecast,certificateMeter]);
        var source=f.Market(at,actuals:_=>"40");var inputs=Inputs(source,instrument,meter,at);
        var discount=new ExplicitDiscounting(D("0.9"),DiscountFactorSource.Explicit);
        ValueMeasureSet Value(IntervalValuationInputs prepared,PpaAcceptanceFixture.MarketInputs market,PpaInstrument selected,ISideDiscounting df)=>
            ValueMeasures.From(prepared.Session(market.Build).Value(selected,coverage,df)).Scale(D("80"));
        var value=Value(inputs,source,instrument,discount);
        Check("due equality preserves mixed knowledge on one payment side and excludes both balances from primary",()=>{
            Money(value.OverdueKnownBalance,"800");Money(value.OverdueEstimated,"400");Money(value.ValueExcludingOverdue,"590");
            Equal(value.CompletePv,null);Equal(value.UnavailableReason,"due_cash_policy_required");
            var cash=value.Rows.Where(r=>r.SideId==indexed.Id).ToArray();Equal(cash.Length,2);
            Equal(cash.Select(r=>r.ValueAt).Distinct().Count(),1);Equal(cash[0].ValueAt,at);
            Equal(cash[0].Coverage,ValueCoverage.OverdueBalance);Equal(cash[1].Coverage,ValueCoverage.OverdueEstimate);
            Equal(cash[0].PresentValue,null);Equal(cash[1].PresentValue,null);
            var undiscounted=Instrument(false);var disabled=Value(Inputs(source,undiscounted,meter,at),source,undiscounted,new ExplicitDiscounting(D("1"),DiscountFactorSource.Undiscounted));
            Money(disabled.ValueExcludingOverdue,"600");Equal(disabled.CompletePv,null);
            Export("05_mixed_overdue/overdue_known",value.OverdueKnownBalance);Export("05_mixed_overdue/overdue_estimated",value.OverdueEstimated);
            Export("05_mixed_overdue/primary",value.ValueExcludingOverdue);Export("05_mixed_overdue/undiscounted_primary",disabled.ValueExcludingOverdue);
            OracleValues["05_mixed_overdue/complete_pv"]=value.CompletePv;OracleValues["05_mixed_overdue/known_pv"]=cash[0].PresentValue;OracleValues["05_mixed_overdue/estimated_pv"]=cash[1].PresentValue;
        });
        Check("later meter corrections change estimated overdue amounts only in a new pinned context",()=>{
            var later=at.AddDays(1);var corrected=meter with {Cells=meter.Cells.Add(new(f.From.AddHours(1),f.From.AddHours(2),D("30"),2,later,f.QuantityKnowledge))};
            var updatedMarket=f.Market(at,later,actuals:_=>"40");var updated=Value(Inputs(updatedMarket,instrument,corrected,later),updatedMarket,instrument,discount);
            var row=updated.Rows.Single(r=>r.SideId==indexed.Id && r.Interval.Start==f.From.AddHours(1));
            Money(row.Amount,"480");Equal(row.Coverage,ValueCoverage.OverdueBalance);Money(updated.OverdueEstimated,"0");
            Money(value.OverdueEstimated,"400");Export("06_deadlines_and_cutoffs/corrected_known",row.Amount);
            var due=f.From.AddHours(2).AddMinutes(f.Source.MeterDue.OffsetMinutes);
            OracleValues["06_deadlines_and_cutoffs/meter_deadline_missing"]=Reject("meter_actual_missing",()=>Inputs(f.Market(due,actuals:_=>"40"),instrument,meter,due));
            OracleValues["06_deadlines_and_cutoffs/price_deadline_missing"]=Reject("interval_actual_missing",()=>Inputs(f.Market(at,actuals:h=>h.Start==f.From.AddHours(1)?null:"40"),instrument,meter,at));
        });
        Check("commodity scenarios retain overdue estimates and known balances outside the live holding measure",()=>{
            var session=inputs.Session(source.Build);_=session.Value(instrument,coverage,discount);
            var scenarios=source.Model.Scenarios(source.Build,[source.Bumps["synthetic_certificate"]]).Single();
            foreach(var (label,build,primary) in new[]{("base",source.Build,"590"),("up",scenarios.Up,"690"),("down",scenarios.Down,"490")})
            {
                var changed=ValueMeasures.From(inputs.Session(build,session).Value(instrument,coverage,discount)).Scale(D("80"));
                Money(changed.OverdueKnownBalance,"800");Money(changed.OverdueEstimated,"400");Money(changed.ValueExcludingOverdue,primary);
                Export($"06_deadlines_and_cutoffs/{label}_overdue_known",changed.OverdueKnownBalance);
                Export($"06_deadlines_and_cutoffs/{label}_overdue_estimated",changed.OverdueEstimated);
            }
        });
        Check("complete PV is available only with no overdue rows while permitted estimates stay labelled",()=>{
            var zero=new PpaFloatingSide(Guid.NewGuid(),f.Terms(f.Volume,20),new(f.C("synthetic_energy"),D("1"),new(D("-40"),f.EnergyPrice)));
            var noDue=new PpaInstrument(Guid.NewGuid(),f.Definitions,f.Reference,
                [new(new(PpaRole.IndexedPayment),zero,D("1")),new(new(PpaRole.StrikePayment),future,D("1")),new(new(PpaRole.CertificateDelivery),certificate,D("1"))],
                [f.Asset.Key],true,"ppa_instrument_v1");
            var available=Value(Inputs(source,noDue,meter,at),source,noDue,discount);
            Money(available.ValueExcludingOverdue,"590");Money(available.CompletePv!,"590");Equal(available.UnavailableReason,null);
            Equal(available.Rows.Any(r=>r.Class==EconomicClass.Estimated),true);
            Export("12_measure_reconciliation/no_overdue_complete_pv",available.CompletePv!);
        });
        Check("canceling known overdue balances still leave complete PV unavailable",()=>{
            var physical=f.Physical("40");var prices=f.Market(at,energy:"40",actuals:_=>"40");
            var energySide=(PowerPhysicalSide)physical.Memberships[0].Side;
            var opposite=new PpaFixedSide(Guid.NewGuid(),f.Terms(new SlotQuantity([new(PpaRole.EnergyDelivery)])),new FlatPrice(new(D("40"),f.EnergyPrice)));
            var canceled=new PpaInstrument(Guid.NewGuid(),f.Definitions,f.Reference,
                [new(new(PpaRole.EnergyDelivery),energySide,D("1")),new(new(PpaRole.StrikePayment),physical.Memberships[1].Side,D("-1")),new(new(PpaRole.FixedCharge),opposite,D("1"))],[],false,"ppa_instrument_v1");
            var prepared=f.Prepare(prices,[canceled]);var result=ValueMeasures.From(prepared.Session(prices.Build).Value(canceled,new(f.From,f.From.AddHours(1)),new ExplicitDiscounting(D("1"),DiscountFactorSource.Undiscounted)));
            Money(result.OverdueKnownBalance,"0");Equal(result.HasOverdue,true);Equal(result.CompletePv,null);
            OracleValues["05_mixed_overdue/canceling_overdue_complete_pv"]=result.CompletePv;
        });
        Check("delivered physical value at exact value-date equality remains outside primary and axes reconcile",()=>{
            var physical=f.Physical("0");var physicalInputs=f.Prepare(source,[physical]);
            var delivered=ValueMeasures.From(physicalInputs.Session(source.Build).Value(physical,new(f.From,f.From.AddHours(1)),new ExplicitDiscounting(D("1"),DiscountFactorSource.Undiscounted))).Scale(D("10"));
            var row=delivered.Rows.Single(r=>r.Kind==ComponentKind.Delivery);Money(row.Amount,"400");Equal(row.ValueAt,at);
            Equal(row.Coverage,ValueCoverage.DeliveredValue);Equal(row.Class,EconomicClass.Realised);Equal(row.Lifecycle,null);Money(delivered.ValueExcludingOverdue,"0");
            var combined=value.ValueExcludingOverdue.Add(delivered.ValueExcludingOverdue);Money(combined,"590");
            var rows=value.Rows.AddRange(delivered.Rows);
            ExactMoney Sum(IEnumerable<ExactMoney> values)=>values.Aggregate((a,b)=>a.Add(b));
            var total=Sum(rows.Select(r=>r.Amount));Money(total,"2200");
            Equal(Sum(rows.GroupBy(r=>r.Class).Select(g=>Sum(g.Select(r=>r.Amount)))),total);
            Equal(Sum(rows.GroupBy(r=>r.Coverage).Select(g=>Sum(g.Select(r=>r.Amount)))),total);
            var cash=rows.Where(r=>r.Kind==ComponentKind.Payment).ToArray();
            Equal(cash.All(r=>r.Lifecycle.HasValue),true);Equal(rows.Where(r=>r.Kind!=ComponentKind.Payment).All(r=>r.Lifecycle is null),true);
            Equal(Sum(cash.GroupBy(r=>r.Lifecycle).Select(g=>Sum(g.Select(r=>r.Amount)))),Sum(cash.Select(r=>r.Amount)));
            Export("12_measure_reconciliation/primary",combined);Export("12_measure_reconciliation/delivered_value",row.Amount);Export("12_measure_reconciliation/all_amounts",total);
            ExactMoney Partition(IEnumerable<ScheduleRowValue> selected)=>selected.Select(r=>r.Amount).Aggregate(ExactMoney.FromMoney(new(default,instrument.Currency)),(a,b)=>a.Add(b));
            foreach(var (label,classification) in new[]{("realised",EconomicClass.Realised),("estimated",EconomicClass.Estimated),("held",EconomicClass.Held),("unrealised",EconomicClass.Unrealised)})
                Export("12_measure_reconciliation/class_"+label,Partition(rows.Where(r=>r.Class==classification)));
            foreach(var (label,classification) in new[]{("overdue_known",ValueCoverage.OverdueBalance),("overdue_estimated",ValueCoverage.OverdueEstimate),("discounted_amount",ValueCoverage.Discounted),("holding",ValueCoverage.HoldingValue),("delivered",ValueCoverage.DeliveredValue)})
                Export("12_measure_reconciliation/coverage_"+label,Partition(rows.Where(r=>r.Coverage==classification)));
            Export("12_measure_reconciliation/lifecycle_delivered",Partition(rows.Where(r=>r.Lifecycle==PaymentLifecycle.DeliveredUnsettled)));
            Export("12_measure_reconciliation/lifecycle_remaining",Partition(rows.Where(r=>r.Lifecycle==PaymentLifecycle.RemainingProjected)));
            OracleValues["12_measure_reconciliation/complete_pv"]=value.CompletePv;
        });
        Check("an estimated delivered physical quantity never becomes realised merely by crossing its value date",()=>{
            var physical=f.Physical("0",volume:true);var missing=f.Meter(h=>h.Start==f.From?null:"0");
            var prepared=f.Prepare(source,[physical],missing);
            var result=ValueMeasures.From(prepared.Session(source.Build).Value(physical,new(f.From,f.From.AddHours(1)),new ExplicitDiscounting(D("1"),DiscountFactorSource.Undiscounted))).Scale(D("40"));
            var row=result.Rows.Single(r=>r.Kind==ComponentKind.Delivery);Money(row.Amount,"400");
            Equal(row.Class,EconomicClass.Estimated);Equal(row.Coverage,ValueCoverage.DeliveredValue);Money(result.RealisedDeliveryValue,"0");Money(result.ValueExcludingOverdue,"0");
            Export("12_measure_reconciliation/estimated_delivered_realised_value",result.RealisedDeliveryValue);
        });
        Check("portfolio risk carries base overdue and delivered measures beside the live primary value",()=>{
            var physical=f.Physical("0");
            var preparedInputs=f.Prepare(source,[instrument,physical],meter,forecastOverride:forecast,extraSources:[certificateSource],extraSeries:[certificateForecast,certificateMeter]);
            var book=Guid.NewGuid();
            RiskPosition Position(PpaInstrument selected,string mw,DeliveryCoverage scope)=>new(Guid.NewGuid(),Guid.NewGuid(),book,selected.Id,new(D(mw),f.Capacity.Key),
                new(new Money(default,selected.Currency),f.Capacity.Key),new(default,selected.Currency)){Coverage=scope};
            var portfolio=new ValuationPortfolio([instrument,physical],[Position(instrument,"80",coverage),Position(physical,"10",new(f.From,f.From.AddHours(1)))],
                new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,discount){Interval=preparedInputs});
            var prepared=RiskEngine.Prepare(source.Model,source.Snapshot,[portfolio]);
            var run=RiskEngine.CalculateExact(prepared,[source.Bumps["synthetic_certificate"]],RiskEngine.ExactNumericalPolicy);
            var group=run.Groups.Single();var retained=group.BaseMeasures.Single();Equal(retained.BookId,book);
            Equal(retained.Bindings.Length,2);
            Equal(retained.Bindings.Select(b=>(b.TradeId,b.BindingId)).ToHashSet().SetEquals(portfolio.Positions.Select(p=>(p.TradeId,p.BindingId))),true);
            Money(group.BaseValues.Single().Value,"590");Money(retained.Measures.ValueExcludingOverdue,"590");
            Money(retained.Measures.OverdueKnownBalance,"800");Money(retained.Measures.OverdueEstimated,"400");Money(retained.Measures.RealisedDeliveryValue,"400");
            Equal(retained.Measures.CompletePv,null);
            var risk=group.Factors.Single().Totals.Single().Values;Money(risk.UpPnl,"100");Money(risk.DownPnl,"-100");
            Export("12_measure_reconciliation/primary",retained.Measures.ValueExcludingOverdue);Export("12_measure_reconciliation/delivered_value",retained.Measures.RealisedDeliveryValue);
        });
        return count;
    }

    private static int PriceAndContextChecks()
    {
        var count=0;
        void Check(string label,Action action){action();count++;Console.WriteLine("PASS PPA independent acceptance: "+label);}
        var f=new PpaAcceptanceFixture();var at=PpaAcceptanceFixture.At("2031-10-30T00:00:00Z");
        var discount=new ExplicitDiscounting(D("1"),DiscountFactorSource.Undiscounted);var hour=new DeliveryCoverage(f.From,f.From.AddHours(1));
        PpaFloatingSide Floating(string curve,string multiplier="1",string adder="0")=>new(Guid.NewGuid(),f.Terms(f.Profile),new(f.C(curve),D(multiplier),new(D(adder),f.EnergyPrice)));
        PpaInstrument Instrument(params PpaMembership[] members)=>new(Guid.NewGuid(),f.Definitions,f.Reference,[..members],[],false,"ppa_instrument_v1");
        Check("virtual fixed for floating has only gross cash rows and exact negative spread",()=>{
            var receive=Floating("synthetic_hub");var pay=new PpaFixedSide(Guid.NewGuid(),f.Terms(f.Profile),new FlatPrice(new(D("40"),f.EnergyPrice)));
            var instrument=Instrument(new(new(PpaRole.IndexedPayment),receive,D("1")),new(new(PpaRole.StrikePayment),pay,D("-1")));
            var market=f.Market(at);var inputs=f.Prepare(market,[instrument]);
            var value=ValueMeasures.From(inputs.Session(market.Build).Value(instrument,hour,discount)).Scale(D("100"));
            Money(value.ValueExcludingOverdue,"-1000");Equal(value.Rows.All(r=>r.Kind==ComponentKind.Payment),true);Equal(value.Rows.Length,2);
            Export("plan_virtual/primary",value.ValueExcludingOverdue);
        });
        Check("index multiplication precedes its explicit adder and physical components remain gross",()=>{
            foreach(var multiplier in new[]{false,true})
            {
                var market=f.Market(at,energy:multiplier?"40":"32");
                var physical=new PowerPhysicalSide(Guid.NewGuid(),f.Terms(f.Profile),f.Asset.Location);
                var pay=new PpaFloatingSide(Guid.NewGuid(),f.Terms(new SlotQuantity([new(PpaRole.EnergyDelivery)])),
                    new(f.C("synthetic_energy"),D(multiplier?"0.95":"1"),new(D(multiplier?"2":"1"),f.EnergyPrice)));
                var instrument=Instrument(new(new(PpaRole.EnergyDelivery),physical,D("1")),new(new(PpaRole.IndexedPayment),pay,D("-1")));
                var inputs=f.Prepare(market,[instrument]);var session=inputs.Session(market.Build);
                var value=ValueMeasures.From(session.Value(instrument,hour,discount)).Scale(D("100"));
                var cash=value.Rows.Single(r=>r.Kind==ComponentKind.Payment);
                if(multiplier){Price(cash.PriceComponents.Single().Price,"40");Money(value.ValueExcludingOverdue,"0");OracleValues["plan_indexed/multiplied_price"]=new{numerator=cash.PriceComponents.Single().Price.Numerator,denominator=cash.PriceComponents.Single().Price.Denominator};}
                else
                {
                    Money(cash.Amount,"-3300");Money(value.ValueExcludingOverdue,"-100");
                    Export("plan_indexed/cash_with_adder",cash.Amount);Export("plan_indexed/primary_with_adder",value.ValueExcludingOverdue);
                    Export("plan_indexed/physical",value.Rows.Single(r=>r.Kind==ComponentKind.Delivery).Amount);
                }
                Equal(session.Resolver.Resolved,1);
            }
        });
        Check("basis cash keeps two explicit curves and both signed contributions",()=>{
            var instrument=Instrument(new(new(PpaRole.IndexedPayment,0),Floating("synthetic_hub"),D("1")),
                new(new(PpaRole.IndexedPayment,1),Floating("synthetic_energy"),D("-1")));
            var market=f.Market(at,energy:"32");var inputs=f.Prepare(market,[instrument]);
            var value=ValueMeasures.From(inputs.Session(market.Build).Value(instrument,hour,discount)).Scale(D("100"));
            Money(value.ValueExcludingOverdue,"-200");Equal(value.Rows.Length,2);
            Export("plan_basis/primary",value.ValueExcludingOverdue);Export("plan_basis/receive",value.Rows.Single(r=>r.Slot.Ordinal==0).Amount);
            Export("plan_basis/pay",value.Rows.Single(r=>r.Slot.Ordinal==1).Amount);
        });
        Check("shared physical work does not collapse differing strike side values",()=>{
            var physical=new PowerPhysicalSide(Guid.NewGuid(),f.Terms(f.Volume),f.Asset.Location);
            PpaInstrument WithStrike(string strike)=>new(Guid.NewGuid(),f.Definitions,f.Reference,
                [new(new(PpaRole.EnergyDelivery),physical,D("1")),new(new(PpaRole.StrikePayment),new PpaFixedSide(Guid.NewGuid(),f.Terms(new SlotQuantity([new(PpaRole.EnergyDelivery)])),new FlatPrice(new(D(strike),f.EnergyPrice))),D("-1"))],
                [f.Asset.Key],false,"ppa_instrument_v1");
            var first=WithStrike("40");var second=WithStrike("50");var market=f.Market(at);var inputs=f.Prepare(market,[first,second]);var session=inputs.Session(market.Build);
            var a=ValueMeasures.From(session.Value(first,hour,discount)).Scale(D("80"));var b=ValueMeasures.From(session.Value(second,hour,discount)).Scale(D("80"));
            Money(a.ValueExcludingOverdue,"40");Money(b.ValueExcludingOverdue,"-160");
            Equal(inputs.VolumeComputations,1);Equal(session.Resolver.Resolved,1);Equal(session.UniqueSideCalculations,4);
            Export("10_shared_work/cash_quantity_0_strike_0",a.Rows.Single(r=>r.Kind==ComponentKind.Payment).Amount);
            Export("10_shared_work/cash_quantity_0_strike_1",b.Rows.Single(r=>r.Kind==ComponentKind.Payment).Amount);
            Export("10_shared_work/combined_quantity_0_strike_1",b.ValueExcludingOverdue);
            var fresh=f.Prepare(market,[first,second]);Reject("ppa_context",()=>fresh.Session(market.Build,session));
            var adjustment=f.Assignments.Select(a=>a.Attribute==ValuedAttribute.Energy?a with {Adjustment=D("2")}:a).ToImmutableArray();
            var adjusted=f.Prepare(market,[first],assignments:adjustment);
            var changed=ValueMeasures.From(adjusted.Session(market.Build).Value(first,hour,discount)).Scale(D("80"));Money(changed.ValueExcludingOverdue,"80");
            Money(a.ValueExcludingOverdue,"40");
        });
        Check("one reusable strike resolves its own instrument sibling quantities",()=>{
            var other=f.Source with {Key=new(f.Definitions,"synthetic_second_volume"),ForecastSeries=Guid.NewGuid(),MeteredSeries=Guid.NewGuid()};
            var sharedStrike=new PpaFixedSide(Guid.NewGuid(),f.Terms(new SlotQuantity([new(PpaRole.EnergyDelivery)])),new FlatPrice(new(D("40"),f.EnergyPrice)));
            PpaInstrument WithSource(VolumeSourceKey source)=>new(Guid.NewGuid(),f.Definitions,f.Reference,
                [new(new(PpaRole.EnergyDelivery),new PowerPhysicalSide(Guid.NewGuid(),f.Terms(new VolumeQuantity([source],QuantityStage.Generated)),f.Asset.Location),D("1")),new(new(PpaRole.StrikePayment),sharedStrike,D("-1"))],
                [f.Asset.Key],false,"ppa_instrument_v1");
            var first=WithSource(f.Source.Key);var second=WithSource(other.Key);var market=f.Market(at);
            var inputs=f.Prepare(market,[first,second],extraSources:[other],extraSeries:[f.Forecast("0.5") with {Id=other.ForecastSeries},f.Meter() with {Id=other.MeteredSeries}]);
            var session=inputs.Session(market.Build);var a=ValueMeasures.From(session.Value(first,hour,discount)).Scale(D("80"));var b=ValueMeasures.From(session.Value(second,hour,discount)).Scale(D("80"));
            Money(a.Rows.Single(r=>r.Kind==ComponentKind.Payment).Amount,"-800");Money(b.Rows.Single(r=>r.Kind==ComponentKind.Payment).Amount,"-1600");
            Money(b.ValueExcludingOverdue,"80");Equal(session.UniqueSideCalculations,4);Equal(session.Resolver.Resolved,1);
            Export("10_shared_work/physical_1",b.Rows.Single(r=>r.Kind==ComponentKind.Delivery).Amount);
            Export("10_shared_work/cash_quantity_1_strike_0",b.Rows.Single(r=>r.Kind==ComponentKind.Payment).Amount);
            Export("10_shared_work/combined_quantity_1_strike_0",b.ValueExcludingOverdue);
            var higher=new PpaInstrument(Guid.NewGuid(),f.Definitions,f.Reference,
                [second.Memberships[0],new(new(PpaRole.StrikePayment),new PpaFixedSide(Guid.NewGuid(),sharedStrike.Terms,new FlatPrice(new(D("50"),f.EnergyPrice))),D("-1"))],[f.Asset.Key],false,"ppa_instrument_v1");
            var different=f.Prepare(market,[higher],extraSources:[other],extraSeries:[f.Forecast("0.5") with {Id=other.ForecastSeries},f.Meter() with {Id=other.MeteredSeries}]);
            var c=ValueMeasures.From(different.Session(market.Build).Value(higher,hour,discount)).Scale(D("80"));
            Money(c.ValueExcludingOverdue,"-320");Export("10_shared_work/cash_quantity_1_strike_1",c.Rows.Single(r=>r.Kind==ComponentKind.Payment).Amount);
            Export("10_shared_work/combined_quantity_1_strike_1",c.ValueExcludingOverdue);
        });
        Check("eighty and twenty MW reproduce one hundred MW for distinct exact unit values",()=>{
            foreach(var (label,mark,expected80,expected20,expected100) in new[]{("09_unit_reuse","43.125","250","62.5","312.5"),("11_independent_discounting","42.5","200","50","250")})
            {
                var market=f.Market(at,energy:mark);var instrument=f.Physical();var inputs=f.Prepare(market,[instrument]);
                var unit=ValueMeasures.From(inputs.Session(market.Build).Value(instrument,hour,discount));
                var sizes=new[]{"80","20","100"};var expected=new[]{expected80,expected20,expected100};
                for(var n=0;n<sizes.Length;n++)
                {
                    var value=unit.Scale(D(sizes[n])).ValueExcludingOverdue;Equal(value,ExactMoney.FromMoney(new(D(expected[n]),instrument.Currency)));Export($"{label}/scaled_{n}",value);
                }
                Equal(unit.Scale(D("80")).ValueExcludingOverdue.Add(unit.Scale(D("20")).ValueExcludingOverdue),unit.Scale(D("100")).ValueExcludingOverdue);
            }
        });
        Check("economic classes reconcile realised cash and delivery independently from future unrealised value",()=>{
            var past=f.Physical("50");var observed=f.Market(PpaAcceptanceFixture.At("2031-12-10T00:00:00Z"),actuals:_=>"40");
            var inputs=f.Prepare(observed,[past]);var realised=ValueMeasures.From(inputs.Session(observed.Build).Value(past,hour,discount)).Scale(D("10"));
            Money(realised.RealisedCash,"-500");Money(realised.RealisedDeliveryValue,"400");Money(realised.Classes[EconomicClass.Realised],"-100");
            Export("plan_classes/realised",realised.Classes[EconomicClass.Realised]);
            var future=f.Physical("40");var market=f.Market(at,energy:"50");var prepared=f.Prepare(market,[future]);
            var unrealised=ValueMeasures.From(prepared.Session(market.Build).Value(future,hour,discount)).Scale(D("10"));
            Money(unrealised.Classes[EconomicClass.Unrealised],"100");Export("plan_classes/unrealised",unrealised.Classes[EconomicClass.Unrealised]);
        });
        Check("economically equal reconstructed sides cannot borrow another request instrument lineage",()=>{
            var instrument=f.Physical();var market=f.Market(at);var inputs=f.Prepare(market,[instrument]);
            var oldPhysical=(PowerPhysicalSide)instrument.Memberships[0].Side;var oldCash=(PpaFixedSide)instrument.Memberships[1].Side;
            var clone=new PpaInstrument(instrument.Id,f.Definitions,f.Reference,
                [new(new(PpaRole.EnergyDelivery),new PowerPhysicalSide(Guid.NewGuid(),oldPhysical.Terms,oldPhysical.Location),D("1")),
                 new(new(PpaRole.StrikePayment),new PpaFixedSide(Guid.NewGuid(),oldCash.Terms,oldCash.Price),D("-1"))],[],false,"ppa_instrument_v1");
            Equal(instrument.EconomicIdentity,clone.EconomicIdentity);
            Reject("ppa_context",()=>inputs.Session(market.Build).Value(clone,hour,discount));
            var populated=inputs.Session(market.Build);_=populated.Value(instrument,hour,discount);
            Reject("ppa_context",()=>populated.Value(clone,hour,discount));
        });
        Check("quarter-hour covariance multiplies each native quantity by its own actual price",()=>{
            var quarter=new PpaAcceptanceFixture(15);var valuation=quarter.From.AddHours(1);var instrument=quarter.Physical("0",volume:true);
            string? Meter(PowerInterval p)=>p.Start<valuation?(((int)(p.Start-quarter.From).TotalMinutes/15)+1).ToString():null;
            string? Actual(PowerInterval p)=>p.Start<valuation?(10*(((int)(p.Start-quarter.From).TotalMinutes/15)+1)).ToString():null;
            var market=quarter.Market(valuation,actuals:Actual);var inputs=quarter.Prepare(market,[instrument],quarter.Meter(Meter,valuation));
            var value=ValueMeasures.From(inputs.Session(market.Build).Value(instrument,new(quarter.From,valuation),discount)).Scale(D("200"));
            Money(value.ValueExcludingOverdue,"300");Equal(value.Rows.Count(r=>r.Kind==ComponentKind.Delivery),4);
            Export("plan_covariance/amount",value.ValueExcludingOverdue);
        });
        Check("commodity risk pins discount factors across its base up and down evaluations",()=>{
            var instrument=f.Physical(discounting:true);var market=f.Market(at);var inputs=f.Prepare(market,[instrument]);
            var position=new RiskPosition(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),instrument.Id,new(D("100"),f.Capacity.Key),
                new(new Money(default,instrument.Currency),f.Capacity.Key),new(default,instrument.Currency)){Coverage=hour};
            var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,new ChangingDiscounting()){Interval=inputs};
            var portfolio=new ValuationPortfolio([instrument],[position],setup);
            var prepared=RiskEngine.Prepare(market.Model,market.Snapshot,[portfolio]);
            Reject("ppa_context",()=>RiskEngine.CalculateExact(prepared,[market.Bumps["synthetic_energy"]],RiskEngine.ExactNumericalPolicy));
            var pinned=new ValuationPortfolio([instrument],[position],setup with {Discounting=new ExplicitDiscounting(D("0.9"),DiscountFactorSource.Explicit)});
            var run=RiskEngine.CalculateExact(RiskEngine.Prepare(market.Model,market.Snapshot,[pinned]),[market.Bumps["synthetic_energy"]],RiskEngine.ExactNumericalPolicy);
            var risk=run.Groups.Single().Factors.Single().Totals.Single().Values;
            Money(risk.UpPnl,"90");Money(risk.DownPnl,"-90");
        });
        Check("November certificate generation retains an annual vintage contract rather than requiring a November contract",()=>{
            var instrument=f.WithCertificate(cash:false);var market=f.Market(at,annualCertificate:true);var inputs=f.Prepare(market,[instrument]);
            var value=ValueMeasures.From(inputs.Session(market.Build).Value(instrument,hour,discount)).Scale(D("400"));
            var row=value.Rows.Single(r=>r.Slot.Role==PpaRole.CertificateDelivery);Quantity(row.Quantity,"100");Money(row.Amount,"500");
            var resolved=row.PriceComponents.Single().Resolution!;
            Equal(resolved.Interval.Start,new DateTimeOffset(2031,1,1,0,0,0,TimeSpan.Zero));
            Equal(resolved.Interval.End,new DateTimeOffset(2032,1,1,0,0,0,TimeSpan.Zero));
        });
        return count;
    }

    private sealed class ChangingDiscounting:ISideDiscounting
    {
        private int calls;
        public ImmutableArray<SidePaymentFactor> Resolve(ImmutableArray<SidePaymentKey> payments,MarketSnapshot snapshot)
        {
            var factor=D(calls++==0?"0.9":"0.8");
            return [..payments.Select(p=>new SidePaymentFactor(p,factor,snapshot,DiscountFactorSource.Explicit))];
        }
    }

    private sealed class Fixture
    {
        internal ReferenceRevision Reference { get; }=new(Guid.NewGuid(),Guid.NewGuid());
        internal DefinitionRevision Definitions { get; }=new(Guid.NewGuid(),Guid.NewGuid());
        internal ReferenceKey R(string code)=>new(Reference,code);
        internal ReferenceKey Currency=>R("synthetic_usd");
        internal Quantity UnitMw=>new(D("1"),R("synthetic_mw"));
        internal PriceUnit EnergyPrice=>new(Currency,R("synthetic_mwh"),D("1"));
        internal PpaDelivery Delivery=>new(R("synthetic_clock"),60,new(2031,11,1),new(2031,12,1));
        internal PaymentRule Payment=>new(PaymentFrequency.Monthly,PaymentDateRule.DayOfFollowingMonth,null,10,null,ShortMonth.Reject,DateRoll.Unadjusted,null,PaymentRule.Implementation);
        internal ProfileQuantity Profile=>new(R("synthetic_mw"),R("synthetic_mwh"),new(BlockMembership.All,null,[]));
        internal PpaSideTerms Terms(PpaQuantity quantity)=>new(Delivery,quantity,Currency,UnitMw,Payment);
    }
}
