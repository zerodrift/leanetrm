using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Risk;

internal static class IntervalAnchorChecks
{
    private static void Equal<T>(T actual,T expected)
    {if(!EqualityComparer<T>.Default.Equals(actual,expected))throw new Exception($"Expected {expected}; got {actual}.");}
    private static void Reject(string code,Action action)
    {try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
    public static int Run()
    {
        var f=new Fixture();var quote=f.Quotes.Single(q=>q.Key.Code=="power_out");var unit=PriceUnit.FromQuote(quote);
        var clock=PowerClock.Capture(TimeZoneInfo.Utc);
        var from=Fixture.Utc("2031-11-01T00:00:00Z");var to=from.AddDays(30);var at=from.AddDays(9).AddHours(12.5);
        var monthly=new MarketContract(f.K(9201),ContractKind.Forward,new(2031,11,1),new(2031,12,1),from.AddDays(-1),null);
        var balance=monthly with{Key=f.K(9202),DeliveryFrom=new(2031,11,11),Expiry=from.AddDays(10)};
        Curve Curve(string code,MarketContract c)=>new(f.C(code),quote.Key,f.R("location"),f.R("hourly"),new(SettlementKind.Index,f.R("index"),null),
            CurveRule.Direct,[c.Key],[],ObservationRole.TraderMark,f.R("source"),null);
        var period=Curve("period",monthly) with{ConstituentExpiry=ConstituentExpiryRule.FreezeFinalSettlement};var bal=Curve("balance",balance);
        var model=new CurveModel(f.Revision,f.Reference,f.Quantities,f.Quotes,[monthly,balance],[period,bal],[],[],
            [new(f.S(9201),monthly.Key,quote.Key,null),new(f.S(9202),balance.Key,quote.Key,null)]);
        // A synthetic contract-final quote is explicitly supplied under its expiry policy. It is not a
        // completed monthly index or hourly settlement; missing publisher finals are tested separately.
        SelectedObservation Obs(int n,InputUse use,ObservationRole role,string value)=>new(new(f.S(n),use),
            new(new(f.Revision.CatalogId,Guid.NewGuid()),f.S(n),f.R("source"),role,PublicationState.Final,from,ExactDecimal.Parse(value)));
        var final=Obs(9201,InputUse.FrozenFact,ObservationRole.FinalContractSettlement,"58");
        var live=Obs(9202,InputUse.Variable,ObservationRole.TraderMark,"70");
        var snapshot=new MarketSnapshot(Guid.NewGuid(),f.Revision,at,at,InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,[final,live]);
        var definition=new ShapedCurveDefinition("shape",ShapeMethod.Additive,BlockShaping.CalibrationPolicy,BlockShaping.OperatorVersion,Guid.NewGuid(),new(clock,60,[]),
            [new(period.Key,BlockMembership.All,null,[monthly])]){BalanceBlocks=[new(bal.Key,BlockMembership.All,null,[balance])]};
        var curve=new ShapedIntervalCurve(f.C("shape"),unit,new("interval_end",60,IntervalActualRule.SupportedVersion),definition)
            {SelectedAnchors=ImmutableHashSet.Create(monthly.Key,balance.Key),FrozenKnowledge=ImmutableDictionary<ObservationKey,DateTimeOffset>.Empty.Add(final.Observation.Key,from)};
        var cells=Enumerable.Range(0,720).Select(h=>new SeriesCell(from.AddHours(h),from.AddHours(h+1),ExactDecimal.Parse("0"),1)).ToArray();
        var actuals=Enumerable.Range(0,227).Select(h=>new IntervalActual(from.AddHours(h),from.AddHours(h+1),new(ExactDecimal.Parse("40"),unit),at,Guid.NewGuid(),1)).ToArray();
        PreparedCurve Prepare(ShapedIntervalCurve c,IReadOnlyList<IntervalActual>? observed=null,DateTimeOffset? known=null)=>
            PreparedCurve.Prepare(c,[(from,to)],cells,observed??actuals,at,known??at);
        var prepared=Prepare(curve);var requests=prepared.Dependencies(new(2031,11,1));
        var built=model.Build(snapshot,requests);var curves=new Dictionary<CurveKey,PreparedCurve>{{curve.Key,prepared}};
        void RejectKnowledge(ImmutableDictionary<ObservationKey,DateTimeOffset> knowledge,DateTimeOffset known)
        {
            var invalid=Prepare(curve with{FrozenKnowledge=knowledge},known:known);
            var rejected=new IntervalPriceResolver(built,new Dictionary<CurveKey,PreparedCurve>{{curve.Key,invalid}});
            for(var attempt=0;attempt<2;attempt++)Reject("frozen_fact",()=>rejected.Prices(curve.Key,new(2031,11,1)));
        }
        RejectKnowledge(ImmutableDictionary<ObservationKey,DateTimeOffset>.Empty,at);
        RejectKnowledge(curve.FrozenKnowledge.SetItem(final.Observation.Key,at.AddHours(1)),at);
        // Even a later research cutoff cannot bring later-learned finals into live valuation cells.
        RejectKnowledge(curve.FrozenKnowledge.SetItem(final.Observation.Key,at.AddHours(1)),at.AddHours(2));
        var resolver=new IntervalPriceResolver(built,curves);var rows=resolver.Prices(curve.Key,new(2031,11,1));
        Equal(rows.Count(r=>r.State==IntervalPriceState.Actual),227);
        var pending=rows.Single(r=>r.Start==from.AddHours(227));Equal(pending.State,IntervalPriceState.Unresolved);
        Equal(pending.Price,null);Equal(pending.Estimate!.Numerator,"58");Equal(pending.Reason!.Code,"awaiting_actual");
        Equal(rows.Count(r=>r.State==IntervalPriceState.Modeled),12);
        Equal(rows.Where(r=>r.State==IntervalPriceState.Modeled).All(r=>r.Price!.Numerator=="58" && r.Publication=="final_settlement_anchor" && r.LiveMarketRiskCoverage=="frozen_anchor_only"),true);
        Equal(rows.Count(r=>r.State==IntervalPriceState.Projected),480);
        var shock=model.Scenarios(built,[new(live.Input,quote.Key,unit,ExactDecimal.Parse("1"),QuoteBump.Convention)]).Single();
        foreach(var (scenario,expected) in new[]{(shock.Up,"71"),(shock.Down,"69")})
        {
            var result=new IntervalPriceResolver(scenario,curves,resolver).Prices(curve.Key,new(2031,11,1));
            for(var i=0;i<result.Length;i++)
                if(result[i].Start>=from.AddDays(10))Equal(result[i].Price!.Numerator,expected);else Equal(result[i],rows[i]);
        }
        // Two selected live balance contracts covering the same product are ambiguous, even if their dates differ.
        var extra=balance with{Key=f.K(9203),DeliveryFrom=new(2031,11,12),Expiry=from.AddDays(11)};
        Reject("interval_anchor_ambiguous",()=>Prepare(curve with{Definition=definition with{BalanceBlocks=[..definition.BalanceBlocks,new(f.C("extra"),BlockMembership.All,null,[extra])]},
            SelectedAnchors=curve.SelectedAnchors!.Add(extra.Key)}));
        Reject("interval_anchor_missing",()=>Prepare(curve with{SelectedAnchors=ImmutableHashSet.Create(balance.Key)}));
        // An interval no period block covers has no price; one two period blocks cover is an ambiguous definition.
        Reject("interval_block_coverage",()=>new IntervalPriceResolver(built,new Dictionary<CurveKey,PreparedCurve>{{curve.Key,Prepare(curve with{Definition=definition with{Blocks=[]}})}}).Prices(curve.Key,new(2031,11,1)));
        Reject("interval_block_partition",()=>new IntervalPriceResolver(built,new Dictionary<CurveKey,PreparedCurve>{{curve.Key,Prepare(curve with{Definition=definition with{Blocks=[..definition.Blocks,..definition.Blocks]}})}}).Prices(curve.Key,new(2031,11,1)));
        Reject("interval_actual_missing",()=>Prepare(curve,actuals.Skip(1).ToArray()));
        var laterActual=new IntervalActual(from.AddDays(10),from.AddDays(10).AddHours(1),new(ExactDecimal.Parse("999"),unit),at.AddHours(1),Guid.NewGuid(),2);
        var noHindsight=Prepare(curve,[..actuals,laterActual],at.AddHours(2));
        var noHindsightRows=new IntervalPriceResolver(built,new Dictionary<CurveKey,PreparedCurve>{{curve.Key,noHindsight}}).Prices(curve.Key,new(2031,11,1));
        Equal(noHindsightRows.Single(r=>r.Start==laterActual.Start).Price!.Numerator,"70");
        Console.WriteLine("PASS resolver anchors: whole-period frozen model, actual override, unresolved estimate, explicit live balance, ambiguity/gaps and certified knowledge without hindsight");
        PeriodFixings();
        return 2;
    }
    private static void PeriodFixings()
    {
        var f=new IndexFixingChecks.FixingFixture(monthly:true);
        var curve=new PeriodContractCurve(f.Curve.Key,PriceUnit.FromQuote(f.Quote),[..f.Projections.Select(p=>p.Contract)],f.Fixings);
        var from=new DateTimeOffset(f.From.ToDateTime(TimeOnly.MinValue,DateTimeKind.Utc));
        var to=new DateTimeOffset(f.To.ToDateTime(TimeOnly.MinValue,DateTimeKind.Utc));
        IntervalPriceResolution Resolve(MarketSnapshot snapshot,PeriodContractCurve? selected=null)
        {
            selected??=curve;var prepared=PreparedCurve.Prepare(selected,[(from,to)],[],[],snapshot.ValuationAt,snapshot.ValuationAt);
            return new IntervalPriceResolver(f.Model.Build(snapshot,prepared.Dependencies(f.From)),new Dictionary<CurveKey,PreparedCurve>{{selected.Key,prepared}}).Prices(selected.Key,f.From).Single();
        }
        var projected=Resolve(f.Snapshot(f.Fixings[0].FixingAt.AddDays(-2)));Equal(projected.State,IntervalPriceState.Projected);Equal(projected.Price!.Numerator,"11");
        var snapshot=f.Snapshot(f.Fixings[0].FixingAt,1);
        var fixed_=Resolve(snapshot);Equal(fixed_.State,IntervalPriceState.PublishedFixing);Equal(fixed_.Price!.Numerator,"10");Equal(fixed_.Publication,"published_fixing");
        Reject("index_fixing_missing",()=>Resolve(f.Snapshot(f.Fixings[0].FixingAt)));
        Reject("source_expired",()=>Resolve(snapshot,curve with{Fixings=[]}));
        var wrong=snapshot with{Selections=[..snapshot.Selections.Select(s=>s.Input.Use==InputUse.FrozenFact?s with{Observation=s.Observation with{Source=f.Base.R("wrong")}}:s)]};
        Reject("index_fixing_selection",()=>Resolve(wrong));
        var settlement=snapshot with{Selections=[..snapshot.Selections.Select(s=>s.Input.Use==InputUse.FrozenFact?s with{Observation=s.Observation with{Role=ObservationRole.FinalContractSettlement}}:s)]};
        Reject("index_fixing_selection",()=>Resolve(settlement));
        Console.WriteLine("PASS resolver period: exact qualified fixing consumer shares swap eligibility; expired mark/settlement fallback refused");
    }
}
