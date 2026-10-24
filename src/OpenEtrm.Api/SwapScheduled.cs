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
public sealed record SwapScheduledInput(string Implementation,SwapScheduledSideInput First,SwapScheduledSideInput Second)
{
    [JsonIgnore] internal ImmutableArray<RetainedScheduledSide> Retained { get; init; }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SwapScheduledPriceRequest(SwapWriteRequest Terms,string PriceMode,ParRateInput? ParRatePolicy);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SwapScheduledPeriodsRequest(FuturesReadRequest Context,string From,string To);
internal sealed record RetainedScheduledSide(Guid Id,ImmutableArray<Guid> PeriodIds,ImmutableArray<ExactRatio> Shares,ExactRatio RelativeQuantity,ImmutableArray<JsonElement> Evidence);

public sealed partial class SwapStore
{
    public const string ScheduledPriceApplication="period_average_times_quantity_v1";
    private sealed record ScheduledPreview(int? ExpectedRevision,object Valuation,object QuantityInputs,[property:JsonIgnore]ScheduledResolution Prepared,[property:JsonIgnore]MarketSnapshot Snapshot);
    private sealed record ScheduledResolution(ScheduledFinancialSwap Instrument,Quantity Quantity,
        ImmutableArray<SwapQuantitySchedule> Quantities,ImmutableArray<ImmutableArray<PowerQuantitySchedule>> Power);
    private static SwapScheduledInput CanonicalScheduled(SwapScheduledInput input)
    {
        SwapScheduledSideInput Side(SwapScheduledSideInput side)=>side with{
            Amount=side.Amount is null?null:ExactDecimal.Parse(Required(side.Amount)).ToString(),
            Periods=side.Periods.IsDefault?side.Periods:side.Periods.Select(p=>p is null?throw Error("scheduled_swap_input","Supply every explicit period."):p with{Amount=p.Amount is null?null:ExactDecimal.Parse(Required(p.Amount)).ToString(),
                Fixed=p.Fixed is {} fixedInput?fixedInput with{Price=fixedInput.Price is null?null:ExactDecimal.Parse(Required(fixedInput.Price)).ToString()}:null,
                Floating=p.Floating is {} floating?floating with{Spread=floating.Spread is null?null:ExactDecimal.Parse(Required(floating.Spread)).ToString()}:null}).ToImmutableArray()};
        if(input.First is null||input.Second is null)throw Error("scheduled_swap_input","Supply both independent sides.");
        return input with{First=Side(input.First),Second=Side(input.Second)};
    }
    private ScheduledResolution ResolveScheduled(PreviewData data,SwapWriteRequest body,Guid? instrumentId=null)
    {
        var input=body.Scheduled??throw Error("scheduled_swap_input","Supply scheduled side terms.");
        if(input.Implementation!=ScheduledFinancialSwap.Implementation||body.Convention!=Convention||body.Discounting is null||
            body.Fixed is not null||body.Floating is not null||body.FirstFloating is not null||body.FixedCoefficient is not null||
            body.SwapType!="scheduled_financial_swap"||body.Quantity is not null&&input.Retained.IsDefaultOrEmpty||body.DefinitionRevisionId!=data.DefinitionRevision.RevisionId||
            body.KnowledgeMode is not("current" or "retained"))throw Error("scheduled_swap_input","Choose the explicit scheduled-swap version, independent side terms and direction. Its binding quantity is calculated by the backend.");
        var sides=new[]{input.First,input.Second};
        if(sides.Any(s=>s is null||s.Periods.IsDefaultOrEmpty||s.Periods.Length>120||s.PriceApplication!=ScheduledPriceApplication||
            s.Periods.Any(p=>p is null||(p.Fixed is null)==(p.Floating is null))))throw Error("scheduled_swap_input","Supply one to 120 explicit monthly periods per side and the contractual period-average price application.");
        if(sides[1].Periods.Any(p=>p.Floating is null)||sides[0].Periods.Any(p=>p.Fixed is null)&&sides[0].Periods.Any(p=>p.Floating is null))
            throw Error("scheduled_swap_input","Use a fixed/floating or floating/floating composition; each side has one pricing kind.");
        var common=ResolveFloating(data,sides[1].Periods[0].Floating!,Required(body.Commodity),null);
        var converter=UnitOperations.Converter(new(data.ReferenceRevision,data.References));
        var quantities=ImmutableArray.CreateBuilder<SwapQuantitySchedule>();
        var powers=ImmutableArray.CreateBuilder<ImmutableArray<PowerQuantitySchedule>>();
        var resolved=ImmutableArray.CreateBuilder<ScheduledSwapSide>();
        for(var slot=0;slot<2;slot++)
        {
            var side=sides[slot];var retained=input.Retained.IsDefaultOrEmpty?null:input.Retained[slot];
            var periods=ImmutableArray.CreateBuilder<Side>();var boundaries=ImmutableArray.CreateBuilder<QuantityPeriod>();
            for(var n=0;n<side.Periods.Length;n++)
            {
                var p=side.Periods[n];var id=retained?.PeriodIds[n]??Guid.NewGuid();Side child;
                if(p.Floating is {} floating)
                {
                    if(floating.PaymentAt is not null||floating.FirstFixingAt is not null||floating.PaymentSchedule is null||floating.Spread is null)
                        throw Error("scheduled_swap_input","Each floating period needs its own payment and observation rules and explicit spread. Fixing boundaries come from the qualified index.");
                    child=ResolveFloating(data,floating,body.Commodity,id);
                    if(retained is not null)ValidateProjections(((SidePaymentSchedule)child.GenerateSchedule()).Payments.SelectMany(p=>p.Prices),retained.Evidence[n]);
                }
                else
                {
                    var fixedInput=p.Fixed!;
                    if(fixedInput.PaymentAt is not null||fixedInput.PaymentSchedule is null||fixedInput.Currency!=common.Quote.Currency.Code)
                        throw Error("scheduled_swap_input","Each fixed period needs an agreed price, exact currency and explicit payment rule.");
                    child=ResolveFixed(data,fixedInput,common.Quote,id);
                }
                var plan=((SidePaymentSchedule)child.GenerateSchedule()).Payments.Single();
                boundaries.Add(new(plan.DeliveryFrom,plan.DeliveryTo));periods.Add(child);
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
                resolved.Add(new(retained.Id,periods.ToImmutable(),retained.Shares,ScheduledSwapSide.Implementation));
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
            else amounts=amounts.Select(a=>converter.Convert(new Quantity(a,side.Quantity),common.UnitQuantity.Specification).Amount).ToImmutableArray();
            var quantity=SwapQuantitySchedule.Resolve(boundaries.ToImmutable(),amounts,basis,allocation,common.UnitQuantity.Specification,power);
            quantities.Add(quantity);powers.Add(power);resolved.Add(new(Guid.NewGuid(),periods.ToImmutable(),quantity.Shares,ScheduledSwapSide.Implementation));
        }
        var coefficient=ExactDecimal.Parse(Required(body.FirstCoefficient));
        var firstRatio=input.Retained.IsDefaultOrEmpty?ExactRatio.One:input.Retained[0].RelativeQuantity;
        var secondRatio=input.Retained.IsDefaultOrEmpty?SwapQuantitySchedule.Ratio(quantities[1].Total,quantities[0].Total):input.Retained[1].RelativeQuantity;
        var instrument=new ScheduledFinancialSwap(instrumentId??Guid.NewGuid(),[new(resolved[0],coefficient,firstRatio),new(resolved[1],default(ExactDecimal)-coefficient,secondRatio)],body.Discounting.Value,ScheduledFinancialSwap.Implementation);
        var quantityBinding=input.Retained.IsDefaultOrEmpty?quantities[0].Total.ToQuantity():new Quantity(ExactDecimal.Parse(Required(body.Quantity)),instrument.UnitQuantity.Specification);
        return new(instrument,quantityBinding,quantities.ToImmutable(),powers.ToImmutable());
    }
    private (SidedInstrument Instrument,Quantity Quantity) ResolveAny(PreviewData data,SwapWriteRequest body,Guid? instrument,Guid? first,Guid? second)
    {
        if(body.Scheduled is not null){var resolved=ResolveScheduled(data,body,instrument);return(resolved.Instrument,resolved.Quantity);}
        var legacy=Resolve(data,body,instrument,first,second);return(legacy.Instrument,legacy.Quantity);
    }
    private async Task<(PreviewData Data,InputFrame Frame)> ScheduledMarket(NpgsqlConnection connection,PreviewData data,InputFrame frame,Guid revision,CancellationToken ct)
    {
        if(data.DefinitionRevision.RevisionId==revision)return(data,frame);
        return One((await database.ReadSwapMarkets(connection,frame,ct,data)).Where(m=>m.Data.DefinitionRevision.RevisionId==revision));
    }
    private async Task<FinancialSwapValue> ScheduledPrice(NpgsqlConnection connection,PreviewData data,InputFrame frame,SwapWriteRequest body,Guid? instrument,CancellationToken ct)=>
        (await ScheduledPriceDetail(connection,data,frame,body,instrument,ct)).Value;
    private async Task<(FinancialSwapValue Value,ScheduledResolution Resolved)> ScheduledPriceDetail(NpgsqlConnection connection,PreviewData data,InputFrame frame,SwapWriteRequest body,Guid? instrument,CancellationToken ct)
    {
        (data,frame)=await ScheduledMarket(connection,data,frame,body.DefinitionRevisionId,ct);
        var resolved=ResolveScheduled(data,body,instrument);var snapshot=data.Snapshot(frame);
        var mapping=body.Discounting==true?await Discount(connection,data,frame,resolved.Instrument.Currency,ct):null;
        if(frame.ContextId is not {} contextId)throw Error("swap_context","Pricing requires the exact retained context.");
        var context=(await new KnowledgeStore(manifest.CatalogId).Read(connection,contextId,ct)).Context;
        var connected=await CustomCurves.PrepareMarket(connection,database,data,frame,snapshot,context,[resolved.Instrument],resolved.Instrument.DependenciesAt(snapshot),ct);
        return(FinancialSwapValuation.Evaluate(resolved.Instrument,connected.Valuation,connected.Bindings,connected.Authority,mapping,ct).Scale(resolved.Quantity),resolved);
    }
    private static bool ScheduledAmendmentEligible(ScheduledFinancialSwap instrument,DateTimeOffset at)=>
        instrument.Sides.SelectMany(s=>((SidePaymentSchedule)s.Side.GenerateSchedule()).Payments).All(p=>at<p.PaymentAt&&(p.FirstFixingAt is null||at<p.FirstFixingAt));
    private async Task<object> PreviewScheduled(NpgsqlConnection connection,PreviewData data,InputFrame frame,SwapWriteRequest body,CancellationToken ct)
    {
        body=body with{Scheduled=CanonicalScheduled(body.Scheduled??throw Error("scheduled_swap_input","Supply scheduled side terms."))};
        body=await BookingTerms(connection,frame,body,ct);
        await TradeAnnotations.Validate(connection,manifest.CatalogId,body.TraderId,body.TraderRevision,body.Memo,SchedulePreview.ParseAt(body.ExecutedAt),ct);
        await TradeParties.Validate(connection,manifest.CatalogId,TradeParties.Payload(body.TradeDate,body.LegalEntityId,body.LegalEntityRevision,body.CounterpartyId,body.CounterpartyRevision),SchedulePreview.ParseAt(body.ExecutedAt),ct);
        if(!(await new FuturesStore(manifest).Books(connection,ct)).Any(b=>b.Id("id")==body.BookId))throw Error("swap_book","Choose an authorized book.");
        if(body.TradeId is {} trade)
        {
            var head=(await Rows(connection,trade,ct))[^1];
            var retained=Terms(data,head,frame.Id);
            var original=(await ScheduledMarket(connection,data,frame,retained.DefinitionRevisionId,ct)).Data;
            if(retained.Scheduled is null||!ScheduledAmendmentEligible(ResolveScheduled(original,retained,head.Id("instrument_id")).Instrument,frame.At))throw Error("swap_amendment","Fixings or payments are due on a retained side; use an explicit lifecycle operation.");
            if(head.OptionalId("scheduled_first_id") is null||body.ExpectedRevision!=int.Parse(head.Text("revision"),CultureInfo.InvariantCulture)||
                body.ExpectedRevision>=256||frame.At<head.Instant("effective_at")||SchedulePreview.ParseAt(body.EffectiveAt)!=frame.At||
                string.IsNullOrWhiteSpace(body.Reason)||body.Reason.Length>500||frame.At>=head.Instant("first_fixing_at")||frame.At>=head.Instant("floating_payment"))
                throw Error("swap_amendment","Amend the latest eligible scheduled revision at As of with a reason; due fixings or payments require a separate lifecycle operation.");
        }
        else if(body.ExpectedRevision is not null)throw Error("swap_input","A new trade has no prior revision.");
        var priced=await ScheduledPriceDetail(connection,data,frame,body,null,ct);
        return new ScheduledPreview(body.ExpectedRevision,Value(priced.Value),QuantityInputs(body.Scheduled!,priced.Resolved),priced.Resolved,priced.Value.Instrument.Snapshot);
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
            return(Resolved:preview.Prepared,preview.Snapshot);
        },ct);
        var resolved=prepared.Resolved;
        var pins=SideComponents.ActualFixingPins(prepared.Snapshot,resolved.Instrument.Sides.Select(m=>m.Side)).ToArray();
        var compiled=resolved.Instrument.Sides.Select((m,slot)=>new {relativeNumerator=m.QuantityRatio!.Numerator.ToString(CultureInfo.InvariantCulture),relativeDenominator=m.QuantityRatio.Denominator.ToString(CultureInfo.InvariantCulture),clock=resolved.Power[slot].IsEmpty?null:new{resolved.Power[slot][0].Clock.Rules,resolved.Power[slot][0].Clock.Fingerprint},
            periods=((ScheduledSwapSide)m.Side).Quantities.Select((q,n)=>new {fixingPins=SideComponents.ActualFixingPins(prepared.Snapshot,[((ScheduledSwapSide)m.Side).Periods[n]]),numerator=q.Numerator.ToString(CultureInfo.InvariantCulture),denominator=q.Denominator.ToString(CultureInfo.InvariantCulture),
                power=resolved.Power[slot].IsEmpty?null:CompiledPower(resolved.Power[slot][n])}).ToArray()}).ToArray();
        return await WriteScheduled(connection,body,compiled,resolved.Quantity.Amount.ToString(),frame.At,pins,ct);
    }
    private static object CompiledPower(PowerQuantitySchedule schedule)=>new{
        durationTicks=schedule.Duration.Ticks.ToString(CultureInfo.InvariantCulture),energyQuantity=schedule.EnergyDefinition.Key.Code,
        total=new{schedule.Total.Numerator,schedule.Total.Denominator},intervals=schedule.Intervals.Select(p=>new{interval=new{p.Interval.Start,p.Interval.End,p.Interval.OperatingDate},energy=new{p.Energy.Numerator,p.Energy.Denominator}})};
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
         SELECT jsonb_build_object('slot',p.slot,'period',p.period_ordinal,'ordinal',p.ordinal,'start',p.starts_at,'end',p.ends_at,'operatingDate',p.operating_date,
          'energyNumerator',p.energy_numerator::text,'energyDenominator',p.energy_denominator::text,'energyQuantity',q.energy_quantity_code)::text
         FROM trading.swap_power_quantity_intervals p JOIN trading.swap_quantity_amounts q ON(q.catalog_id,q.trade_id,q.revision,q.slot,q.ordinal)=(p.catalog_id,p.trade_id,p.revision,p.slot,p.period_ordinal)
         WHERE(p.catalog_id,p.trade_id,p.revision)=($1,$2,$3) ORDER BY p.slot,p.period_ordinal,p.ordinal
         """,[manifest.CatalogId,trade,selected],ct);
        return new{tradeId=trade,revision=selected,quantityInputs=await RetainedQuantityInputs(connection,row,ct),intervals};
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
            return new{terms=body,valuation=agreed.Valuation,quantityInputs=agreed.QuantityInputs,parRate=(object?)null};
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
        return new{terms=body,valuation=preview.Valuation,quantityInputs=preview.QuantityInputs,parRate=par};
    }
    private static SwapWriteRequest ScheduledTerms(JsonElement row,Guid snapshot)
    {
        var json=new JsonSerializerOptions(JsonSerializerDefaults.Web){Converters={new ExactDecimalJsonConverter()}};
        var original=row.GetProperty("request").GetProperty("body").Deserialize<SwapWriteRequest>(json)!;
        var retained=row.GetProperty("scheduled_data");
        if(retained.ValueKind!=JsonValueKind.Object)throw Error("swap_definition","The exact scheduled contract is unavailable under the selected context and current authority.");
        var input=retained.GetProperty("terms").Deserialize<SwapScheduledInput>(json)??throw Error("scheduled_swap_input","The exact scheduled terms are unavailable.");
        input=input with{Retained=retained.GetProperty("bindings").EnumerateArray().Select((side,slot)=>new RetainedScheduledSide(side.Id("id"),
            side.GetProperty("periods").EnumerateArray().Select(p=>p.Id("id")).ToImmutableArray(),
            side.GetProperty("periods").EnumerateArray().Select(p=>new ExactRatio(BigInteger.Parse(p.Text("n"),CultureInfo.InvariantCulture),BigInteger.Parse(p.Text("d"),CultureInfo.InvariantCulture))).ToImmutableArray(),
            slot==0?ExactRatio.One:new ExactRatio(BigInteger.Parse(row.Text("second_quantity_numerator"),CultureInfo.InvariantCulture),BigInteger.Parse(row.Text("second_quantity_denominator"),CultureInfo.InvariantCulture)),side.GetProperty("periods").EnumerateArray().ToImmutableArray())).ToImmutableArray()};
        return original with{SnapshotId=snapshot,KnowledgeMode="retained",OperationId=Guid.Empty,Quantity=row.Text("quantity"),ExecutedAt=row.Text("execution_at"),Scheduled=input,
            TradeId=null,ExpectedRevision=null,EffectiveAt=null,Reason=null,BookId=row.Id("book_id"),Memo=row.OptionalText("memo"),TradeDate=row.OptionalText("trade_date")??row.Text("booking_date"),
            LegalEntityId=row.OptionalId("legal_entity_id"),LegalEntityRevision=int.Parse(row.Text("legal_entity_revision"),CultureInfo.InvariantCulture),
            CounterpartyId=row.OptionalId("counterparty_id"),CounterpartyRevision=int.Parse(row.Text("counterparty_revision"),CultureInfo.InvariantCulture)};
    }

}
