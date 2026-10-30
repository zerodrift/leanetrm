using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

internal static class ContractObservationScheduleChecks
{
    internal static void Run()
    {
        var definitions=new DefinitionRevision(Guid.NewGuid(),Guid.NewGuid());var references=new ReferenceRevision(Guid.NewGuid(),Guid.NewGuid());
        ReferenceKey R(string code)=>new(references,code);
        var quote=new QuoteSpecification(R("price"),QuoteKind.Outright,R("usd"),R("energy"),ExactDecimal.Parse("1"),ExactDecimal.Parse("0.01"),ExactDecimal.Parse("0.01"),ExactDecimal.Parse("-1000"),ExactDecimal.Parse("1000"));
        DateTimeOffset At(DateOnly day)=>new(day.ToDateTime(new TimeOnly(18,0),DateTimeKind.Utc));
        var from=new DateOnly(2040,2,1);var to=from.AddMonths(1);var payment=At(to.AddDays(2));
        var contracts=new[]{new MarketContract(new(definitions,Guid.NewGuid()),ContractKind.Future,new(2040,6,1),new(2040,7,1),At(new(2040,5,25)),R("june")),
            new MarketContract(new(definitions,Guid.NewGuid()),ContractKind.Future,new(2040,7,1),new(2040,8,1),At(new(2040,6,25)),R("july"))};
        var series=new[]{new SeriesKey(definitions,Guid.NewGuid()),new SeriesKey(definitions,Guid.NewGuid())};
        var observations=Enumerable.Range(0,to.DayNumber-from.DayNumber).Select(n=>
        {
            var selected=n<14?0:1;var observation=new ContractObservation(n,from.AddDays(n),from.AddDays(n+1),At(from.AddDays(n)));
            return new ContractObservationLookup(new(new(definitions,"futures"),contracts[selected].Key),contracts[selected],quote,observation,
                observation.FixingAt,R("exchange"),series[selected],ObservationRole.DailySettlement,ContractObservationExpiry.RequireLive,"contract_observation_source_v1");
        }).ToImmutableArray();
        var period=new FloatingSidePeriod(from,to,observations[0].Observation.FixingAt,payment,FloatingAveraging.CalendarDayArithmetic,
            [..observations.Select(o=>new SwapProjection(o.Market,o.Contract))],[],null,null,null,null,new ExactRatio(1,1),FloatingSide.ContractObservationImplementation)
            {ContractObservations=observations};
        var floating=new FloatingSide(Guid.NewGuid(),quote,new(ExactDecimal.Parse("1"),quote.Quantity),[period]);
        var definition=ScheduleDefinitionCompiler.Generate(floating);var program=BookedSideProgram.Compile(definition);var content=program.Write();
        var loaded=BookedSideProgram.Read(content,new(BookedSideProgram.Digest(content),definition.Manifest,definition.Family,definition.EconomicIdentity,definition.PricingBindingIdentity));
        var prices=loaded.Document.Instructions.Where(i=>i.Output.Operation=="contract_observation_price").ToArray();
        if(prices.Length!=29)throw new Exception("A monthly payment must retain its 29 daily futures observations, not monthly contract-delivery windows.");
        for(var n=0;n<prices.Length;n++)
        {
            var row=prices[n].Output;var expected=observations[n];
            if(row.ObservationFrom!=expected.Observation.From||row.ObservationTo!=expected.Observation.To||
                row.DeliveryFrom!=from||row.DeliveryTo!=to||row.Parameters["selectionAt"]!=expected.SelectionAt.ToString("O")||
                row.Parameters["sampleAt"]!=expected.Observation.FixingAt.ToString("O")||
                prices[n].Bindings.Single(b=>b.Port=="market").Market!=expected.Market||
                prices[n].Bindings.Single(b=>b.Port=="settlement").Series!=expected.Series)
                throw new Exception("Stored futures source lost its observation interval, independent delivery, anchor or exact contract/series.");
        }
        var first=prices[0];
        void Reject(ScheduleDefinitionRow row,IEnumerable<ScheduleDefinitionBinding> bindings,string message)
        {
            try { _=ContractObservationScheduleSource.Read(row,bindings,contracts[0],quote); }
            catch(MarketDataException) { return; }
            throw new Exception(message);
        }
        Reject(first.Output with{ObservationTo=first.Output.ObservationTo!.Value.AddDays(1)},first.Bindings,
            "A retained daily source must reject an expanded observation interval.");
        Reject(first.Output with{Parameters=first.Output.Parameters.SetItem("selectionAt",At(from.AddDays(1)).ToString("O"))},first.Bindings,
            "A retained source must reject a changed selection anchor.");
        Reject(first.Output,first.Bindings.Select(b=>b.Port=="settlement"?b with{Coordinate="TraderMark"}:b),
            "A futures source must reject a retained trader-mark role.");
        var fixedSide=new FixedSide(Guid.NewGuid(),new(ExactDecimal.Parse("3"),quote),new(ExactDecimal.Parse("1"),quote.Quantity),from,to,payment,FixedSide.Implementation,quote);
        var swap=new FinancialSwapInstrument(Guid.NewGuid(),[new(fixedSide,ExactDecimal.Parse("-1")),new(floating,ExactDecimal.Parse("1"))],FinancialSwapInstrument.Implementation,false);
        var retained=RetainedSideSchedules.Attach(swap,new Dictionary<Guid,ScheduleDefinition>{{fixedSide.Id,ScheduleDefinitionCompiler.Generate(fixedSide)},{floating.Id,definition}});
        var reloaded=((SidePaymentSchedule)retained.Floating.GenerateSchedule()).Payments.SelectMany(p=>p.Prices).Select(p=>p.ContractObservation).ToArray();
        if(!reloaded.SequenceEqual(observations))throw new Exception("The retained valuation adapter changed a stored dated-contract observation.");
        Console.WriteLine("PASS contract observation schedules: distinct observation/delivery windows, anchors, roles and concrete futures survive executable bytes and retained loading");
    }
}
