using System.Collections.Immutable;
using System.Text.Json.Serialization;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

public sealed record ObservationContractSelectionInput([property:JsonRequired]string Mode,
    [property:JsonRequired]ContractKey? Contract,[property:JsonRequired]int? PromptOffset,
    [property:JsonRequired]string? Roll,[property:JsonRequired]ImmutableArray<ContractRollBoundary>? Boundaries);
public sealed record ObservationContractInput([property:JsonRequired]int Ordinal,[property:JsonRequired]DateOnly From,
    [property:JsonRequired]DateOnly To,[property:JsonRequired]DateTimeOffset FixingAt);
public sealed record ObservationContractPreviewRequest([property:JsonRequired]string Curve,
    [property:JsonRequired]ObservationContractSelectionInput Selection,
    [property:JsonRequired]ImmutableArray<ObservationContractInput> Observations);

// Authoring resolution only. It returns concrete dated contracts, never prices,
// and does not admit these new terms to booking before storage and pricing support them.
public static class ObservationContractPreview
{
    public static ImmutableArray<ResolvedObservationContract> Resolve(ContractSequence sequence,ObservationContractPreviewRequest request)
    {
        if(request is null||request.Selection is not {} input||request.Curve!=sequence.Key.Code||request.Observations.IsDefault||request.Observations.Any(row=>row is null))
            throw Invalid();
        var selector=input switch
        {
            {Mode:"exact",Contract:{} contract,PromptOffset:null,Roll:null,Boundaries:null}=>ObservationContractSelector.Exact(contract),
            {Mode:"prompt_offset",Contract:null,PromptOffset:{} offset,Roll:ContractRollRule.ExpiryImplementation,Boundaries:null}=>
                ObservationContractSelector.Prompt(offset,ContractRollRule.AtExpiry()),
            {Mode:"prompt_offset",Contract:null,PromptOffset:{} offset,Roll:ContractRollRule.ExplicitImplementation,Boundaries:{} boundaries} when !boundaries.IsDefault=>
                ObservationContractSelector.Prompt(offset,ContractRollRule.Explicit(boundaries)),
            _=>throw Invalid()
        };
        return selector.Resolve(sequence,request.Observations.Select(row=>new ContractObservation(row.Ordinal,row.From,row.To,row.FixingAt)));
    }
    private static MarketDataException Invalid()=>new("contract_selection","Supply exactly one explicit contract-selection mode, its complete terms and the observation fixing instants.");
}
