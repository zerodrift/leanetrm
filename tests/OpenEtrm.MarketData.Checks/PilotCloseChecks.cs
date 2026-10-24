using OpenEtrm.MarketData;

internal static class PilotCloseChecks
{
    public static int Run()
    {
        var passed=0;
        void Check(string name,Action test){test();passed++;Console.WriteLine("PASS pilot close: "+name);}
        void Equal<T>(T actual,T expected){if(!Equals(actual,expected))throw new Exception($"Expected {expected}; got {actual}.");}
        void Reject(Action test){try{test();}catch(MarketDataException e)when(e.Code=="pilot_generation"){return;}throw new Exception("Expected pilot_generation.");}
        ExactDecimal D(string value)=>ExactDecimal.Parse(value);
        var reference=new ReferenceRevision(Guid.Parse("10000000-0000-0000-0000-000000000001"),Guid.Parse("20000000-0000-0000-0000-000000000001"));
        var definitions=new DefinitionRevision(Guid.Parse("40000000-0000-0000-0000-000000000001"),Guid.Parse("60000000-0000-0000-0000-000000000001"));
        ReferenceKey R(string code)=>new(reference,code);
        var quote=new QuoteSpecification(R("synthetic_quote"),QuoteKind.Outright,R("usd"),R("gas_energy"),D("1"),D("0.001"),D("0.0025"),D("-100"),D("100"));
        var anchor=new PilotPriceAnchor(new(definitions,Guid.Parse("70000000-0000-0000-0000-000000000001")),
            new(definitions.CatalogId,Guid.Parse("80000000-0000-0000-0000-000000000001")),R("synthetic_source"),"synthetic",
            DateTimeOffset.Parse("2026-10-19T20:00:00Z"),ObservationRole.DailySettlement,D("3"),quote);
        var close=DateTimeOffset.Parse("2026-10-20T20:00:00Z");
        Check("seeded marks match independently calculated SHA256 tick moves and preserve quote units",()=>
        {
            // Independent SHA256 and integer-modulus oracle: seed17 gives-1 tick;
            // seed18 gives+6 ticks. Clearing increment is0.0025, not trading tick.
            var first=PilotMarkGenerator.Generate(anchor,close,17,7,PilotMarkGenerator.Implementation);
            Equal(first.MoveTicks,-1);Equal(first.Value.ToString(),"2.9975");
            Equal(PilotMarkGenerator.Generate(anchor,close,18,7,PilotMarkGenerator.Implementation).Value.ToString(),"3.015");
            Equal(first,PilotMarkGenerator.Generate(anchor,close,17,7,PilotMarkGenerator.Implementation));
            Equal(first.Anchor,anchor);Equal(first.Anchor.Value.ToString(),"3");
        });
        Check("generation refuses unknown provenance, policies, time boundaries and unsupported facts",()=>
        {
            foreach(var changed in new[]{anchor with{SourceKind="external_document"},anchor with{Role=ObservationRole.FinalContractSettlement},
                anchor with{Role=ObservationRole.Parameter},anchor with{Value=D("3.001")},anchor with{ObservedAt=close},
                anchor with{Source=new(new(Guid.NewGuid(),Guid.NewGuid()),"synthetic_source")}})
                Reject(()=>PilotMarkGenerator.Generate(changed,close,17,7,PilotMarkGenerator.Implementation));
            Reject(()=>PilotMarkGenerator.Generate(anchor,close,17,7,"unknown"));
            Reject(()=>PilotMarkGenerator.Generate(anchor,close,-1,7,PilotMarkGenerator.Implementation));
            Reject(()=>PilotMarkGenerator.Generate(anchor,close,17,0,PilotMarkGenerator.Implementation));
            Reject(()=>PilotMarkGenerator.Generate(anchor,close.ToOffset(TimeSpan.FromHours(1)),17,7,PilotMarkGenerator.Implementation));
        });
        Check("a generated value outside the declared domain fails instead of clamping or retrying",()=>
            Reject(()=>PilotMarkGenerator.Generate(anchor with{Value=D("100")},close,18,7,PilotMarkGenerator.Implementation)));
        return passed;
    }
}
