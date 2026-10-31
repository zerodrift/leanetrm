using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Trading;

// Synthetic December 2031 gas-day sides. The daily side is a hand-built
// delivery program whose quantities and prices are explicit source inputs; the
// monthly cash sides are compiled from real fixed-price payment terms.
internal static class ScheduleViewChecks
{
    private static readonly ReferenceRevision Reference=new(Guid.NewGuid(),Guid.NewGuid());
    private static readonly DefinitionRevision Definitions=new(Guid.NewGuid(),Guid.NewGuid());
    private static ReferenceKey R(string code)=>new(Reference,code);
    private static readonly DateTimeOffset From=new(2031,12,1,0,0,0,TimeSpan.Zero);
    private static readonly DateTimeOffset PayAt=new(2032,1,26,0,0,0,TimeSpan.Zero);
    private static readonly QuoteSpecification Quote=new(R("synthetic_quote"),QuoteKind.Outright,R("synthetic_usd"),R("synthetic_mmbtu"),
        ExactDecimal.Parse("1"),ExactDecimal.Parse("0.001"),ExactDecimal.Parse("0.001"),ExactDecimal.Parse("-1000"),ExactDecimal.Parse("1000"));
    private static readonly ImmutableArray<string> Levels=["month","day"];
    private static readonly ImmutableArray<(string,ScheduleViewMeasure)> Columns=
        [("quantity",ScheduleViewMeasure.Quantity),("price",ScheduleViewMeasure.Price),("amount",ScheduleViewMeasure.Amount)];
    private static ExactDecimal D(string text)=>ExactDecimal.Parse(text);
    private static ScheduleNumber N(string text)=>ScheduleNumber.Parse(text);

    internal static void Run()
    {
        var before=From.AddDays(-1);
        var daily=Daily(31,before,_=>(N("10000"),VolumeState.Contractual),_=>(N("3.412"),SchedulePriceFacts.Fixed));
        var view=ScheduleViews.Build([daily],Levels,TimeZoneInfo.Utc,before);
        var month=Row(view,"2031-12");var days=view.Rows.Where(row=>row.Level=="day").ToArray();
        Equal(month.Cells["physical.quantity"].Value,"310000");
        if(days.Length!=31||days[0].Key!="2031-12-01"||days[0].OperatingDate!="2031-12-01"||days.Any(day=>day.Parent!="2031-12")||
            month.Parent is not null||month.Start!=From||month.End!=From.AddDays(31)||days[18].Start!=From.AddDays(18)||view.Rows[0]!=month)
            throw new Exception("Daily quantities sum exactly to the month and leaf rows are keyed by operating date.");
        Pass("daily quantities sum exactly to the month");

        var cash=Cash("cash","3.412",1,before);var fee=Cash("fee","0.045",-1,before);
        view=ScheduleViews.Build([daily,cash,fee],Levels,TimeZoneInfo.Utc,before);month=Row(view,"2031-12");
        if(view.Rows.Where(row=>row.Level=="day").Any(day=>day.Cells["cash.amount"].Value is not null||day.Cells["cash.price"].Value is not null||
            !day.Cells["cash.amount"].Sources.IsEmpty))
            throw new Exception("A monthly payment side is blank on day rows.");
        Equal(month.Cells["cash.amount"].Value,"1057720");Equal(month.Cells["cash.quantity"].Value,"310000");Equal(month.Cells["cash.price"].Value,"3.412");
        Pass("a monthly payment side is blank on day rows and set on the month row");

        Equal(month.Cells["physical.price"].Value,"3.412");
        var uneven=Daily(31,before,_=>(N("10000"),VolumeState.Contractual),day=>(N(day==4?"3.413":"3.412"),SchedulePriceFacts.Fixed));
        var unevenView=ScheduleViews.Build([uneven],Levels,TimeZoneInfo.Utc,before);
        if(Row(unevenView,"2031-12").Cells["physical.price"].Value is not null||Row(unevenView,"2031-12-05").Cells["physical.price"].Value!="3.413")
            throw new Exception("A coarse price is set only when all children agree.");
        Pass("coarse price is set only when all children agree");

        Equal(month.Cells["physical.amount"].Value,"1057720");
        var third=Daily(1,before,_=>(new(1,3),VolumeState.Contractual),_=>(N("3"),SchedulePriceFacts.Fixed));
        var thirdDay=Row(ScheduleViews.Build([third],Levels,TimeZoneInfo.Utc,before),"2031-12-01");
        Equal(thirdDay.Cells["physical.amount"].Value,"1");Equal(thirdDay.Cells["physical.quantity"].Value,"1/3");
        var scaled=Row(ScheduleViews.Build([daily with{Scale=D("2"),Coefficient=D("-1")}],Levels,TimeZoneInfo.Utc,before),"2031-12");
        Equal(scaled.Cells["physical.quantity"].Value,"-620000");Equal(scaled.Cells["physical.amount"].Value,"-2115440");Equal(scaled.Cells["physical.price"].Value,"3.412");
        Pass("amounts keep exact decimals");

        Equal(month.Cells["trade.net_cash"].Value,"1043770");Equal(view.Totals["trade.net_cash"].Value,"1043770");
        Equal(month.Cells["trade.payment_at"].Value,PayAt.ToString("O",System.Globalization.CultureInfo.InvariantCulture));
        if(view.Rows.Where(row=>row.Level=="day").Any(day=>day.Cells["trade.net_cash"].Value is not null))
            throw new Exception("Net cash sits only on the rows where sides report payments.");
        Pass("net cash sums signed cash sides");

        var sourced=Daily(31,before,day=>(N("10000"),day==0?VolumeState.Metered:VolumeState.Contractual),
            _=>(N("3.412"),SchedulePriceFacts.Projected|SchedulePriceFacts.PublishedFixing));
        var sourcedView=ScheduleViews.Build([sourced],Levels,TimeZoneInfo.Utc,before);
        Sources(Row(sourcedView,"2031-12").Cells["physical.quantity"],"contract","meter");
        Sources(Row(sourcedView,"2031-12-01").Cells["physical.quantity"],"meter");
        Sources(Row(sourcedView,"2031-12-02").Cells["physical.price"],"fwd","fixed");
        Sources(Row(sourcedView,"2031-12").Cells["physical.amount"],"contract","meter","fwd","fixed");
        Pass("sources map and union upward");

        if(view.Delivered is not null||view.Remaining is not null||view.DeliveredLeaves!=0||view.Leaves!=31||view.Rows.Any(row=>row.Delivered))
            throw new Exception("Before the first day nothing is delivered.");
        var at=From.AddDays(19);
        var delivered=ScheduleViews.Build([Daily(31,at,_=>(N("10000"),VolumeState.Contractual),_=>(N("3.412"),SchedulePriceFacts.Fixed)),
            Cash("cash","3.412",1,at),Cash("fee","0.045",-1,at)],Levels,TimeZoneInfo.Utc,at);
        if(delivered.DeliveredLeaves!=19||delivered.Leaves!=31||delivered.Delivered is null||delivered.Remaining is null||delivered.ValuationAt!=at||
            !Row(delivered,"2031-12-19").Delivered||Row(delivered,"2031-12-20").Delivered||Row(delivered,"2031-12").Delivered)
            throw new Exception("Rows ending by the valuation instant are delivered.");
        Equal(delivered.Delivered["physical.quantity"].Value,"190000");Equal(delivered.Remaining["physical.quantity"].Value,"120000");
        Equal(delivered.Delivered["physical.amount"].Value,"648280");Equal(delivered.Remaining["physical.amount"].Value,"409440");
        Equal(delivered.Delivered["cash.amount"].Value,null);Equal(delivered.Remaining["trade.net_cash"].Value,"1043770");
        foreach(var key in new[]{"physical.quantity","physical.amount"})
            if(D(delivered.Delivered[key].Value!)+D(delivered.Remaining[key].Value!)!=D(delivered.Totals[key].Value!))
                throw new Exception("Delivered plus remaining equals the totals.");
        Pass("rows ending by the valuation instant are delivered");

        var partial=Daily(31,before,_=>(N("10000"),VolumeState.Contractual),_=>(N("3.412"),SchedulePriceFacts.Fixed),new(From,From.AddDays(10)));
        Reject("schedule_view",()=>ScheduleViews.Build([partial],Levels,TimeZoneInfo.Utc,before));
        var offset=TimeZoneInfo.CreateCustomTimeZone("synthetic_minus_five",TimeSpan.FromHours(-5),"synthetic_minus_five","synthetic_minus_five");
        Reject("schedule_view",()=>ScheduleViews.Build([daily,cash],Levels,offset,before));
        Reject("schedule_view",()=>ScheduleViews.Build([daily],Levels,TimeZoneInfo.Utc,at));
        Reject("schedule_view",()=>ScheduleViews.Build([daily],["month"],TimeZoneInfo.Utc,before));
        Reject("schedule_view",()=>ScheduleViews.Build([cash],["day"],TimeZoneInfo.Utc,before));
        Reject("schedule_view",()=>ScheduleViews.Build([daily,daily],Levels,TimeZoneInfo.Utc,before));
        Pass("missing cells, clock disagreement, valuation mismatch and unsupported levels refuse");
    }

    private static SideViewInput Daily(int days,DateTimeOffset valuation,Func<int,(ScheduleNumber,VolumeState)> quantity,Func<int,(ScheduleNumber,SchedulePriceFacts)> price,
        DeliveryCoverage? coverage=null)
    {
        var program=Load(DailyDefinition(days));var demand=program.Inputs(Context(valuation),coverage:coverage);
        var evaluation=program.Execute(demand,"base","base",Inputs(program,demand,(row,cell)=>row.Operation switch
        {
            "quantity_input"=>(quantity(cell).Item1,new(quantity(cell).Item2,SchedulePriceFacts.None){Stage=QuantityStage.Delivered}),
            "price_lookup"=>(price(cell).Item1,new(null,price(cell).Item2)),
            _=>(N("1"),default)
        }));
        return new("physical",evaluation,program,D("1"),D("1"),Columns);
    }
    private static SideViewInput Cash(string code,string price,int direction,DateTimeOffset valuation)
    {
        var side=new FixedSide(Guid.NewGuid(),new FlatPrice(new UnitPrice(D(price),Quote)),Quote,new(D("1"),Quote.Quantity),
            [new(DateOnly.FromDateTime(From.UtcDateTime),DateOnly.FromDateTime(From.AddDays(31).UtcDateTime),PayAt,new(310000,1),null)]);
        var program=Load(ScheduleDefinitionCompiler.Generate(side));var demand=program.Inputs(Context(valuation));
        var evaluation=program.Execute(demand,"base","base",Inputs(program,demand,(_,_)=>(N("1"),default)));
        return new(code,evaluation,program,D("1"),D(direction.ToString(System.Globalization.CultureInfo.InvariantCulture)),Columns);
    }
    private static ScheduleDefinition DailyDefinition(int days)
    {
        ScheduleDefinitionRow Row(int ordinal,string key,string role,string operation,string kind,int? grid,ReferenceKey? unit,ReferenceKey? currency,string? quote,
            DateTimeOffset? at,int? payment,ImmutableSortedDictionary<string,string> parameters)=>
            new(ordinal,key,role,operation,1,kind,unit,currency,quote,null,null,null,null,null,at,payment,null,null,parameters){Grid=grid};
        var P=ScheduleProgramFixture.P;
        var rows=new[]{
            Row(0,"quantity","source","quantity_input","quantity",0,Quote.Quantity,null,null,null,null,P(("stage","Delivered"))),
            Row(1,"price","source","price_lookup","price",0,Quote.Quantity,Quote.Currency,"1",null,null,P(("selection","interval_attribute_lookup_v1"))),
            Row(2,"amount","contribution","amount","money",0,null,Quote.Currency,null,null,null,P()),
            Row(3,"discount","source","discount","factor",null,null,null,null,PayAt,0,P(("policy","context_required_v1"))),
            Row(4,"discounted","intermediate","pv","money",0,null,Quote.Currency,null,PayAt,0,P()),
            Row(5,"value","root","output_guard","money",0,null,Quote.Currency,null,PayAt,0,P(("measure","value_excluding_overdue_v1"),("lifecycle","interval_delivery_v1"),
                ("pricePolicy","selected_context_v1"),("averaging","none"),("firstFixingAt","none"),("paymentRule","synthetic_explicit_dates_v1"),("unadjustedPayment","none"))),
            Row(6,"total","intermediate","sum_cells","money",null,null,Quote.Currency,null,null,null,P()),
            Row(7,"side/pv","root","sum","money",null,null,Quote.Currency,null,null,null,P())};
        var first=DateOnly.FromDateTime(From.UtcDateTime).DayNumber;
        return new(Guid.NewGuid(),"synthetic_gas_days","synthetic_daily_delivery_v1","",ScheduleDefinition.CurrentSchema,ScheduleDefinition.CurrentGenerator,rows,
            [new(2,"quantity",0,0),new(2,"price",0,1),new(4,"amount",0,2),new(4,"discount",0,3),new(5,"amount",0,2),new(5,"discounted",0,4),new(6,"input",0,5),new(7,"input",0,6)],
            [new(0,"source","quantity_source",null,null,null,null,null,null,null,Definitions,"synthetic_meter"),
             new(1,"curve","curve_interval",null,null,null,null,null,null,null,Definitions,"synthetic_gas")],
            [new("pv",7,"pv")],[],
            [new(0,"gas_days",R("synthetic_utc_gas_day"),new(2031,12,1),0,From,TimeSpan.TicksPerDay,days,[..Enumerable.Range(0,days)],
                [..Enumerable.Repeat(1,days)],[..Enumerable.Range(first,days)],[..Enumerable.Repeat(0,days)],[..Enumerable.Repeat(1,days)])]);
    }
    private static BookedSideProgram Load(ScheduleDefinition definition)
    {
        var bytes=BookedSideProgram.Compile(definition).Write();
        return BookedSideProgram.Read(bytes,new(BookedSideProgram.Digest(bytes),definition.Manifest,definition.Family,definition.EconomicIdentity,definition.PricingBindingIdentity));
    }
    private static ResolvedCalculationContext Context(DateTimeOffset at)=>new(Guid.NewGuid(),"synthetic_schedule_view_v1",at,at,at,at);
    private static Dictionary<ScheduleInputAddress,ScheduleInputValue<ScheduleValueColumn>> Inputs(BookedSideProgram program,ScheduleInputDemandSet demand,
        Func<ScheduleDefinitionRow,int,(ScheduleNumber Number,ScheduleCellState State)> resolve)
    {
        var result=new Dictionary<ScheduleInputAddress,ScheduleInputValue<ScheduleValueColumn>>();
        foreach(var input in demand.Sources)
        {
            var row=program.Document.Instructions[input.Instruction].Output;
            var numbers=ImmutableArray.CreateBuilder<ScheduleNumber>();var states=ImmutableArray.CreateBuilder<ScheduleCellState>();var presence=ImmutableArray.CreateBuilder<ScheduleCellPresence>();
            for(var cell=0;cell<input.Uses.Length;cell++)
            {
                if(input.Uses[cell]==ScheduleInputLanes.None){numbers.Add(default);states.Add(default);presence.Add(ScheduleCellPresence.NotDemanded);continue;}
                var value=resolve(row,cell);numbers.Add(value.Number);states.Add(value.State);presence.Add(ScheduleCellPresence.Resolved);
            }
            result.Add(new(row.Ordinal,"base"),new(demand.Context,"base",new(row.OutputKind,row.Grid,row.Unit,row.Currency,row.QuoteMultiplier,
                numbers.ToImmutable(),states.ToImmutable(),presence.ToImmutable())));
        }
        return result;
    }
    private static ScheduleViewRow Row(ScheduleView view,string key)=>view.Rows.Single(row=>row.Key==key);
    private static void Equal(string? actual,string? expected)
    {if(actual!=expected)throw new Exception($"Expected exact view value {expected ?? "blank"}; received {actual ?? "blank"}.");}
    private static void Sources(ScheduleViewCell cell,params string[] expected)
    {if(!cell.Sources.SequenceEqual(expected))throw new Exception($"Expected sources [{string.Join(",",expected)}]; received [{string.Join(",",cell.Sources)}].");}
    private static void Reject(string code,Action action)
    {try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected structured refusal:"+code);}
    private static void Pass(string name)=>Console.WriteLine("PASS schedule view: "+name);
}
