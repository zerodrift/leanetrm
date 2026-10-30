using System.Collections.Immutable;

namespace OpenEtrm.MarketData;

public sealed record ContractRollBoundary(ContractKey Contract,DateTimeOffset RollAt);
public sealed record ContractObservation(int Ordinal,DateOnly From,DateOnly To,DateTimeOffset FixingAt);
public sealed record ResolvedObservationContract(ContractObservation Observation,CurveKey Sequence,
    ReferenceRevision References,string Implementation,DateTimeOffset SelectionAt,ContractKey Contract,
    DateOnly DeliveryFrom,DateOnly DeliveryTo,DateTimeOffset Expiry,DateTimeOffset EligibleFrom);

// Roll boundaries are selection terms, not replacement exchange expirations.
// Calendar rules may supply explicit boundaries after resolving their own pinned calendars.
public sealed class ContractRollRule
{
    public const string ExpiryImplementation="contract_expiry_roll_v1";
    public const string ExplicitImplementation="explicit_contract_roll_v1";
    public string Implementation { get; }
    public ImmutableArray<ContractRollBoundary> Boundaries { get; }
    private ContractRollRule(string implementation,ImmutableArray<ContractRollBoundary> boundaries)
    {Implementation=implementation;Boundaries=boundaries;}
    public static ContractRollRule AtExpiry()=>new(ExpiryImplementation,[]);
    public static ContractRollRule Explicit(IEnumerable<ContractRollBoundary> boundaries)
    {
        if(boundaries is null)throw Invalid();
        var values=boundaries.Take(10_001).ToImmutableArray();
        if(values.Length is <1 or >10_000||values.Any(value=>value is null||!ObservationContractSelector.FiniteUtc(value.RollAt))||
            values.Select(value=>value.Contract).Distinct().Count()!=values.Length)throw Invalid();
        return new(ExplicitImplementation,values);
    }
    internal ImmutableArray<DateTimeOffset> Compile(ContractSequence sequence)
    {
        if(Implementation==ExpiryImplementation)return [];
        if(Boundaries.Length!=sequence.Entries.Length)throw Invalid();
        for(var i=0;i<Boundaries.Length;i++)
        {
            var boundary=Boundaries[i];var entry=sequence.Entries[i];
            if(boundary.Contract!=entry.Contract.Key||boundary.RollAt>entry.Contract.Expiry||
                boundary.RollAt<=entry.EligibleFrom||i>0&&Boundaries[i-1].RollAt>=boundary.RollAt)throw Invalid();
        }
        return [..Boundaries.Select(boundary=>boundary.RollAt)];
    }
    private static MarketDataException Invalid()=>new("contract_roll",
        "Supply one ordered explicit roll instant per exact sequence contract, after eligibility and no later than expiry.");
}

// A reusable selection definition. It has no valuation clock: every observation
// carries the fixing instant at which its contract is selected, including future observations.
public sealed class ObservationContractSelector
{
    public const string Implementation="observation_fixing_contract_v1";
    public ContractKey? Contract { get; }
    public int? PromptOffset { get; }
    public ContractRollRule? Roll { get; }
    private ObservationContractSelector(ContractKey? contract,int? promptOffset,ContractRollRule? roll)
    {Contract=contract;PromptOffset=promptOffset;Roll=roll;}
    public static ObservationContractSelector Exact(ContractKey contract)
    {
        if(contract.Id==Guid.Empty||contract.Revision.CatalogId==Guid.Empty||contract.Revision.RevisionId==Guid.Empty)
            throw Invalid();
        return new(contract,null,null);
    }
    public static ObservationContractSelector Prompt(int offset,ContractRollRule roll)
    {
        if(offset<0||offset>=10_000||roll is null)throw Invalid();
        return new(null,offset,roll);
    }
    public ImmutableArray<ResolvedObservationContract> Resolve(ContractSequence sequence,IEnumerable<ContractObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        if(observations is null)throw ObservationError();
        var rows=observations.Take(10_001).ToImmutableArray();
        if(rows.Length is <1 or >10_000)throw ObservationError();
        var rolls=Roll?.Compile(sequence)??[];
        var result=ImmutableArray.CreateBuilder<ResolvedObservationContract>(rows.Length);
        for(var i=0;i<rows.Length;i++)
        {
            var row=rows[i];
            if(row is null||row.Ordinal!=i||row.From>=row.To||row.To.Year>=9999||!FiniteUtc(row.FixingAt)||
                i>0&&rows[i-1].To>row.From)throw ObservationError();
            var entry=Select(sequence,row.FixingAt,rolls);
            result.Add(new(row,sequence.Key,sequence.References,Implementation,row.FixingAt,entry.Contract.Key,
                entry.Contract.DeliveryFrom,entry.Contract.DeliveryTo,entry.Contract.Expiry,entry.EligibleFrom));
        }
        return result.MoveToImmutable();
    }
    internal ContractSequenceEntry Select(ContractSequence sequence,DateTimeOffset at)=>Select(sequence,at,Roll?.Compile(sequence)??[]);
    private ContractSequenceEntry Select(ContractSequence sequence,DateTimeOffset at,ImmutableArray<DateTimeOffset> rolls)
    {
        if(!FiniteUtc(at))throw new MarketDataException("sequence_context","Supply a finite UTC selection instant.");
        if(Contract is {} exact)
        {
            var entry=sequence.Entries.SingleOrDefault(entry=>entry.Contract.Key==exact)
                ??throw new MarketDataException("contract_selection","The exact contract is not in the selected pinned sequence.");
            if(at<entry.EligibleFrom)throw new MarketDataException("sequence_ineligible","The exact contract is not yet eligible at this selection instant.");
            return entry;
        }
        if(at<sequence.From||at>=sequence.To)throw new MarketDataException("sequence_coverage","The selection instant is outside the pinned sequence coverage.");
        var low=0;var high=sequence.Entries.Length;
        while(low<high){var middle=low+(high-low)/2;if((Roll!.Implementation==ContractRollRule.ExpiryImplementation?sequence.Entries[middle].Contract.Expiry:rolls[middle])<=at)low=middle+1;else high=middle;}
        var target=(long)low+PromptOffset!.Value;
        if(target>=sequence.Entries.Length)throw new MarketDataException("sequence_horizon","The requested prompt offset is beyond the supplied roll horizon.");
        for(var i=low;i<=target;i++)
            if(sequence.Entries[i].EligibleFrom>at)throw new MarketDataException("sequence_ineligible","An expected contract is not yet eligible; it cannot be skipped.");
        return sequence.Entries[(int)target];
    }
    internal static bool FiniteUtc(DateTimeOffset value)=>value.Offset==TimeSpan.Zero&&value>DateTimeOffset.MinValue&&value.Year<9999;
    private static MarketDataException Invalid()=>new("contract_selection","Choose an exact pinned contract, or a zero-based prompt offset with an explicit roll rule.");
    private static MarketDataException ObservationError()=>new("contract_observation","Supply ordered nonoverlapping observation periods, consecutive ordinals and explicit finite UTC fixing instants.");
}
