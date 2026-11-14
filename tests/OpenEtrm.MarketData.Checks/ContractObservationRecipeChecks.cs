using System.Collections.Immutable;
using System.Globalization;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.Shared.Temporal;

// The synthetic crude fixture: futures E (2026-12) to J (2027-04) expiring at 19:30 UTC, nine business days
// and three terms for delivery 2027-01 (a calendar-month average and two trade-month roll spreads).
internal static class ContractObservationRecipeChecks
{
    public static void Run()
    {
        var revision=new DefinitionRevision(Guid.NewGuid(),Guid.NewGuid());var reference=new ReferenceRevision(Guid.NewGuid(),Guid.NewGuid());
        ReferenceKey R(string code)=>new(reference,code);
        var quote=new QuoteSpecification(R("crude_out"),QuoteKind.Outright,R("usd"),R("crude_quantity"),ExactDecimal.Parse("1"),ExactDecimal.Parse("0.01"),
            ExactDecimal.Parse("0.01"),ExactDecimal.Parse("-1000"),ExactDecimal.Parse("1000"));
        var futures=new List<FuturesSeriesSpecification>();
        RecipeContract Future(string code,int year,int month,DateTimeOffset expiry)
        {
            var from=new DateOnly(year,month,1);
            futures.Add(new(R(code),R("crude_quantity"),R("usd"),ExactDecimal.Parse("1"),ExactDecimal.Parse("0.01"),R("location"),R("uniform"),
                from,from.AddMonths(1),expiry,R("crude_quantity"),ExactDecimal.Parse("1000"),LotBasis.FixedTotal,null,"cash"));
            return new(new(new(revision,Guid.NewGuid()),ContractKind.Future,from,from.AddMonths(1),expiry,R(code)),new(2026,1,1,0,0,0,TimeSpan.Zero));
        }
        DateTimeOffset Expiry(int year,int month,int day)=>new(year,month,day,19,30,0,TimeSpan.Zero);
        var source=new RecipeSource(new(revision,"synthetic_crude_future"),PriceUnit.FromQuote(quote),[
            Future("crude_e",2026,12,Expiry(2026,11,20)),Future("crude_f",2027,1,Expiry(2026,12,21)),Future("crude_g",2027,2,Expiry(2027,1,20)),
            Future("crude_h",2027,3,Expiry(2027,2,22)),Future("crude_j",2027,4,Expiry(2027,3,22))]);
        // The differential curve retains one monthly contract per case; the 2027-02 contract only proves panel alignment.
        var differentialKey=new CurveKey(revision,"synthetic_crude_differential");
        RecipeContract[] differentials=[Future("crude_diff_f",2027,1,Expiry(2026,12,21)),Future("crude_diff_g",2027,2,Expiry(2027,1,20))];
        var calendar=DateSet.Create(new(2026,11,1),new(2027,4,1),[new(2026,12,15),new(2026,12,16),new(2026,12,17),new(2026,12,18),
            new(2027,1,18),new(2027,1,19),new(2027,1,20),new(2027,1,21),new(2027,1,22)]);
        var noon=new TimeOnly(12,0);
        var node=new RecipeNode("crude",RecipeEngine.ContractObservations,new Dictionary<string,string>{
                ["implementation"]=RecipeEngine.ObservationTermsVersion,["targetFrom"]="2027-01-01",["targetCount"]="1",["days"]=RecipeEngine.BusinessDays,["calendar"]="synthetic_crude_days",
                ["roll"]=ContractRollRule.ExpiryImplementation,["sampling"]="context_utc_time",["corrections"]="exact_context_frame_history_latest_visible"}.ToImmutableDictionary(),
            ImmutableDictionary<string,string>.Empty.Add("source","source"),
            ObservationTerms:[new(0,"average","1",1,null,RecipeEngine.CalendarMonthPeriod),new(1,"roll","2",1,2,RecipeEngine.TradeMonthPeriod),
                new(2,"roll","1",1,3,RecipeEngine.TradeMonthPeriod)]);
        RecipeDefinition Definition(RecipeNode observations)=>new("crude_formula","Synthetic indexed crude",[
            new("source","source",ImmutableDictionary<string,string>.Empty.Add("curve","synthetic_crude_future"),ImmutableDictionary<string,string>.Empty),observations,
            new("out","output",ImmutableDictionary<string,string>.Empty,ImmutableDictionary<string,string>.Empty.Add("values",observations.Id))],"out");
        // The formula panel plus the differential sampled at the context date: a signed sum with weights 1 and 1.
        RecipeDefinition WithDifferential(RecipeNode observations)=>new("crude_formula","Synthetic indexed crude",[
            new("source","source",ImmutableDictionary<string,string>.Empty.Add("curve","synthetic_crude_future"),ImmutableDictionary<string,string>.Empty),observations,
            new("differential","source",ImmutableDictionary<string,string>.Empty.Add("curve",differentialKey.Code),ImmutableDictionary<string,string>.Empty),
            new("select","select",new Dictionary<string,string>{["mode"]="positions",["first"]="1",["count"]="1",["binding"]="context"}.ToImmutableDictionary(),
                ImmutableDictionary<string,string>.Empty.Add("source","differential")),
            new("dates","dates",ImmutableDictionary<string,string>.Empty.Add("mode","context"),ImmutableDictionary<string,string>.Empty),
            new("sample","sample",ImmutableDictionary<string,string>.Empty,ImmutableDictionary<string,string>.Empty.Add("selection","select").Add("dates","dates")),
            new("total","weighted_panels",ImmutableDictionary<string,string>.Empty.Add("alignment",RecipeEngine.ExactDeliveryAlignment).Add("aggregation",RecipeEngine.SignedSum),
                ImmutableDictionary<string,string>.Empty.Add("observed",observations.Id).Add("differential","sample"),PanelWeights:[new("observed","1"),new("differential","1")]),
            new("out","output",ImmutableDictionary<string,string>.Empty,ImmutableDictionary<string,string>.Empty.Add("values","total"))],"out");
        RecipeNode Set(RecipeNode n,string key,string value)=>n with{Parameters=n.Parameters.SetItem(key,value)};
        RecipeNode Term(RecipeNode n,int ordinal,Func<RecipeObservationTerm,RecipeObservationTerm> change)=>
            n with{ObservationTerms=n.ObservationTerms.SetItem(ordinal,change(n.ObservationTerms[ordinal]))};
        RecipeNode TargetFebruary(RecipeNode n)=>Set(n,"targetFrom","2027-02-01");
        RecipeNode CalendarDaysOf(RecipeNode n)=>n with{Parameters=n.Parameters.SetItem("days",RecipeEngine.CalendarDays).Remove("calendar")};
        RecipeNode RollOnly(RecipeNode n)=>n with{ObservationTerms=[..n.ObservationTerms.Skip(1).Select(t=>t with{Ordinal=t.Ordinal-1})]};

        RecipeEngine.Validate(Definition(node));
        var windows=RecipeEngine.ObservationWindows(node,source,calendar,noon);
        Equal("targets",RecipeEngine.ObservationTargets(node).Single(),new RecipeDelivery(new(2027,1,1),new(2027,2,1)));
        Equal("one window per target and term",windows.Length,3);
        Days(windows,term:0,"2027-01-18","2027-01-19","2027-01-20","2027-01-21","2027-01-22");
        Days(windows,term:1,"2026-12-15","2026-12-16","2026-12-17","2026-12-18");
        Days(windows,term:2,"2026-12-15","2026-12-16","2026-12-17","2026-12-18");
        // At 12:00 G leads on all three days; at 20:00 it has expired on 2027-01-20 and that day drops out.
        Days(RecipeEngine.ObservationWindows(TargetFebruary(RollOnly(node)),source,calendar,noon),term:0,"2027-01-18","2027-01-19","2027-01-20");
        Days(RecipeEngine.ObservationWindows(TargetFebruary(RollOnly(node)),source,calendar,new TimeOnly(20,0)),term:0,"2027-01-18","2027-01-19");
        // The last contract of the universe drops its own expiry day the same way: J leads from 2027-02-22 20:00 to 2027-03-21.
        var april=RecipeEngine.ObservationWindows(CalendarDaysOf(Set(RollOnly(node),"targetFrom","2027-04-01")) with{
            ObservationTerms=[new(0,"roll","1",1,null,RecipeEngine.TradeMonthPeriod)]},source,null,new TimeOnly(20,0)).Single().Days;
        Equal("last trade month starts after the prior expiry",april[0],new DateOnly(2027,2,22));
        Equal("last trade month drops its expiry day",april[^1],new DateOnly(2027,3,21));
        Equal("last trade month day count",april.Length,28);

        void Refused(string label,RecipeNode candidate)
        {
            Code(label,"recipe_definition",()=>RecipeEngine.Validate(Definition(candidate)));
            Code(label,"recipe_definition",()=>RecipeEngine.ObservationWindows(candidate,source,calendar,noon));
        }
        // The calendar is a reference: its absence is refused where the declaration pins it, below.
        foreach(var key in node.Parameters.Keys.Where(k=>k!="calendar"))Refused($"Missing {key}",node with{Parameters=node.Parameters.Remove(key)});
        Code("A calendar on a calendar-day node","recipe_definition",()=>RecipeEngine.Validate(Definition(Set(node,"days",RecipeEngine.CalendarDays))));
        Refused("Another implementation",Set(node,"implementation","contract_observation_terms_v2"));
        Refused("Another roll",Set(node,"roll","contract_roll_boundaries_v1"));
        Refused("Another day basis",Set(node,"days","trading_days_v1"));
        Refused("Another period",Term(node,0,t=>t with{Period="average_month_v1"}));
        Refused("A target not on day 1",Set(node,"targetFrom","2027-01-02"));
        Refused("No terms",node with{ObservationTerms=[]});
        Refused("Default terms",node with{ObservationTerms=default});
        Refused("A gap in ordinals",Term(node,2,t=>t with{Ordinal=3}));
        foreach(var weight in new[]{"0","-1","2/3"})Refused($"Weight {weight}",Term(node,1,t=>t with{Weight=weight}));
        Refused("A spread against its own position",Term(node,1,t=>t with{LessPosition=1}));
        // Every term refusal names the term as the editor numbers its rows, from 1, and the member at fault, so an editor row in
        // progress is answered beside the operation. The ordinal stays 0-based.
        void Names(string label,int number,string member,RecipeNode candidate)=>
            Message(label,"recipe_definition",$"Observation term {number} {member}:",()=>RecipeEngine.Validate(Definition(candidate)));
        Names("A blank position",2,"position",Term(node,1,t=>t with{Position=null}));
        Names("Position 0",2,"position",Term(node,1,t=>t with{Position=0}));
        Names("Position 121",3,"position",Term(node,2,t=>t with{Position=121}));
        Names("Spread position 121",2,"lessPosition",Term(node,1,t=>t with{LessPosition=121}));
        Names("A spread named by its own position",2,"lessPosition",Term(node,1,t=>t with{LessPosition=1}));
        Names("An invalid group",1,"group",Term(node,0,t=>t with{Group="Average"}));
        Names("An unsupported period",3,"period",Term(node,2,t=>t with{Period="average_month_v1"}));
        foreach(var weight in new[]{"0","-1","2/3",""," 1","1e2","1.0000000000000000001","9999999999999999999999999"})
            Names($"Weight '{weight}'",2,"weight",Term(node,1,t=>t with{Weight=weight}));
        // Each weight is in range but their group's sum is not: the recipe could never evaluate, so it refuses when saved.
        Message("A group whose weights cannot be summed","recipe_definition","Observation group roll weights: the weights of terms 2, 3 must sum below 10^24.",()=>RecipeEngine.Validate(Definition(
            Term(Term(node,1,t=>t with{Weight="999999999999999999999999"}),2,t=>t with{Weight="999999999999999999999999"}))));
        Code("Observation terms on another kind","recipe_definition",()=>RecipeEngine.Validate(Definition(node) with{Nodes=Definition(node).Nodes.SetItem(0,
            Definition(node).Nodes[0] with{ObservationTerms=node.ObservationTerms})}));
        Code("A missing source port","recipe_definition",()=>RecipeEngine.Validate(Definition(node with{Inputs=ImmutableDictionary<string,string>.Empty})));
        Code("Business days without a calendar","recipe_definition",()=>RecipeEngine.ObservationWindows(node,source,null,noon));
        Code("Calendar days with a calendar","recipe_definition",()=>RecipeEngine.ObservationWindows(Set(node,"days",RecipeEngine.CalendarDays),source,calendar,noon));

        Code("No delivery contract","recipe_input",()=>RecipeEngine.ObservationWindows(Set(RollOnly(node),"targetFrom","2027-06-01"),source,calendar,noon));
        Code("A first delivery contract has no trade-month start","recipe_input",()=>RecipeEngine.ObservationWindows(Set(RollOnly(node),"targetFrom","2026-12-01"),source,calendar,noon));
        Code("A position beyond the universe","recipe_input",()=>RecipeEngine.ObservationWindows(Term(node,0,t=>t with{Position=5}),source,calendar,noon));
        Message("No business day in the window","recipe_input","Term 1 has no observation day for 2027-01.",()=>RecipeEngine.ObservationWindows(node,source,
            DateSet.Create(new(2026,11,1),new(2027,4,1),[new(2026,11,2)]),noon));

        // Prices through the connected plan. Each contract has one daily-settlement series; a day's rows share one snapshot.
        var contracts=new Dictionary<string,RecipeContract>{["F"]=source.Contracts[1],["G"]=source.Contracts[2],["H"]=source.Contracts[3],["J"]=source.Contracts[4],
            ["D"]=differentials[0],["D2"]=differentials[1]};
        CurveKey CurveOf(string contract)=>contract.StartsWith('D')?differentialKey:source.Key;
        var series=source.Contracts.Concat(differentials).ToDictionary(c=>c.Contract.Key,c=>new SeriesKey(revision,Guid.NewGuid()));
        Curve Direct(CurveKey key,IEnumerable<RecipeContract> members)=>new(key,quote.Key,R("location"),R("uniform"),new(SettlementKind.FutureSeries,null,null),CurveRule.Direct,
            [..members.Select(c=>c.Contract.Key)],[],ObservationRole.DailySettlement,R("source"),null);
        // A composite (basis-spread) source: monthly forwards priced as the future of the same delivery month plus a direct basis.
        var basisQuote=quote with{Key=R("crude_basis"),Kind=QuoteKind.Basis};
        var forwards=source.Contracts.Select(c=>new RecipeContract(c.Contract with{Key=new(revision,Guid.NewGuid()),Kind=ContractKind.Forward,FuturesSeries=null},c.EligibleFrom)).ToImmutableArray();
        var basisKey=new CurveKey(revision,"synthetic_crude_basis");var compositeKey=new CurveKey(revision,"synthetic_crude_composite");
        var forwardMaps=forwards.Zip(source.Contracts).Select(p=>new ConstituentMapping(p.First.Contract.Key,p.Second.Contract.Key)).ToImmutableArray();
        var model=new CurveModel(revision,reference,[new QuantitySpecification(R("crude_quantity"),R("crude"),R("crude_volume"),R("barrel"),R("volume"))],[quote,basisQuote],
            source.Contracts.Concat(differentials).Concat(forwards).Select(c=>c.Contract),[Direct(source.Key,source.Contracts),Direct(differentialKey,differentials),
                new(basisKey,basisQuote.Key,R("location"),R("uniform"),new(SettlementKind.IndexDifference,R("crude_composite_index"),R("crude_index")),CurveRule.Direct,
                    [..forwards.Select(c=>c.Contract.Key)],[],ObservationRole.TraderMark,R("source"),null),
                new(compositeKey,quote.Key,R("location"),R("uniform"),new(SettlementKind.Index,R("crude_composite_index"),null),CurveRule.Add,[..forwards.Select(c=>c.Contract.Key)],
                    [new(InputSlot.Base,source.Key,SourceExpiryRule.RequireLive,forwardMaps),
                     new(InputSlot.Spread,basisKey,SourceExpiryRule.RequireLive,[..forwards.Select(c=>new ConstituentMapping(c.Contract.Key,c.Contract.Key))])],null,null,null)],
            futures,[],series.Select(s=>new MarketSeries(s.Value,s.Key,quote.Key,null)).Concat(forwards.Select(c=>new MarketSeries(new(revision,Guid.NewGuid()),c.Contract.Key,basisQuote.Key,null))));
        (string Day,string Contract,string Value)[] Rows(string day,string f,string g,string h)=>[(day,"F",f),(day,"G",g),(day,"H",h)];
        var december=Rows("2026-12-15","70.00","69.40","68.80").Concat(Rows("2026-12-16","70.30","69.60","68.90"))
            .Concat(Rows("2026-12-17","69.90","69.40","68.80")).Concat(Rows("2026-12-18","70.20","69.60","69.10")).ToArray();
        (string,string,string)[] january=[("2027-01-18","G","69.60"),("2027-01-19","G","69.50"),("2027-01-20","G","69.30"),("2027-01-21","H","68.90"),("2027-01-22","H","68.70")];
        (string,string)[] marks=[("F","70.00"),("G","69.40"),("H","68.80")];
        var identity=new ConnectedRecipeIdentity(revision.CatalogId,Guid.NewGuid(),1,RecipeEngine.Version);
        (RecipeInputs Inputs,ImmutableArray<ConnectedRecipeSample> Samples,ResolvedCalculationContext Resolved,ConnectedRecipeReferencePins? Pins) Prepared(string context,string time,
            IEnumerable<(string Day,string Contract,string Value)> history,IEnumerable<(string Contract,string Value)> live,
            Func<ImmutableDictionary<string,ImmutableArray<RecipeObservationWindow>>,ImmutableDictionary<string,ImmutableArray<RecipeObservationWindow>>>? windows,
            RecipeNode stated,RecipeContract? differential,ConnectedRecipeReferencePins? pins)
        {
            var at=DateTimeOffset.Parse(context+"T"+time+"Z",CultureInfo.InvariantCulture);var day=DateOnly.FromDateTime(at.UtcDateTime);
            var resolved=new ResolvedCalculationContext(Guid.NewGuid(),"synthetic-crude-observations",at,at,at,at);
            var samples=history.GroupBy(r=>r.Day).Select(g=>(Date:DateOnly.ParseExact(g.Key,"yyyy-MM-dd",CultureInfo.InvariantCulture),Rows:g.Select(r=>(r.Contract,r.Value))))
                .Append((Date:day,Rows:live)).SelectMany(g=>
                {
                    var observed=new DateTimeOffset(g.Date.ToDateTime(TimeOnly.FromDateTime(at.UtcDateTime)),TimeSpan.Zero);
                    var snapshot=new MarketSnapshot(Guid.NewGuid(),revision,observed,observed,InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,
                        [..g.Rows.Select(r=>new SelectedObservation(new(series[contracts[r.Contract].Contract.Key],InputUse.Variable),new(new(revision.CatalogId,Guid.NewGuid()),
                            series[contracts[r.Contract].Contract.Key],R("source"),ObservationRole.DailySettlement,PublicationState.Final,observed,ExactDecimal.Parse(r.Value))))]);
                    // The context frame supplies each contract twice: live for later days and frozen for the context day's own observation.
                    return g.Rows.SelectMany(r=>new ConnectedRecipeSample(CurveOf(r.Contract),g.Date,contracts[r.Contract].Contract.Key,model,snapshot,observed<at) is var sample&&observed<at?
                        [sample]:new[]{sample,sample with{FreezeObservation=true}});
                }).ToImmutableArray();
            var inputs=new RecipeInputs(day,at,ImmutableDictionary<string,RecipeSource>.Empty.Add("source",source),ImmutableDictionary<string,ImmutableArray<DateOnly>>.Empty)
                {Windows=ImmutableDictionary<string,ImmutableArray<RecipeObservationWindow>>.Empty.Add("crude",RecipeEngine.ObservationWindows(stated,source,
                    stated.Parameters["days"]==RecipeEngine.BusinessDays?calendar:null,TimeOnly.FromDateTime(at.UtcDateTime)))};
            if(windows is not null)inputs=inputs with{Windows=windows(inputs.Windows)};
            if(differential is not null)inputs=inputs with{Sources=inputs.Sources.Add("differential",new(differentialKey,source.Unit,[differential])),Dates=inputs.Dates.Add("dates",[day])};
            // A business-day node is declared with its exact resolved calendar, as a business-day date sequence is.
            pins??=stated.Parameters["days"]==RecipeEngine.BusinessDays?new([new("crude",R(stated.Parameters["calendar"]),calendar)],[]):null;
            return(inputs,samples,resolved,pins);
        }
        (ConnectedRecipeGraph Graph,ConnectedScenario Basis) Priced(string context,string time,IEnumerable<(string Day,string Contract,string Value)> history,IEnumerable<(string Contract,string Value)> live,
            Func<ImmutableDictionary<string,ImmutableArray<RecipeObservationWindow>>,ImmutableDictionary<string,ImmutableArray<RecipeObservationWindow>>>? windows=null,
            RecipeNode? terms=null,RecipeContract? differential=null,ConnectedRecipeReferencePins? pins=null,RecipeOutputDemand? demand=null)
        {
            var stated=terms??node;var prepared=Prepared(context,time,history,live,windows,stated,differential,pins);
            var graph=RecipeEngine.Connect(differential is null?Definition(stated):WithDifferential(stated),prepared.Inputs,identity,prepared.Samples,prepared.Resolved,
                references:prepared.Pins,demand:demand);
            return(graph,ConnectedScenario.Base(ConnectedCurves.Compile(graph.Definitions,[..graph.Outputs.Select(o=>o.Point)],prepared.Resolved,new Visible())));
        }
        ExactUnitPrice Shifted((ConnectedRecipeGraph Graph,ConnectedScenario Basis) priced,params string[] shifted)=>priced.Basis.WithShifts([..shifted.Select(name=>
        {
            var point=priced.Basis.Plan.Points.Values.Single(p=>p.Expression is ConnectedSource s&&s.Input.Series==series[contracts[name].Contract.Key]&&s.Input.Use==InputUse.Variable);
            return new CurveShift(CurveShockCoordinate.MarketQuote,new PointCurveTarget(point.Key.Curve,[point.Key.PointId]),point.Unit,ExactDecimal.Parse("1"),CurveShift.ParentAdditive);
        })]).Price(priced.Graph.Outputs.Single().Point);
        ExactUnitPrice Base((ConnectedRecipeGraph Graph,ConnectedScenario Basis) priced)=>priced.Basis.Price(priced.Graph.Outputs.Single().Point);
        bool Variable((ConnectedRecipeGraph Graph,ConnectedScenario Basis) priced,string name)=>
            priced.Basis.Plan.Primitives.Any(p=>p.Source.Input.Series==series[contracts[name].Contract.Key]&&p.Source.Input.Use==InputUse.Variable);

        // Before the pricing period every observation is projected at today's price of its selected contract.
        var before=Priced("2026-12-01","12:00:00",[],marks);
        Price("before",Base(before),"1749","25");Price("before F+1",Shifted(before,"F"),"1774","25");
        Price("before G+1",Shifted(before,"G"),"5242","75");Price("before H+1",Shifted(before,"H"),"5252","75");
        Price("before F,G,H+1",Shifted(before,"F","G","H"),"1774","25");
        // The December roll window is settled; January is still projected.
        var settledRoll=Priced("2026-12-22","12:00:00",december,marks.Skip(1));
        Price("settled roll",Base(settledRoll),"1749","25");Equal("F is settled",Variable(settledRoll,"F"),false);
        Price("settled roll G+1",Shifted(settledRoll,"G"),"1764","25");Price("settled roll H+1",Shifted(settledRoll,"H"),"1759","25");
        // Inside January: two settled days, the context day frozen at its own close and two later days at today's prices.
        // (69.60+69.50+69.40+68.80+68.80)/5+0.80: G's only observation from here on is today's, so G no longer moves the price.
        var during=Priced("2027-01-20","12:00:00",[..december,..january.Take(2)],marks.Skip(1));
        Price("during",Base(during),"3501","50");Equal("during G is frozen",Variable(during,"G"),false);
        Price("during H+1",Shifted(during,"H"),"3521","50");
        // The day before: 2027-01-19 is frozen and 2027-01-20 projects the same contract, so one calculation holds a frozen and a
        // live observation of G at the context instant. (69.60+69.50+69.50+68.80+68.80)/5+0.80; G+1 moves one day in five.
        var dayBefore=Priced("2027-01-19","12:00:00",[..december,..january.Take(1)],[("G","69.50"),("H","68.80")]);
        Price("day before",Base(dayBefore),"1751","25");Price("day before G+1",Shifted(dayBefore,"G"),"1756","25");
        Price("day before H+1",Shifted(dayBefore,"H"),"1761","25");
        // Samples are ordered by source, date, contract and then frozen after live, so the order never depends on graph traversal.
        Equal("day before G samples, live then frozen",string.Join(",",dayBefore.Graph.Result(dayBefore.Basis).Inputs
            .Where(i=>i.Date==new DateOnly(2027,1,19)&&i.Contract==contracts["G"].Contract.Key).Select(i=>i.Fixed?"frozen":"live")),"live,frozen");
        // Inside the trade month: 12-15 and 12-16 settled, 12-17 frozen at its close, 12-18 and January projected.
        // January (3*69.40+2*68.80)/5=69.16; F, G and H average 70.025, 69.45 and 68.825; roll (2*0.575+1.20)/3; total 20983/300.
        // F+1 moves 12-18 only: (2+1)/4/3=1/4. G+1: 3/5 in January less 2/4/3 in the roll, 13/30. H+1: 2/5 less 1/4/3, 19/60.
        var tradeMonth=Priced("2026-12-17","12:00:00",december.Take(6),[("F","69.90"),("G","69.40"),("H","68.80")]);
        Price("trade month",Base(tradeMonth),"20983","300");Price("trade month F+1",Shifted(tradeMonth,"F"),"10529","150");
        Price("trade month G+1",Shifted(tradeMonth,"G"),"21113","300");Price("trade month H+1",Shifted(tradeMonth,"H"),"3513","50");
        // The context day is frozen only from the context frame's own observation at the valuation instant: an absent or earlier
        // mark of the selected contract refuses, and is never carried into the average.
        ImmutableArray<ConnectedRecipeSample> ContextG(ImmutableArray<ConnectedRecipeSample> samples,Func<MarketSnapshot,MarketSnapshot> change)
        {
            var context=new DateOnly(2027,1,20);var changed=change(samples.First(s=>s.Date==context).Snapshot);
            return[..samples.Select(s=>s.Date==context?s with{Snapshot=changed}:s)];
        }
        var gSeries=series[contracts["G"].Contract.Key];
        foreach(var (label,change) in new (string,Func<MarketSnapshot,MarketSnapshot>)[]{
            ("a context frame without the context day's contract",s=>s with{Selections=[..s.Selections.Where(o=>o.Input.Series!=gSeries)]}),
            ("a context day's contract marked before the valuation instant",s=>s with{Selections=[..s.Selections.Select(o=>o.Input.Series!=gSeries?o:
                o with{Observation=o.Observation with{ObservedAt=o.Observation.ObservedAt.AddHours(-1)}})]})})
        {
            var stale=Prepared("2027-01-20","12:00:00",[..december,..january.Take(2)],marks.Skip(1),null,node,null,null);
            Code(label,"recipe_input",()=>RecipeEngine.Connect(Definition(node),stale.Inputs,identity,ContextG(stale.Samples,change),stale.Resolved,references:stale.Pins));
        }
        // A frozen observation belongs to the context frame: a past day's frame cannot supply one.
        var pastFrozen=Prepared("2027-01-20","12:00:00",[..december,..january.Take(2)],marks.Skip(1),null,node,null,null);
        Message("a frozen observation from a past frame","recipe_input","A frozen context observation is read from the context frame only.",()=>RecipeEngine.Connect(Definition(node),pastFrozen.Inputs,identity,
            pastFrozen.Samples.Add(pastFrozen.Samples.Single(s=>s.Date==new DateOnly(2027,1,19)) with{FreezeObservation=true}),pastFrozen.Resolved,references:pastFrozen.Pins));
        // A composite source has no settlement of its own to freeze: on an observation-day context its frozen sample refuses with
        // its own message, before any price is compiled.
        var composite=Prepared("2027-01-20","12:00:00",[..december,..january.Take(2)],marks.Skip(1),null,node,null,null);
        var compositeSource=new RecipeSource(compositeKey,source.Unit,forwards);var forwardG=forwards[2].Contract.Key;
        var compositeDefinition=Definition(node) with{Nodes=Definition(node).Nodes.SetItem(0,Definition(node).Nodes[0] with{Parameters=
            ImmutableDictionary<string,string>.Empty.Add("curve",compositeKey.Code)})};
        Message("a composite source on an observation day","recipe_input","A frozen context observation needs a direct source curve; synthetic_crude_composite",
            ()=>RecipeEngine.Connect(compositeDefinition,composite.Inputs with{Sources=composite.Inputs.Sources.SetItem("source",compositeSource)},identity,
                [new ConnectedRecipeSample(compositeKey,new(2027,1,20),forwardG,model,composite.Samples.First(s=>s.Date==new DateOnly(2027,1,20)).Snapshot,false){FreezeObservation=true}],
                composite.Resolved,references:composite.Pins));
        // A source carrying contract price terms settles through those terms, not through its own observation: its frozen sample refuses.
        var exchangeFinal=new OpenEtrm.MarketData.Curves.ContractPriceTerms(OpenEtrm.MarketData.Curves.ContractPriceTerms.ExchangeFinal,null,null,
            OpenEtrm.MarketData.Curves.ContractPriceTerms.SingleAssessment,OpenEtrm.MarketData.Curves.ContractPriceTerms.RequireFinal,null);
        var termsSet=new OpenEtrm.MarketData.Curves.ContractPriceTermsSet(exchangeFinal,[],OpenEtrm.MarketData.Curves.ContractPriceMappings.SameDeliveryPeriod,
            OpenEtrm.MarketData.Curves.ContractPriceMappings.Resolve(OpenEtrm.MarketData.Curves.ContractPriceMappings.SameDeliveryPeriod,
                [..source.Contracts.Select(c=>c.Contract)],[..source.Contracts.Select(c=>c.Contract)],[]));
        var termed=Prepared("2027-01-20","12:00:00",[..december,..january.Take(2)],marks.Skip(1),null,node,null,null);
        var termedDefinition=Definition(node) with{Nodes=Definition(node).Nodes.SetItem(0,Definition(node).Nodes[0] with{ContractTerms=termsSet})};
        Message("a source with contract price terms on an observation day","recipe_input","A frozen context observation reads the source contract's own settlement or mark",
            ()=>RecipeEngine.Connect(termedDefinition,termed.Inputs,identity,[..termed.Samples.Select(s=>s.Source==source.Key?s with{MappedContract=s.Contract,
                PriceTerms=new OpenEtrm.MarketData.Curves.ContractPriceTermsBinding(exchangeFinal,null,R("crude"),null)}:s)],termed.Resolved,references:termed.Pins));
        // A consumer of the published curve plans it before reading prices: topology needs the sampling instant of every
        // observation day, past days included, not only of the context date.
        var planned=Prepared("2027-01-20","12:00:00",[..december,..january.Take(2)],marks.Skip(1),null,node,null,null);
        ImmutableDictionary<DateOnly,DateTimeOffset> Instants(IEnumerable<DateOnly> days)=>
            days.Append(new DateOnly(2027,1,20)).Distinct().ToImmutableDictionary(d=>d,d=>new DateTimeOffset(d.ToDateTime(noon),TimeSpan.Zero));
        Code("topology at the context instant only","recipe_input",()=>RecipeEngine.Topology(Definition(node),planned.Inputs,ImmutableDictionary<string,RecipeTopology>.Empty,Instants([])));
        var instants=Instants(planned.Inputs.Windows["crude"].SelectMany(w=>w.Days));
        var topology=RecipeEngine.Topology(Definition(node),planned.Inputs,ImmutableDictionary<string,RecipeTopology>.Empty,instants);
        var january2027=new RecipeDelivery(new(2027,1,1),new(2027,2,1));
        Equal("planned cells",string.Join(",",topology.Outputs.Select(o=>$"{o.Position}:{o.DeliveryFrom:yyyy-MM-dd}..{o.DeliveryTo:yyyy-MM-dd}")),"1:2027-01-01..2027-02-01");
        // Each past observation day's own sample of every contract its terms select, the context day's frozen G and the context date's live H.
        Equal("planned native inputs",string.Join(",",topology.NativeInputs([january2027]).Select(d=>(d.Date,Name:contracts.Single(c=>c.Value.Contract.Key==d.Contract).Key+(d.FreezeObservation?" frozen":"")))
            .OrderBy(d=>d.Date).ThenBy(d=>d.Name,StringComparer.Ordinal).Select(d=>d.Date.ToString("MM-dd",CultureInfo.InvariantCulture)+d.Name)),
            "12-15F,12-15G,12-15H,12-16F,12-16G,12-16H,12-17F,12-17G,12-17H,12-18F,12-18G,12-18H,01-18G,01-19G,01-20G frozen,01-20H");
        Price("priced under the planned demand",Base(Priced("2027-01-20","12:00:00",[..december,..january.Take(2)],marks.Skip(1),
            demand:new([january2027],instants))),"3501","50");
        // After the pricing period nothing is variable.
        var after=Priced("2027-01-25","12:00:00",[..december,..january],[]);
        Price("after",Base(after),"70","1");Equal("after G is fixed",Variable(after,"G"),false);Equal("after H is fixed",Variable(after,"H"),false);
        // Review focus 3: a context date that is no observation day adds no observation.
        var weekend=Priced("2027-01-17","12:00:00",december,marks.Skip(1));
        Price("context not an observation day",Base(weekend),"1749","25");
        Equal("observation days exclude the context date",string.Join(",",weekend.Graph.Outputs.Single().Dates.Select(d=>d.ToString("MM-dd",CultureInfo.InvariantCulture))),
            "12-15,12-16,12-17,12-18,01-18,01-19,01-20,01-21,01-22");
        Equal("five projected January leaves and sixteen settled roll leaves",weekend.Graph.Outputs.Single().SampleCount,21);
        // Review focus 1: at 20:00 G has expired on its last trading day, so 2027-01-20 observes H.
        var late=Priced("2027-01-25","20:00:00",[..december,..january,("2027-01-20","H","68.80")],[]);
        Price("sampling after expiry",Base(late),"699","10");
        Equal("January leaves after expiry",string.Join(",",late.Graph.Result(late.Basis).Inputs.Where(i=>i.Date.Year==2027).OrderBy(i=>i.Date)
            .Select(i=>i.Date.ToString("MM-dd",CultureInfo.InvariantCulture)+contracts.Single(c=>c.Value.Contract.Key==i.Contract).Key)),"01-18G,01-19G,01-20H,01-21H,01-22H");
        // Review focus 2: a past observation day without its retained sample never shrinks the average.
        Code("missing past observation","recipe_input",()=>Priced("2027-01-25","12:00:00",[..december,..january.Where(r=>r.Item1!="2027-01-19")],[]));
        // Supplied windows cover exactly the observation nodes and each node's targets and terms.
        foreach(var (label,change) in new (string,Func<ImmutableDictionary<string,ImmutableArray<RecipeObservationWindow>>,ImmutableDictionary<string,ImmutableArray<RecipeObservationWindow>>>)[]{
            ("no windows",w=>w.Clear()),("an undeclared node",w=>w.Add("other",w["crude"])),("a missing term",w=>w.SetItem("crude",w["crude"].RemoveAt(2))),
            ("a repeated term",w=>w.SetItem("crude",w["crude"].Add(w["crude"][2]))),("an empty window",w=>w.SetItem("crude",w["crude"].SetItem(0,w["crude"][0] with{Days=[]}))),
            ("another target",w=>w.SetItem("crude",w["crude"].SetItem(0,w["crude"][0] with{Target=new(new(2027,2,1),new(2027,3,1))})))})
            Code(label,"recipe_input",()=>Priced("2026-12-01","12:00:00",[],marks,change));
        // Supplied windows are self-consistent: a calendar-month day lies in its month, a trade-month day selects the delivery contract as position 1.
        // Calendar days, so that no business calendar refuses the added day first.
        (string,string)[] allMarks=[..marks,("J","68.20")];
        Code("a calendar-month day outside its target month","recipe_input",()=>Priced("2026-12-01","12:00:00",[],allMarks,
            w=>w.SetItem("crude",w["crude"].SetItem(0,w["crude"][0] with{Days=w["crude"][0].Days.Add(new(2027,2,1))})),terms:CalendarDaysOf(node)));
        Code("a trade-month day on which G leads","recipe_input",()=>Priced("2026-12-01","12:00:00",[],allMarks,
            w=>w.SetItem("crude",w["crude"].SetItem(1,w["crude"][1] with{Days=w["crude"][1].Days.Add(new(2026,12,22))})),terms:CalendarDaysOf(node)));
        // A business-day node requires the pin of exactly its named calendar, and every observation day is a business day of it.
        Code("a business-day node without its calendar pin","recipe_reference",()=>Priced("2026-12-01","12:00:00",[],marks,pins:new([],[])));
        Code("a calendar pin naming another calendar","recipe_reference",()=>Priced("2026-12-01","12:00:00",[],marks,
            pins:new([new("crude",R("synthetic_other_days"),calendar)],[])));
        Code("an observation day that is not a business day","recipe_reference",()=>Priced("2026-12-01","12:00:00",[],marks,
            w=>w.SetItem("crude",w["crude"].SetItem(0,w["crude"][0] with{Days=[..w["crude"][0].Days.Add(new(2027,1,23)).Order()]}))));
        // Cells feed only a panel combination or the output; a contract mean would merge target months.
        Code("A mean over observation cells","recipe_definition",()=>RecipeEngine.Validate(Definition(node) with{Nodes=[..Definition(node).Nodes.SkipLast(1),
            new("mean","mean",ImmutableDictionary<string,string>.Empty.Add("axis","contracts"),ImmutableDictionary<string,string>.Empty.Add("values","crude")),
            new("out","output",ImmutableDictionary<string,string>.Empty,ImmutableDictionary<string,string>.Empty.Add("values","mean"))]}));

        // Two calendar-month targets at 12:00 on 2026-12-01: January observes G on the 1st to the 20th and H on the 21st to the 31st,
        // (20*69.40+11*68.80)/31; February observes H on the 1st to the 22nd and J on the 23rd to the 28th, (22*68.80+6*68.20)/28.
        var twoMonths=CalendarDaysOf(Set(node,"targetCount","2")) with{ObservationTerms=[new(0,"average","1",1,null,RecipeEngine.CalendarMonthPeriod)]};
        var twoTargets=Priced("2026-12-01","12:00:00",[],[("G","69.40"),("H","68.80"),("J","68.20")],terms:twoMonths);
        Equal("two target cells",string.Join(",",twoTargets.Graph.Outputs.Select(o=>o.Position)),"1,2");
        Price("January target",twoTargets.Basis.Price(twoTargets.Graph.Outputs[0].Point),"10724","155");
        Price("February target",twoTargets.Basis.Price(twoTargets.Graph.Outputs[1].Point),"4807","70");

        // The 2027-01 differential at -1.50, sampled at the context date, is added to the formula panel: 69.96-1.50.
        var withDifferential=Priced("2026-12-01","12:00:00",[],[..marks,("D","-1.50")],differential:differentials[0]);
        Price("formula plus differential",Base(withDifferential),"3423","50");
        Price("formula plus differential D+1",Shifted(withDifferential,"D"),"3473","50");
        Code("A differential for another delivery month","recipe_input",()=>Priced("2026-12-01","12:00:00",[],[..marks,("D2","-1.50")],differential:differentials[1]));
        Equal("kind",CurveKinds.Of(Definition(node)),"observation_formula");
        Equal("kind with a differential",CurveKinds.Of(WithDifferential(node)),"observation_formula");
        // Formula text carries the typed terms and refuses a missing or unknown term member.
        var recipe=WithDifferential(node);var text=RecipeFormula.Format(recipe);var compiled=RecipeFormula.Compile(text,recipe.Code,recipe.Name);
        Equal("formula round trip",RecipeFormula.Format(compiled),text);
        Equal("formula text keeps the terms",compiled.SameGraph(recipe),true);
        Code("A term without its explicit spread position","recipe_formula",()=>RecipeFormula.Compile(text.Replace("\"lessPosition\":null,",""),recipe.Code,recipe.Name));
        Message("A blank position in formula text","recipe_definition","Observation term 2 position:",()=>RecipeFormula.Compile(
            text.Replace("\"ordinal\":1,\"group\":\"roll\",\"weight\":\"2\",\"position\":1,","\"ordinal\":1,\"group\":\"roll\",\"weight\":\"2\",\"position\":null,",StringComparison.Ordinal),recipe.Code,recipe.Name));
        Code("A term with an unknown member","recipe_formula",()=>RecipeFormula.Compile(text.Replace("\"ordinal\":0,","\"ordinal\":0,\"script\":\"x\",",StringComparison.Ordinal),recipe.Code,recipe.Name));
        // A position member present as anything but a whole number or null is a typed entry the term refuses; an absent member
        // is a malformed request.
        RecipeObservationTerm Bind(string member,string value)=>System.Text.Json.JsonSerializer.Deserialize<RecipeObservationTerm>(
            "{\"ordinal\":1,\"group\":\"roll\",\"weight\":\"2\","+(member=="position"?"":"\"position\":1,")+(member=="lessPosition"?"":"\"lessPosition\":null,")+
            (value.Length==0?"":$"\"{member}\":{value},")+"\"period\":\"trade_month_of_delivery_contract_v1\"}",
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        foreach(var (member,value) in new[]{("lessPosition","\"2e\""),("position","1.5"),("position","true"),("lessPosition","{}"),("position","\"1\""),("position","3000000000")})
            Message($"A {member} of {value}","recipe_definition","An observation term position must be a whole number from 1 to 120.",()=>Bind(member,value));
        Equal("a null second position binds",Bind("lessPosition","null").LessPosition is null,true);
        Equal("a whole second position binds",Bind("lessPosition","2").LessPosition==2,true);
        Equal("a null position binds",Bind("position","null").Position is null,true);
        try{Bind("position","");throw new Exception("An absent position bound.");}catch(System.Text.Json.JsonException){}
        // The declaration retains the terms and the resolved windows.
        string Manifest((ConnectedRecipeGraph Graph,ConnectedScenario Basis) priced)=>
            priced.Graph.Definitions.SelectMany(d=>d.Points).Select(p=>p.Expression).OfType<ConnectedRecipeProjection>().First().Declaration.ManifestId;
        var declared=Manifest(Priced("2026-12-01","12:00:00",[],marks));
        Equal("the same declaration",Manifest(Priced("2026-12-01","12:00:00",[],marks)),declared);
        Equal("a changed weight changes the graph",Definition(node).SameGraph(Definition(Term(node,1,t=>t with{Weight="3"}))),false);
        Equal("a changed weight changes the declaration",Manifest(Priced("2026-12-01","12:00:00",[],marks,terms:Term(node,1,t=>t with{Weight="3"})))!=declared,true);
        Equal("a changed period changes the declaration",Manifest(Priced("2026-12-01","12:00:00",[],marks,terms:Term(node,0,t=>t with{Period=RecipeEngine.TradeMonthPeriod})))!=declared,true);
        Equal("a shorter window changes the declaration",Manifest(Priced("2026-12-01","12:00:00",[],marks,
            w=>w.SetItem("crude",w["crude"].SetItem(0,w["crude"][0] with{Days=w["crude"][0].Days.RemoveAt(4)}))))!=declared,true);
        Console.WriteLine("PASS contract observation recipes: calendar and trade-month windows, expiry-day roll, differential, declaration identity, formula text and explicit refusals");
    }
    private static void Price(string label,ExactUnitPrice value,string numerator,string denominator)
    {if(value.Numerator!=numerator||value.Denominator!=denominator)throw new Exception($"{label}: expected {numerator}/{denominator}, got {value.Numerator}/{value.Denominator}");}
    private sealed class Visible:ICurveBindingResolver
    {
        public CurveKey Resolve(LogicalCurveKey curve,ResolvedCalculationContext context)=>throw new NotSupportedException();
        public void RequireVisible(CurveKey curve,ResolvedCalculationContext context){}
    }
    private static void Days(ImmutableArray<RecipeObservationWindow> windows,int term,params string[] days)
    {
        var actual=windows.Single(w=>w.Term==term).Days.Select(d=>d.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)).ToArray();
        if(!actual.SequenceEqual(days))throw new Exception($"Term {term}: expected {string.Join(",",days)}, got {string.Join(",",actual)}");
    }
    private static void Equal<T>(string label,T actual,T expected) where T:notnull
    {if(!actual.Equals(expected))throw new Exception($"{label}: expected {expected}, got {actual}");}
    private static void Message(string label,string code,string fragment,Action action)
    {
        try{action();}
        catch(MarketDataException error)when(error.Code==code)
        {if(!error.Message.Contains(fragment,StringComparison.Ordinal))throw new Exception($"{label}: expected a refusal naming '{fragment}', got: {error.Message}");return;}
        catch(MarketDataException error){throw new Exception($"{label}: expected {code}, got {error.Code}: {error.Message}");}
        throw new Exception(label+" was accepted.");
    }
    private static void Code(string label,string code,Action action)
    {
        try{action();}catch(MarketDataException error)when(error.Code==code){return;}
        catch(MarketDataException error){throw new Exception($"{label}: expected {code}, got {error.Code}: {error.Message}");}
        throw new Exception(label+" was accepted.");
    }
}
