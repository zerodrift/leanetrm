using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Risk;

internal static class AlignedFactorHistoryChecks
{
    public static int Run()
    {
        var count=0;
        void Check(string label,Action check){check();count++;Console.WriteLine("PASS aligned factor history: "+label);}
        Check("one shared dated column per input, exact signed changes and deterministic ordering",()=>
        {
            var f=new Fixture();var result=f.Align([f.X,f.Y,f.X],f.Observations);
            if(result.Columns.Length!=2||result.Samples.Length!=2||!result.Available)throw new Exception("Shared instruments must not duplicate factors or changes.");
            var x=Index(result,f.X);var y=Index(result,f.Y);
            Equal(result.Samples[0].Changes[x],"2");Equal(result.Samples[1].Changes[x],"-3");
            Equal(result.Samples[0].Changes[y],"0");Equal(result.Samples[1].Changes[y],"-2");
            var replay=f.Align([f.Y,f.X,f.X],[..f.Observations.Reverse()]);
            if(result.Id!=replay.Id)throw new Exception("Manifest identity must not depend on enumeration order.");
            if(result.Samples.Any(s=>s.From.Marks.Length!=2||s.To.Marks.Any(m=>m is null)||s.To.Marks.Any(m=>m!.Lineage.IsEmpty)))throw new Exception("Every complete change needs exact endpoint and lineage evidence.");
        });
        Check("manifest and session identities are independent of the process culture",()=>
        {
            var f=new Fixture();var expected=f.Align().Id;var culture=System.Globalization.CultureInfo.CurrentCulture;
            try{System.Globalization.CultureInfo.CurrentCulture=System.Globalization.CultureInfo.GetCultureInfo("th-TH");if(f.Align().Id!=expected)throw new Exception("A display culture changed historical sample identities.");}
            finally{System.Globalization.CultureInfo.CurrentCulture=culture;}
        });
        Check("later corrections cannot hide an earlier visible head",()=>
        {
            var f=new Fixture();var original=f.Observations[1];
            var correction=f.Correct(original,"15",200,f.Forecast.KnownAt.AddSeconds(1));
            var past=f.Align(observations:f.Observations.Add(correction));
            Equal(past.Samples[0].Changes[Index(past,f.X)],"2");
            var present=FactorHistory.Align(f.Forecast with{KnownAt=correction.KnownAt!.Value,MarketKnownThrough=200},f.Policy,f.Calendar,f.Clock,[f.X,f.Y],f.Observations.Add(correction));
            Equal(present.Samples[0].Changes[Index(present,f.X)],"5");
            if(past.Id==present.Id||present.Samples[0].To.Marks[Index(present,f.X)]!.Lineage.Length!=2)throw new Exception("The corrected manifest must retain both correction identity and ancestry.");
        });
        Check("visible ineligible successor never resurrects a formerly final mark",()=>
        {
            var f=new Fixture();var original=f.Observations[1];var correction=f.Correct(original,"15",90,f.Forecast.KnownAt.AddSeconds(-1));
            correction=correction with{Observation=correction.Observation with{State=PublicationState.Preliminary}};
            var result=f.Align(observations:f.Observations.Add(correction));
            if(result.Samples.Length!=0||result.Rejected.Length!=2||result.Available||!result.Rejected.SelectMany(r=>r.Issues).Any(i=>i.Reason=="publication_state"))throw new Exception("A state-changing correction must make both adjacent returns unavailable.");
        });
        Check("missing session is never bridged, while holiday-adjacent sessions retain their original interval",()=>
        {
            var f=new Fixture();var missing=f.Align(observations:f.Observations.RemoveAt(1));
            if(missing.Samples.Length!=0||missing.Rejected.Length!=2||missing.Available)throw new Exception("Missing Tuesday must not create a Monday-to-Wednesday change.");
            var monday=new DateOnly(2026,1,5);var tuesday=monday.AddDays(1);var thursday=monday.AddDays(3);
            var calendar=f.Calendar with{To=thursday.AddDays(1),BusinessDates=[monday,tuesday,thursday]};
            var observations=f.Observations.Select(o=>o.Observation.ObservedAt.Date==monday.AddDays(2).ToDateTime(TimeOnly.MinValue).Date?
                o with{Observation=o.Observation with{ObservedAt=new DateTimeOffset(thursday.ToDateTime(new TimeOnly(16,0),DateTimeKind.Utc))}}:o).ToImmutableArray();
            var holiday=FactorHistory.Align(f.Forecast,f.Policy,calendar,FactorHistory.CaptureClock(calendar,f.Clock.Reference,PowerClock.Capture(TimeZoneInfo.Utc),"16:00:00","reject","reject"),[f.X,f.Y],observations);
            if(holiday.Samples.Length!=2||holiday.Samples[1].To.At-holiday.Samples[1].From.At!=TimeSpan.FromDays(2))throw new Exception("Declared holiday gaps are one adjacent business-session change.");
        });
        Check("off-close observations and stale earlier values remain unavailable",()=>
        {
            var f=new Fixture();var shifted=f.Observations.SetItem(1,f.Observations[1] with{Observation=f.Observations[1].Observation with{ObservedAt=f.Observations[1].Observation.ObservedAt.AddMinutes(-1)}});
            var result=f.Align(observations:shifted);
            if(result.Samples.Length!=0||!result.Rejected.SelectMany(r=>r.Issues).Any(i=>i.Reason=="asynchronous_close"))throw new Exception("An off-close mark cannot be carried to the close.");
        });
        Check("conflicting heads and changed units do not become a selected latest row",()=>
        {
            var f=new Fixture();var a=f.Correct(f.Observations[1],"13",80,f.Forecast.KnownAt.AddSeconds(-2));var b=f.Correct(f.Observations[1],"14",81,f.Forecast.KnownAt.AddSeconds(-1));
            var fork=f.Align(observations:f.Observations.Add(a).Add(b));
            if(!fork.Rejected.SelectMany(r=>r.Issues).Any(i=>i.Reason=="ambiguous_revision"))throw new Exception("An event order cannot choose between a correction fork.");
            var regime=f.Align(observations:f.Observations.SetItem(1,f.Observations[1] with{Unit=new(f.Unit.Currency,new(f.Unit.Quantity.Revision,"other_quantity"),ExactDecimal.Parse("1"))}));
            if(!regime.Rejected.SelectMany(r=>r.Issues).Any(i=>i.Reason=="mapping_regime"))throw new Exception("Historical source units cannot be silently substituted.");
        });
        Check("forecast market cutoff, explicit policies, minimum complete changes and cancellation",()=>
        {
            var f=new Fixture();Reject("risk_history_context",()=>FactorHistory.Align(f.Forecast with{MarketCutoff=new(2026,1,7,15,0,0,TimeSpan.Zero)},f.Policy,f.Calendar,f.Clock,[f.X,f.Y],f.Observations));
            Reject("risk_history_policy",()=>FactorHistory.Align(f.Forecast,f.Policy with{Transform="log"},f.Calendar,f.Clock,[f.X,f.Y],f.Observations));
            var insufficient=FactorHistory.Align(f.Forecast,f.Policy with{MinimumSamples=3},f.Calendar,f.Clock,[f.X,f.Y],f.Observations);
            if(insufficient.Available||insufficient.UnavailableReason!="insufficient_common_history"||insufficient.Samples.Length!=2)throw new Exception("Minimum samples counts complete changes without deleting their evidence.");
            using var cancel=new CancellationTokenSource();cancel.Cancel();
            try{FactorHistory.Align(f.Forecast,f.Policy,f.Calendar,f.Clock,[f.X,f.Y],f.Observations,cancel.Token);throw new Exception("Cancellation was ignored.");}catch(OperationCanceledException){}
        });
        Check("inconsistent shared factors and incomplete correction ancestry refuse",()=>
        {
            var f=new Fixture();Reject("risk_history_factor",()=>f.Align([f.X,f.X with{Factor=f.X.Factor with{Source=new(f.Unit.Currency.Revision,"other_source")}}]));
            var missing=f.Observations[1] with{Revises=new(f.Forecast.CatalogId,Guid.NewGuid())};
            Reject("risk_history_lineage",()=>f.Align(observations:f.Observations.SetItem(1,missing)));
        });
        Check("a retained manifest replays exact inputs and refuses altered vectors or clock evidence",()=>
        {
            var f=new Fixture();var result=f.Align();
            if(FactorHistory.Replay(result).Id!=result.Id)throw new Exception("Retained history must replay without a current-data resolver.");
            Reject("risk_history_manifest",()=>FactorHistory.Replay(result with{Samples=result.Samples.SetItem(0,result.Samples[0] with{Changes=[ExactDecimal.Parse("999"),default]})}));
            Reject("risk_history_calendar",()=>FactorHistory.Replay(result with{Clock=result.Clock with{Rules=result.Clock.Rules+"changed"}}));
        });
        Check("pinned local close rules preserve DST hours and reject gaps and folds",()=>
        {
            var f=new Fixture();var zone=PowerClock.Capture(TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));

            var days=ImmutableArray.Create(new DateOnly(2026,3,6),new DateOnly(2026,3,9),new DateOnly(2026,3,10));
            var calendar=f.Calendar with{From=days[0],To=days[^1].AddDays(1),BusinessDates=days};
            var clock=FactorHistory.CaptureClock(calendar,f.Clock.Reference,zone,"16:00:00","reject","reject");
            var forecast=f.Forecast with{ValuationAt=new(2026,3,11,22,0,0,TimeSpan.Zero),MarketCutoff=new(2026,3,11,22,0,0,TimeSpan.Zero),KnownAt=new(2026,3,11,23,0,0,TimeSpan.Zero)};
            var rows=f.Observations.Select((r,n)=>r with{Observation=r.Observation with{ObservedAt=zone.AtLocal(days[n%3],new(16,0)).ToUniversalTime()},KnownAt=forecast.KnownAt}).ToImmutableArray();
            var result=FactorHistory.Align(forecast,f.Policy,calendar,clock,[f.X,f.Y],rows);
            if(result.Samples.Length!=2||result.Sessions[1].Close-result.Sessions[0].Close!=TimeSpan.FromHours(71))throw new Exception("Business sessions must preserve the pinned DST close rather than adding 24 hours.");
            var gap=calendar with{From=new(2026,3,7),To=new(2026,3,10),BusinessDates=[new(2026,3,7),new(2026,3,8),new(2026,3,9)]};
            Reject("clock_rule",()=>FactorHistory.Align(forecast,f.Policy,gap,FactorHistory.CaptureClock(gap,f.Clock.Reference,zone,"02:30:00","reject","reject"),[f.X,f.Y],rows));
            var fold=calendar with{From=new(2026,10,31),To=new(2026,11,3),BusinessDates=[new(2026,10,31),new(2026,11,1),new(2026,11,2)]};
            Reject("clock_rule",()=>FactorHistory.Align(forecast with{ValuationAt=new(2026,11,3,22,0,0,TimeSpan.Zero),MarketCutoff=new(2026,11,3,22,0,0,TimeSpan.Zero)},f.Policy,fold,FactorHistory.CaptureClock(fold,f.Clock.Reference,zone,"01:30:00","reject","reject"),[f.X,f.Y],rows));
        });
        Check("frozen facts stay outside the shock universe and market-cutoff-future rows cannot change history",()=>
        {
            var f=new Fixture();var frozen=f.X with{Factor=f.X.Factor with{Input=f.X.Factor.Input with{Use=InputUse.FrozenFact}}};
            if(f.Align([f.X,f.Y,frozen]).Id!=f.Align().Id)throw new Exception("Only explicitly frozen inputs may be omitted.");
            var forecast=f.Forecast with{MarketCutoff=new(2026,1,7,16,0,0,TimeSpan.Zero)};
            var future=f.Observations[0] with{Observation=f.Observations[0].Observation with{Key=new(f.Forecast.CatalogId,Guid.NewGuid()),ObservedAt=new(2026,1,8,16,0,0,TimeSpan.Zero)}};
            var past=FactorHistory.Align(forecast,f.Policy,f.Calendar,f.Clock,[f.X,f.Y],f.Observations);
            if(FactorHistory.Align(forecast,f.Policy,f.Calendar,f.Clock,[f.X,f.Y],f.Observations.Add(future)).Id!=past.Id)throw new Exception("Forecast market cutoff must bound evidence even when valuation and knowledge are later.");
        });
        Check("factor/session matrices and observation versions refuse before calibration",()=>
        {
            var f=new Fixture();var from=new DateOnly(2020,1,1);
            var calendar=f.Calendar with{From=from,To=from.AddDays(1000),BusinessDates=[..Enumerable.Range(0,1000).Select(from.AddDays)]};
            var factors=Enumerable.Range(0,251).Select(_=>f.X with{Factor=f.X.Factor with{Input=new(new(f.X.Factor.Input.Series.Revision,Guid.NewGuid()),InputUse.Variable),Contract=new(f.X.Factor.Contract.Revision,Guid.NewGuid())}}).ToImmutableArray();
            Reject("risk_history_capacity",()=>FactorHistory.Align(f.Forecast,f.Policy,calendar,FactorHistory.CaptureClock(calendar,f.Clock.Reference,PowerClock.Capture(TimeZoneInfo.Utc),"16:00:00","reject","reject"),factors,[]));
            Reject("risk_history_capacity",()=>f.Align(observations:[..Enumerable.Repeat(f.Observations[0],FactorHistory.MaximumObservations+1)]));
        });
        Console.WriteLine($"{count} aligned factor history checks passed.");return count;
    }
    private static int Index(AlignedFactorHistory h,RiskHistoryFactor factor)=>Array.FindIndex(h.Columns.ToArray(),c=>c.Mapping.Factor.Input==factor.Factor.Input);
    private static void Equal(ExactDecimal value,string expected){if(value!=ExactDecimal.Parse(expected))throw new Exception($"Expected {expected}, got {value}.");}
    private static void Reject(string code,Action action){try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
    private sealed class Fixture
    {
        internal readonly PriceUnit Unit;
        internal readonly RiskHistoryForecast Forecast;
        internal readonly RiskHistoryPolicy Policy=new(RiskHistoryPolicy.Version1,CurveModel.NumericalPolicy,RiskHistoryPolicy.Raw,RiskHistoryPolicy.Heads,RiskHistoryPolicy.ExactCloses,RiskHistoryPolicy.Absolute,RiskHistoryPolicy.Complete,RiskHistoryPolicy.Fixed,2);
        internal readonly RiskHistoryCalendar Calendar;
        internal readonly RiskHistoryClock Clock;
        internal readonly RiskHistoryFactor X,Y;
        internal readonly ImmutableArray<RiskHistoryObservation> Observations;
        internal Fixture()
        {
            var revision=new DefinitionRevision(Guid.NewGuid(),Guid.NewGuid());var refs=new ReferenceRevision(Guid.NewGuid(),Guid.NewGuid());
            Unit=new(new(refs,"usd"),new(refs,"energy"),ExactDecimal.Parse("1"));
            var at=new DateTimeOffset(2026,1,8,16,0,0,TimeSpan.Zero);
            Forecast=new(Guid.NewGuid(),revision.CatalogId,at,at,at.AddHours(1),100,[new(revision,Guid.NewGuid(),new string('a',64))]);
            Calendar=new(new(refs,"business"),new(2026,1,5),new(2026,1,8),[new(2026,1,5),new(2026,1,6),new(2026,1,7)]);
            var clock=PowerClock.Capture(TimeZoneInfo.Utc);Clock=FactorHistory.CaptureClock(Calendar,new(refs,"utc"),clock,"16:00:00","reject","reject");
            var rows=ImmutableArray.CreateBuilder<RiskHistoryObservation>();
            RiskHistoryFactor Column(string name,string[] prices)
            {
                var series=new SeriesKey(revision,Guid.NewGuid());var contract=new ContractKey(revision,Guid.NewGuid());var quote=new ReferenceKey(refs,name);
                foreach(var (price,n) in prices.Select((p,n)=>(p,n)))rows.Add(new(new(new(revision.CatalogId,Guid.NewGuid()),series,new(refs,"source"),ObservationRole.TraderMark,PublicationState.Final,
                    new DateTimeOffset(Calendar.BusinessDates[n].ToDateTime(new TimeOnly(16,0),DateTimeKind.Utc)),ExactDecimal.Parse(price)),quote,Unit,contract,null,10+n,Forecast.KnownAt.AddDays(-1)));
                var last=rows[^1].Observation;
                return new(new(new(series,InputUse.Variable),quote,Unit,contract,last.Key,last.Source,last.Role,last.Value,[new(revision,name)],ExactDecimal.Parse("0.01")),PublicationState.Final);
            }
            X=Column("x",["10","12","9"]);Y=Column("y",["0","0","-2"]);Observations=rows.ToImmutable();
        }
        internal RiskHistoryObservation Correct(RiskHistoryObservation original,string value,int order,DateTimeOffset known)=>original with{
            Observation=original.Observation with{Key=new(Forecast.CatalogId,Guid.NewGuid()),Value=ExactDecimal.Parse(value)},Revises=original.Observation.Key,KnowledgeOrder=order,KnownAt=known};
        internal AlignedFactorHistory Align(ImmutableArray<RiskHistoryFactor> factors=default,ImmutableArray<RiskHistoryObservation> observations=default)=>
            FactorHistory.Align(Forecast,Policy,Calendar,Clock,factors.IsDefault?[X,Y]:factors,observations.IsDefault?Observations:observations);
    }
}
