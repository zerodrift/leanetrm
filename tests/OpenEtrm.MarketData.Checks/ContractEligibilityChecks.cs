using OpenEtrm.MarketData;

internal static class ContractEligibilityChecks
{
    public static void Run()
    {
        var from=DateTimeOffset.Parse("2020-12-01T00:00:00Z");
        var until=DateTimeOffset.Parse("2020-12-21T21:00:00Z");
        var revision=new DefinitionRevision(Guid.NewGuid(),Guid.NewGuid());
        var key=new ContractKey(revision,Guid.NewGuid());
        var policy=new ContractOperationWindow(key,ContractOperation.LiveCurveOutput,from,until,ContractOperationWindow.Version);
        void Is(DateTimeOffset at,EligibilityState expected)
        {
            var decision=ContractEligibility.Evaluate(policy,new(at,until.AddDays(1)),[]);
            if(decision.State!=expected||decision.Context.At!=at||decision.Window!=policy)
                throw new Exception($"Expected {expected} at {at:O}, got {decision.State}.");
        }
        Is(from.AddTicks(-10),EligibilityState.NotYetEligible);
        Is(from,EligibilityState.Eligible);
        Is(until.AddTicks(-10),EligibilityState.Eligible);
        Is(until,EligibilityState.EligibilityEnded);
        var marking=policy with{From=null,Implementation=ContractOperationWindow.MarkingCutoffVersion};
        if(ContractEligibility.At(marking,from.AddYears(-2))!=EligibilityState.Eligible||ContractEligibility.At(marking,until)!=EligibilityState.EligibilityEnded)
            throw new Exception("A declared marking-cutoff policy must not invent first-trading or definition-creation bounds.");
        Reject("contract_eligibility_context",()=>ContractEligibility.At(marking with{Operation=ContractOperation.NewTrade},from));
        var restriction=new ContractRestriction(Guid.NewGuid(),key,ContractOperation.LiveCurveOutput,
            from.AddDays(5),null,from.AddDays(8),"suspended");
        var beforeKnown=ContractEligibility.Evaluate(policy,new(from.AddDays(6),from.AddDays(7)),[restriction]);
        var afterKnown=ContractEligibility.Evaluate(policy,new(from.AddDays(6),from.AddDays(9)),[restriction]);
        if(beforeKnown.State!=EligibilityState.Eligible||afterKnown.State!=EligibilityState.Restricted||afterKnown.Reason!="suspended")
            throw new Exception("Restriction effective and knowledge time must both be respected.");
        var trading=policy with{Operation=ContractOperation.NewTrade,Until=until.AddHours(-3)};
        if(ContractEligibility.Evaluate(trading,new(until.AddHours(-2),until),[]).State!=EligibilityState.EligibilityEnded||
            ContractEligibility.Evaluate(policy,new(until.AddHours(-2),until),[]).State!=EligibilityState.Eligible)
            throw new Exception("Trading and marking must use independent configured windows.");
        Reject("contract_eligibility_policy",()=>ContractEligibility.Evaluate(policy with{Implementation="guess"},new(from,until),[]));
        Console.WriteLine("PASS contract eligibility: exact bounds, separate operations and bitemporal restrictions");
    }
    static void Reject(string code,Action action)
    {try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception($"Expected {code}.");}
}
