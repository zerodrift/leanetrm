using System.Collections.Immutable;
using OpenEtrm.Api;
using OpenEtrm.Shared.Temporal;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Trading;

internal static class PpaLimitChecks
{
    public static void Run()
    {
        var reference=new ReferenceRevision(Guid.NewGuid(),Guid.NewGuid());var definitions=new DefinitionRevision(Guid.NewGuid(),Guid.NewGuid());
        ReferenceKey R(string code)=>new(reference,code);
        var clock=PowerClock.Capture(TimeZoneInfo.Utc);var one=ExactDecimal.Parse("1");
        var payment=new PaymentRule(PaymentFrequency.Monthly,PaymentDateRule.DayOfFollowingMonth,null,10,null,ShortMonth.Reject,DateRoll.Unadjusted,null,PaymentRule.Implementation);
        PpaInstrument Instrument(DateOnly first,DateOnly last,int minutes=60,bool capacity=false,bool peak=false,bool volume=false)
        {
            var delivery=new PpaDelivery(R("clock"),minutes,first,last);AssetKey[] assets=capacity||volume?[new(definitions,"asset_a"),new(definitions,"asset_b")]:[];
            var role=capacity?PpaRole.CapacityDelivery:PpaRole.EnergyDelivery;
            PpaQuantity quantity=capacity?new AccreditedQuantity([..assets]):volume?new VolumeQuantity([new(definitions,"source_a"),new(definitions,"source_b")],QuantityStage.Generated,R("mwh")):new ProfileQuantity(R("mw"),R("mwh"),new(peak?BlockMembership.Peak:BlockMembership.All,peak?new(7,23,false):null,[]));
            var schedule=new IntervalScheduleParameters(delivery,clock,capacity?IntervalScheduleCadence.OperatingMonths:IntervalScheduleCadence.NativeIntervals,
                capacity?null:quantity is ProfileQuantity p?p.Hours:new(BlockMembership.All,null,[]));
            var normalization=new PpaNormalization([..assets],R("mw"),assets.Length==0?PpaNormalizationPolicy.ContractUnit:PpaNormalizationPolicy.SumCapacityInForce);
            PpaSideTerms Terms(PpaQuantity q)=>new(delivery,q,R("usd"),new(one,R("mw")),payment,schedule,R(capacity?"mw_day":"mwh"),normalization,
                q is SlotQuantity linked?[..linked.Slots.Select(slot=>new ScheduleInputPort(slot,R(capacity?"mw_day":"mwh"),schedule))]:[]);
            Side physical=capacity?new CapacityDeliverySide(Guid.NewGuid(),Terms(quantity),R("mw_day"),"mw_day_per_operating_day_v1"):new PowerPhysicalSide(Guid.NewGuid(),Terms(quantity),R("node"));
            var cash=new FixedSide(Guid.NewGuid(),Terms(new SlotQuantity([new(role)])),new FlatPrice(new UnitPrice(one,new PriceUnit(R("usd"),R(capacity?"mw_day":"mwh"),one))));
            return new(Guid.NewGuid(),definitions,reference,[new(new(role),physical,one),new(new(capacity?PpaRole.CapacityPayment:PpaRole.StrikePayment),cash,ExactDecimal.Parse("-1"))],false,"ppa_instrument_v2");
        }
        void Work(IEnumerable<(PpaInstrument,DeliveryCoverage)> scopes,ImmutableHashSet<DateOnly>? detail=null)=>PpaCompiler.Work(scopes,detail??[],_=>clock);
        void Refuse(Action action,string name){try{action();throw new Exception(name+" was admitted");}catch(MarketDataException e)when(e.Code=="ppa_limit"){Console.WriteLine("PASS PPA limits: "+name);}}
        var assets=Enumerable.Range(0,100).Select(n=>"synthetic_asset_"+n).ToArray();
        var sources=Enumerable.Range(0,100).Select(n=>"synthetic_source_"+n).ToArray();
        var dateRule=new PpaPaymentInput("monthly","day_of_following_month",null,10,null,"reject","unadjusted",null,PaymentRule.Implementation);
        var mixed=new PpaTermsInput(definitions.RevisionId,reference.CatalogId,reference.RevisionId,new("clock",15,"2000-01-01","2010-01-01"),
            "usd","mw",false,PpaInstrument.Implementation,assets,
            [new("energy_delivery",0,"1",new("energy",new("volume",sources,"generated",null,null,null,[],[]),dateRule,"node",null,null,null)),
             new("capacity_delivery",0,"1",new("capacity",new("accredited",[],null,null,null,null,[],assets),dateRule,null,null,null,
                new("mw_day",null,null,null,null,"mw_day_per_operating_day_v1")))]);
        PpaCompiler.PreflightWork([(mixed,"2000-01-01T00:00:00Z","2000-01-02T00:00:00Z","short_coverage")]);
        // Capacity adds a daily source; it cannot turn a separately declared
        // native generation source's ten-year, 100-asset work into daily work.
        Refuse(()=>PpaCompiler.PreflightWork([(mixed,"2000-01-01T00:00:00Z","2010-01-01T00:00:00Z","full_retained_term")]),
            "mixed native and capacity sources retain the complete native preparation budget");
        Refuse(()=>RetainedScheduleLimits.RequireFullPreparation(mixed,clock),
            "a short booking coverage cannot bypass the retained full-term source-preparation limit");
        var first=new DateTimeOffset(2000,1,1,0,0,0,TimeSpan.Zero);var coverage=new DeliveryCoverage(first,first.AddHours(250_000));
        var units=Enumerable.Range(0,4).Select(_=>(Instrument(new(2000,1,1),new(2030,1,1),volume:true),coverage)).ToArray();
        // Four distinct 250,000-hour, two-side schedules exactly equal2m evaluated
        // rows. Their two contributing assets do not double normalized side rows.
        Work(units);Work(units.Concat(units));
        Console.WriteLine("PASS PPA limits: exact two-million evaluated rows, multiple assets and duplicate unit reuse");
        Refuse(()=>Work(units.Skip(1).Prepend((units[0].Item1,new(coverage.From,coverage.To.AddHours(1))))),"one native interval above evaluated-row limit");
        var leap=Instrument(new(2032,1,1),new(2033,1,1));var year=new DeliveryCoverage(new(2032,1,1,0,0,0,TimeSpan.Zero),new(2033,1,1,0,0,0,TimeSpan.Zero));
        var months=Enumerable.Range(1,12).Select(m=>new DateOnly(2032,m,1)).ToImmutableHashSet();
        Work([(leap,year),(leap,year)],months);Console.WriteLine("PASS PPA limits: exactly35,136 returned rows count both bindings despite reused calculation");
        Refuse(()=>Work([(leap,year),(leap,year),(leap,year)],months),"returned-row overflow across repeated bindings");
        Work([(Instrument(new(2032,1,1),new(2033,1,1),15,capacity:true),year)],months);
        Console.WriteLine("PASS PPA limits: quarter-hour capacity still has24 monthly rows");
        var peak=Instrument(new(2032,1,1),new(2033,1,1),peak:true);
        Work(Enumerable.Repeat((peak,year),4),months);Console.WriteLine("PASS PPA limits: peak profile budgets selected hours instead of all-hours upper bound");
        var settlementSize=SettlementPreview.MaximumResponseBytes;
        if(PpaStore.Response(new{value=new string('x',settlementSize-12)},SettlementPreview.JsonOptions,settlementSize).Length!=settlementSize)throw new Exception("Settlement response boundary");
        Refuse(()=>PpaStore.Response(new{value=new string('x',settlementSize-11)},SettlementPreview.JsonOptions,settlementSize),"settlement response one byte over its independent bound");
        var size=PpaCompiler.MaximumResponseBytes;
        var exact=PpaStore.Response(new{value=new string('x',size-12)});
        if(exact.Length!=size)throw new Exception("Expected exact UTF-8 response boundary");
        Refuse(()=>PpaStore.Response(new{value=new string('x',size-11)}),"one serialized byte above response limit");
        Refuse(()=>PpaStore.Response(new{value=new string('\u00e9',size/6)}),"escaped non-ASCII bytes counted in response bound");
    }
}
