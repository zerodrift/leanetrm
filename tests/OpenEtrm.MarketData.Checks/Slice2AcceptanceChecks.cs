using System.Numerics;
using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Power;
using OpenEtrm.Shared.Temporal;

internal static class Slice2AcceptanceChecks
{
    private static ExactDecimal D(string value)=>ExactDecimal.Parse(value);
    private static void Equal<T>(T actual,T expected)
    {if(!EqualityComparer<T>.Default.Equals(actual,expected))throw new Exception($"Expected {expected}; got {actual}.");}
    private static void Reject(string code,Action action)
    {
        try{action();}
        catch(MarketDataException error)when(error.Code==code){return;}
        catch(TemporalException error)when(error.Code==code){return;}
        throw new Exception("Expected "+code);
    }
    private static void Fraction(string numerator,string denominator,string expectedNumerator,string expectedDenominator)
    {Equal(numerator,expectedNumerator);Equal(denominator,expectedDenominator);}

    public static int Run()
    {
        var passed=0;
        void Check(string label,Action action){action();passed++;Console.WriteLine("PASS slice 2 acceptance: "+label);}
        var reference=new ReferenceRevision(Guid.NewGuid(),Guid.NewGuid());
        ReferenceKey R(string code)=>new(reference,code);
        var unit=new PriceUnit(R("usd"),R("energy"),D("1"));
        var price=ExactUnitPrice.FromPrice(new(D("1"),unit));
        var basis=ContractYearBasis.AnniversaryStep;

        Check("contract years change on the explicit anniversary operating date",()=>{
            var first=new DateOnly(2010,7,19);
            Equal(ContractYears.Exponent(first,first,basis),0);
            Equal(ContractYears.Exponent(first,new(2011,7,18),basis),0);
            Equal(ContractYears.Exponent(first,new(2011,7,19),basis),1);
            Equal(ContractYears.Exponent(first,new(2030,7,18),basis),19);
            Equal(ContractYears.Exponent(first,new(2030,7,19),basis),20);
        });
        Check("invalid contract-year dates and unsupported basis never choose a default",()=>{
            Reject("contract_year_basis",()=>ContractYears.Exponent(new(2024,2,29),new(2025,3,1),basis));
            Reject("contract_year_basis",()=>ContractYears.Exponent(new(2025,7,19),new(2025,7,18),basis));
            Reject("contract_year_basis",()=>ContractYears.Exponent(new(2025,1,1),new(2026,1,1),(ContractYearBasis)0));
            Reject("contract_year_basis",()=>ContractYears.Exponent(new(2025,1,1),new(2026,1,1),(ContractYearBasis)2));
        });
        Check("twenty annual degradation and escalation factors remain exact",()=>{
            // Independently calculated with Python fractions.Fraction; no production power routine supplies the expectations.
            var degradation=D("0.995").Power(20);
            Fraction(degradation.Numerator.ToString(),degradation.Denominator.ToString(),
                "9485528389644374568132540373419686462887596001","10485760000000000000000000000000000000000000000");
            var escalation=ContractFactors.Escalate(price,D("0.025"),basis,new(2010,7,19),new(2030,7,19));
            Fraction(escalation.Numerator,escalation.Denominator,
                "180167782956420929503029846064801","109951162777600000000000000000000");
            Equal(escalation.Unit,unit);
        });
        Check("zero and negative escalation rates retain exact prices and anniversary timing",()=>{
            var initial=new DateOnly(2025,8,9);
            var before=ContractFactors.Escalate(price,D("0.25"),basis,initial,new(2026,8,8));
            Fraction(before.Numerator,before.Denominator,"1","1");
            var zero=ContractFactors.Escalate(price,D("0"),basis,initial,new(2045,8,9));
            Fraction(zero.Numerator,zero.Denominator,"1","1");
            var negative=ContractFactors.Escalate(price,D("-0.2"),basis,initial,new(2027,8,9));
            Fraction(negative.Numerator,negative.Denominator,"16","25");
            foreach(var invalid in new[]{"-1","-1.01"})
                Reject("contract_escalation",()=>ContractFactors.Escalate(price,D(invalid),basis,initial,initial));
        });
        Check("powers reject negative exponents and bounded rational overflow",()=>{
            var zero=D("0.995").Power(0);
            Equal(zero.Numerator,BigInteger.One);Equal(zero.Denominator,BigInteger.One);
            Reject("numeric_exponent",()=>D("0.995").Power(-1));
            Reject("numeric_range",()=>D("0.995").Power(1000));
        });
        return passed+VolumeChecks();
    }

    private static int VolumeChecks()
    {
        var passed=0;
        void Check(string label,Action action){action();passed++;Console.WriteLine("PASS slice 2 acceptance: "+label);}
        var f=new VolumeFixture();
        var day=new DateOnly(2031,11,1);
        var periods=f.Periods(day,60).Take(1).ToArray();var interval=periods[0].Delivery;
        var a=f.Asset("synthetic_a","200","80");var b=f.Asset("synthetic_b","100","30");
        var sa=f.Source(a);var sb=f.Source(b);
        var live=f.Context(interval.Start.AddDays(-1));
        var delivered=f.Context(interval.End.AddHours(1));
        AssetVolume Actual(GenerationAsset asset,VolumeSourceDefinition source,string amount)=>
            AssetVolumes.Compute(source,asset,QuantityStage.Generated,periods,f.Forecast(source,periods,"0.5"),
                f.Meter(source,periods,amount,interval.End),delivered,f.Energy).Single();

        Check("multiple whole assets normalize once by their summed basis and contract MW scales once",()=>{
            var av=Actual(a,sa,"50");var bv=Actual(b,sb,"30");
            var quantity=CapacityNormalization.PerUnit([a,b],[av,bv],interval,QuantityStage.Generated,delivered);
            Fraction(quantity.Aggregate.Numerator,quantity.Aggregate.Denominator,"80","1");
            Fraction(quantity.CapacityBasis.Numerator,quantity.CapacityBasis.Denominator,"300","1");
            Fraction(quantity.PerUnit.Numerator,quantity.PerUnit.Denominator,"4","15");
            var contracted=quantity.PerUnit.Scale(D("80"));
            Fraction(contracted.Numerator,contracted.Denominator,"64","3");
            var contributions=quantity.Contributions.ToDictionary(c=>c.Volume.Asset.Code);
            Fraction(contributions[a.Key.Code].PerUnit.Numerator,contributions[a.Key.Code].PerUnit.Denominator,"1","6");
            Fraction(contributions[b.Key.Code].PerUnit.Numerator,contributions[b.Key.Code].PerUnit.Denominator,"1","10");
            Equal(contributions.Values.Select(c=>c.PerUnit).Aggregate((x,y)=>x.Add(y)),quantity.PerUnit);
            Equal(quantity.State,VolumeState.Metered);
        });
        Check("one prepared asset set bounds repeated native normalization allocation",()=>{
            ImmutableArray<GenerationAsset> assets=[a,b];ImmutableArray<AssetVolume> volumes=[Actual(a,sa,"50"),Actual(b,sb,"30")];
            long Measure(bool prepare)
            {
                var before=GC.GetAllocatedBytesForCurrentThread();
                var prepared=prepare?CapacityNormalization.Prepare(assets,delivered):null;
                for(var n=0;n<1000;n++)
                {
                    var normalized=prepared is null?CapacityNormalization.PerUnit(assets,volumes,interval,QuantityStage.Generated,delivered):
                        prepared.PerUnit(volumes,interval,QuantityStage.Generated);
                    Fraction(normalized.PerUnit.Numerator,normalized.PerUnit.Denominator,"4","15");
                }
                return GC.GetAllocatedBytesForCurrentThread()-before;
            }
            Measure(false);Measure(true);
            var independent=Measure(false);var batch=Measure(true);
            Console.WriteLine($"Normalization allocation: 1,000 rows, two assets; independent {independent} bytes; prepared {batch} bytes.");
            if(batch>independent*0.6)throw new Exception("Prepared normalization must avoid repeating immutable asset admission and basis construction for every native row.");
        });
        Check("explicit zero retains its asset denominator while a missing or duplicate contribution refuses",()=>{
            var av=Actual(a,sa,"50");var zero=Actual(b,sb,"0");
            var quantity=CapacityNormalization.PerUnit([a,b],[av,zero],interval,QuantityStage.Generated,delivered);
            Fraction(quantity.PerUnit.Numerator,quantity.PerUnit.Denominator,"1","6");
            Equal(quantity.Contributions.Length,2);
            Reject("ppa_normalization",()=>CapacityNormalization.PerUnit([a,b],[av],interval,QuantityStage.Generated,delivered));
            Reject("ppa_normalization",()=>CapacityNormalization.PerUnit([a,b],[av,av],interval,QuantityStage.Generated,delivered));
            Reject("ppa_normalization",()=>CapacityNormalization.PerUnit([a,a],[av,av],interval,QuantityStage.Generated,delivered));
        });
        Check("capacity basis versions switch at operating midnight and accreditation is a numerator",()=>{
            var boundary=f.Clock.StartOfDay(day.AddDays(1)).ToUniversalTime();
            var changing=a with {Versions=[a.Versions[0] with {To=boundary},a.Versions[0] with {From=boundary,CapacityBasis=D("400"),AccreditedCapacity=D("120")}]};
            var before=CapacityNormalization.AccreditedPerUnit([changing,b],day,live);
            Fraction(before.PerUnit.Numerator,before.PerUnit.Denominator,"11","30");
            var after=CapacityNormalization.AccreditedPerUnit([changing,b],day.AddDays(1),live);
            Fraction(after.PerUnit.Numerator,after.PerUnit.Denominator,"3","10");
            Equal(after.Versions.Single(v=>v.Asset==a.Key).From,boundary);
            var next=f.Periods(day.AddDays(1),60).Take(1).ToArray();
            var changed=AssetVolumes.Compute(sa,changing,QuantityStage.Generated,next,f.Forecast(sa,next,"0.5"),f.EmptyMeter(sa),live,f.Energy).Single();
            Fraction(changed.Energy.Numerator,changed.Energy.Denominator,"200","1");
            Equal(changed.AssetVersion.From,boundary);
            Reject("asset_version",()=>CapacityNormalization.AccreditedPerUnit([a with {Versions=[a.Versions[0] with {AccreditedCapacity=null}]}],day,live));
        });
        Check("prepared normalization retains version boundaries, gaps and contribution context checks",()=>{
            var boundary=f.Clock.StartOfDay(day.AddDays(1)).ToUniversalTime();
            var changing=a with {Versions=[a.Versions[0] with {To=boundary},a.Versions[0] with {From=boundary,CapacityBasis=D("400")}]};
            var context=f.Context(boundary.AddDays(2));var next=f.Periods(day.AddDays(1),60).Take(1).ToArray();
            AssetVolume Value(GenerationAsset asset,VolumeSourceDefinition source,string amount,PowerInterval[]? cells=null)
            {
                var selected=cells??periods;
                return AssetVolumes.Compute(source,asset,QuantityStage.Generated,selected,f.Forecast(source,selected,"0.5"),
                    f.Meter(source,selected,amount,selected[0].End),context,f.Energy).Single();
            }
            ImmutableArray<AssetVolume> before=[Value(changing,sa,"50"),Value(b,sb,"30")];
            ImmutableArray<AssetVolume> after=[Value(changing,sa,"50",next),Value(b,sb,"30",next)];
            var prepared=CapacityNormalization.Prepare([changing,b],context);
            void CheckValue(ImmutableArray<AssetVolume> values,string numerator,string denominator)
            {
                var normalized=prepared.PerUnit(values,values[0].Interval,QuantityStage.Generated);
                Fraction(normalized.PerUnit.Numerator,normalized.PerUnit.Denominator,numerator,denominator);
            }
            CheckValue(before,"4","15");CheckValue(after,"4","25");CheckValue(before,"4","15");
            var across=before[0].Interval with {End=next[0].End};
            Reject("asset_version",()=>prepared.PerUnit([..before.Select(v=>v with{Interval=across})],across,QuantityStage.Generated));
            CheckValue(after,"4","25");
            var gap=CapacityNormalization.Prepare([changing with {Versions=[changing.Versions[0],changing.Versions[1] with{From=f.Clock.StartOfDay(day.AddDays(2)).ToUniversalTime()}]},b],context);
            Reject("asset_version",()=>gap.PerUnit(after,after[0].Interval,QuantityStage.Generated));
            var restored=gap.PerUnit(before,before[0].Interval,QuantityStage.Generated);
            Fraction(restored.PerUnit.Numerator,restored.PerUnit.Denominator,"4","15");
            var zero=before[0].Interval with {Start=boundary,End=boundary};
            Reject("asset_version",()=>prepared.PerUnit([..before.Select(v=>v with{Interval=zero})],zero,QuantityStage.Generated));
            Reject("ppa_normalization",()=>CheckValue(after.SetItem(0,after[0] with{AssetVersion=before[0].AssetVersion}),"4","25"));
            Reject("ppa_normalization",()=>CheckValue(after.SetItem(0,after[0] with{Context=context with{KnownAt=context.KnownAt.AddMinutes(1)}}),"4","25"));
            Reject("ppa_normalization",()=>prepared.PerUnit([after[0],after[0]],after[0].Interval,QuantityStage.Generated));
        });
        Check("forecast reductions and generated-to-delivered loss each apply once; meters skip forecast reductions",()=>{
            var source=sa with {Loss=D("0.1"),Availability=new(D("0.8"),false),Curtailment=new(D("0.25"),false)};
            var forecast=f.Forecast(source,periods,"0.5");
            var adjusted=AssetVolumes.Compute(source,a,QuantityStage.Delivered,periods,forecast,f.EmptyMeter(source),live,f.Energy).Single();
            Fraction(adjusted.Energy.Numerator,adjusted.Energy.Denominator,"54","1");
            Equal(adjusted.SourceStage,QuantityStage.Generated);Equal(adjusted.Stage,QuantityStage.Delivered);
            var reflected=source with {Availability=new(null,true),Curtailment=new(null,true)};
            var reflectedValue=AssetVolumes.Compute(reflected,a,QuantityStage.Delivered,periods,forecast,f.EmptyMeter(source),live,f.Energy).Single();
            Fraction(reflectedValue.Energy.Numerator,reflectedValue.Energy.Denominator,"90","1");
            var metered=AssetVolumes.Compute(source,a,QuantityStage.Delivered,periods,forecast,f.Meter(source,periods,"50",interval.End),delivered,f.Energy).Single();
            Fraction(metered.Energy.Numerator,metered.Energy.Denominator,"45","1");
            var deliveredSource=source with {MeteredStage=QuantityStage.Delivered};
            var unchanged=AssetVolumes.Compute(deliveredSource,a,QuantityStage.Delivered,periods,forecast,f.Meter(source,periods,"50",interval.End),delivered,f.Energy).Single();
            Fraction(unchanged.Energy.Numerator,unchanged.Energy.Denominator,"50","1");
            var reverse=AssetVolumes.Compute(deliveredSource,a,QuantityStage.Generated,periods,forecast,f.Meter(source,periods,"50",interval.End),delivered,f.Energy).Single();
            Fraction(reverse.Energy.Numerator,reverse.Energy.Denominator,"500","9");
        });
        Check("declared forecast degradation uses an exact twenty-year factor without reducing meters",()=>{
            var selected=f.Periods(new(2030,7,19),60).Take(1).ToArray();var one=f.Asset("synthetic_one","1","1");
            var source=f.Source(one) with {Degradation=new(D("0.005"),ContractYearBasis.AnniversaryStep,new(2010,7,19)),DegradationReflected=false};
            var context=f.Context(selected[0].Start.AddDays(-1));
            var value=AssetVolumes.Compute(source,one,QuantityStage.Generated,selected,f.Forecast(source,selected,"1"),f.EmptyMeter(source),context,f.Energy).Single();
            Fraction(value.Energy.Numerator,value.Energy.Denominator,
                "9485528389644374568132540373419686462887596001","10485760000000000000000000000000000000000000000");
            var reflected=source with {Degradation=null,DegradationReflected=true};
            var already=AssetVolumes.Compute(reflected,one,QuantityStage.Generated,selected,f.Forecast(source,selected,"1"),f.EmptyMeter(source),context,f.Energy).Single();
            Fraction(already.Energy.Numerator,already.Energy.Denominator,"1","1");
        });
        Check("hourly and quarter-hour forecasts retain the exact 23-hour and 25-hour delivery duration",()=>{
            var one=f.Asset("synthetic_dst","1","1");
            foreach(var (date,hours) in new[]{(new DateOnly(2027,3,14),23),(new DateOnly(2026,11,1),25)})
                foreach(var minutes in new[]{60,15}){
                    var selected=f.Periods(date,minutes);var source=f.Source(one) with {IntervalMinutes=minutes};
                    var rows=AssetVolumes.Compute(source,one,QuantityStage.Generated,selected,f.Forecast(source,selected,"1"),f.EmptyMeter(source),f.Context(selected[0].Start.AddDays(-1)),f.Energy);
                    Equal(rows.Length,hours*60/minutes);
                    var total=rows.Select(r=>r.Energy).Aggregate((x,y)=>x.Add(y));
                    Fraction(total.Numerator,total.Denominator,hours.ToString(),"1");
                    if(minutes==15)foreach(var row in rows)Fraction(row.Energy.Numerator,row.Energy.Denominator,"1","4");
                }
        });
        Check("meter deadlines are elapsed UTC minutes across DST with equality already due",()=>{
            var selected=f.Periods(new(2027,3,13),60).TakeLast(1).ToArray();
            var source=sa with {MeterDue=new("meter_due_v1",1440)};
            var deadline=new DateTimeOffset(2027,3,15,5,0,0,TimeSpan.Zero);
            Equal(selected[0].End.AddMinutes(1440),deadline);
            var pending=f.Context(new DateTimeOffset(2027,3,15,4,30,0,TimeSpan.Zero));
            var forecast=f.Forecast(source,selected,"0.5");
            var estimate=AssetVolumes.Compute(source,a,QuantityStage.Generated,selected,forecast,f.EmptyMeter(source),pending,f.Energy).Single();
            Equal(estimate.State,VolumeState.Estimated);Equal(estimate.MeterDueAt,deadline);
            Reject("actuals_required",()=>AssetVolumes.Compute(source,a,QuantityStage.Generated,selected,forecast,f.EmptyMeter(source),pending with {Policy=PendingActualsPolicy.RequireActuals},f.Energy));
            foreach(var policy in new[]{PendingActualsPolicy.RequireActuals,PendingActualsPolicy.EstimateUntilActual})
                Reject("meter_actual_missing",()=>AssetVolumes.Compute(source,a,QuantityStage.Generated,selected,forecast,f.EmptyMeter(source),pending with {ValuationAt=deadline,Policy=policy},f.Energy));
            var lateMeter=f.Meter(source,selected,"73",deadline.AddHours(1));
            var revised=AssetVolumes.Compute(source,a,QuantityStage.Generated,selected,forecast,lateMeter,pending with {ValuationAt=deadline,KnownAt=deadline.AddHours(2)},f.Energy).Single();
            Fraction(revised.Energy.Numerator,revised.Energy.Denominator,"73","1");
        });
        Check("meter corrections obey certified cutoff and later research never replaces a live forecast",()=>{
            var first=f.Meter(sa,periods,"50",interval.End.AddMinutes(10));
            var correction=first.Cells[0] with {Value=D("60"),Revision=2,KnownAt=interval.End.AddHours(2),ContextId=Guid.NewGuid()};
            var meter=first with {Cells=first.Cells.Add(correction)};
            var forecast=f.Forecast(sa,periods,"0.5");
            var earlier=AssetVolumes.Compute(sa,a,QuantityStage.Generated,periods,forecast,meter,delivered,f.Energy).Single();
            Fraction(earlier.Energy.Numerator,earlier.Energy.Denominator,"50","1");Equal(earlier.Revision,1);
            var later=AssetVolumes.Compute(sa,a,QuantityStage.Generated,periods,forecast,meter,delivered with {KnownAt=interval.End.AddHours(3)},f.Energy).Single();
            Fraction(later.Energy.Numerator,later.Energy.Denominator,"60","1");Equal(later.Revision,2);Equal(later.ContextId,correction.ContextId);
            var stillLive=AssetVolumes.Compute(sa,a,QuantityStage.Generated,periods,forecast,meter,live with {KnownAt=interval.End.AddHours(3)},f.Energy).Single();
            Fraction(stillLive.Energy.Numerator,stillLive.Energy.Denominator,"100","1");Equal(stillLive.State,VolumeState.Forecast);
        });
        Check("pending estimates remain explicit in normalization and missing forecast does not become zero",()=>{
            var source=sa with {MeterDue=new("meter_due_v1",120)};
            var estimate=AssetVolumes.Compute(source,a,QuantityStage.Generated,periods,f.Forecast(source,periods,"0.5"),f.EmptyMeter(source),delivered,f.Energy).Single();
            var quantity=CapacityNormalization.PerUnit([a,b],[estimate,Actual(b,sb,"30")],interval,QuantityStage.Generated,delivered);
            Equal(quantity.State,VolumeState.Estimated);Equal(quantity.Contributions.Count(c=>c.Volume.State==VolumeState.Estimated),1);
            Reject("volume_missing",()=>AssetVolumes.Compute(source,a,QuantityStage.Generated,periods,f.Forecast(source,periods,"0.5") with {Cells=[]},f.EmptyMeter(source),delivered,f.Energy));
        });
        Check("forecast ranges and explicit adjustment dispositions reject instead of clamping or guessing",()=>{
            AssetVolume Forecast(VolumeSourceDefinition source,string factor)=>AssetVolumes.Compute(source,a,QuantityStage.Generated,periods,f.Forecast(source,periods,factor),f.EmptyMeter(source),live,f.Energy).Single();
            foreach(var value in new[]{"-0.1","1.1"}){
                Reject("volume_range",()=>Forecast(sa,value));
                Reject("volume_range",()=>Forecast(sa with {Availability=new(D(value),false)},"0.5"));
                Reject("volume_range",()=>Forecast(sa with {Curtailment=new(D(value),false)},"0.5"));
            }
            foreach(var zero in new[]{Forecast(sa,"0"),Forecast(sa with {Availability=new(D("0"),false)},"1"),Forecast(sa with {Curtailment=new(D("1"),false)},"1")})
                Fraction(zero.Energy.Numerator,zero.Energy.Denominator,"0","1");
            Reject("volume_source",()=>Forecast(sa with {Availability=new(null,false)},"0.5"));
            Reject("volume_source",()=>Forecast(sa with {Availability=new(D("1"),true)},"0.5"));
            Reject("volume_source",()=>Forecast(sa with {Degradation=null,DegradationReflected=false},"0.5"));
            foreach(var loss in new[]{"-0.1","1"})Reject("volume_range",()=>Forecast(sa with {Loss=D(loss)},"0.5"));
            Reject("volume_range",()=>AssetVolumes.Compute(sa,a,QuantityStage.Generated,periods,f.Forecast(sa,periods,"0.5"),f.Meter(sa,periods,"-1",interval.End),delivered,f.Energy));
        });
        Check("asset versions require complete nonoverlapping midnight coverage with positive basis",()=>{
            AssetVolume Forecast(GenerationAsset asset)=>AssetVolumes.Compute(sa,asset,QuantityStage.Generated,periods,f.Forecast(sa,periods,"0.5"),f.EmptyMeter(sa),live,f.Energy).Single();
            Reject("asset_version",()=>Forecast(a with {Versions=[]}));
            Reject("asset_version",()=>Forecast(a with {Versions=[a.Versions[0],a.Versions[0]]}));
            Reject("asset_version",()=>Forecast(a with {Versions=[a.Versions[0] with {From=interval.Start.AddMinutes(30)}]}));
            foreach(var basis in new[]{"0","-1"})Reject("asset_version",()=>Forecast(a with {Versions=[a.Versions[0] with {CapacityBasis=D(basis)}]}));
        });
        Check("definition reference and calculation-context pins cannot be substituted",()=>{
            var forecast=f.Forecast(sa,periods,"0.5");
            Reject("ppa_definition_revision",()=>AssetVolumes.Compute(sa,a,QuantityStage.Generated,periods,forecast,f.EmptyMeter(sa),live with {Definitions=new(Guid.NewGuid(),Guid.NewGuid())},f.Energy));
            Reject("ppa_reference_revision",()=>AssetVolumes.Compute(sa,a,QuantityStage.Generated,periods,forecast,f.EmptyMeter(sa),live with {Reference=new(Guid.NewGuid(),Guid.NewGuid())},f.Energy));
            Reject("volume_source",()=>AssetVolumes.Compute(sa,a,QuantityStage.Generated,periods,forecast with {Id=Guid.NewGuid()},f.EmptyMeter(sa),live,f.Energy));
            Reject("volume_source",()=>AssetVolumes.Compute(sa,a,QuantityStage.Generated,periods,forecast with {Clock=f.R("other_clock")},f.EmptyMeter(sa),live,f.Energy));
            var av=Actual(a,sa,"50");var bv=Actual(b,sb,"30");
            Reject("ppa_normalization",()=>CapacityNormalization.PerUnit([a,b],[av,bv],interval,QuantityStage.Generated,delivered with {KnownAt=delivered.KnownAt.AddHours(1)}));
        });
        Check("meter due rule and offset are explicit and bounded from zero through ninety elapsed days",()=>{
            var forecast=f.Forecast(sa,periods,"0.5");
            foreach(var deadline in new[]{new MeterDeadline("unsupported",0),new("meter_due_v1",-1),new("meter_due_v1",129601)})
                Reject("volume_source",()=>AssetVolumes.Compute(sa with {MeterDue=deadline},a,QuantityStage.Generated,periods,forecast,f.EmptyMeter(sa),delivered,f.Energy));
            var ninety=sa with {MeterDue=new("meter_due_v1",129600)};
            var pending=AssetVolumes.Compute(ninety,a,QuantityStage.Generated,periods,forecast,f.EmptyMeter(sa),delivered,f.Energy).Single();
            Equal(pending.MeterDueAt,interval.End.AddDays(90));Equal(pending.State,VolumeState.Estimated);
        });
        Check("contract profiles are already per contract MW with no asset normalization",()=>{
            foreach(var minutes in new[]{60,15}){
                var profile=new ContractProfileQuantity(f.Clock,minutes,new(BlockMembership.All,null,[]),f.Capacity,f.Energy);
                foreach(var (month,date,hours) in new[]{(new DateOnly(2027,3,1),new DateOnly(2027,3,14),23),(new DateOnly(2026,11,1),new DateOnly(2026,11,1),25)}){
                    var rows=profile.Rows(month).Where(r=>r.Interval.OperatingDate==date).ToArray();
                    Equal(rows.Length,hours*60/minutes);
                    foreach(var row in rows){Equal(row.State,VolumeState.Contractual);Equal(row.Contributions.Length,0);}
                    var total=rows.Select(r=>r.PerUnit).Aggregate((x,y)=>x.Add(y));
                    Fraction(total.Numerator,total.Denominator,hours.ToString(),"1");
                    var contractTotal=total.Scale(D("80"));
                    Fraction(contractTotal.Numerator,contractTotal.Denominator,(hours*80).ToString(),"1");
                }
            }
        });
        Check("location assignments require exactly one full interval and retain one explicit adjustment",()=>{
            var assignment=new LocationAssignment("synthetic_location",a.Location,ValuedAttribute.Energy,interval.Start,interval.End,new(f.Definitions,"synthetic_curve"),new(f.R("usd"),f.Energy.Key,D("1")),D("-2.5"));
            var selected=LocationValuation.Partition([assignment],a.Location,ValuedAttribute.Energy,[interval],f.Definitions);
            Equal(selected.Single(),assignment);Equal(selected[0].Adjustment,D("-2.5"));
            Reject("location_valuation_missing",()=>LocationValuation.Partition([],a.Location,ValuedAttribute.Energy,[interval],f.Definitions));
            Reject("location_valuation_overlap",()=>LocationValuation.Partition([assignment,assignment with {Code="overlap"}],a.Location,ValuedAttribute.Energy,[interval],f.Definitions));
            Reject("location_assignment",()=>LocationValuation.Partition([assignment with {To=interval.Start.AddMinutes(30)},assignment with {Code="next",From=interval.Start.AddMinutes(30)}],a.Location,ValuedAttribute.Energy,[interval],f.Definitions));
        });
        Check("asset location and clock keys cannot be empty even within the correct revision",()=>{
            foreach(var asset in new[]{a with {Location=f.R("")},a with {Clock=f.R("")}}){
                var source=sa with {Clock=asset.Clock};
                Reject("missing_reference",()=>AssetVolumes.Compute(source,asset,QuantityStage.Generated,periods,f.Forecast(source,periods,"0.5"),f.EmptyMeter(source),live,f.Energy));
            }
        });
        Check("the quantity adapter captures immutable request-owned rows",()=>{
            var normalized=CapacityNormalization.PerUnit([a,b],[Actual(a,sa,"50"),Actual(b,sb,"30")],interval,QuantityStage.Generated,delivered);
            var mutable=new List<NormalizedQuantity>{normalized};
            var source=new AssetVolumeQuantity(mutable);
            mutable.Clear();
            var retained=source.Rows(new(day.Year,day.Month,1));
            Equal(retained.Length,1);Equal(retained[0].PerUnit,normalized.PerUnit);
        });
        Check("location lookup rejects empty exact reference and curve identities",()=>{
            var assignment=new LocationAssignment("synthetic_location",a.Location,ValuedAttribute.Energy,interval.Start,interval.End,new(f.Definitions,"synthetic_curve"),new(f.R("usd"),f.Energy.Key,D("1")),D("0"));
            var emptyLocation=f.R("");
            Reject("missing_reference",()=>LocationValuation.Partition([assignment with {Location=emptyLocation}],emptyLocation,ValuedAttribute.Energy,[interval],f.Definitions));
            Reject("location_assignment",()=>LocationValuation.Partition([assignment with {Curve=new(f.Definitions,"")}],a.Location,ValuedAttribute.Energy,[interval],f.Definitions));
        });
        Check("revision order cannot contradict certified knowledge ordering",()=>{
            var first=f.Meter(sa,periods,"50",interval.End.AddHours(2));
            var earlierCorrection=first.Cells[0] with {Value=D("60"),Revision=2,KnownAt=interval.End.AddHours(1),ContextId=Guid.NewGuid()};
            var inconsistent=first with {Cells=first.Cells.Add(earlierCorrection)};
            Reject("volume_grid",()=>AssetVolumes.Compute(sa,a,QuantityStage.Generated,periods,f.Forecast(sa,periods,"0.5"),inconsistent,delivered with {KnownAt=interval.End.AddHours(3)},f.Energy));
            var two=f.Periods(day,60).Take(2).ToArray();
            var sameRevision=f.Meter(sa,two,"50",two[1].End);
            sameRevision=sameRevision with {Cells=sameRevision.Cells.SetItem(1,sameRevision.Cells[1] with {KnownAt=two[1].End.AddMinutes(1)})};
            Reject("volume_grid",()=>AssetVolumes.Compute(sa,a,QuantityStage.Generated,two,f.Forecast(sa,two,"0.5"),sameRevision,f.Context(two[1].End.AddHours(1)),f.Energy));
        });
        Check("contract profiles refuse missing clock or hours as explicit unsupported terms",()=>{
            Reject("ppa_term_unsupported",()=>new ContractProfileQuantity(f.Clock,60,null!,f.Capacity,f.Energy).Rows(new(2031,11,1)));
            Reject("ppa_term_unsupported",()=>new ContractProfileQuantity(null!,60,new(BlockMembership.All,null,[]),f.Capacity,f.Energy).Rows(new(2031,11,1)));
        });
        Check("a monthly contract profile accepts a multi-year holiday catalog and still refuses duplicate dates",()=>{
            var holidays=Enumerable.Range(0,500).Select(i=>new DateOnly(2030,1,1).AddDays(i)).ToImmutableArray();
            var profile=new ContractProfileQuantity(f.Clock,60,new(BlockMembership.All,null,holidays),f.Capacity,f.Energy);
            var rows=profile.Rows(new(2031,11,1));
            Equal(rows.Length,721);
            var total=rows.Select(r=>r.PerUnit).Aggregate((x,y)=>x.Add(y));
            Fraction(total.Numerator,total.Denominator,"721","1");
            Reject("power_holidays",()=>new ContractProfileQuantity(f.Clock,60,new(BlockMembership.All,null,[..holidays,holidays[0]]),f.Capacity,f.Energy).Rows(new(2031,11,1)));
        });
        return passed;
    }

    private sealed class VolumeFixture
    {
        internal readonly ReferenceRevision Reference=new(Guid.NewGuid(),Guid.NewGuid());
        internal readonly DefinitionRevision Definitions=new(Guid.NewGuid(),Guid.NewGuid());
        internal readonly PowerClock Clock=PowerClock.Capture(TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
        internal ReferenceKey R(string code)=>new(Reference,code);
        internal QuantitySpecification Energy=>new(R("mwh"),R("synthetic_power"),R("energy_amount"),R("megawatt_hour"),R("energy"));
        internal QuantitySpecification Capacity=>new(R("mw"),R("synthetic_power"),R("capacity_basis"),R("megawatt"),R("power"));
        internal GenerationAsset Asset(string code,string capacity,string accredited)=>new(new(Definitions,code),R("synthetic_location"),Capacity,R("synthetic_eastern"),Clock,
            [new(Clock.StartOfDay(new(2010,1,1)).ToUniversalTime(),Clock.StartOfDay(new(2060,1,1)).ToUniversalTime(),D(capacity),D(accredited))]);
        internal VolumeSourceDefinition Source(GenerationAsset asset)=>new(new(Definitions,asset.Key.Code+"_source"),asset.Key,Guid.NewGuid(),QuantityStage.Generated,
            Guid.NewGuid(),QuantityStage.Generated,D("0"),new(null,true),new(null,true),null,true,VolumeRange.GenerationNonNegative,R("synthetic_eastern"),60,new("meter_due_v1",0));
        internal PowerInterval[] Periods(DateOnly day,int minutes)=>PowerHours.Compile(day,day.AddDays(1),Clock,TimeOfUse.All(minutes),[],minutes);
        internal VolumeContext Context(DateTimeOffset valuation)=>new(Definitions,Reference,valuation,valuation,PendingActualsPolicy.EstimateUntilActual);
        internal QuantitySeries Forecast(VolumeSourceDefinition source,IEnumerable<PowerInterval> periods,string value)=>new(source.ForecastSeries,"capacity_factor_forecast",source.Clock,source.IntervalMinutes,null,
            [..periods.Select(p=>new QuantityCell(p.Start,p.End,D(value),1,new(2009,1,1,0,0,0,TimeSpan.Zero),Guid.NewGuid()))]);
        internal QuantitySeries EmptyMeter(VolumeSourceDefinition source)=>new(source.MeteredSeries,"metered_energy",source.Clock,source.IntervalMinutes,Energy.Key,[]);
        internal QuantitySeries Meter(VolumeSourceDefinition source,IEnumerable<PowerInterval> periods,string value,DateTimeOffset known)=>new(source.MeteredSeries,"metered_energy",source.Clock,source.IntervalMinutes,Energy.Key,
            [..periods.Select(p=>new QuantityCell(p.Start,p.End,D(value),1,known,Guid.NewGuid()))]);
    }
}
