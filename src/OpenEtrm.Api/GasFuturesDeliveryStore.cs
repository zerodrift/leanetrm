using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal sealed record GasFuturesDeliveryReview(JsonElement? Continuation,JsonElement? Details);
internal sealed record GasFuturesDeliveryPrincipalSource(FuturesPhysicalDeliveryProfile Profile,Guid? PositionId,Quantity Quantity,int Revision,
    ImmutableArray<GasDeliveryPrincipalPerformance> Performances,ImmutableDictionary<Guid,Guid> PreviousIds,
    ImmutableArray<GasDeliveryPrincipalPrevious> Previous,ImmutableArray<Guid> Retractions);

// The reviewed pure plan is also the native writer's input. Native constraints
// independently prove its complete prefix, rounding and original cash dates.
internal static class GasFuturesDeliveryStore
{
    internal const string Version="gas_futures_delivery_continuation_v1";
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);

    internal static async Task<GasFuturesDeliveryReview> PrincipalReview(NpgsqlConnection connection,PreviewDatabase database,
        InputFrame frame,MarketSnapshot snapshot,FuturesMarginTrajectory plan,CancellationToken ct)
    {
        if(plan.FinalDelivery is not {} delivery)return new(null,null);
        var final=plan.Steps.LastOrDefault(s=>s.Occurrence.Phase==FuturesMarginPhase.Final)
            ??throw Error("Retain the exact final occurrence in the reviewed margin prefix.");
        if(plan.Policy.Delivery is not {} selected||frame.ContextId!=snapshot.Knowledge?.ContextId||frame.At!=snapshot.ValuationAt)
            throw Error("Retain the adopted delivery contract and exact certified frame.");
        var source=await ReadPrincipal(connection,database,frame,plan.Owner,selected,delivery.Quantity,ct);
        if(source.Profile.BookId!=plan.BookId||source.Profile.Configuration!=selected||source.Profile.Contract!=plan.Policy.Contract||
            delivery.FinalPrice!=final.Evidence.Price||delivery.FinalPrice.Unit!=new PriceUnit(source.Profile.Terms.Currency,source.Profile.Terms.Quantity,source.Profile.QuoteMultiplier))
            throw Error("The principal plan must use the exact held quantity, final quotation and adopted delivery terms.");
        return Plan(source,delivery.FinalPrice,final.Occurrence.Id,final.Evidence.Observation.Id);
    }

    internal static GasFuturesDeliveryReview Plan(GasFuturesDeliveryPrincipalSource source,ExactUnitPrice price,Guid finalOccurrence,Guid observation)
    {
        var planned=GasDeliveryPrincipal.PlanRetained(source.Quantity,price,source.Profile.PrincipalRounding,source.Profile.PrincipalScope,source.Performances,source.Retractions,source.Previous);
        return Review(source,price,finalOccurrence,observation,planned);
    }
    internal static GasFuturesDeliveryReview Correct(GasFuturesDeliveryPrincipalSource source,GasFuturesQuantityReconciliationSelection selection,
        ExactUnitPrice price,Guid finalOccurrence,Guid observation)=>CorrectMany(source,[selection],price,finalOccurrence,observation);

    internal static GasFuturesDeliveryReview CorrectMany(GasFuturesDeliveryPrincipalSource source,ImmutableArray<GasFuturesQuantityReconciliationSelection> selections,
        ExactUnitPrice price,Guid finalOccurrence,Guid observation)
    {
        if(selections.IsDefaultOrEmpty||selections.Select(s=>s.PerformanceId).Distinct().Count()!=selections.Length||
            selections.Any(s=>source.PositionId!=s.PositionId||source.Revision!=s.ExpectedPrincipalVersion)||source.Previous.Length!=source.Performances.Length)
            throw Error("Retain the complete current financial prefix and every selected physical performance.");
        var selected=selections.ToImmutableDictionary(s=>s.PerformanceId);
        if(selected.Keys.Except(source.Performances.Select(p=>p.Id)).Any())throw Error("Every selected performance must belong to this exact principal prefix.");
        var sign=source.Quantity.Amount.CompareTo(default)>0?ExactDecimal.Parse("1"):ExactDecimal.Parse("-1");
        var changed=source.Performances.Select(p=>selected.TryGetValue(p.Id,out var selection)?p with{
            SignedQuantity=new(ExactDecimal.Parse(selection.Quantity)*sign,p.SignedQuantity.Specification)}:p).ToImmutableArray();
        var retractions=source.Retractions.Where(id=>!selected.ContainsKey(id)).Concat(selections.Where(s=>s.Retracted).Select(s=>s.PerformanceId)).ToImmutableArray();
        var contributions=source.Previous.ToImmutableDictionary(p=>p.PerformanceId,p=>p.Contribution);
        var previous=source.Performances.Select(p=>new GasDeliveryPrincipalQuantityPrior(p.Id,p.At,contributions[p.Id])).ToImmutableArray();
        var planned=GasDeliveryPrincipal.CorrectQuantities(source.Quantity,price,source.Profile.PrincipalRounding,source.Profile.PrincipalScope,changed,retractions,previous);
        return Review(source,price,finalOccurrence,observation,planned);
    }
    private static GasFuturesDeliveryReview Review(GasFuturesDeliveryPrincipalSource source,ExactUnitPrice price,Guid finalOccurrence,Guid observation,
        ImmutableArray<GasDeliveryPrincipalStep> planned)
    {
        var continuation=JsonSerializer.SerializeToElement(new{implementation=Version,positionId=source.PositionId,expectedVersion=source.Revision,
            finalOccurrenceId=finalOccurrence,sourceObservationId=observation,steps=planned.Select(s=>new{
                performanceId=s.Performance.Id,previousDeterminationId=source.PreviousIds.GetValueOrDefault(s.Performance.Id) is var old&&old!=Guid.Empty?(Guid?)old:null,
                cumulativeQuantity=s.CumulativeQuantity.Amount.ToString(),exactTotal=s.ExactCumulative.ToExactString(),
                roundedTotal=s.RoundedCumulative.Amount.ToString(),contribution=s.Contribution.Amount.ToString(),adjustment=s.Adjustment.Amount.ToString()})},Json);
        var details=JsonSerializer.SerializeToElement(new{implementation=Version,source.PositionId,source.Profile.Id,source.Profile.Revision,
            originalQuantity=source.Quantity.Amount.ToString(),finalPrice=price.ToExactString(),currency=source.Profile.Terms.Currency,
            principalDueAt=source.Profile.Terms.CashDueAt,principalScope=source.Profile.PrincipalScope,
            performances=planned.Select(s=>new{s.Performance.Id,s.Performance.BusinessReference,s.Performance.At,
                quantity=s.Performance.SignedQuantity.Amount.ToString(),contribution=s.Contribution.Amount.ToString(),adjustment=s.Adjustment.Amount.ToString()})},Json);
        return new(continuation,details);
    }

    internal static async Task<GasFuturesDeliveryPrincipalSource> ReadPrincipal(NpgsqlConnection connection,PreviewDatabase database,InputFrame frame,
        MeasureBinding owner,FuturesFinalDeliveryConfiguration selected,Quantity original,CancellationToken ct)
    {
        if(frame.ContextId is not {} context||owner.TradeId==Guid.Empty||owner.BindingId==Guid.Empty)throw Error("Select an exact native held future and certified source.");
        await using var command=ScheduleStore.Command(connection,
            "SELECT settlement_data.read_futures_delivery_principal($1,$2,$3,$4,$5,$6,$7)::text",database.Manifest.CatalogId,
            owner.TradeId,owner.BindingId,selected.ProfileId,selected.ProfileRevision,context,frame.At);
        var source=JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);
        var profile=FuturesPhysicalDeliveryStore.Materialize(source);
        database.RegisterCommitAdmission(connection,new FuturesPhysicalReadAdmission(database.Manifest.CatalogId,profile.Id,profile.Revision,source.Instant("authorityUntil")));
        var position=source.GetProperty("position");var link=source.GetProperty("link");var run=source.GetProperty("run");
        Guid? positionId=position.ValueKind==JsonValueKind.Null?null:position.Id("id");
        if(profile.Configuration!=selected||original.Specification!=profile.Terms.Quantity||original.Amount==default||
            (positionId is null)!=(link.ValueKind==JsonValueKind.Null)||positionId is null&&run.ValueKind!=JsonValueKind.Null)
            throw Error("Retain the exact delivery configuration, source owner and original signed quantity.");
        if(positionId is {} id&&(link.Id("trade_id")!=owner.TradeId||link.Id("binding_id")!=owner.BindingId||link.Id("position_id")!=id||
            position.Number("quantity")!=original.Amount||position.Id("account_id")!=profile.AccountId||position.Text("kind")!="delivery_obligation"))
            throw Error("Native principal history must belong to this exact separately held physical obligation.");
        var performanceRows=source.GetProperty("performances").EnumerateArray().ToImmutableArray();
        if(performanceRows.Any(p=>Integer(p,"financial_revision")<1||p.GetProperty("retracted").GetBoolean()!=(p.Number("quantity")==default)))
            throw Error("Retain each native financial revision and its explicit retraction state.");
        var performances=performanceRows.Select(p=>new GasDeliveryPrincipalPerformance(p.Id("id"),p.Text("business_reference"),
            p.Instant("performed_at"),new(p.Number("quantity")*(original.Amount.CompareTo(default)>0?ExactDecimal.Parse("1"):ExactDecimal.Parse("-1")),original.Specification))).ToImmutableArray();
        var previous=source.GetProperty("previous").EnumerateArray().ToImmutableArray();
        var revision=run.ValueKind==JsonValueKind.Null?0:Integer(run,"revision");
        if(positionId is null&&(performances.Length!=0||previous.Length!=0)||revision==0&&previous.Length!=0||
            previous.Any(p=>p.Id("position_id")!=positionId||Integer(p,"run_revision")!=revision)||previous.Select(p=>p.Id("performance_id")).Distinct().Count()!=previous.Length)
            throw Error("Native source history must contain one complete, distinct prior principal prefix.");
        return new(profile,positionId,original,revision,performances,previous.ToImmutableDictionary(p=>p.Id("performance_id"),p=>p.Id("id")),
            previous.Select(p=>new GasDeliveryPrincipalPrevious(p.Id("performance_id"),new(p.Number("contribution"),profile.Terms.Currency))).ToImmutableArray(),
            [..performanceRows.Where(p=>p.GetProperty("retracted").GetBoolean()).Select(p=>p.Id("id"))]);
    }
    private static int Integer(JsonElement value,string key)=>int.Parse(value.Text(key),CultureInfo.InvariantCulture);
    private static MarketDataException Error(string message)=>new("futures_delivery_principal",message);
}
