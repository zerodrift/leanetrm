using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record FuturesOptionBookRequest(Guid OperationId,Guid SnapshotId,Guid BookId,string? Series,string? Lots,string? Premium,
    string? PremiumPaymentDate,string? PremiumStyle,string? ExecutedAt,string? Convention,string? KnowledgeMode,Guid? TraderId=null,int? TraderRevision=null,
    string? Memo=null,string? TradeDate=null,Guid? LegalEntityId=null,int? LegalEntityRevision=null,Guid? CounterpartyId=null,int? CounterpartyRevision=null);
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record FuturesOptionStatusRequest(Guid OperationId);

// Listed futures options with premium paid up front: the bookable series at a frame, and booking through the one
// authorized operation, security.book_futures_option, which owns every booking rule.
public sealed class FuturesOptionStore(PreviewManifest manifest)
{
    // The booking and valuation convention an option trade records.
    public const string Convention="futures_option_premium_v1";
    private static MarketDataException Input(string message)=>new("option_booking_input",message);
    public static void RequireConvention(string? value)
    { if(value!=Convention)throw new MarketDataException("option_convention",$"Select the explicit {Convention} convention."); }

    // Every listed option series whose expiry is after the frame, with its daily settlement at the frame.
    public async Task<object> Catalog(NpgsqlConnection connection,PreviewData data,InputFrame frame,CancellationToken ct)
    {
        var listed=data.FuturesOptionSeries.Where(s=>frame.At<s.Terms.ExpiryAt).Select(series=>
        {
            var contract=Single(data.Contracts.Where(c=>c.Kind==ContractKind.FutureOption&&c.FuturesSeries==series.Key));
            var curve=Single(data.Definitions["curve_members"].Where(m=>m.Id("contract_id")==contract.Key.Id)).Text("curve_code");
            return (Series:series,Contract:contract,Request:new CurveRequest(new(contract.Key.Revision,curve),contract.Key));
        }).OrderBy(s=>s.Series.Terms.ExpiryAt).ThenBy(s=>s.Series.Key.Code,StringComparer.Ordinal).ToArray();
        var prices=listed.Length==0?null:data.Model.Build(data.Snapshot(frame),[..listed.Select(s=>s.Request)]).Values.ToLookup(v=>v.Request);
        var directory=await FuturesStore.ReadBookingDirectory(connection,manifest.CatalogId,ct);
        return new {kind="futures_options",directory.Books,directory.BookingActor,directory.LegalEntities,directory.Counterparties,convention=Convention,
            options=listed.Select(s=>
            {
                var product=Single(data.ReferenceRows("futures_option_products","code",s.Series.Product.Code));
                var quote=Single(data.QuoteRows(product.Text("quote_code")));
                return new {series=s.Series.Key.Code,instrumentId=ContractIdentity.FuturesOption(s.Series.Key),contractId=s.Contract.Key.Id,
                    product=product.Text("code"),name=product.Text("name"),exchange=product.Text("exchange_code"),underlyingSeries=s.Series.UnderlyingSeries.Code,
                    right=s.Series.Terms.Right.ToString().ToLowerInvariant(),strike=s.Series.Terms.Strike.ToString(),expiryAt=s.Series.Terms.ExpiryAt.ToString("O",CultureInfo.InvariantCulture),
                    exerciseStyle=s.Series.Terms.ExerciseStyle,premiumStyle=s.Series.PremiumStyle,contraryInstructionsAllowed=s.Series.ContraryInstructionsAllowed,
                    multiplier=s.Series.Multiplier.ToString(),currency=Single(data.ReferenceRows("currencies","code",s.Series.Currency.Code)).Text("display_code"),
                    premiumTick=quote.TradingTick.ToString(),minimumPremium=quote.Minimum.ToString(),maximumPremium=quote.Maximum.ToString(),
                    curve=s.Request.Curve.Code,settlementPrice=prices![s.Request].Single().Price.Value.ToString()};
            }).ToArray()};
    }

    public async Task<JsonElement> Book(NpgsqlConnection connection,InputFrame frame,FuturesOptionBookRequest body,CancellationToken ct)
    {
        TradeAnnotations.RequireServerFacts(body.ExecutedAt,body.TraderId,body.TraderRevision,body.TradeDate);
        RequireConvention(body.Convention);
        if(body.KnowledgeMode!="current"||body.OperationId==Guid.Empty)throw Input("Booking needs explicit current knowledge and a stable operation identity.");
        if(string.IsNullOrWhiteSpace(body.Series)||body.Lots is null||body.Premium is null||body.PremiumStyle is null)throw Input("Supply the series, lots, premium and premium style explicitly.");
        if(!DateOnly.TryParseExact(body.PremiumPaymentDate,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var paymentDate))
            throw Input("Supply the premium payment date explicitly as a date.");
        TradeAnnotations.ValidateShape(null,null,body.Memo);
        await using var command=ScheduleStore.Command(connection,
            "SELECT security.book_futures_option($1,$2,$3,$4,$5,$6::numeric,$7::numeric,$8,$9,$10,$11,$12,$13,$14,$15,$16::jsonb)::text",
            manifest.CatalogId,body.OperationId,body.BookId,manifest.RevisionId,body.Series,ExactDecimal.Parse(body.Lots).ToString(),ExactDecimal.Parse(body.Premium).ToString(),
            paymentDate,body.PremiumStyle,frame.At,DBNull.Value,Convention,DBNull.Value,DBNull.Value,body.Memo is {} memo?memo:DBNull.Value,
            TradeParties.Submitted(body.LegalEntityId,body.LegalEntityRevision,body.CounterpartyId,body.CounterpartyRevision));
        return JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);
    }

    public async Task<JsonElement> Status(NpgsqlConnection connection,Guid operation,CancellationToken ct)
    {
        if(operation==Guid.Empty)throw Input("Supply an exact operation identity.");
        await using var command=ScheduleStore.Command(connection,"SELECT security.futures_option_operation_status($1,$2)::text",manifest.CatalogId,operation);
        return JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);
    }

    private static T Single<T>(IEnumerable<T> rows)=>rows.Take(2).ToArray() is [var row]?row
        :throw new MarketDataException("option_definition","The exact option reference is missing or ambiguous.");
}
