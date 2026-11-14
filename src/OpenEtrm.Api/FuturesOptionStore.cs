using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record FuturesOptionBookRequest(Guid OperationId,Guid SnapshotId,Guid BookId,string? Series,string? Side,string? Lots,string? Premium,
    string? PremiumPaymentDate,string? PremiumStyle,string? ExecutedAt,string? Convention,string? KnowledgeMode,Guid? TraderId=null,int? TraderRevision=null,
    string? Memo=null,string? TradeDate=null,Guid? LegalEntityId=null,int? LegalEntityRevision=null,Guid? CounterpartyId=null,int? CounterpartyRevision=null,
    [property:System.Text.Json.Serialization.JsonRequired] Guid DefinitionRevisionId=default);
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record FuturesOptionStatusRequest(Guid OperationId);
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record FuturesOptionEventRequest(Guid OperationId,Guid SnapshotId,Guid TradeId,string? Lots,string? Convention,string? KnowledgeMode);
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record FuturesOptionInstructionRequest(Guid OperationId,Guid SnapshotId,Guid TradeId,string? Instruction,string? Convention,string? KnowledgeMode);
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record FuturesOptionExpiryRequest(Guid OperationId,Guid SnapshotId,Guid TradeId,string? Convention,string? KnowledgeMode);
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record FuturesOptionHoldingRequest(FuturesReadRequest Context,Guid TradeId);

// Listed futures options with premium paid up front: the bookable series at a frame, and booking through the one
// authorized operation, security.book_futures_option, which owns every booking rule.
public sealed partial class FuturesOptionStore(PreviewDatabase database)
{
    private PreviewManifest manifest=>database.Manifest;
    [System.Text.RegularExpressions.GeneratedRegex("^[0-9]+$")]
    private static partial System.Text.RegularExpressions.Regex WholeLots();
    // The booking and valuation convention an option trade records.
    public const string Convention="futures_option_premium_v1";
    private static MarketDataException Input(string message)=>new("option_booking_input",message);
    public static void RequireConvention(string? value)
    { if(value!=Convention)throw new MarketDataException("option_convention",$"Select the explicit {Convention} convention."); }

    // The options/catalog page read: the listed series and the booking directory a booking chooses from.
    // Each underlying future is described once, with its product's name and delivery period, for the page's choice of the contract its
    // options exercise into.
    public async Task<object> Catalog(NpgsqlConnection connection,PreviewData data,InputFrame frame,CancellationToken ct)
    {
        var options=ImmutableArray.CreateBuilder<object>();var underlyings=ImmutableArray.CreateBuilder<object>();
        var unavailable=ImmutableArray.CreateBuilder<object>();
        foreach(var source in await MarketInputSelection.ReadCatalogueTerms(database,connection,ct,frame.Time.Date))
        {
            var codes=source.Members.Where(m=>m.Kind=="curve").Select(m=>m.Code).ToHashSet(StringComparer.Ordinal);
            if(!source.Data.Contracts.Any(c=>c.Kind==ContractKind.FutureOption&&codes.Any(code=>source.Data.HasCurveMember(code,c.Key.Id))))continue;
            try
            {
                var inputs=await MarketInputSelection.Resolve(database,connection,frame,source.Source,[(data,frame)],false,ct);
                var selected=inputs.Frame with{CurveContracts=inputs.Frame.CurveContracts.Where(p=>codes.Contains(p.Key)).ToImmutableDictionary(StringComparer.Ordinal)};
                options.AddRange(Series(inputs.Data,selected));
                foreach(var key in Listed(inputs.Data,selected).Select(s=>s.UnderlyingSeries).Distinct())
                {
                    var future=Single(inputs.Data.FuturesSeries.Where(f=>f.Key==key));
                    var series=Single(inputs.Data.ReferenceRows("futures_series","code",future.Key.Code));
                    var product=Single(inputs.Data.ReferenceRows("futures_products","code",series.Text("product_code")));
                    underlyings.Add(new{definitionRevisionId=inputs.Data.DefinitionRevision.RevisionId,series=future.Key.Code,name=product.Text("name"),
                        deliveryFrom=future.DeliveryFrom.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),deliveryTo=future.DeliveryTo.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)});
                }
            }
            catch(MarketDataException error)when(error.Code=="market_inputs_missing")
            {unavailable.Add(new{source.Source.DefinitionRevisionId,reason=error.Code});}
        }
        var directory=await FuturesStore.ReadBookingDirectory(connection,manifest.CatalogId,ct);
        return new{kind="futures_options",directory.Books,directory.BookingActor,directory.LegalEntities,directory.Counterparties,
            convention=Convention,options=options.ToImmutable(),underlyings=underlyings.ToImmutable(),unavailable=unavailable.ToImmutable()};
    }

    // The listed option series whose expiry is after the frame.
    private static IEnumerable<FuturesOptionSeriesSpecification> Listed(PreviewData data,InputFrame frame)
    {
        var members=frame.CurveContracts.SelectMany(pair=>pair.Value).ToHashSet();
        var series=data.Contracts.Where(contract=>contract.Kind==ContractKind.FutureOption&&members.Contains(contract.Key.Id)).Select(contract=>contract.FuturesSeries).ToHashSet();
        return data.FuturesOptionSeries.Where(option=>series.Contains(option.Key)&&frame.At<option.Terms.ExpiryAt);
    }

    // Every listed option series whose expiry is after the frame, with its exchange, commodity and daily settlement at the
    // frame: read from the trading read's market data alone.
    public static ImmutableArray<object> Series(PreviewData data,InputFrame frame)=>Describe(data,frame,Listed(data,frame));

    // The listed series of one option product and expiry date, as Series describes them: what the page assistant's catalog
    // tool reads. A product that is not in the market data read, whether unknown or unseen, and an expiry date the product
    // lists no unexpired series on, are refused as an unknown series is.
    public static ImmutableArray<object> ExpirySeries(PreviewData data,InputFrame frame,string product,DateOnly expiry)
    {
        _=Single(data.ReferenceRows("futures_option_products","code",product));
        var selected=Listed(data,frame).Where(s=>s.Product.Code==product&&
            DateOnly.ParseExact(Single(data.ReferenceRows("futures_option_series","code",s.Key.Code)).Text("last_trading_date"),"yyyy-MM-dd",CultureInfo.InvariantCulture)==expiry).ToArray();
        if(selected.Length==0)throw new MarketDataException("option_definition",$"Option product {product} lists no unexpired series expiring on {expiry:yyyy-MM-dd}.");
        return Describe(data,frame,selected);
    }

    private static ImmutableArray<object> Describe(PreviewData data,InputFrame frame,IEnumerable<FuturesOptionSeriesSpecification> selected)
    {
        var listed=selected.Select(series=>
        {
            var contract=Single(data.Contracts.Where(c=>c.Kind==ContractKind.FutureOption&&c.FuturesSeries==series.Key));
            var curve=Single(data.Definitions["curve_members"].Where(m=>m.Id("contract_id")==contract.Key.Id)).Text("curve_code");
            return (Series:series,Contract:contract,Request:new CurveRequest(new(contract.Key.Revision,curve),contract.Key));
        }).OrderBy(s=>s.Series.Terms.ExpiryAt).ThenBy(s=>s.Series.Key.Code,StringComparer.Ordinal).ToArray();
        var prices=listed.Length==0?null:data.Model.Build(data.Snapshot(frame),[..listed.Select(s=>s.Request)]).Values.ToLookup(v=>v.Request);
        return [..listed.Select(object (s)=>
            {
                var product=Single(data.ReferenceRows("futures_option_products","code",s.Series.Product.Code));
                var quote=Single(data.QuoteRows(product.Text("quote_code")));
                var exchange=Single(data.ReferenceRows("exchanges","code",product.Text("exchange_code")));
                return new {definitionRevisionId=data.DefinitionRevision.RevisionId,series=s.Series.Key.Code,instrumentId=ContractIdentity.FuturesOption(s.Series.Key),contractId=s.Contract.Key.Id,
                    product=product.Text("code"),name=product.Text("name"),exchange=new{code=exchange.Text("code"),name=exchange.Text("name")},
                    commodity=data.Commodity(s.Request.Curve.Code),underlyingSeries=s.Series.UnderlyingSeries.Code,
                    right=s.Series.Terms.Right.ToString().ToLowerInvariant(),strike=s.Series.Terms.Strike.ToString(),expiryAt=s.Series.Terms.ExpiryAt.ToString("O",CultureInfo.InvariantCulture),
                    exerciseStyle=s.Series.Terms.ExerciseStyle,premiumStyle=s.Series.PremiumStyle,contraryInstructionsAllowed=s.Series.ContraryInstructionsAllowed,
                    multiplier=s.Series.Multiplier.ToString(),currency=Single(data.ReferenceRows("currencies","code",s.Series.Currency.Code)).Text("display_code"),
                    premiumTick=quote.TradingTick.ToString(),minimumPremium=quote.Minimum.ToString(),maximumPremium=quote.Maximum.ToString(),
                    curve=s.Request.Curve.Code,settlementPrice=prices![s.Request].Single().Price.Value.ToString()};
            })];
    }

    public async Task<JsonElement> Book(NpgsqlConnection connection,InputFrame frame,FuturesOptionBookRequest body,CancellationToken ct)
    {
        TradeAnnotations.RequireServerFacts(body.ExecutedAt,body.TraderId,body.TraderRevision,body.TradeDate);
        RequireConvention(body.Convention);
        if(body.KnowledgeMode!="current"||body.OperationId==Guid.Empty)throw Input("Booking needs explicit current knowledge and a stable operation identity.");
        if(string.IsNullOrWhiteSpace(body.Series)||body.Lots is null||body.Premium is null||body.PremiumStyle is null)throw Input("Supply the series, lots, premium and premium style explicitly.");
        // The side states the direction; lots are an unsigned whole number, signed here for the booking operation.
        if(body.Side is not("buy" or "sell"))throw Input("Choose an explicit buy or sell side.");
        if(!WholeLots().IsMatch(body.Lots))throw Input("Supply the lots as an unsigned whole number; the side states the direction.");
        var lots=ExactDecimal.Parse(body.Side=="sell"?"-"+body.Lots:body.Lots);
        if(!DateOnly.TryParseExact(body.PremiumPaymentDate,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var paymentDate))
            throw Input("Supply the premium payment date explicitly as a date.");
        TradeAnnotations.ValidateShape(null,null,body.Memo);
        await using var command=ScheduleStore.Command(connection,
            "SELECT security.book_futures_option($1,$2,$3,$4,$5,$6::numeric,$7::numeric,$8,$9,$10,$11,$12,$13,$14,$15,$16::jsonb)::text",
            manifest.CatalogId,body.OperationId,body.BookId,(await MarketInputSelection.ReadTerms(database,connection,body.DefinitionRevisionId,ct)).DefinitionRevision.RevisionId,body.Series,lots.ToString(),ExactDecimal.Parse(body.Premium).ToString(),
            paymentDate,body.PremiumStyle,frame.At,DBNull.Value,Convention,DBNull.Value,DBNull.Value,body.Memo is {} memo?memo:DBNull.Value,
            TradeParties.Submitted(body.LegalEntityId,body.LegalEntityRevision,body.CounterpartyId,body.CounterpartyRevision));
        return JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);
    }

    public async Task<JsonElement> Status(NpgsqlConnection connection,Guid operation,CancellationToken ct)
    {
        if(operation==Guid.Empty)throw Input("Supply an exact operation identity.");
        await using var capture=ScheduleStore.Command(connection,"SELECT settlement_data.futures_margin_preview_authority($1)",manifest.CatalogId);
        var until=new DateTimeOffset((DateTime)(await capture.ExecuteScalarAsync(ct))!);
        database.RegisterCommitAdmission(connection,new LifecycleReadAdmission(manifest.CatalogId,until));
        await using var command=ScheduleStore.Command(connection,"SELECT security.futures_option_operation_status($1,$2)::text",manifest.CatalogId,operation);
        var result=JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);
        if(result.GetProperty("found").GetBoolean())
            database.RegisterCommitAdmission(connection,new FuturesOptionOriginAdmission(manifest.CatalogId,result.Id("tradeId"),result.GetProperty("revision").GetInt32(),until));
        return result;
    }

    // Event writes require the explicit saved lifecycle policy, source and
    // generated-future enrollment contract. Older UI callers receive a useful
    // refusal instead of entering a primitive that no longer has runtime grants.
    public Task<JsonElement> Close(NpgsqlConnection connection,InputFrame frame,FuturesOptionEventRequest body,string kind,CancellationToken ct)=>throw LifecycleRequired();
    public Task<JsonElement> Instruct(NpgsqlConnection connection,InputFrame frame,FuturesOptionInstructionRequest body,CancellationToken ct)=>throw LifecycleRequired();
    public Task<JsonElement> Expire(NpgsqlConnection connection,InputFrame frame,FuturesOptionExpiryRequest body,CancellationToken ct)=>throw LifecycleRequired();
    private static MarketDataException LifecycleRequired()=>new("lifecycle_configuration_required","Configure and review the option action in Lifecycle before recording its event.");

    // One option holding at the read's knowledge: its booked and open lots and the events known by then.
    public async Task<object> Holding(NpgsqlConnection connection,Guid trade,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,
            "SELECT jsonb_build_object('tradeId',r.trade_id,'lots',trim_scale(r.lots)::text,'openLots',trim_scale(abs(r.lots)-coalesce(sum(e.lots),0))::text,"+
            "'events',coalesce(jsonb_agg(security.futures_option_event_outcome(e) ORDER BY e.publication_order) FILTER(WHERE e.id IS NOT NULL),'[]'))::text "+
            "FROM trading.futures_option_trade_revisions r LEFT JOIN trading.futures_option_events e ON(e.catalog_id,e.trade_id)=(r.catalog_id,r.trade_id) "+
            "WHERE(r.catalog_id,r.trade_id)=($1,$2) GROUP BY r.trade_id,r.lots",manifest.CatalogId,trade);
        if(await command.ExecuteScalarAsync(ct) is not string held)throw new MarketDataException("option_trade","Select an available option trade.");
        var holding=JsonNode.Parse(held)!.AsObject();
        foreach(var e in holding["events"]!.AsArray())await NameLinkedFuture(connection,e!.AsObject(),ct);
        return holding;
    }

    // The refusal message of each lifecycle code the authorized operations raise.
    public static string? EventRefusal(string code)=>code switch
    {
        "option_event_input"=>"Supply a stable request identity, an option trade and whole positive lots, or an explicit instruction.",
        "option_trade"=>"Select an available option trade.",
        "option_event_side"=>"Exercise and instructions apply to long holdings; assignment applies to short holdings.",
        "option_lots_exceeded"=>"The event closes more lots than the holding has open.",
        "option_exercise_window"=>"Exercise and assignment run from the trade's execution through the option's expiry date.",
        "option_calendar"=>"The event date is not a business day of the underlying's trading calendar, or the calendar does not cover it.",
        "option_expired"=>"This holding has already expired.",
        "option_holding_closed"=>"This holding has no open lots.",
        "option_contrary_instructions_not_accepted"=>"This option product does not accept exercise or abandon instructions; expiry follows its automatic exercise rule.",
        "option_instruction_cutoff_unknown"=>"This series has no configured instruction cutoff.",
        "option_instruction_window"=>"An instruction is timed at or after the trade's execution.",
        "option_event_future"=>"An event cannot be dated after the moment it is recorded.",
        "option_instruction_closed"=>"The series' instruction cutoff has passed.",
        "option_instruction_conflict"=>"This holding already has the contrary instruction.",
        "option_expiry_not_reached"=>"The option's expiry instant has not passed; expiry is recorded only after it.",
        "option_expiry_conflict"=>"This holding already expired on a different underlying settlement.",
        "option_expiry_settlement_missing"=>"The selected inputs hold no final daily settlement of the underlying on the option's expiry date; the holding stays open.",
        "option_expiry_settlement_ambiguous"=>"The selected inputs hold more than one final daily settlement of the underlying on the expiry date.",
        "option_exercise_rule_unknown"=>"The product's automatic exercise rule is not stated in a form the backend evaluates.",
        "option_underlying_market"=>"The underlying future needs exactly one direct daily-settlement curve in the option's market definition.",
        _=>null,
    };

    // A read event that booked a future names it by its trade reference beside its key; one the reader cannot see is said to be so. The
    // recording write does not read references, so its receipt carries the key alone and the holding read names it.
    private async Task NameLinkedFuture(NpgsqlConnection connection,JsonObject outcome,CancellationToken ct)
    {
        if(outcome["futuresTradeId"]?.GetValue<string>() is not {} id)return;
        await using var command=ScheduleStore.Command(connection,"SELECT reference_number FROM trading.trade_references WHERE(catalog_id,trade_id)=($1,$2)",manifest.CatalogId,Guid.Parse(id));
        outcome["futuresTradeReference"]=await command.ExecuteScalarAsync(ct) is {} number and not DBNull
            ?TradingReference.Trade(Convert.ToInt32(number,CultureInfo.InvariantCulture)):"A futures trade this account cannot read";
    }

    private static T Single<T>(IEnumerable<T> rows)=>rows.Take(2).ToArray() is [var row]?row
        :throw new MarketDataException("option_definition","The exact option reference is missing or ambiguous.");
}
