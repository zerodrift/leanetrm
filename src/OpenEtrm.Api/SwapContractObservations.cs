using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SwapDefinitionRevisionInput([property:JsonRequired]Guid CatalogId,[property:JsonRequired]Guid RevisionId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SwapContractKeyInput([property:JsonRequired]SwapDefinitionRevisionInput Revision,[property:JsonRequired]Guid Id)
{
    internal ContractKey ToKey()=>Revision is not null?new(new(Revision.CatalogId,Revision.RevisionId),Id):
        throw new MarketDataException("contract_selection","Supply an exact contract revision.");
    public static implicit operator SwapContractKeyInput(ContractKey key)=>new(new(key.Revision.CatalogId,key.Revision.RevisionId),key.Id);
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SwapContractRollBoundaryInput([property:JsonRequired]SwapContractKeyInput Contract,[property:JsonRequired]DateTimeOffset RollAt);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SwapContractSelectionInput([property:JsonRequired]string Mode,
    [property:JsonRequired]SwapContractKeyInput? Contract,[property:JsonRequired]int? PromptOffset,
    [property:JsonRequired]string? Roll,[property:JsonRequired]ImmutableArray<SwapContractRollBoundaryInput>? Boundaries);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SwapContractObservationInput([property:JsonRequired]int Ordinal,
    [property:JsonRequired]DateOnly From,[property:JsonRequired]DateOnly To,[property:JsonRequired]DateTimeOffset FixingAt);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SwapContractObservationsInput([property:JsonRequired]string Implementation,
    [property:JsonRequired]string HistoryPolicy,[property:JsonRequired]string Role,[property:JsonRequired]string ExpiryPolicy,
    [property:JsonRequired]SwapContractSelectionInput Selection,
    [property:JsonRequired]ImmutableArray<SwapContractObservationInput> Observations);

internal static class SwapContractObservations
{
    internal const string HistoryPolicy = "visible_corrections_at_cutoff_v1";
    internal static ImmutableArray<ResolvedObservationContract> Resolve(ContractSequence sequence,
        SwapContractObservationsInput input,DateOnly from,DateOnly to)
    {
        if(input is null || input.Implementation!="contract_observation_source_v1" || input.HistoryPolicy!=HistoryPolicy ||
            input.Role!="daily_settlement" || input.ExpiryPolicy is not("require_live" or "carry_final_settlement") ||
            input.Selection is null || input.Observations.IsDefaultOrEmpty || input.Observations.Length!=to.DayNumber-from.DayNumber ||
            input.Observations.Where((row,n)=>row is null || row.Ordinal!=n || row.From!=from.AddDays(n) || row.To!=row.From.AddDays(1)).Any())
            throw new MarketDataException("contract_observation_input","Supply complete daily observations and explicit supported source, history and expiry policies.");
        var selection=input.Selection;
        if(selection.Boundaries is {} supplied&&supplied.IsDefault)
            throw new MarketDataException("contract_roll","Supply explicit boundary rows.");
        var resolved=ObservationContractPreview.Resolve(sequence,new(sequence.Key.Code,
            new(selection.Mode,selection.Contract?.ToKey(),selection.PromptOffset,selection.Roll,
                selection.Boundaries is {} boundaries?[..boundaries.Select(boundary=>boundary?.Contract is {} contract?new ContractRollBoundary(contract.ToKey(),boundary.RollAt):
                    throw new MarketDataException("contract_roll","Supply every exact boundary contract and instant."))]:null),
            [..input.Observations.Select(row=>new ObservationContractInput(row.Ordinal,row.From,row.To,row.FixingAt))]));
        if(input.ExpiryPolicy=="require_live" && resolved.Any(row=>row.SelectionAt>=row.Expiry))
            throw new MarketDataException("source_expired","The selected contract expires before an observation; choose an explicit final-settlement carry or a different selection.");
        return resolved;
    }
}

public sealed partial class SwapStore
{
    private ResolvedFloatingSwapPeriod ResolveContractObservationPeriod(PreviewData data,SwapFloatingInput input,string? commodity,JsonElement? retained)
    {
        var terms=input.ContractObservations!;
        if(input.Index is not null || input.Settlement is not null || input.CustomProjection is not null || input.ObservationSchedule is not null ||
            input.FirstFixingAt is not null || input.Averaging is not null || input.ResetFrequency!="daily" || input.AveragingMethod!="arithmetic")
            throw Error("contract_observation_input","Contract observations require daily arithmetic sampling without index-fixing or custom-projection terms.");
        var curve=One(data.DefinitionRows("curves","code",Required(input.Curve)));
        var quote=One(data.QuoteRows(curve.Text("quote_code")));
        if(curve.Text("rule")!="direct" || curve.Text("direct_role")!=terms.Role || terms.Role!="daily_settlement" ||
            quote.Kind!=QuoteKind.Outright || quote.Currency.Code!=input.Currency || commodity is not null&&commodity!=curve.Text("commodity_code"))
            throw Error("contract_observation_source","Choose an exact direct futures settlement curve with the declared quote, commodity and role.");
        var from=Date(input.DeliveryFrom);var to=Date(input.DeliveryTo);
        if(from.Day!=1 || from.Year>=9999 || from.AddMonths(1)!=to || input.PaymentSchedule is null || input.Spread is null)
            throw Error("contract_observation_input","Supply one complete month, explicit payment terms and spread.");
        var policy=terms.ExpiryPolicy switch {"require_live"=>ContractObservationExpiry.RequireLive,"carry_final_settlement"=>ContractObservationExpiry.CarryFinalSettlement,
            _=>throw Error("contract_observation_input","Select an explicit supported expiry policy.")};
        if(policy==ContractObservationExpiry.CarryFinalSettlement && !data.Definitions["curve_expiry_rules"].Any(row=>row.Text("curve_code")==input.Curve&&row.Text("rule_version")=="freeze_final_settlement_v1"))
            throw Error("contract_observation_source","Final carry requires the selected curve's explicit qualified final-settlement expiry rule.");
        ImmutableArray<ContractObservationLookup> lookups;
        if(retained is {} evidence)
        {
            if(!evidence.TryGetProperty("contract_observations",out var rows) || rows.ValueKind!=JsonValueKind.Array || rows.GetArrayLength()!=to.DayNumber-from.DayNumber || terms.Observations.IsDefaultOrEmpty)
                throw Error("contract_observation_source","The retained exact observation sources are unavailable.");
            lookups=[..rows.EnumerateArray().Select(row=>{
                if(row.Text("history_policy")!=terms.HistoryPolicy || row.Text("role")!=terms.Role || row.Text("expiry_policy")!=terms.ExpiryPolicy || row.Text("implementation")!=terms.Implementation)
                    throw Error("contract_observation_source","Retained observation source policies must agree with their exact stored terms.");
                var contract=One(data.Contracts.Where(c=>c.Key.Id==row.Id("contract_id")));
                var observation=new ContractObservation(int.Parse(row.Text("ordinal"),System.Globalization.CultureInfo.InvariantCulture),
                    Date(row.Text("observation_from")),Date(row.Text("observation_to")),row.Instant("fixing_at"));
                return Lookup(contract,observation,row.Instant("selection_at"),row.Id("series_id"),row.Text("source_code"));
            })];
            if(terms.Implementation!=ContractObservationLookup.SupportedImplementation || terms.HistoryPolicy!=SwapContractObservations.HistoryPolicy ||
                lookups.Where((row,n)=>row.Ordinal!=n || row.From!=from.AddDays(n) || row.To!=row.From.AddDays(1)).Any() ||
                !lookups.Select(row=>new SwapContractObservationInput(row.Ordinal,row.From,row.To,row.FixingAt)).SequenceEqual(terms.Observations))
                throw Error("contract_observation_source","Retained source rows must agree with the complete authored observation terms.");
        }
        else
        {
            var coverage=One((manifest.Schedules??[]).Where(row=>row.Curve==input.Curve));
            var resolved=SwapContractObservations.Resolve(SchedulePreview.Compile(data,coverage),terms,from,to);
            lookups=[..resolved.Select(row=>{
                var contract=One(data.Contracts.Where(c=>c.Key==row.Contract));
                var series=One(data.Definitions["series"].Where(s=>s.OptionalId("contract_id")==contract.Key.Id && s.OptionalText("quote_code")==quote.Key.Code));
                return Lookup(contract,row.Observation,row.SelectionAt,series.Id("id"),curve.Text("direct_source_code"));
            })];
        }
        ContractObservationLookup Lookup(MarketContract contract,ContractObservation observation,DateTimeOffset selection,Guid series,string source)
        {
            if(contract.Kind!=ContractKind.Future || !data.HasCurveMember(input.Curve!,contract.Key.Id) || source!=curve.Text("direct_source_code") ||
                !data.Definitions["series"].Any(row=>row.Id("id")==series&&row.OptionalId("contract_id")==contract.Key.Id&&row.OptionalText("quote_code")==quote.Key.Code))
                throw Error("contract_observation_source","Each observation requires its retained exact curve member, quote series and source.");
            var lookup=new ContractObservationLookup(new(new(data.DefinitionRevision,input.Curve!),contract.Key),contract,quote,observation,
                selection,new(data.ReferenceRevision,source),new(data.DefinitionRevision,series),ObservationRole.DailySettlement,policy,terms.Implementation);
            lookup.Validate();return lookup;
        }
        var rule=Payment(data,input.PaymentSchedule);
        var payment=new DateTimeOffset(rule.Generate(from,to).Date.ToDateTime(TimeOnly.MinValue,DateTimeKind.Utc));
        return new(null,quote,new(ExactDecimal.Parse("1"),quote.Quantity),
            new(from,to,lookups.Min(row=>row.FixingAt),payment,FloatingAveraging.CalendarDayArithmetic,
                [..lookups.Select(row=>new SwapProjection(row.Market,row.Contract))],[],null,SwapScheduledAssembly.Spread(input.Spread,quote),
                rule,null,OpenEtrm.Shared.Numerics.ExactRatio.One,FloatingSide.ContractObservationImplementation){ContractObservations=lookups});
    }
}
