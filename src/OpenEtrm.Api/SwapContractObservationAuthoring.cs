using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SwapFixingClockInput([property:JsonRequired]ReferenceKey Clock,[property:JsonRequired]string Time,
    [property:JsonRequired]string AmbiguousTime,[property:JsonRequired]string InvalidTime);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SwapContractObservationAuthoringInput([property:JsonRequired]string Implementation,
    [property:JsonRequired]string HistoryPolicy,[property:JsonRequired]string Role,[property:JsonRequired]string ExpiryPolicy,
    [property:JsonRequired]SwapContractSelectionInput Selection,[property:JsonRequired]SwapFixingClockInput FixingClock);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SwapContractObservationPreviewRequest([property:JsonRequired]FuturesReadRequest Context,
    [property:JsonRequired]Guid DefinitionRevisionId,[property:JsonRequired]string Commodity,[property:JsonRequired]string Curve,
    [property:JsonRequired]string Currency,[property:JsonRequired]string DeliveryFrom,[property:JsonRequired]string DeliveryTo,
    [property:JsonRequired]SwapPaymentInput PaymentSchedule,[property:JsonRequired]string Spread,
    [property:JsonRequired]SwapContractObservationAuthoringInput Source)
{
    [JsonRequired]public SideCalendarPolicyInput CalendarPolicy { get; init; }=null!;
}

internal static class SwapContractObservationAuthoring
{
    internal static ImmutableArray<SwapContractObservationInput> Generate(DateOnly from,DateOnly to,SwapFixingClockInput input,PowerClock clock)
    {
        if(from.Day!=1 || from.Year>=9999 || from.AddMonths(1)!=to)
            throw new MarketDataException("contract_observation_input","Choose one complete calendar month.");
        if(input is null || input.AmbiguousTime!="reject" || input.InvalidTime!="reject" ||
            !TimeOnly.TryParseExact(input.Time,"HH:mm:ss",CultureInfo.InvariantCulture,DateTimeStyles.None,out var time))
            throw new MarketDataException("contract_observation_clock","Supply an explicit HH:mm:ss fixing time and reject policies for ambiguous and nonexistent local times.");
        return [..Enumerable.Range(0,to.DayNumber-from.DayNumber).Select(n=>new SwapContractObservationInput(n,from.AddDays(n),from.AddDays(n+1),clock.AtLocal(from.AddDays(n),time).ToUniversalTime()))];
    }
}

public sealed partial class SwapStore
{
    public async Task<object> ContractObservationPreview(NpgsqlConnection connection,PreviewData data,InputFrame frame,
        SwapContractObservationPreviewRequest body,CancellationToken ct)
    {
        (data,frame)=await ScheduledMarket(connection,data,frame,body.DefinitionRevisionId,ct);
        data=await SideCalendarPolicies.Prepare(connection,data,[body.CalendarPolicy],ct);
        var calendars=SideCalendarPolicies.Resolve(data,body.CalendarPolicy);
        calendars.RequirePurposes(true,true,true);
        calendars.For(CalendarPurpose.Observation).RequireCoverage(Date(body.DeliveryFrom),Date(body.DeliveryTo));
        var source=body.Source??throw Error("contract_observation_input","Supply explicit observation source terms.");
        var input=source.FixingClock??throw Error("contract_observation_clock","Select an exact fixing clock.");
        if(input.Clock.Revision!=data.ReferenceRevision)throw Error("contract_observation_clock","Select a clock in the exact source reference revision.");
        var clock=PublishedClocks.Resolve(data,input.Clock.Code);
        var observations=SwapContractObservationAuthoring.Generate(Date(body.DeliveryFrom),Date(body.DeliveryTo),input,clock);
        var floating=new SwapFloatingInput(body.Curve,null,body.Currency,body.DeliveryFrom,body.DeliveryTo,
            PaymentSchedule:body.PaymentSchedule,ResetFrequency:"daily",AveragingMethod:"arithmetic",Spread:body.Spread,
            ContractObservations:new(source.Implementation,source.HistoryPolicy,source.Role,source.ExpiryPolicy,source.Selection,observations));
        var resolved=ResolveContractObservationPeriod(data,floating,Required(body.Commodity),null,calendars);
        _=new FloatingSide(Guid.NewGuid(),resolved.Quote,resolved.UnitQuantity,[resolved.Period],calendars);
        var contextId=frame.ContextId??throw Error("swap_context","Supply the exact certified context.");
        var context=(await new KnowledgeResolver(manifest.CatalogId).Read(connection,contextId,ct)).Context;
        if(data.Snapshot(frame).MarketCutoff!=frame.At)throw Error("contract_observation_context","This source consumer requires an explicit market cutoff equal to the valuation instant.");
        var samples=await ContractObservationSamples.Read(connection,database,data,frame,context,resolved.Period.ContractObservations,ct);
        var calculation=new OpenEtrm.MarketData.Calculation.ResolvedCalculationContext(contextId,frame.Id.ToString(),frame.At,
            SchedulePreview.ParseAt(context.KnownAt),frame.At,frame.At);
        _=ContractObservationPricing.Compile(samples,calculation,new CustomCurves.PreparedBindings(
            resolved.Period.ContractObservations.Select(o=>o.Market.Curve).ToHashSet(),calculation));
        return new{floating,observations=resolved.Period.ContractObservations,fixingClock=new{reference=input.Clock,time=input.Time,fingerprint=clock.Fingerprint},available=true};
    }

    private IEnumerable<object> ContractObservationSources(PreviewData data)
    {
        foreach(var coverage in manifest.Schedules??[])
        {
            var curves=data.DefinitionRows("curves","code",coverage.Curve).ToArray();
            if(curves.Length!=1)continue;
            var curve=curves[0];
            if(curve.Text("rule")!="direct"||curve.OptionalText("direct_role")!="daily_settlement")continue;
            var quote=One(data.QuoteRows(curve.Text("quote_code")));
            if(quote.Kind!=QuoteKind.Outright)continue;
            ContractSequence sequence;
            try { sequence=SchedulePreview.Compile(data,coverage); }
            catch(MarketDataException) { continue; }
            if(sequence.Entries.Any(e=>e.Contract.Kind!=ContractKind.Future||e.Contract.Interval is not null))continue;
            var quantity=One(data.References["quantity_specifications"].Where(q=>q.Text("code")==quote.Quantity.Code));
            var currency=One(data.References["currencies"].Where(c=>c.Text("code")==quote.Currency.Code));
            var unit=One(data.References["units"].Where(u=>u.Text("code")==quantity.Text("unit_code")));
            var carry=data.Definitions["curve_expiry_rules"].Any(r=>r.Text("curve_code")==coverage.Curve&&r.Text("rule_version")=="freeze_final_settlement_v1");
            yield return new{definitionRevisionId=data.DefinitionRevision.RevisionId,curve=coverage.Curve,name=coverage.Curve,
                commodity=curve.Text("commodity_code"),quote=quote.Key,currencyCode=quote.Currency.Code,currency=currency.Text("display_code"),
                quantityCode=quote.Quantity.Code,quantityChoices=DraftQuantities(data,quote.Quantity),unit=unit.Text("symbol"),
                quoteMultiplier=quote.Multiplier.ToString(),minimumPrice=quote.Minimum.ToString(),maximumPrice=quote.Maximum.ToString(),
                contracts=sequence.Entries.Select(e=>new{contract=e.Contract.Key,deliveryFrom=Date(e.Contract.DeliveryFrom),deliveryTo=Date(e.Contract.DeliveryTo),expiry=e.Contract.Expiry,eligibleFrom=e.EligibleFrom,label=$"{e.Contract.DeliveryFrom:yyyy-MM} · {e.Contract.Key.Id}"}),
                sequenceFrom=sequence.From,sequenceTo=sequence.To,selectionModes=new[]{"exact","prompt_offset"},
                rollRules=new[]{"contract_expiry_roll_v1","explicit_contract_roll_v1"},expiryPolicies=carry?new[]{"require_live","carry_final_settlement"}:new[]{"require_live"},
                historyPolicies=new[]{SwapContractObservations.HistoryPolicy},roles=new[]{"daily_settlement"},
                clocks=data.References["clock_rules"].Select(c=>new{reference=new ReferenceKey(data.ReferenceRevision,c.Text("code")),label=c.OptionalText("iana_zone")??c.Text("code")}),
                samplings=new[]{new{resetFrequency="daily",averagingMethod="arithmetic"}}};
        }
    }
}
