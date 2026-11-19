using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Power.Structured;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Commands;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HeatRateCallExerciseRuleInput([property:JsonRequired]string Implementation,[property:JsonRequired]int LeadBusinessDays,
    [property:JsonRequired]string LocalCutoff,[property:JsonRequired]string Calendar);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HeatRateCallTermsInput([property:JsonRequired]Guid DefinitionRevisionId,[property:JsonRequired]Guid ReferenceCatalogId,
    [property:JsonRequired]Guid ReferenceRevisionId,[property:JsonRequired]string Quantity,[property:JsonRequired]string CapacityQuantity,
    [property:JsonRequired]string PowerCurve,[property:JsonRequired]string FuelCurve,[property:JsonRequired]string HeatRate,[property:JsonRequired]string Strike,
    [property:JsonRequired]string ExerciseStyle,[property:JsonRequired]string Model,[property:JsonRequired]string DayCount,
    [property:JsonRequired]string DeliveryFrom,[property:JsonRequired]string DeliveryTo,[property:JsonRequired]string Clock,[property:JsonRequired]int IntervalMinutes,
    [property:JsonRequired]PpaHoursInput Hours,[property:JsonRequired]HeatRateCallExerciseRuleInput ExerciseRule,[property:JsonRequired]PpaPaymentInput PaymentRule);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HeatRateCallBookRequest([property:JsonRequired]FuturesReadRequest Context,[property:JsonRequired]Guid OperationId,[property:JsonRequired]Guid BookId,
    [property:JsonRequired]Guid LegalEntityId,[property:JsonRequired]int LegalEntityRevision,[property:JsonRequired]Guid CounterpartyId,[property:JsonRequired]int CounterpartyRevision,
    [property:JsonRequired]string Reason,[property:JsonRequired]HeatRateCallTermsInput Terms);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HeatRateCallStatusRequest([property:JsonRequired]Guid OperationId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HeatRateCallReadRequest([property:JsonRequired]FuturesReadRequest Context,[property:JsonRequired]Guid TradeId);
public sealed record HeatRateCallReceipt(Guid OperationId,Guid TradeId,int Revision,Guid InstrumentId,bool Changed);
public sealed record HeatRateCallPreparedExercise(string DeliveryDay,string ExerciseAt,string PaymentAt,string EnergyPerUnit,Guid PowerContractId,Guid FuelContractId);
public sealed record HeatRateCallPrepared(Guid InstrumentId,ImmutableArray<HeatRateCallPreparedExercise> Exercises);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SpreadMarkInput([property:JsonRequired]string Month,[property:JsonRequired]int ExpectedRevision,[property:JsonRequired]string LongVolatility,
    [property:JsonRequired]string ShortVolatility,[property:JsonRequired]string Correlation);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SpreadMarksRecordRequest([property:JsonRequired]FuturesReadRequest Context,[property:JsonRequired]Guid OperationId,[property:JsonRequired]Guid DefinitionRevisionId,
    [property:JsonRequired]string LongCurve,[property:JsonRequired]string ShortCurve,[property:JsonRequired]string Reason,[property:JsonRequired]ImmutableArray<SpreadMarkInput> Marks);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SpreadMarksReadRequest([property:JsonRequired]FuturesReadRequest Context,[property:JsonRequired]Guid DefinitionRevisionId,
    [property:JsonRequired]string LongCurve,[property:JsonRequired]string ShortCurve,[property:JsonRequired]ImmutableArray<string> Months);

// Booking resolves the ticket's reference terms once, expands the dated exercises with the managed schedule and
// hands both to the native operation, which owns admission, idempotency and retention.
public sealed class HeatRateCallStore(PreviewDatabase database,TouProductStore products)
{
    public const string Convention="heat_rate_call_trade_v1";
    private Guid Catalog=>database.Manifest.CatalogId;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private static MarketDataException Invalid(string message)=>new("heat_rate_call_terms",message);
    // One sentence a trader can act on for each native refusal; an unlisted code keeps the general wording.
    internal static string Refusal(string code)=>code switch{
        "heat_rate_call_denied" or "spread_mark_denied"=>"This account may not book or mark heat-rate call options here.",
        "heat_rate_call_request_conflict" or "spread_mark_request_conflict"=>"This request identity was already used for a different command. Recover its outcome or send a new request.",
        "heat_rate_call_policy"=>"Select the supported exercise style, model, day count and exercise rule versions.",
        "heat_rate_call_terms"=>"Supply a nonzero capacity, a positive heat rate, a nonnegative strike and two outright curves of different commodities in one currency.",
        "heat_rate_call_exercise"=>"Every exercise must fall after the booking instant, inside the delivery period and in delivery order.",
        "heat_rate_call_contract"=>"Each delivery day needs exactly one contract delivering it on the power curve and on the fuel curve.",
        "heat_rate_call_source" or "spread_mark_source"=>"The selected market definition or reference revision is not available to this account.",
        "heat_rate_call_book"=>"Select a book that existed at the booking instant.",
        "spread_mark_stale"=>"These marks changed since they were read. Reload them and mark again.",
        "spread_mark_time"=>"A mark cannot take effect before the one it replaces.",
        "spread_mark_value"=>"Supply volatilities from 0 to 10, a correlation from -1 to 1 and each month once.",
        "spread_mark_curves"=>"Marks need two outright curves of different commodities in one currency.",
        _=>"Supply the complete, exact heat-rate call option command."};
    private static void Context(FuturesReadRequest context)
    {
        if(context is null||context.SnapshotId==Guid.Empty||context.Convention!=Convention||context.KnowledgeMode!="current"||context.ContextId is not null)
            throw new MarketDataException("historical_read_only","Select the working financial snapshot under the heat-rate call convention.");
    }
    private static string Command(HeatRateCallBookRequest r)=>JsonSerializer.Serialize(new{r.OperationId,convention=Convention,r.BookId,r.LegalEntityId,r.LegalEntityRevision,
        r.CounterpartyId,r.CounterpartyRevision,r.Reason,r.Terms},Json);
    private static ExactDecimal Number(string text)
    {try{return ExactDecimal.Parse(text);}catch(Exception e)when(e is FormatException or OverflowException or ArgumentException){throw Invalid("Supply exact decimal terms.");}}

    public async Task<HeatRateCallPrepared> Prepare(NpgsqlConnection connection,PreviewData data,InputFrame frame,HeatRateCallBookRequest request,CancellationToken ct)
    {
        Context(request.Context);var t=request.Terms;
        if(request.OperationId==Guid.Empty||request.BookId==Guid.Empty||t?.Hours is null||t.ExerciseRule is null||t.PaymentRule is null)throw Invalid("Supply the complete option command.");
        if(t.DefinitionRevisionId!=data.DefinitionRevision.RevisionId||new ReferenceRevision(t.ReferenceCatalogId,t.ReferenceRevisionId)!=data.ReferenceRevision)
            throw Invalid("Book against the selected snapshot's exact market definition and reference revision.");
        ReferenceKey Key(string code)=>new(data.ReferenceRevision,code);
        var from=DateWorkspace.Date(t.DeliveryFrom);var to=DateWorkspace.Date(t.DeliveryTo);
        if(from>=to)throw Invalid("The delivery period must end after it starts.");
        var calendars=new[]{t.Hours.Calendar,t.ExerciseRule.Calendar,t.PaymentRule.Calendar}.OfType<string>().Select(Key).ToArray();
        if(t.Hours.Calendar is null)throw Invalid("Select the delivery holiday calendar.");
        data=await SideCalendarPolicies.PrepareReferences(connection,data,calendars,ct);
        var holidays=CalendarDefinitions.From(data,Key(t.Hours.Calendar));var exerciseDays=CalendarDefinitions.From(data,Key(t.ExerciseRule.Calendar));
        var membership=PowerPreview.Membership(t.Hours.Membership);var firstMonth=new DateOnly(from.Year,from.Month,1);var endMonth=new DateOnly(to.AddDays(-1).Year,to.AddDays(-1).Month,1).AddMonths(1);
        DeliveryHours hours;
        if(membership==BlockMembership.All)
        {
            if(t.Hours.TouProduct is not null||t.Hours.TouVersion is not null)throw Invalid("All hours has no block product.");
            hours=new(membership,null,holidays.Closures(firstMonth,endMonth));
        }
        else
        {
            var version=(await products.Read(connection,ct)).SingleOrDefault(v=>v.Id==t.Hours.TouProduct&&v.Version==t.Hours.TouVersion&&v.State=="published")
                ??throw Invalid("Select an exact published block product version.");
            var policy=new SideCalendarPolicy(CalendarBinding.Owned(holidays),CalendarBinding.NotApplicable(),CalendarBinding.NotApplicable(),SideCalendarPolicy.Implementation);
            hours=version.Binding(data.DefinitionRevision.CatalogId).Bind(membership,Key(t.Clock),policy,firstMonth,endMonth);
        }
        QuoteSpecification Quote(string curve)
        {
            var definitions=data.DefinitionRows("curves","code",curve).ToArray();
            return definitions.Length==1&&data.QuoteRows(definitions[0].Text("quote_code")).ToArray() is [var quote]?quote:throw Invalid("Select one exact power curve and one exact fuel curve.");
        }
        HeatRateCallCurve Curve(string curve)=>new(new(data.DefinitionRevision,curve),
            [..data.Requests(frame,curve).Select(r=>data.ContractRows(r.Contract.Id).ToArray() is [var contract]?contract:throw Invalid("Every curve contract needs one exact definition."))]);
        var power=Quote(t.PowerCurve);
        if(!TimeOnly.TryParseExact(t.ExerciseRule.LocalCutoff,"HH:mm",CultureInfo.InvariantCulture,DateTimeStyles.None,out var cutoff))throw Invalid("Supply the exercise cutoff as HH:mm.");
        var p=t.PaymentRule;PaymentCalendar? paymentDays=p.Calendar is null?null:CalendarDefinitions.From(data,Key(p.Calendar)) is var c?new(c.Key,c.BusinessDays):null;
        var payment=new PaymentRule(SwapPaymentOptions.Parse(SwapPaymentOptions.Frequencies,p.Frequency),SwapPaymentOptions.Parse(SwapPaymentOptions.Rules,p.Rule),p.Days,p.DayOfMonth,
            p.DayBasis is null?null:SwapPaymentOptions.Parse(SwapPaymentOptions.DayBases,p.DayBasis),p.ShortMonth is null?null:SwapPaymentOptions.Parse(SwapPaymentOptions.ShortMonths,p.ShortMonth),
            SwapPaymentOptions.Parse(SwapPaymentOptions.Adjustments,p.Adjustment),paymentDays,p.Implementation);
        var delivery=new HeatRateCallDelivery(from,to,PpaQuantitiesStore.Clock(data,t.Clock),t.IntervalMinutes,hours,
            PpaQuantitiesStore.Quantity(data,t.CapacityQuantity),PpaQuantitiesStore.Quantity(data,power.Quantity.Code));
        var rule=new HeatRateCallExerciseRule(t.ExerciseRule.LeadBusinessDays,cutoff,new(exerciseDays.Key,exerciseDays.BusinessDays),t.ExerciseRule.Implementation);
        var exercises=HeatRateCallSchedule.Expand(delivery,rule,payment,Curve(t.PowerCurve),Curve(t.FuelCurve));
        // The same terms the native operation retains must already form a valid managed contract.
        var fuel=Quote(t.FuelCurve);
        _=new HeatRateCallInstrument(OperationIdentity.For("heat_rate_call_instrument_v1",request.OperationId),new(Key(t.CapacityQuantity),power,fuel,
            new QuantityFactor(Number(t.HeatRate),fuel.Quantity,power.Quantity,QuantityFactor.Implementation),new UnitPrice(Number(t.Strike),PriceUnit.FromQuote(power)),
            t.ExerciseStyle,t.Model,t.DayCount,exercises),null);
        static string Instant(DateTimeOffset at)=>at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",CultureInfo.InvariantCulture);
        return new(OperationIdentity.For("heat_rate_call_instrument_v1",request.OperationId),[..exercises.Select(e=>new HeatRateCallPreparedExercise(
            e.DeliveryDay.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),Instant(e.ExerciseAt),Instant(e.PaymentAt),e.EnergyPerUnit.ToString(),e.Power.Contract.Id,e.Fuel.Contract.Id))]);
    }
    public Task<HeatRateCallReceipt?> Replay(NpgsqlConnection c,HeatRateCallBookRequest request,CancellationToken ct)=>
        Receipt(c,"SELECT security.heat_rate_call_booking_replay($1,$2::jsonb)::text",ct,Catalog,Command(request));
    public Task<HeatRateCallReceipt?> Status(NpgsqlConnection c,HeatRateCallStatusRequest request,CancellationToken ct)=>
        Receipt(c,"SELECT security.heat_rate_call_booking_status($1,$2)::text",ct,Catalog,request.OperationId);
    public async Task<HeatRateCallReceipt> Save(NpgsqlConnection c,InputFrame frame,HeatRateCallBookRequest request,HeatRateCallPrepared prepared,CancellationToken ct)=>
        await Receipt(c,"SELECT security.book_heat_rate_call($1,$2::jsonb,$3::jsonb,$4)::text",ct,Catalog,Command(request),JsonSerializer.Serialize(prepared,Json),frame.At)
            ??throw Invalid("The booking returned no outcome.");
    private static async Task<HeatRateCallReceipt?> Receipt(NpgsqlConnection c,string sql,CancellationToken ct,params object[] values)
    {await using var query=ScheduleStore.Command(c,sql,values);return await query.ExecuteScalarAsync(ct) is string text?JsonSerializer.Deserialize<HeatRateCallReceipt>(text,Json):null;}

    // The booked option as the blotter lists it, with its retained exercises: valued, or with the reason it cannot be.
    public async Task<object> Read(NpgsqlConnection c,InputFrame frame,Guid trade,CancellationToken ct)
    {
        var positions=await HeatRateCallPositions.Read(c,database.Manifest,[],[],ct,trades:[trade]);
        if(positions.Length!=1)throw new MarketDataException("heat_rate_call_unavailable","The option is not available under the selected context.");
        var run=new HeatRateCallBlotter.OptionRun(c,database,frame,positions);
        await foreach(var unit in run.Units(ct))return new{position=unit[0],exercises=positions[0].Exercises};
        throw new MarketDataException("heat_rate_call_unavailable","The option is not available under the selected context.");
    }
    public async Task<JsonElement> RecordMarks(NpgsqlConnection c,InputFrame frame,SpreadMarksRecordRequest request,CancellationToken ct)
    {
        Context(request.Context);
        var command=JsonSerializer.Serialize(new{request.OperationId,convention=HeatRateCallMarks.HeldLogReturn,request.DefinitionRevisionId,request.LongCurve,request.ShortCurve,request.Reason,request.Marks},Json);
        await using var query=ScheduleStore.Command(c,"SELECT security.record_spread_marks($1,$2::jsonb,$3)::text",Catalog,command,frame.At);
        return JsonSerializer.Deserialize<JsonElement>((string)(await query.ExecuteScalarAsync(ct))!);
    }
    public async Task<object> ReadMarks(NpgsqlConnection c,SpreadMarksReadRequest request,CancellationToken ct)
    {
        var months=request.Months.Select(m=>DateOnly.TryParseExact(m+"-01","yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var month)?month:
            throw new MarketDataException("spread_mark_input","Supply delivery months as yyyy-MM.")).ToArray();
        return new{marks=await PreviewDatabase.Rows(c,"SELECT value::text FROM jsonb_array_elements(security.read_spread_marks($1,$2,$3,$4,$5::date[]))",
            [Catalog,request.DefinitionRevisionId,request.LongCurve,request.ShortCurve,months],ct)};
    }
}
