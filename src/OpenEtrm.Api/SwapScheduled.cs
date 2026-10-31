using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SwapScheduledPeriodInput(string? Amount,SwapFixedInput? Fixed,SwapFloatingInput? Floating);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SwapPowerScheduleInput(ReferenceKey Clock,string Product,int? StartHour,int? EndHour,bool? Saturday,ReferenceKey? Calendar);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SwapScheduledSideInput(string Basis,ReferenceKey Quantity,string? Amount,string? Allocation,
    string PriceApplication,SwapPowerScheduleInput? Power,ImmutableArray<SwapScheduledPeriodInput> Periods);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SwapQuantityFactorInput(string Value,ReferenceKey NumeratorQuantity,ReferenceKey DenominatorQuantity,string Implementation,string UnitConversionImplementation);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SwapScheduledInput(string Implementation,SwapScheduledSideInput First,SwapScheduledSideInput Second,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]SwapQuantityFactorInput? Factor=null)
{
    [JsonIgnore] internal ImmutableArray<RetainedScheduledSide> Retained { get; init; }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SwapScheduledPriceRequest(SwapWriteRequest Terms,string PriceMode,ParRateInput? ParRatePolicy);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SwapScheduledPeriodsRequest(FuturesReadRequest Context,string From,string To);
internal sealed record RetainedScheduledSide(Guid Id,ImmutableArray<ExactRatio> Shares,ExactRatio RelativeQuantity,ImmutableArray<JsonElement> Evidence);

public sealed partial class SwapStore
{
    public const string ScheduledPriceApplication="period_average_times_quantity_v1";
    private sealed record ScheduledPreview(int? ExpectedRevision,object Valuation,object QuantityInputs,SwapChangeReview? ChangeReview,[property:JsonIgnore]ScheduledResolution Prepared,[property:JsonIgnore]MarketSnapshot Snapshot,[property:JsonIgnore]ImmutableDictionary<ContractObservationLookup,Guid> ContractObservationPins);
    private sealed record ScheduledResolution(SidedInstrument Instrument,Quantity Quantity,
        ImmutableArray<SwapQuantitySchedule> Quantities,ImmutableArray<ImmutableArray<PowerQuantitySchedule>> Power);
    private static SwapScheduledInput CanonicalScheduled(SwapScheduledInput input)
    {
        SwapScheduledSideInput Side(SwapScheduledSideInput side)=>side with{
            Amount=side.Amount is null?null:ExactDecimal.Parse(Required(side.Amount)).ToString(),
            Periods=side.Periods.IsDefault?side.Periods:side.Periods.Select(p=>p is null?throw Error("scheduled_swap_input","Supply every explicit period."):p with{Amount=p.Amount is null?null:ExactDecimal.Parse(Required(p.Amount)).ToString(),
                Fixed=p.Fixed is {} fixedInput?fixedInput with{Price=fixedInput.Price is null?null:ExactDecimal.Parse(Required(fixedInput.Price)).ToString()}:null,
                Floating=p.Floating is {} floating?floating with{Spread=floating.Spread is null?null:ExactDecimal.Parse(Required(floating.Spread)).ToString()}:null}).ToImmutableArray()};
        if(input.First is null||input.Second is null)throw Error("scheduled_swap_input","Supply both independent sides.");
        return input with{First=Side(input.First),Second=Side(input.Second),Factor=input.Factor is {} factor?factor with{Value=ExactDecimal.Parse(Required(factor.Value)).ToString()}:null};
    }
    private ScheduledResolution ResolveScheduled(PreviewData data,SwapWriteRequest body,Guid? instrumentId=null)
    {
        var input=body.Scheduled??throw Error("scheduled_swap_input","Supply scheduled side terms.");
        var factorContract=input.Implementation==CrossCommodityScheduledSwap.Implementation;
        if((!factorContract&&input.Implementation!=ScheduledFinancialSwap.Implementation)||body.Convention!=Convention||body.Discounting is null||
            body.Fixed is not null||body.Floating is not null||body.FirstFloating is not null||body.FixedCoefficient is not null||
            body.SwapType!="scheduled_financial_swap"||body.Quantity is not null&&input.Retained.IsDefaultOrEmpty||body.DefinitionRevisionId!=data.DefinitionRevision.RevisionId||
            body.KnowledgeMode is not("current" or "retained"))throw Error("scheduled_swap_input","Choose the explicit scheduled-swap version, independent side terms and direction. Its binding quantity is calculated by the backend.");
        var sides=new[]{input.First,input.Second};
        if(sides.Any(s=>s is null||s.Periods.IsDefaultOrEmpty||s.Periods.Length>120||s.PriceApplication!=ScheduledPriceApplication||
            s.Periods.Any(p=>p is null||(p.Fixed is null)==(p.Floating is null))))throw Error("scheduled_swap_input","Supply one to 120 explicit monthly periods per side and the contractual period-average price application.");
        if(sides[1].Periods.Any(p=>p.Floating is null)||sides[0].Periods.Any(p=>p.Fixed is null)&&sides[0].Periods.Any(p=>p.Floating is null))
            throw Error("scheduled_swap_input","Use a fixed/floating or floating/floating composition; each side has one pricing kind.");
        if(factorContract?input.Factor is null||sides[0].Periods.Any(p=>p.Floating is null)||sides[0].Basis=="factor_of_first_side_v1"||sides[1].Basis!="factor_of_first_side_v1":input.Factor is not null)
            throw Error("quantity_factor","Choose the explicit factor contract, two floating sides, authored first-side quantities and derived second-side quantities.");
        if(input.Factor is {} selectedFactor&&selectedFactor.UnitConversionImplementation!=UnitConverter.Implementation)throw Error("quantity_factor","Select the supported pinned unit-conversion implementation.");
        var factor=input.Factor is {} f?new QuantityFactor(ExactDecimal.Parse(Required(f.Value)),f.NumeratorQuantity,f.DenominatorQuantity,f.Implementation):null;
        var common=ResolveFloatingPeriod(data,sides[factorContract?0:1].Periods[0].Floating!,Required(body.Commodity),input.Retained.IsDefaultOrEmpty?null:input.Retained[factorContract?0:1].Evidence[0]);
        var converter=UnitOperations.Converter(new(data.ReferenceRevision,data.References));
        var quantities=ImmutableArray.CreateBuilder<SwapQuantitySchedule>();
        var powers=ImmutableArray.CreateBuilder<ImmutableArray<PowerQuantitySchedule>>();
        var resolved=ImmutableArray.CreateBuilder<Side>();
        for(var slot=0;slot<2;slot++)
        {
            var side=sides[slot];var retained=input.Retained.IsDefaultOrEmpty?null:input.Retained[slot];
            var fixedPeriods=ImmutableArray.CreateBuilder<ResolvedFixedSwapPeriod>();
            var floatingPeriods=ImmutableArray.CreateBuilder<ResolvedFloatingSwapPeriod>();
            var boundaries=ImmutableArray.CreateBuilder<QuantityPeriod>();
            for(var n=0;n<side.Periods.Length;n++)
            {
                var p=side.Periods[n];DateOnly from;DateOnly to;
                if(p.Floating is {} floating)
                {
                    if(floating.PaymentAt is not null||floating.FirstFixingAt is not null||floating.PaymentSchedule is null||floating.Spread is null)
                        throw Error("scheduled_swap_input","Each floating period needs its own payment and observation rules and explicit spread. Fixing boundaries come from the qualified index.");
                    var parameters=ResolveFloatingPeriod(data,floating,factorContract&&slot==1?null:body.Commodity,retained?.Evidence[n]);
                    floatingPeriods.Add(parameters);from=parameters.Period.DeliveryFrom;to=parameters.Period.DeliveryTo;
                    if(retained is not null)ValidatePeriodProjections(parameters,retained.Evidence[n]);
                }
                else
                {
                    var fixedInput=p.Fixed!;
                    if(fixedInput.PaymentAt is not null||fixedInput.PaymentSchedule is null||fixedInput.Currency!=common.Quote.Currency.Code)
                        throw Error("scheduled_swap_input","Each fixed period needs an agreed price, exact currency and explicit payment rule.");
                    var parameters=ResolveFixedPeriod(data,fixedInput,common.Quote);
                    fixedPeriods.Add(parameters);from=parameters.Period.DeliveryFrom;to=parameters.Period.DeliveryTo;
                }
                boundaries.Add(new(from,to));
            }
            var native=floatingPeriods.Count>0?floatingPeriods[0].UnitQuantity.Specification:common.UnitQuantity.Specification;
            Side Assemble(Guid id,ImmutableArray<ExactRatio> shares)=>fixedPeriods.Count>0?
                SwapScheduledAssembly.Fixed(id,fixedPeriods.ToImmutable(),shares):SwapScheduledAssembly.Floating(id,floatingPeriods.ToImmutable(),shares);
            if(factorContract&&slot==1)
            {
                if(side.Amount is not null||side.Allocation is not null||side.Power is not null||side.Periods.Any(p=>p.Amount is not null)||side.Quantity!=native)
                    throw Error("quantity_factor","The second side is derived only from the first-side quantity and the explicit factor, in its native quote quantity.");
                if(retained is null)quantities.Add(quantities[0].Times(factor!,native,converter));
                resolved.Add(Assemble(retained?.Id??Guid.NewGuid(),retained?.Shares??quantities[1].Shares));
                powers.Add([]);continue;
            }
            var basis=side.Basis switch {"period_total"=>SwapQuantityBasis.PeriodTotal,"calendar_day_rate"=>SwapQuantityBasis.CalendarDayRate,"contract_total"=>SwapQuantityBasis.ContractTotal,"selected_interval_power"=>SwapQuantityBasis.SelectedIntervalPower,_=>throw Error("scheduled_quantity","Choose a supported quantity basis explicitly.")};
            var allocation=side.Allocation switch {null=>(SwapQuantityAllocation?)null,"equal_periods"=>SwapQuantityAllocation.EqualPeriods,"calendar_days"=>SwapQuantityAllocation.CalendarDays,_=>throw Error("scheduled_quantity","Select an explicit supported allocation.")};
            if(basis==SwapQuantityBasis.ContractTotal?side.Periods.Any(p=>p.Amount is not null):side.Amount is not null)throw Error("scheduled_quantity","Enter either one contract total or one amount/rate per period, according to the selected basis.");
            if((basis==SwapQuantityBasis.SelectedIntervalPower)!=(side.Power is not null))throw Error("scheduled_quantity","Only power-rate entry consumes delivery hours and a clock.");
            var amounts=basis==SwapQuantityBasis.ContractTotal?ImmutableArray.Create(ExactDecimal.Parse(Required(side.Amount))):side.Periods.Select(p=>ExactDecimal.Parse(Required(p.Amount))).ToImmutableArray();
            SwapQuantitySchedule.ValidateAuthoring(boundaries.ToImmutable(),amounts,basis,allocation);
            _ = converter.Specification(side.Quantity);
            if(retained is not null)
            {
                // Replay consumes retained exact allocations, never today's zone rules.
                resolved.Add(Assemble(retained.Id,retained.Shares));
                powers.Add([]);continue;
            }
            ImmutableArray<PowerQuantitySchedule> power=[];
            if(side.Power is {} powerInput)
            {
                if(powerInput.Clock.Revision!=data.ReferenceRevision)throw Error("clock_reference","Choose an exact clock from the selected reference revision.");
                var row=One(data.References["clock_rules"].Where(c=>c.Text("code")==powerInput.Clock.Code));
                var rule=new ClockRule(row.Text("code"),row.Text("kind"),row.OptionalText("fixed_offset_seconds") is {} offset?int.Parse(offset,CultureInfo.InvariantCulture):null,row.OptionalText("iana_zone"),row.OptionalText("tzdb_version"));
                var clock=PowerClock.FromRule(rule,TimeZoneDatabase.Installed());
                var mask=powerInput.Product switch {
                    "all_hours" when powerInput.StartHour is null&&powerInput.EndHour is null&&powerInput.Saturday is null&&powerInput.Calendar is null=>TimeOfUse.All(60),
                    "weekday_block" when powerInput.StartHour is {} start&&powerInput.EndHour is {} end&&powerInput.Saturday is {} saturday&&powerInput.Calendar is not null=>TimeOfUse.WeekdayBlock(60,start,end,saturday),
                    _=>throw Error("scheduled_power","Choose all hours or an explicit weekday block, clock and holiday calendar.")
                };
                var source=converter.Specification(side.Quantity);var energy=converter.Specification(common.UnitQuantity.Specification);
                power=boundaries.Select((p,n)=>PowerQuantitySchedule.Compile(new(amounts[n],side.Quantity),PowerQuantityBasis.FlatPower,source,energy,p.From,p.To,clock,mask,
                    powerInput.Calendar is {} calendar?PowerPreview.Holidays(data,One(data.References["calendars"].Where(c=>new ReferenceKey(data.ReferenceRevision,c.Text("code"))==calendar)),p.From,p.To):[],60,null)).ToImmutableArray();
            }
            if(!factorContract&&power.IsEmpty)amounts=amounts.Select(a=>converter.Convert(new Quantity(a,side.Quantity),common.UnitQuantity.Specification).Amount).ToImmutableArray();
            var quantity=SwapQuantitySchedule.Resolve(boundaries.ToImmutable(),amounts,basis,allocation,factorContract&&power.IsEmpty?side.Quantity:common.UnitQuantity.Specification,power);
            if(factorContract)quantity=quantity.ConvertTo(common.UnitQuantity.Specification,converter);
            quantities.Add(quantity);powers.Add(power);resolved.Add(Assemble(Guid.NewGuid(),quantity.Shares));
        }
        var coefficient=ExactDecimal.Parse(Required(body.FirstCoefficient));
        var firstRatio=input.Retained.IsDefaultOrEmpty?ExactRatio.One:input.Retained[0].RelativeQuantity;
        var secondRatio=!input.Retained.IsDefaultOrEmpty?input.Retained[1].RelativeQuantity:
            factorContract?factor!.NativeRatio(quantities[0].Total.Specification,quantities[1].Total.Specification,converter):SwapQuantitySchedule.Ratio(quantities[1].Total,quantities[0].Total);
        ImmutableArray<SideMembership> memberships=[new(resolved[0],coefficient,firstRatio),new(resolved[1],default(ExactDecimal)-coefficient,secondRatio)];
        SidedInstrument instrument=factorContract?new CrossCommodityScheduledSwap(instrumentId??Guid.NewGuid(),memberships,factor!,converter,body.Discounting.Value,input.Implementation):
            new ScheduledFinancialSwap(instrumentId??Guid.NewGuid(),memberships,body.Discounting.Value,input.Implementation);
        var quantityBinding=input.Retained.IsDefaultOrEmpty?quantities[0].Total.ToQuantity():new Quantity(ExactDecimal.Parse(Required(body.Quantity)),instrument.UnitQuantity.Specification);
        return new(instrument,quantityBinding,quantities.ToImmutable(),powers.ToImmutable());
    }
    private (SidedInstrument Instrument,Quantity Quantity) ResolveAny(PreviewData data,SwapWriteRequest body,Guid? instrument,Guid? first,Guid? second)
    {
        if(body.Scheduled is not null){var resolved=ResolveScheduled(data,body,instrument);return(resolved.Instrument,resolved.Quantity);}
        var dated=Resolve(data,body,instrument,first,second);return(dated.Instrument,dated.Quantity);
    }
    private async Task<(SidedInstrument Instrument,Quantity Quantity)> ResolveRetained(NpgsqlConnection connection,PreviewData data,InputFrame frame,
        SwapWriteRequest body,Guid instrument,Guid? first,Guid? second,CancellationToken ct,RetainedScheduleStore? schedules=null)
    {
        var resolved=ResolveAny(data,body,instrument,first,second);
        return(RetainedSideSchedules.Attach(resolved.Instrument,await (schedules??new RetainedScheduleStore(database)).Load(connection,instrument,frame.ContextId,frame.ContextId is null?null:frame.At,ct)),resolved.Quantity);
    }
    private async Task<(PreviewData Data,InputFrame Frame)> ScheduledMarket(NpgsqlConnection connection,PreviewData data,InputFrame frame,Guid revision,CancellationToken ct)
    {
        if(data.DefinitionRevision.RevisionId==revision)return(data,frame);
        return One((await database.ReadSwapMarkets(connection,frame,ct,data)).Where(m=>m.Data.DefinitionRevision.RevisionId==revision));
    }
    private async Task<FinancialSwapValue> ScheduledPrice(NpgsqlConnection connection,PreviewData data,InputFrame frame,SwapWriteRequest body,Guid? instrument,CancellationToken ct)=>
        (await ScheduledPriceDetail(connection,data,frame,body,instrument,ct)).Value;
    private async Task<(FinancialSwapValue Value,ScheduledResolution Resolved)> ScheduledPriceDetail(NpgsqlConnection connection,PreviewData data,InputFrame frame,SwapWriteRequest body,Guid? instrument,CancellationToken ct,RetainedScheduleStore? schedules=null,ContractObservationSamples.History? sourceHistories=null)
    {
        (data,frame)=await ScheduledMarket(connection,data,frame,body.DefinitionRevisionId,ct);
        var resolved=ResolveScheduled(data,body,instrument);var snapshot=data.Snapshot(frame);
        if(instrument is {} retainedId)resolved=resolved with{Instrument=RetainedSideSchedules.Attach(resolved.Instrument,
            await (schedules??new RetainedScheduleStore(database)).Load(connection,retainedId,frame.ContextId,frame.ContextId is null?null:frame.At,ct))};
        var mapping=body.Discounting==true?await Discount(connection,data,frame,resolved.Instrument.Currency,ct):null;
        if(frame.ContextId is not {} contextId)throw Error("swap_context","Pricing requires the exact retained context.");
        var context=(await new KnowledgeStore(manifest.CatalogId).Read(connection,contextId,ct)).Context;
        var connected=await CustomCurves.PrepareMarket(connection,database,data,frame,snapshot,context,[resolved.Instrument],resolved.Instrument.DependenciesAt(snapshot),ct,sourceHistories:sourceHistories);
        var evaluated=resolved.Instrument switch {
            ScheduledFinancialSwap swap=>FinancialSwapValuation.Evaluate(swap,connected.Valuation,connected.Bindings,connected.Authority,mapping,ct),
            CrossCommodityScheduledSwap cross=>FinancialSwapValuation.Evaluate(cross,connected.Valuation,connected.Bindings,connected.Authority,mapping,ct),
            _=>throw Error("swap_definition","Unsupported swap version.")};
        return(evaluated.Scale(resolved.Quantity),resolved);
    }
    internal static bool ScheduledAmendmentEligible(SidedInstrument instrument,DateTimeOffset at)=>
        instrument.Sides.SelectMany(s=>((SidePaymentSchedule)s.Side.GenerateSchedule()).Payments)
            .All(p=>at<p.PaymentAt&&p.Prices.All(price=>price.ContractObservation is not {} observation||at<observation.FixingAt));
    private async Task<object> PreviewScheduled(NpgsqlConnection connection,PreviewData data,InputFrame frame,SwapWriteRequest body,CancellationToken ct)
    {
        body=body with{Scheduled=CanonicalScheduled(body.Scheduled??throw Error("scheduled_swap_input","Supply scheduled side terms."))};
        body=await BookingTerms(connection,frame,body,ct);
        await TradeAnnotations.Validate(connection,manifest.CatalogId,body.TraderId,body.TraderRevision,body.Memo,SchedulePreview.ParseAt(body.ExecutedAt),ct);
        await TradeParties.Validate(connection,manifest.CatalogId,TradeParties.Payload(body.TradeDate,body.LegalEntityId,body.LegalEntityRevision,body.CounterpartyId,body.CounterpartyRevision),SchedulePreview.ParseAt(body.ExecutedAt),ct);
        if(!(await BookScope.Read(connection,manifest.CatalogId,ct)).Any(b=>b.Id("id")==body.BookId))throw Error("swap_book","Choose an authorized book.");
        SwapWriteRequest? booked=null;
        if(body.TradeId is {} trade)
        {
            var head=(await Rows(connection,trade,ct))[^1];
            var retained=Terms(data,head,frame.Id);booked=retained;
            if(retained.Scheduled?.Implementation!=body.Scheduled!.Implementation)throw Error("swap_amendment","An amendment must preserve the retained contract implementation. Book a separate contract to change its quantity semantics.");
            var original=(await ScheduledMarket(connection,data,frame,retained.DefinitionRevisionId,ct)).Data;
            if(retained.Scheduled is null||!ScheduledAmendmentEligible(ResolveScheduled(original,retained,head.Id("instrument_id")).Instrument,frame.At))throw Error("swap_amendment","Fixings or payments are due on a retained side; use an explicit lifecycle operation.");
            if(head.OptionalId("scheduled_first_id") is null||body.ExpectedRevision!=int.Parse(head.Text("revision"),CultureInfo.InvariantCulture)||
                body.ExpectedRevision>=256||frame.At<head.Instant("effective_at")||SchedulePreview.ParseAt(body.EffectiveAt)!=frame.At||
                string.IsNullOrWhiteSpace(body.Reason)||body.Reason.Length>500||frame.At>=head.Instant("first_fixing_at")||frame.At>=head.Instant("floating_payment"))
                throw Error("swap_amendment","Amend the latest eligible scheduled revision at As of with a reason; due fixings or payments require a separate lifecycle operation.");
        }
        else if(body.ExpectedRevision is not null)throw Error("swap_input","A new trade has no prior revision.");
        var priced=await ScheduledPriceDetail(connection,data,frame,body,null,ct);
        return new ScheduledPreview(body.ExpectedRevision,Value(priced.Value),QuantityInputs(body.Scheduled!,priced.Resolved),booked is null?null:ReviewChanges(booked,body),priced.Resolved,priced.Value.Instrument.Snapshot,
            priced.Value.Sides.SelectMany(side=>side.Payments).SelectMany(payment=>payment.Cashflow.Resets)
                .Where(reset=>reset.Price.Term.ContractObservation is not null).GroupBy(reset=>reset.Price.Term.ContractObservation!)
                .ToImmutableDictionary(group=>group.Key,group=>group.SelectMany(reset=>reset.Price.Lineage).Distinct().Single().Id));
    }
    private async Task<JsonElement> SaveScheduled(NpgsqlConnection connection,PreviewData data,InputFrame frame,SwapWriteRequest body,CancellationToken ct)
    {
        if(body.KnowledgeMode!="current"||body.OperationId==Guid.Empty)throw Error("swap_input","Use a stable operation identity and working context.");
        TradeAnnotations.RequireServerFacts(body.ExecutedAt,body.TraderId,body.TraderRevision,body.TradeDate);
        body=body with{Scheduled=CanonicalScheduled(body.Scheduled??throw Error("scheduled_swap_input","Supply scheduled side terms."))};
        var prior=await Status(connection,body.OperationId,ct);
        if(prior.TryGetProperty("tradeId",out _))return await WriteScheduled(connection,body,Array.Empty<object>(),"0",frame.At,[],ct);
        var prepared=await WithBookingRead(connection,frame,async(selected,pinned)=>{
            var preview=(ScheduledPreview)await PreviewScheduled(connection,selected,pinned,body,ct);
            if(preview.ChangeReview is {Action:"keep_booked_terms"})
                throw Error("swap_no_change","Change at least one booked trade term before submitting an amendment.");
            return(Resolved:preview.Prepared,preview.Snapshot,preview.ContractObservationPins);
        },ct);
        var resolved=prepared.Resolved;
        var pins=SideComponents.ActualFixingPins(prepared.Snapshot,resolved.Instrument.Sides.Select(m=>m.Side)).Concat(prepared.ContractObservationPins.Values).Distinct().ToArray();
        var compiled=resolved.Instrument.Sides.Select((m,slot)=>new {relativeNumerator=m.QuantityRatio!.Numerator.ToString(CultureInfo.InvariantCulture),relativeDenominator=m.QuantityRatio.Denominator.ToString(CultureInfo.InvariantCulture),clock=resolved.Power[slot].IsEmpty?null:new{resolved.Power[slot][0].Clock.Rules,resolved.Power[slot][0].Clock.Fingerprint},
            periods=SwapSidePeriods.Allocations(m.Side).Select((q,n)=>new {contractObservations=m.Side is FloatingSide floating&&!floating.Periods[n].ContractObservations.IsDefaultOrEmpty?floating.Periods[n].ContractObservations.Select(row=>new{observationId=prepared.ContractObservationPins[row],ordinal=row.Ordinal,observationFrom=Date(row.From),observationTo=Date(row.To),fixingAt=row.FixingAt,selectionAt=row.SelectionAt,contractId=row.Contract.Key.Id,seriesId=row.Series.Id,sourceCode=row.Source.Code}).ToArray():null,fixingPins=PeriodFixingPins(prepared.Snapshot,m.Side,n),numerator=q.Numerator.ToString(CultureInfo.InvariantCulture),denominator=q.Denominator.ToString(CultureInfo.InvariantCulture),
                power=resolved.Power[slot].IsEmpty?null:CompiledPower(resolved.Power[slot][n])}).ToArray()}).ToArray();
        return await WriteScheduled(connection,body,compiled,resolved.Quantity.Amount.ToString(),frame.At,pins,ct);
    }
    private static object CompiledPower(PowerQuantitySchedule schedule)=>new{
        durationTicks=schedule.Duration.Ticks.ToString(CultureInfo.InvariantCulture),energyQuantity=schedule.EnergyDefinition.Key.Code,
        total=new{schedule.Total.Numerator,schedule.Total.Denominator},intervals=schedule.Intervals.Select(p=>new{interval=new{p.Interval.Start,p.Interval.End,p.Interval.OperatingDate},energy=new{p.Energy.Numerator,p.Energy.Denominator}})};
    private static ImmutableArray<Guid> PeriodFixingPins(MarketSnapshot snapshot,Side side,int ordinal)
    {
        if(side is not FloatingSide floating)return [];
        var series=floating.Periods[ordinal].Fixings.Select(f=>f.Series).ToHashSet();
        return [..snapshot.Selections.Where(s=>s.Input.Use==InputUse.FrozenFact && s.Observation.Role==ObservationRole.IndexFixing && series.Contains(s.Input.Series))
            .Select(s=>s.Observation.Key.Id).Distinct().Order()];
    }
    private static void ValidatePeriodProjections(ResolvedFloatingSwapPeriod parameters,JsonElement evidence)
    {
        var period=parameters.Period;
        if(!period.ContractObservations.IsDefaultOrEmpty)return; // Exact retained source rows were validated during reconstruction.
        var plan=FloatingObservationPlan.Generate(period.DeliveryFrom,period.DeliveryTo,period.Averaging,parameters.Quote,period.Calendar,
            period.Averaging==FloatingAveraging.QuantityWeighted?[..period.Projections.Select(p=>new FloatingObservation(p.Contract.DeliveryFrom,p.Contract.DeliveryTo,p.AveragingQuantity))]:[]);
        var contracts=evidence.GetProperty("projection_contracts");var weights=evidence.GetProperty("projection_weights");
        if(contracts.ValueKind!=JsonValueKind.Array || weights.ValueKind!=JsonValueKind.Array ||
            !period.Projections.Select(p=>p.Contract.Key.Id).SequenceEqual(contracts.EnumerateArray().Select(v=>v.GetGuid())) ||
            !plan.Weights.SequenceEqual(weights.EnumerateArray().Select(v=>ExactDecimal.Parse(v.ValueKind==JsonValueKind.String?v.GetString()!:v.GetRawText()))))
            throw Error("swap_projection","The retained period references and weights must match its complete observation rule.");
    }
    private static object QuantityInputs(SwapScheduledInput input,ScheduledResolution resolved)=>new[]{input.First,input.Second}.Select((side,slot)=>new{
        slot,side.Basis,side.Quantity,side.Amount,side.Allocation,side.PriceApplication,side.Power,
        periods=side.Periods.Select((p,n)=>new{p.Amount,deliveryHours=resolved.Power[slot].IsEmpty?null:resolved.Power[slot][n].Hours,
            intervals=resolved.Power[slot].IsEmpty?(int?)null:resolved.Power[slot][n].Intervals.Length,
            clockFingerprint=resolved.Power[slot].IsEmpty?null:resolved.Power[slot][n].Clock.Fingerprint})});
    private async Task<object> RetainedQuantityInputs(NpgsqlConnection connection,JsonElement row,CancellationToken ct)
    {
        var rows=await PreviewDatabase.Rows(connection,"""
         SELECT jsonb_build_object('slot',a.slot,'basis',a.basis,'quantity',a.quantity_code,'amount',trim_scale(a.contract_amount)::text,'allocation',a.allocation,
          'priceApplication',a.price_application,'power',CASE WHEN a.clock_code IS NULL THEN NULL ELSE jsonb_build_object('clock',a.clock_code,'product',a.delivery_product,'startHour',a.start_hour,'endHour',a.end_hour,'saturday',a.saturday,'calendar',a.calendar_code,'clockFingerprint',a.clock_fingerprint) END,
          'periods',coalesce((SELECT jsonb_agg(jsonb_build_object('ordinal',q.ordinal,'amount',trim_scale(q.amount)::text,'durationTicks',q.delivery_duration_ticks::text,'energyNumerator',q.energy_numerator::text,'energyDenominator',q.energy_denominator::text,'energyQuantity',q.energy_quantity_code) ORDER BY q.ordinal) FROM trading.swap_quantity_amounts q WHERE(q.catalog_id,q.trade_id,q.revision,q.slot)=(a.catalog_id,a.trade_id,a.revision,a.slot)),'[]'::jsonb))::text
         FROM trading.swap_quantity_authoring a WHERE(a.catalog_id,a.trade_id,a.revision)=($1,$2,$3) ORDER BY a.slot
         """,[manifest.CatalogId,row.Id("trade_id"),int.Parse(row.Text("revision"),CultureInfo.InvariantCulture)],ct);
        return rows.Select(row=>{
            var result=System.Text.Json.Nodes.JsonNode.Parse(row.GetRawText())!.AsObject();
            result["slot"]=int.Parse(row.Text("slot"),CultureInfo.InvariantCulture);
            foreach(var period in result["periods"]!.AsArray())
                if(period!["durationTicks"] is {} ticks)period["deliveryHours"]=new AmountFormat(18,CashTieRule.ToEven,false,true).FormatFraction(BigInteger.Parse(ticks.GetValue<string>(),CultureInfo.InvariantCulture),TimeSpan.TicksPerHour);
            return JsonSerializer.SerializeToElement(result);
        }).ToArray();
    }
    private async Task<JsonElement> WriteScheduled(NpgsqlConnection connection,SwapWriteRequest body,object compiled,string quantity,DateTimeOffset at,Guid[] pins,CancellationToken ct)
    {
        var json=new JsonSerializerOptions(JsonSerializerDefaults.Web){Converters={new ExactDecimalJsonConverter()}};
        await using var command=ScheduleStore.Command(connection,"SELECT security.save_scheduled_swap($1,$2::jsonb,$3::jsonb,$4::numeric,$5,$6::jsonb,$7::uuid[])::text",manifest.CatalogId,
            JsonSerializer.Serialize(body,json),JsonSerializer.Serialize(compiled,json),quantity,at,
            TradeParties.Submitted(body.LegalEntityId,body.LegalEntityRevision,body.CounterpartyId,body.CounterpartyRevision),pins);
        return JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);
    }
    public async Task<object> QuantitySchedule(NpgsqlConnection connection,InputFrame frame,Guid trade,int? revision,CancellationToken ct)
    {
        var rows=await Rows(connection,trade,ct);if(rows.IsEmpty)throw Error("swap_trade","The trade is unavailable in this context.");
        var selected=revision??rows.Max(r=>int.Parse(r.Text("revision"),CultureInfo.InvariantCulture));
        var row=One(rows.Where(r=>int.Parse(r.Text("revision"),CultureInfo.InvariantCulture)==selected));
        if(row.OptionalId("scheduled_first_id") is null)throw Error("scheduled_quantity","This trade has no scheduled quantity contract.");
        var intervals=await PreviewDatabase.Rows(connection,"""
         SELECT value::text FROM jsonb_array_elements(security.read_swap_power_quantity_intervals($1,$2,$3))
         """,[manifest.CatalogId,trade,selected],ct);
        return new{tradeId=trade,revision=selected,quantityInputs=await RetainedQuantityInputs(connection,row,ct),
            intervals=intervals.Select(p=>new{slot=int.Parse(p.Text("slot"),CultureInfo.InvariantCulture),period=int.Parse(p.Text("period"),CultureInfo.InvariantCulture),ordinal=int.Parse(p.Text("ordinal"),CultureInfo.InvariantCulture),
                start=p.Text("start"),end=p.Text("end"),operatingDate=p.Text("operatingDate"),energyNumerator=p.Text("energyNumerator"),energyDenominator=p.Text("energyDenominator"),energyQuantity=p.Text("energyQuantity")})};
    }
    public static object ScheduledPeriods(SwapScheduledPeriodsRequest body)
    {
        var from=Date(body.From);var to=Date(body.To);var months=(to.Year-from.Year)*12+to.Month-from.Month;
        if(from.Day!=1||to.Day!=1||months is <1 or >120||from.Year<1901||to.Year>2199)throw Error("scheduled_swap_periods","Choose one to 120 complete months with explicit exclusive delivery end.");
        return new{periods=Enumerable.Range(0,months).Select(n=>new{deliveryFrom=Date(from.AddMonths(n)),deliveryTo=Date(from.AddMonths(n+1))})};
    }
    public async Task<object> ScheduledDraftPrice(NpgsqlConnection connection,PreviewData data,InputFrame frame,SwapScheduledPriceRequest request,CancellationToken ct)
    {
        var body=request.Terms;
        if(body.Scheduled is not {First:not null,Second:not null} input)throw Error("scheduled_swap_input","Supply the scheduled contract.");
        if(request.PriceMode=="agreed")
        {
            if(request.ParRatePolicy is not null)throw Error("swap_par_rate","Precision belongs only to a par calculation.");
            var agreed=(ScheduledPreview)await PreviewScheduled(connection,data,frame,body,ct);
            return new{expectedRevision=agreed.ExpectedRevision,terms=body,valuation=agreed.Valuation,quantityInputs=agreed.QuantityInputs,changeReview=agreed.ChangeReview,parRate=(object?)null};
        }
        if(request.PriceMode!="par"||request.ParRatePolicy is not {Implementation:FinancialSwapValuation.ParRatePolicy,DecimalPlaces:>=0 and <=18,TieRule:"to_even"} policy||
            input.First.Periods.IsDefaultOrEmpty||input.First.Periods.Any(p=>p is null||p.Fixed is null||!string.IsNullOrEmpty(p.Fixed.Price)))
            throw Error("swap_par_rate","Calculate one common par fixed price with explicit precision and blank fixed prices. Agreed pricing requires every price.");
        body=body with{Scheduled=input with{First=input.First with{Periods=input.First.Periods.Select(p=>p with{Fixed=p.Fixed! with{Price="0"}}).ToImmutableArray()}}};
        (data,frame)=await ScheduledMarket(connection,data,frame,body.DefinitionRevisionId,ct);
        var value=await ScheduledPrice(connection,data,frame,body,null,ct);
        var par=FinancialSwapValuation.ParRate(value,new AmountFormat(policy.DecimalPlaces,CashTieRule.ToEven,false,true));
        body=body with{Scheduled=body.Scheduled! with{First=body.Scheduled.First with{Periods=body.Scheduled.First.Periods.Select(p=>p with{Fixed=p.Fixed! with{Price=par.Price.Value.ToString()}}).ToImmutableArray()}}};
        var preview=(ScheduledPreview)await PreviewScheduled(connection,data,frame,body,ct);
        return new{expectedRevision=preview.ExpectedRevision,terms=body,valuation=preview.Valuation,quantityInputs=preview.QuantityInputs,changeReview=preview.ChangeReview,parRate=par};
    }
    private static SwapWriteRequest ScheduledTerms(JsonElement row,Guid snapshot)
    {
        var json=new JsonSerializerOptions(JsonSerializerDefaults.Web){Converters={new ExactDecimalJsonConverter()}};
        var original=row.GetProperty("request").GetProperty("body").Deserialize<SwapWriteRequest>(json)!;
        var retained=row.GetProperty("scheduled_data");
        if(retained.ValueKind!=JsonValueKind.Object)throw Error("swap_definition","The exact scheduled contract is unavailable under the selected context and current authority.");
        var input=retained.GetProperty("terms").Deserialize<SwapScheduledInput>(json)??throw Error("scheduled_swap_input","The exact scheduled terms are unavailable.");
        input=input with{Retained=retained.GetProperty("bindings").EnumerateArray().Select((side,slot)=>new RetainedScheduledSide(side.Id("id"),
            side.GetProperty("periods").EnumerateArray().Select(p=>new ExactRatio(BigInteger.Parse(p.Text("n"),CultureInfo.InvariantCulture),BigInteger.Parse(p.Text("d"),CultureInfo.InvariantCulture))).ToImmutableArray(),
            slot==0?ExactRatio.One:new ExactRatio(BigInteger.Parse(row.Text("second_quantity_numerator"),CultureInfo.InvariantCulture),BigInteger.Parse(row.Text("second_quantity_denominator"),CultureInfo.InvariantCulture)),side.GetProperty("periods").EnumerateArray().ToImmutableArray())).ToImmutableArray()};
        return original with{SnapshotId=snapshot,KnowledgeMode="retained",OperationId=Guid.Empty,Quantity=row.Text("quantity"),ExecutedAt=row.Text("execution_at"),Scheduled=input,
            TradeId=null,ExpectedRevision=null,EffectiveAt=null,Reason=null,BookId=row.Id("book_id"),Memo=row.OptionalText("memo"),TradeDate=row.OptionalText("trade_date")??row.Text("booking_date"),
            LegalEntityId=row.OptionalId("legal_entity_id"),LegalEntityRevision=int.Parse(row.Text("legal_entity_revision"),CultureInfo.InvariantCulture),
            CounterpartyId=row.OptionalId("counterparty_id"),CounterpartyRevision=int.Parse(row.Text("counterparty_revision"),CultureInfo.InvariantCulture)};
    }

}
