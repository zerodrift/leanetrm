using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

namespace OpenEtrm.Api;

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record SwapSettlementPreviewRequest(FuturesReadRequest Context, Guid TradeId,
    [property:System.Text.Json.Serialization.JsonRequired] int ExpectedRevision,
    [property:System.Text.Json.Serialization.JsonRequired] int MembershipOrdinal,
    [property:System.Text.Json.Serialization.JsonRequired] int PaymentOrdinal, ReferenceKey Currency,
    [property:System.Text.Json.Serialization.JsonRequired] int DecimalPlaces, string RoundingPolicy);

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record SwapSettlementDetermineRequest(SwapSettlementPreviewRequest Preview, Guid OperationId,
    [property:System.Text.Json.Serialization.JsonRequired] int ExpectedVersion, string PreviewHash,
    string DuePolicy, string RecognitionPolicy, string CorrectionPolicy, string? CorrectionDueAt);

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record SwapCashRequest(FuturesReadRequest Context, Guid TradeId, Guid OperationId,
    [property:System.Text.Json.Serialization.JsonRequired] int ExpectedRevision,
    [property:System.Text.Json.Serialization.JsonRequired] int ExpectedVersion,
    Guid DeterminationId, string Amount, ReferenceKey Currency, string EffectiveAt, string Reference, Guid? ReversesId);

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record SwapSettlementReadRequest(FuturesReadRequest Context, Guid TradeId, int? Revision, string DuePolicy, string RecognitionPolicy);

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record SwapPnlRequest(FuturesReadRequest Opening,FuturesReadRequest Closing,ImmutableArray<Guid> TradeIds,
    ImmutableArray<Guid> BookIds,string ContractChanges,string DuePolicy,string RecognitionPolicy);
internal sealed record SwapPnlCash(DateTimeOffset At,ExactMoney Amount);
internal sealed record SwapPnlHolding(Guid TradeId,Guid BookId,string ContractToken,SwapLifecycleValue Value,ImmutableArray<SwapPnlCash> Cash)
{
    public JsonElement Row {get;init;}
    public SwapLifecycleValue? PriorTermsAtClosing {get;init;}
}
internal sealed record SwapPnlSide(DateTimeOffset At,ImmutableArray<SwapPnlHolding> Holdings)
{
    public ImmutableDictionary<Guid,Guid> VisibleBooks {get;init;}=ImmutableDictionary<Guid,Guid>.Empty;
}

public sealed partial class SwapStore
{
    public async Task<object> SettlementPreview(NpgsqlConnection connection, PreviewData data, InputFrame frame,
        SwapSettlementPreviewRequest request, CancellationToken ct)
    {
        var prepared = await PrepareSettlement(connection, data, frame, request, ct);
        return new { expectedRevision = request.ExpectedRevision, calculation = SettlementValue(prepared.Calculation,prepared.Completion),
            prepared.PreviewHash, settlement = prepared.State,expectedVersion=prepared.State.GetProperty("version").GetInt32(),
            previousTotal=prepared.PreviousTotal.Amount.ToString(),proposedContribution=prepared.Contribution.Amount.ToString(),
            isCorrection=prepared.PreviousId is not null,prepared.InputsChanged };
    }

    private sealed record SettlementPrepared(SwapPaymentCalculation Calculation, JsonElement State, Guid ContextId,
        string ContractHash, string InputHash, string PreviewHash,SwapPeriodCompletion Completion,bool Discounting,
        Guid? PreviousId,Money PreviousTotal,Money Contribution,bool InputsChanged);
    private static string SettlementHash(object value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, RiskJobs.Json)));
    private async Task<SettlementPrepared> PrepareSettlement(NpgsqlConnection connection, PreviewData data, InputFrame frame,
        SwapSettlementPreviewRequest request, CancellationToken ct)
    {
        var rows = await Rows(connection, request.TradeId, ct);
        if (rows.IsEmpty) throw Error("swap_trade", "This trade is unavailable in the selected context.");
        var row = rows[^1];
        if (row.Number("revision") != ExactDecimal.Parse(request.ExpectedRevision.ToString(CultureInfo.InvariantCulture)))
            throw Error("swap_revision_conflict", "Reload the selected trade head before calculating settlement.");
        var terms = Terms(data, row, frame.Id);
        (data, frame) = await ScheduledMarket(connection, data, frame, terms.DefinitionRevisionId, ct);
        var resolved = ResolveAny(data, terms, row.Id("instrument_id"), row.Id("fixed_side_id"), row.Id("floating_side_id"));
        if (request.MembershipOrdinal < 0 || request.MembershipOrdinal >= resolved.Instrument.Sides.Length ||
            resolved.Instrument.Sides[request.MembershipOrdinal].Side.GenerateSchedule() is not SidePaymentSchedule schedule ||
            request.PaymentOrdinal < 0 || request.PaymentOrdinal >= schedule.Payments.Length)
            throw Error("settlement_occurrence", "Select an exact contractual payment.");
        var plan = schedule.Payments[request.PaymentOrdinal];
        var completion=(await SettlementCompletions(connection,row,resolved.Instrument,ct)).Single(c=>c.MembershipOrdinal==request.MembershipOrdinal&&c.DeliveryFrom==plan.DeliveryFrom&&c.DeliveryTo==plan.DeliveryTo);
        var currency = One(data.References["currencies"].Where(c => c.Text("code") == plan.Currency.Code));
        if (request.DecimalPlaces != int.Parse(currency.Text("display_minor_units"), CultureInfo.InvariantCulture))
            throw Error("settlement_rounding", "Pin the selected exact currency revision's contractual minor-unit precision.");
        var state = await SettlementState(connection, request.TradeId, ct);
        var existing = state.GetProperty("occurrences").EnumerateArray().SingleOrDefault(o =>
            o.GetProperty("membership_ordinal").GetInt32() == request.MembershipOrdinal &&
            Date(o.Text("delivery_from")) == plan.DeliveryFrom && Date(o.Text("delivery_to")) == plan.DeliveryTo);
        var key = new PaymentOccurrenceKey(request.TradeId, existing.ValueKind == JsonValueKind.Undefined ? row.Id("binding_id") : existing.Id("origin_binding_id"),
            request.MembershipOrdinal, plan.DeliveryFrom, plan.DeliveryTo);
        var rounding = new SwapSettlementRounding(request.Currency, request.DecimalPlaces, CashTieRule.ToEven, request.RoundingPolicy);
        var calculation = SwapSettlement.Calculate(resolved.Instrument, request.MembershipOrdinal, request.PaymentOrdinal,
            resolved.Quantity, key, data.Model.Build(data.Snapshot(frame), []), rounding);
        var contractHash = SettlementHash(new{cashContract=calculation.ContractHash,completion,policy=SwapPeriodCompletion.Policy});
        if (existing.ValueKind != JsonValueKind.Undefined && existing.Text("contract_hash") != contractHash)
            throw Error("settlement_continuity", "Changed determined payment terms require an explicit qualified continuity mapping.");
        var inputHash = calculation.InputHash;
        var previous=existing.ValueKind==JsonValueKind.Undefined?default:state.GetProperty("determinations").EnumerateArray()
            .Where(d=>d.Id("occurrence_id")==existing.Id("id")).OrderBy(d=>d.GetProperty("ordinal").GetInt32()).LastOrDefault();
        var previousId=previous.ValueKind==JsonValueKind.Undefined?(Guid?)null:previous.Id("id");
        var previousTotal=previousId is null?new Money(default,calculation.Amount.Currency):ExactMoney.Parse(previous.Text("rounded_total"),calculation.Amount.Currency).ToMoney();
        var contribution=SettlementDetermination.RestatedContribution(calculation.Amount,previousId is null?null:previousTotal);
        return new(calculation, state, frame.ContextId ?? throw Error("settlement_context", "Retain the certified calculation context."),
            contractHash, inputHash, SettlementHash(new { contractHash, inputHash, frame.Id,frame.At,calculation.Snapshot.Definitions,calculation.Occurrence,
                request.ExpectedRevision,previousId,settlementVersion=state.GetProperty("version").GetInt32() }),completion,
            terms.Discounting??throw Error("settlement_policy","Retain the explicit swap discount policy."),previousId,previousTotal,contribution,
            previousId is null||previous.Text("input_hash")!=inputHash);
    }

    public async Task<JsonElement> SettlementState(NpgsqlConnection connection, Guid trade, CancellationToken ct)
    {
        await using var command = ScheduleStore.Command(connection, "SELECT settlement_data.swap_state($1,$2)::text", manifest.CatalogId, trade);
        return JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);
    }

    public async Task<object> DetermineSettlement(NpgsqlConnection connection, InputFrame frame, SwapSettlementDetermineRequest request, CancellationToken ct)
    {
        if (request.Preview is not { Context.KnowledgeMode: "current", Context.ContextId: null } preview || request.OperationId == Guid.Empty ||
            request.DuePolicy != "swap_due_nominal_v1" || request.RecognitionPolicy != "swap_completed_period_trading_pnl_v1" ||
            request.CorrectionPolicy != "swap_explicit_correction_due_v1" || string.IsNullOrWhiteSpace(request.PreviewHash))
            throw Error("settlement_policy", "Explicitly enroll this swap under the supported due, per-period recognition, correction and currency-rounding policies.");
        var payload = JsonSerializer.Serialize(request, RiskJobs.Json);
        await using (var retry = ScheduleStore.Command(connection, "SELECT settlement_data.swap_command_status($1,$2,$3::jsonb)::text",
            manifest.CatalogId, request.OperationId, payload))
            if (await retry.ExecuteScalarAsync(ct) is string prior) return JsonSerializer.Deserialize<JsonElement>(prior);
        var prepared = await WithBookingRead(connection, frame, (data, pinned) => PrepareSettlement(connection, data, pinned, preview, ct), ct);
        if (request.PreviewHash != prepared.PreviewHash) throw Error("settlement_stale_preview", "Final inputs or contractual terms changed; recalculate and review settlement.");
        var value = prepared.Calculation;
        var correctionDue=request.CorrectionDueAt is {} submittedDue?SettlementInstant(submittedDue,"settlement_correction_due"):(DateTimeOffset?)null;
        var due=correctionDue??value.Evidence.Plan.PaymentAt;
        if(due>frame.At&&(due.Year is <1901 or >2199||prepared.Discounting&&DateOnly.FromDateTime(due.UtcDateTime)==DateOnly.FromDateTime(frame.At.UtcDateTime)))
            throw Error("settlement_discount_instant","The selected future-cash discount policy supports UTC dates in 1901–2199; discounted intraday future cash requires a separately supported convention.");
        await using var command = ScheduleStore.Command(connection,
            "SELECT settlement_data.determine_swap($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20::numeric,$21::numeric,$22::numeric,$23::jsonb,$24::jsonb,$25)::text",
            manifest.CatalogId, request.OperationId, preview.TradeId, preview.ExpectedRevision, request.ExpectedVersion, prepared.ContextId,
            frame.At, preview.MembershipOrdinal, value.Occurrence.DeliveryFrom, value.Occurrence.DeliveryTo, value.Evidence.Plan.PaymentAt,
            value.Amount.Currency.Revision.CatalogId, value.Amount.Currency.Revision.RevisionId, value.Amount.Currency.Code, value.Rounding.DecimalPlaces,
            prepared.Completion.At,SwapPeriodCompletion.Policy,
            prepared.ContractHash, prepared.InputHash, value.Amount.Amount.ToString(), value.ExactAmount.Numerator, value.ExactAmount.Denominator,
            JsonSerializer.Serialize(SettlementValue(value,prepared.Completion), RiskJobs.Json), payload,
            correctionDue is {} selectedDue ? (object)selectedDue : DBNull.Value);
        return JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);
    }

    public async Task<object> RecordSwapCash(NpgsqlConnection connection, InputFrame frame, SwapCashRequest request, CancellationToken ct)
    {
        if (request.Context is not { KnowledgeMode: "current", ContextId: null, Convention: Convention } || request.OperationId == Guid.Empty ||
            request.TradeId == Guid.Empty || request.DeterminationId == Guid.Empty || string.IsNullOrWhiteSpace(request.Reference))
            throw Error("settlement_cash_input", "Supply an explicit current context, trade, contribution, cash amount, currency, effective instant and reference.");
        var amount = ExactDecimal.Parse(Required(request.Amount));
        var at = SettlementInstant(request.EffectiveAt,"settlement_cash_input");
        var payload = JsonSerializer.Serialize(request, RiskJobs.Json);
        await using (var retry = ScheduleStore.Command(connection, "SELECT settlement_data.swap_command_status($1,$2,$3::jsonb)::text",
            manifest.CatalogId, request.OperationId, payload))
            if (await retry.ExecuteScalarAsync(ct) is string prior) return JsonSerializer.Deserialize<JsonElement>(prior);
        var context = await WithBookingRead(connection, frame, async (data, pinned) =>
        {
            _ = await SettlementState(connection, request.TradeId, ct);
            return pinned.ContextId ?? throw Error("settlement_context", "Retain the recording context.");
        }, ct);
        await using var command = ScheduleStore.Command(connection,
            "SELECT settlement_data.record_swap_cash($1,$2,$3,$4,$5,$6,$7,$8,$9::numeric,$10,$11,$12,$13,$14,$15,$16::jsonb)::text",
            manifest.CatalogId, request.OperationId, request.TradeId, request.ExpectedRevision, request.ExpectedVersion,
            context, frame.At, request.DeterminationId, amount.ToString(), request.Currency.Revision.CatalogId,
            request.Currency.Revision.RevisionId, request.Currency.Code, at, request.Reference,
            request.ReversesId is {} reversed ? (object)reversed : DBNull.Value, payload);
        return JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);
    }

    private static bool TradingInstantSupported(DateTimeOffset value) => value.Offset == TimeSpan.Zero && value.Ticks % 10 == 0 && value != default && value.Year < 9999;
    private static DateTimeOffset SettlementInstant(string? value,string code)
    {
        try
        {
            var at=SchedulePreview.ParseAt(value);
            if(!TradingInstantSupported(at))throw Error(code,"Unsupported settlement instant.");
            return at;
        }
        catch(MarketDataException){throw Error(code,"Supply an explicit UTC instant at microsecond precision for the cash or correction due date.");}
    }

    private async Task<ImmutableArray<SwapPeriodCompletion>> SettlementCompletions(NpgsqlConnection connection,JsonElement row,SidedInstrument instrument,CancellationToken ct)
    {
        var sources=instrument is ScheduledFinancialSwap or CrossCommodityScheduledSwap?await PreviewDatabase.Rows(connection,"""
         SELECT jsonb_build_object('slot',a.slot,'basis',a.basis,'ends',
          (SELECT jsonb_object_agg(p.period_ordinal::text,p.completion) FROM
           (SELECT period_ordinal,max(ends_at) completion FROM trading.swap_power_quantity_intervals
            WHERE(catalog_id,trade_id,revision,slot)=(a.catalog_id,a.trade_id,a.revision,a.slot) GROUP BY period_ordinal)p))::text
         FROM trading.swap_quantity_authoring a WHERE(a.catalog_id,a.trade_id,a.revision)=($1,$2,$3)
         """,[manifest.CatalogId,row.Id("trade_id"),int.Parse(row.Text("revision"),CultureInfo.InvariantCulture)],ct):[];
        return instrument.Sides.SelectMany((membership,slot)=>((SidePaymentSchedule)membership.Side.GenerateSchedule()).Payments.Select(plan=>
        {
            if(instrument is ScheduledFinancialSwap or CrossCommodityScheduledSwap)
            {
                var source=One(sources.Where(s=>int.Parse(s.Text("slot"),CultureInfo.InvariantCulture)==slot));
                if(source.Text("basis")=="factor_of_first_side_v1")source=One(sources.Where(s=>int.Parse(s.Text("slot"),CultureInfo.InvariantCulture)==0));
                if(source.Text("basis")=="selected_interval_power")
                {
                    if(source.GetProperty("ends").ValueKind!=JsonValueKind.Object||!source.GetProperty("ends").TryGetProperty(plan.Key.Ordinal.ToString(CultureInfo.InvariantCulture),out var end))
                        throw Error("settlement_completion","Every clocked quantity period requires its retained final delivery interval.");
                    return new SwapPeriodCompletion(slot,plan.DeliveryFrom,plan.DeliveryTo,SchedulePreview.ParseAt(end.GetString()),"retained_quantity_interval_end_v1");
                }
            }
            return new SwapPeriodCompletion(slot,plan.DeliveryFrom,plan.DeliveryTo,
                new DateTimeOffset(plan.DeliveryTo.ToDateTime(TimeOnly.MinValue,DateTimeKind.Utc)),"date_only_utc_v1");
        })).ToImmutableArray();
    }

    private static SwapSettlementInputs SettlementInputs(JsonElement row, Quantity quantity, ReferenceKey currency, JsonElement state,ImmutableArray<SwapPeriodCompletion> completions)
    {
        var zero = ExactMoney.Parse("0",currency);
        var occurrences = state.GetProperty("occurrences").EnumerateArray().ToDictionary(o => o.Id("id"));
        var determinations = state.GetProperty("determinations").EnumerateArray().ToArray();
        var payments = state.GetProperty("payments").EnumerateArray().ToArray();
        var contributions = determinations.Select(d =>
        {
            var o = occurrences[d.Id("occurrence_id")];
            if (o.Text("due_policy") != "swap_due_nominal_v1" || o.Text("recognition_policy") != "swap_completed_period_trading_pnl_v1")
                throw Error("settlement_policy","The retained lifecycle policy is unsupported.");
            var applied = state.GetProperty("allocations").EnumerateArray().Where(p => p.Id("determination_id") == d.Id("id"))
                .Select(p => ExactMoney.Parse(p.Text("amount"),currency)).Aggregate(zero,(a,b) => a.Add(b));
            return new SettlementContribution(d.Id("id"),o.GetProperty("membership_ordinal").GetInt32(),Date(o.Text("delivery_from")),Date(o.Text("delivery_to")),
                d.GetProperty("ordinal").GetInt32(),d.Instant("due_at"),ExactMoney.Parse(d.Text("contribution"),currency),applied);
        }).ToImmutableArray();
        var rounding = determinations.GroupBy(d => d.Id("occurrence_id")).Select(g => g.MaxBy(d => d.GetProperty("ordinal").GetInt32()))
            .Select(d => ExactMoney.Parse(d.Text("rounded_total"),currency).Subtract(ExactMoney.Parse(d.Text("exact_numerator")+"/"+d.Text("exact_denominator"),currency)))
            .Aggregate(zero,(a,b) => a.Add(b));
        return new(row.Id("trade_id"),row.Id("binding_id"),quantity,contributions,
            payments.Select(p => ExactMoney.Parse(p.Text("amount"),currency)).Aggregate(zero,(a,b) => a.Add(b)),rounding,
            completions,
            "swap_due_nominal_v1","swap_completed_period_trading_pnl_v1");
    }

    private sealed record LifecycleEvaluation(SidedInstrument Instrument,Quantity Quantity,SwapSettlementInputs Inputs,
        SwapLifecycleValue Value,JsonElement State);
    private async Task<LifecycleEvaluation> EvaluateLifecycle(NpgsqlConnection connection,PreviewData data,InputFrame frame,JsonElement row,
        SwapWriteRequest terms,JsonElement state,CancellationToken ct)
    {
        (data,frame) = await ScheduledMarket(connection,data,frame,terms.DefinitionRevisionId,ct);
        var resolved = ResolveAny(data,terms,row.Id("instrument_id"),row.Id("fixed_side_id"),row.Id("floating_side_id"));
        var inputs = SettlementInputs(row,resolved.Quantity,resolved.Instrument.Currency,state,await SettlementCompletions(connection,row,resolved.Instrument,ct));
        var snapshot = data.Snapshot(frame);
        var enabled = terms.Discounting ?? throw Error("settlement_policy","Select the explicit swap discount policy.");
        var mapping = enabled ? await Discount(connection,data,frame,resolved.Instrument.Currency,ct) : null;
        var discounting = new CurrencyDefaultDiscounting(inputs.DiscountKeys(resolved.Instrument,snapshot),snapshot,enabled,mapping);
        var context = (await new KnowledgeStore(manifest.CatalogId).Read(connection,frame.ContextId??throw Error("settlement_context","Supply the exact certified context."),ct)).Context;
        var connected = await CustomCurves.PrepareMarket(connection,database,data,frame,snapshot,context,[resolved.Instrument],resolved.Instrument.DependenciesAt(snapshot),ct);
        return new(resolved.Instrument,resolved.Quantity,inputs,inputs.Evaluate(resolved.Instrument,
            connected.Valuation.PaymentContext(connected.Bindings,connected.Authority,discounting,ct)),state);
    }

    public async Task<object> InspectSettlement(NpgsqlConnection connection,PreviewData data,InputFrame frame,SwapSettlementReadRequest request,CancellationToken ct)
    {
        if(request.DuePolicy!="swap_due_nominal_v1"||request.RecognitionPolicy!="swap_completed_period_trading_pnl_v1")
            throw Error("settlement_policy","Explicitly select the supported due-cash and completed-period trading P&L policies.");
        var rows = await Rows(connection,request.TradeId,ct);
        var row = request.Revision is {} revision ? One(rows.Where(r => int.Parse(r.Text("revision"),CultureInfo.InvariantCulture)==revision)) : rows.LastOrDefault();
        if(row.ValueKind==JsonValueKind.Undefined)throw Error("swap_trade","The selected trade is unavailable.");
        var state = await SettlementState(connection,request.TradeId,ct);
        if(request.Revision is {} selected && selected!=int.Parse(rows[^1].Text("revision"),CultureInfo.InvariantCulture) && state.GetProperty("version").GetInt32()!=0)
            throw Error("settlement_revision","Select the trade revision effective in this certified context; retained settlement does not attach to superseded economics.");
        var result = await EvaluateLifecycle(connection,data,frame,row,Terms(data,row,frame.Id),state,ct);
        return new {state=State(data,row,frame.Id),settlement=state,valuation=LifecycleValue(result.Value),currency=result.Instrument.Currency,
            currencyLabel=One(data.References["currencies"].Where(c=>c.Text("code")==result.Instrument.Currency.Code)).Text("display_code"),
            balances=result.Inputs.Contributions.Select(c=>new{determinationId=c.Id,c.MembershipOrdinal,deliveryFrom=Date(c.DeliveryFrom),deliveryTo=Date(c.DeliveryTo),
                c.Ordinal,c.DueAt,amount=Exact(c.Amount),applied=Exact(c.Applied),outstanding=Exact(c.Outstanding)}),
            cashMovements=state.GetProperty("payments").EnumerateArray().Select(p=>new{id=p.Id("id"),determinationId=p.OptionalId("determination_id"),recordKind=p.Text("record_kind"),
                allocations=state.GetProperty("allocations").EnumerateArray().Where(a=>a.Id("payment_id")==p.Id("id")).Select(a=>new{determinationId=a.Id("determination_id"),amount=a.Text("amount")}),
                amount=p.Text("amount"),reversalAmount=Exact(ExactMoney.Parse(p.Text("amount"),result.Instrument.Currency).Scale(ExactDecimal.Parse("-1"))),
                effectiveAt=p.Text("cash_at"),reference=p.Text("reference"),canReverse=p.GetProperty("reverses_id").ValueKind==JsonValueKind.Null&&
                    !state.GetProperty("payments").EnumerateArray().Any(other=>other.OptionalId("reverses_id")==p.Id("id"))}),
            decimalPlaces=int.Parse(One(data.References["currencies"].Where(c=>c.Text("code")==result.Instrument.Currency.Code)).Text("display_minor_units"),CultureInfo.InvariantCulture),
            roundingPolicy=SwapSettlementRounding.Implementation,completionPolicy=SwapPeriodCompletion.Policy,request.DuePolicy,request.RecognitionPolicy};
    }

    private static object LifecycleValue(SwapLifecycleValue value) => new
    {
        remainingValue=Exact(value.RemainingValue),determinedOutstanding=Exact(value.DeterminedOutstanding),determinedPv=Exact(value.DeterminedPv),
        dueBalance=Exact(value.DueBalance),realized=Exact(value.Realized),unrealized=Exact(value.Unrealized),netCash=Exact(value.NetCash),
        corrections=Exact(value.Corrections),roundingEffect=Exact(value.RoundingEffect),estimatedRemainingPv=Exact(value.EstimatedRemainingPv),
        payments=value.Payments.Select(p=>new {p.MembershipOrdinal,p.Plan.Key.Ordinal,p.Plan.Key.SideId,
            deliveryFrom=Date(p.Plan.DeliveryFrom),deliveryTo=Date(p.Plan.DeliveryTo),p.Plan.PaymentAt,p.State,p.Final,p.Realized,p.CompletionAt,
            contractualAmount=Exact(p.ContractualAmount),remainingValue=Exact(p.RemainingValue)})
    };

    private JsonElement LifecycleHolding(PreviewData data,JsonElement row,JsonElement book,SwapWriteRequest terms,LifecycleEvaluation lifecycle)
    {
        var instrument=lifecycle.Instrument;var value=lifecycle.Value;
        var quantity=One(data.References["quantity_specifications"].Where(q=>q.Text("code")==instrument.UnitQuantity.Specification.Code));
        var unit=One(data.References["units"].Where(u=>u.Text("code")==quantity.Text("unit_code")));
        var currency=One(data.References["currencies"].Where(c=>c.Text("code")==instrument.Currency.Code));
        var plans=instrument.Sides.SelectMany(s=>((SidePaymentSchedule)s.Side.GenerateSchedule()).Payments).ToArray();
        var quote=plans.SelectMany(p=>p.Prices).First(p=>p.Lookup is not null).Lookup!.Quote;
        return Json(new{booking=TradeParties.State(manifest.CatalogId,row),bookingFacts=TradeAnnotations.Facts(row),
            traderId=row.Id("booker_id"),trader=row.Text("booker_name"),traderCode=row.Text("booker_login"),memo=row.OptionalText("memo"),
            tradeId=row.Id("trade_id"),tradeReference=TradingReference.Trade(row),instrumentId=instrument.Id,instrumentReference=TradingReference.Instrument(row),bindingId=row.Id("binding_id"),
            revision=row.Text("revision"),bookId=row.Id("book_id"),book=book.Text("name"),portfolioId=book.Id("portfolio_id"),portfolio=book.Text("portfolio"),instrumentType="financial_swap",definitionRevisionId=row.Id("definition_revision_id"),
            contractId=instrument.Id,contractIdentityKind=terms.Scheduled is null?"swap_instrument":"scheduled_swap_instrument",curve=row.Text("curve_code"),
            deliveryFrom=Date(plans.Min(p=>p.DeliveryFrom)),deliveryTo=Date(plans.Max(p=>p.DeliveryTo)),quoteMultiplier=quote.Multiplier.ToString(),lots=(string?)null,
            quantity=terms.Scheduled is null?lifecycle.Quantity.Amount.ToString():null,unit=unit.Text("symbol"),quantitySpecification=lifecycle.Quantity.Specification,currencyReference=instrument.Currency,
            executedAt=row.Text("booked_at"),executionPrice=(string?)null,fixedPrice=(string?)null,mark=(string?)null,executionPnl=(string?)null,
            presentValue=Exact(value.RemainingValue),currency=currency.Text("display_code"),settlement="Active",lifecycle=LifecycleValue(value)});
    }

    internal async Task<object> PnlRead(NpgsqlConnection connection,PreviewData data,InputFrame frame,SwapPnlRequest request,CancellationToken ct,SwapPnlSide? opening=null)
    {
        if(request.TradeIds.IsDefault||request.BookIds.IsDefault||request.TradeIds.Length>256||request.BookIds.Length>64||request.TradeIds.Distinct().Count()!=request.TradeIds.Length||
            request.BookIds.Distinct().Count()!=request.BookIds.Length||request.ContractChanges is not("reject_contract_changes_v1" or SwapContractBridge.Policy)||
            request.DuePolicy!="swap_due_nominal_v1"||request.RecognitionPolicy!="swap_completed_period_trading_pnl_v1")
            throw Error("settlement_bridge","Select explicit financial-swap scopes, due/recognition policies and the unchanged-contract bridge.");
        var books=await new FuturesStore(manifest).Books(connection,ct);FuturesStore.ValidateScope(books,request.BookIds,[]);
        var allRows=await Rows(connection,null,ct);
        var heads=allRows.GroupBy(r=>r.Id("trade_id")).Select(g=>g.MaxBy(r=>r.Number("revision"))!).ToArray();
        var rows=heads.Where(r=>(request.TradeIds.IsEmpty||request.TradeIds.Contains(r.Id("trade_id")))&&(request.BookIds.IsEmpty||request.BookIds.Contains(r.Id("book_id"))));
        var results=ImmutableArray.CreateBuilder<SwapPnlHolding>();
        foreach(var row in rows)
        {
            var state=await SettlementState(connection,row.Id("trade_id"),ct);
            var value=await EvaluateLifecycle(connection,data,frame,row,Terms(data,row,frame.Id),state,ct);
            var token=SettlementHash(new{instrument=row.Id("instrument_id"),quantity=row.Text("quantity"),book=row.Id("book_id"),
                parties=TradeParties.State(manifest.CatalogId,row)});
            SwapLifecycleValue? prior=null;
            var first=opening?.Holdings.SingleOrDefault(h=>h.TradeId==row.Id("trade_id"));
            if(request.ContractChanges==SwapContractBridge.Policy&&first is not null&&first.Row.Text("revision")!=row.Text("revision"))
                await QualifyBridgeAmendments(connection,data,frame,first.Row,row,allRows,state,ct);
            if(request.ContractChanges==SwapContractBridge.Policy&&opening is not null&&first is null)
            {
                var original=allRows.Where(r=>r.Id("trade_id")==row.Id("trade_id")).MinBy(r=>r.Number("revision"));
                if(original.Text("revision")!="1")throw Error("settlement_contract_change","New-trade history must include its original exact booking.");
                if(row.Text("revision")!="1")await QualifyBridgeAmendments(connection,data,frame,original,row,allRows,state,ct);
            }
            if(request.ContractChanges==SwapContractBridge.Policy&&first is not null&&first.ContractToken!=token)
            {
                var visiblePrior=allRows.SingleOrDefault(r=>r.Id("trade_id")==first.TradeId&&r.Text("revision")==first.Row.Text("revision"));
                if(visiblePrior.ValueKind==JsonValueKind.Undefined)throw Error("settlement_contract_change","Prior exact terms are unavailable under closing-context authority.");
                prior=await PriorTermsAtClosing(connection,data,frame,first with{Row=visiblePrior},row,state,ct);
            }
            results.Add(new(row.Id("trade_id"),row.Id("book_id"),token,value.Value,state.GetProperty("payments").EnumerateArray()
                .Select(p=>new SwapPnlCash(p.Instant("cash_at"),ExactMoney.Parse(p.Text("amount"),value.Instrument.Currency))).ToImmutableArray()){Row=row,PriorTermsAtClosing=prior});
        }
        return new SwapPnlSide(frame.At,results.ToImmutable()){VisibleBooks=heads.ToImmutableDictionary(r=>r.Id("trade_id"),r=>r.Id("book_id"))};
    }

    internal static object ReconcilePnl(SwapPnlSide opening,SwapPnlSide closing,SwapPnlRequest request)
    {
        if(request.ContractChanges==SwapContractBridge.Policy)return ReconcileContractPnl(opening,closing,request);
        var old=opening.Holdings.ToDictionary(h=>h.TradeId);var current=closing.Holdings.ToDictionary(h=>h.TradeId);
        if(old.Count==0||!old.Keys.ToHashSet().SetEquals(current.Keys)||
            !request.TradeIds.IsEmpty&&!request.TradeIds.ToHashSet().SetEquals(current.Keys)||old.Any(p=>p.Value.ContractToken!=current[p.Key].ContractToken))
            throw Error("settlement_contract_change","The complete selected swap scope must be unchanged. New trades and contractual changes require separately qualified bridge contributions.");
        var rows=old.OrderBy(p=>p.Key).Select(p=>new{opening=p.Value,closing=current[p.Key],bridge=SwapEconomicBridge.Calculate(p.Value.Value,current[p.Key].Value,
            current[p.Key].Cash.Where(c=>c.At>opening.At&&c.At<=closing.At).Select(c=>c.Amount)
                .Aggregate(ExactMoney.Parse("0",p.Value.Value.NetCash.Currency),(a,b)=>a.Add(b)))}).ToArray();
        object Bridge(SwapEconomicBridge b)=>new{economicPnl=Exact(b.EconomicPnl),remainingValueMovement=Exact(b.RemainingValueMovement),netCashMovement=Exact(b.NetCashMovement),
            realizedTransfer=Exact(b.RealizedTransfer),unrealizedMovement=Exact(b.UnrealizedMovement),contractualCashMovement=Exact(b.ContractualCashMovement),
            discountEffectMovement=Exact(b.DiscountEffectMovement),correctionsMovement=Exact(b.CorrectionsMovement),roundingEffectMovement=Exact(b.RoundingEffectMovement),
            periodCashReceived=Exact(b.PeriodCashReceived),priorCashKnowledgeAdjustment=Exact(b.PriorCashKnowledgeAdjustment)};
        return new{implementation="unchanged_swap_economic_bridge_v1",scope="financial_swaps",request.TradeIds,request.BookIds,
            overlaps="Economic P&L equals remaining-value movement plus period cash and any separately shown prior-cash knowledge adjustment, and also realized transfer plus unrealized movement. Contractual cash movement plus discount-effect movement is another reconciliation. Contractual cash movement includes forecast/final fixing transitions; discount effect includes changing balances. Corrections and rounding are overlapping detail, not additional earnings.",
            trades=rows.Select(r=>new{r.opening.TradeId,r.opening.BookId,currency=r.bridge.EconomicPnl.Currency,
                opening=LifecycleValue(r.opening.Value),closing=LifecycleValue(r.closing.Value),bridge=Bridge(r.bridge),
                transitions=r.closing.Value.Payments.Select(p=>
                {
                    var first=r.opening.Value.Payments.Single(o=>o.MembershipOrdinal==p.MembershipOrdinal&&o.Plan.DeliveryFrom==p.Plan.DeliveryFrom&&o.Plan.DeliveryTo==p.Plan.DeliveryTo);
                    return new{p.MembershipOrdinal,deliveryFrom=Date(p.Plan.DeliveryFrom),deliveryTo=Date(p.Plan.DeliveryTo),
                        openingState=first.State,closingState=p.State,forecastToFinal=!first.Final&&p.Final,openingPv=Exact(first.RemainingValue),closingPv=Exact(p.RemainingValue)};
                })}),
            totals=rows.GroupBy(r=>(r.opening.BookId,r.bridge.EconomicPnl.Currency)).Select(g=>new{g.Key.BookId,g.Key.Currency,
                economicPnl=Exact(g.Select(r=>r.bridge.EconomicPnl).Aggregate((a,b)=>a.Add(b))),
                realizedTransfer=Exact(g.Select(r=>r.bridge.RealizedTransfer).Aggregate((a,b)=>a.Add(b))),
                unrealizedMovement=Exact(g.Select(r=>r.bridge.UnrealizedMovement).Aggregate((a,b)=>a.Add(b))),
                netCashMovement=Exact(g.Select(r=>r.bridge.NetCashMovement).Aggregate((a,b)=>a.Add(b))),
                periodCashReceived=Exact(g.Select(r=>r.bridge.PeriodCashReceived).Aggregate((a,b)=>a.Add(b))),
                priorCashKnowledgeAdjustment=Exact(g.Select(r=>r.bridge.PriorCashKnowledgeAdjustment).Aggregate((a,b)=>a.Add(b)))})};
    }

    private static object SettlementValue(SwapPaymentCalculation value,SwapPeriodCompletion completion) => new
    {
        completion,completionPolicy=SwapPeriodCompletion.Policy,
        value.Occurrence, value.InstrumentId, sidePayment = value.Evidence.Plan.Key,
        paymentAt = value.Evidence.Plan.PaymentAt, implementation = SwapPaymentCalculation.Implementation, value.NumericalPolicy,
        evidenceIdentityVersion=SwapPaymentCalculation.EvidenceIdentityVersion,value.ContractHash,value.InputHash,
        value.Rounding, exactAmount = Exact(value.ExactAmount), amount = value.Amount.Amount.ToString(),
        roundingEffect = Exact(value.RoundingEffect), value.Amount.Currency,
        bookedQuantity = value.BookedQuantity.Amount.ToString(), resolvedQuantity = QuantityText(value.ResolvedQuantity),
        coefficient = value.Coefficient.ToString(), quantityRatio = new
        {
            numerator = value.QuantityRatio.Numerator.ToString(CultureInfo.InvariantCulture),
            denominator = value.QuantityRatio.Denominator.ToString(CultureInfo.InvariantCulture)
        },
        snapshotId = value.Snapshot.Id, value.Snapshot.Definitions, asOf = value.Snapshot.ValuationAt,
        value.Snapshot.MarketCutoff,
        prices = value.Evidence.Prices.Select(p => new
        {
            price = ExactPrice(p.ExactPrice), p.ExactPrice.Unit, p.State, p.Term.Lookup, p.Term.Spread,
            weight = p.Term.Weight.ToString(), lineage = p.Lineage.OrderBy(k => k.Id).ToImmutableArray()
        }),
        applications = value.Evidence.Applications.Select(a => new
        {
            a.Application.TargetFrom, a.Application.TargetTo, a.Application.PriceOrdinal,
            a.Application.MappingRule, weight = a.Application.Weight.ToString(), unitCash = Exact(a.CashContribution)
        })
    };
}
