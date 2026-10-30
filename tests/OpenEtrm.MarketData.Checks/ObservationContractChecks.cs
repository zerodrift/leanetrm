using System.Collections.Immutable;
using OpenEtrm.MarketData;

internal static class ObservationContractChecks
{
    public static void Run()
    {
        var revision=new DefinitionRevision(Guid.NewGuid(),Guid.NewGuid());
        var references=new ReferenceRevision(Guid.NewGuid(),Guid.NewGuid());
        DateTimeOffset At(int day)=>new(2030,11,day,18,0,0,TimeSpan.Zero);
        MarketContract Contract(int month,int expiry)=>new(new(revision,Guid.NewGuid()),ContractKind.Future,
            new(2030,month,1),new DateOnly(2030,month,1).AddMonths(1),At(expiry),new(references,"synthetic_"+month));
        var first=Contract(11,20);var second=Contract(12,25);
        var sequence=new ContractSequence(new(revision,"synthetic_months"),references,At(1),At(25),
            [new(1,first,At(1),new(2030,11,20)),new(2,second,At(1),new(2030,11,25))]);
        void Equal<T>(T actual,T expected){if(!Equals(actual,expected))throw new Exception($"Expected {expected}; got {actual}.");}
        void Reject(string code,Action action){try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
        var observations=new[]{new ContractObservation(0,new(2030,12,1),new(2030,12,2),At(19)),
            new ContractObservation(1,new(2030,12,2),new(2030,12,3),At(20))};
        var prompt=ObservationContractSelector.Prompt(0,ContractRollRule.AtExpiry());
        var plan=prompt.Resolve(sequence,observations);
        Equal(plan[0].Contract,first.Key);Equal(plan[1].Contract,second.Key);
        Equal(plan[0].Observation,observations[0]);Equal(plan[0].DeliveryFrom,first.DeliveryFrom);
        Equal(plan[1].Expiry,At(25));Equal(plan[1].SelectionAt,At(20));
        Equal(prompt.Resolve(sequence,observations)[0],plan[0]);
        var next=ObservationContractSelector.Prompt(1,ContractRollRule.AtExpiry());
        Equal(next.Resolve(sequence,[observations[0]])[0].Contract,second.Key);
        Reject("sequence_horizon",()=>next.Resolve(sequence,[observations[1] with{Ordinal=0}]));
        var early=ObservationContractSelector.Prompt(0,ContractRollRule.Explicit([
            new(first.Key,At(18)),new(second.Key,At(24))]));
        Equal(early.Resolve(sequence,[observations[0]])[0].Contract,second.Key);
        Equal(ObservationContractSelector.Exact(first.Key).Resolve(sequence,observations)[1].Contract,first.Key);
        Reject("contract_selection",()=>ObservationContractSelector.Prompt(-1,ContractRollRule.AtExpiry()));
        Reject("contract_selection",()=>ObservationContractSelector.Exact(default));
        Reject("contract_roll",()=>ObservationContractSelector.Prompt(0,ContractRollRule.Explicit([new(first.Key,At(21)),new(second.Key,At(24))])).Resolve(sequence,observations));
        Reject("contract_roll",()=>ObservationContractSelector.Prompt(0,ContractRollRule.Explicit([new(first.Key,At(18))])).Resolve(sequence,observations));
        Reject("contract_roll",()=>ObservationContractSelector.Prompt(0,ContractRollRule.Explicit([new(second.Key,At(18)),new(first.Key,At(19))])).Resolve(sequence,observations));
        Reject("contract_observation",()=>prompt.Resolve(sequence,[observations[0] with{Ordinal=1}]));
        Reject("contract_observation",()=>prompt.Resolve(sequence,[observations[0] with{FixingAt=default}]));
        Reject("contract_observation",()=>prompt.Resolve(sequence,[observations[0] with{To=observations[0].From}]));
        Reject("sequence_coverage",()=>prompt.Resolve(sequence,[observations[0] with{FixingAt=At(25)}]));
        var unavailable=new ContractSequence(sequence.Key,references,At(1),At(25),[sequence.Entries[0],sequence.Entries[1] with{EligibleFrom=At(22)}]);
        Reject("sequence_ineligible",()=>prompt.Resolve(unavailable,[observations[1] with{Ordinal=0}]));
        var single=new ContractSequence(sequence.Key,references,At(1),At(20),[sequence.Entries[0]]);
        Equal(ObservationContractSelector.Exact(first.Key).Resolve(single,[observations[1] with{Ordinal=0}])[0].Contract,first.Key);
        // Selection returns a dated identity; it never claims that an expired contract has a usable mark.
        Equal(ObservationContractSelector.Exact(first.Key).Resolve(sequence,[observations[1] with{Ordinal=0}])[0].Expiry,At(20));
        Console.WriteLine("PASS observation contract selection: exact identities, zero-based offsets, fixing anchors, early rolls and fail-closed boundaries");
    }
}
