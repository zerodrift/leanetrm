using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal static class TradeParties
{
    internal sealed record Choice(Guid Id, int Revision, string Code, string Name);
    internal static async Task<ImmutableArray<Choice>> Catalog(NpgsqlConnection connection, Guid catalog, bool entities, CancellationToken ct)
    {
        // Table choice is internal, never caller-supplied SQL.
        var table = entities ? "legal_entities" : "counterparties";
        var rows = await PreviewDatabase.Rows(connection, $"""
            SELECT row_to_json(t)::text FROM(SELECT DISTINCT ON(id) id,revision,code,name
            FROM trading.{table} WHERE catalog_id=$1 ORDER BY id,revision DESC)t ORDER BY t.code
            """, [catalog], ct);
        return rows.Select(r => new Choice(r.Id("id"), int.Parse(r.Text("revision"), CultureInfo.InvariantCulture), r.Text("code"), r.Text("name"))).ToImmutableArray();
    }
    private static void ValidateReferences(Guid? entity,int? entityRevision,Guid? party,int? partyRevision)
    {
        if(entity is null||entity==Guid.Empty||entityRevision is null or <=0||party is null||party==Guid.Empty||partyRevision is null or <=0)
            throw new MarketDataException("trade_party","Select legal entity and counterparty with exact available versions.");
    }
    internal static string Payload(string? date, Guid? entity, int? entityRevision, Guid? party, int? partyRevision)
    {
        ValidateReferences(entity,entityRevision,party,partyRevision);
        if(!DateOnly.TryParseExact(date,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var day)||day.Year is <1901 or >2199)
            throw new MarketDataException("trade_party","The retained backend booking date is unavailable.");
        return JsonSerializer.Serialize(new{tradeDate=day,legalEntityId=entity,legalEntityRevision=entityRevision,counterpartyId=party,counterpartyRevision=partyRevision});
    }
    internal static string Submitted(Guid? entity,int? entityRevision,Guid? party,int? partyRevision)
    {
        ValidateReferences(entity,entityRevision,party,partyRevision);
        return JsonSerializer.Serialize(new{legalEntityId=entity,legalEntityRevision=entityRevision,counterpartyId=party,counterpartyRevision=partyRevision});
    }
    internal static async Task Validate(NpgsqlConnection connection, Guid catalog, string payload, DateTimeOffset executed, CancellationToken ct)
    {
        await using var command = ScheduleStore.Command(connection, "SELECT security.require_trade_parties($1,$2::jsonb,$3)", catalog, payload, executed);
        // Validation stays inside the same transaction/current authority as booking.
        await command.ExecuteNonQueryAsync(ct);
    }
    internal static async Task<TradeBookingTerms> Resolve(NpgsqlConnection connection, Guid catalog, string payload, DateTimeOffset executed, CancellationToken ct)
    {
        await Validate(connection, catalog, payload, executed, ct);
        var rows = await PreviewDatabase.Rows(connection, """
            SELECT row_to_json(v)::text FROM(SELECT ($2::jsonb->>'tradeDate')::date AS trade_date,
              e.id AS legal_entity_id,e.revision AS legal_entity_revision,e.code AS legal_entity_code,e.name AS legal_entity_name,
              p.id AS counterparty_id,p.revision AS counterparty_revision,p.code AS counterparty_code,p.name AS counterparty_name
              FROM trading.legal_entities e CROSS JOIN trading.counterparties p
              WHERE e.catalog_id=$1 AND p.catalog_id=$1 AND e.id=($2::jsonb->>'legalEntityId')::uuid AND e.revision=($2::jsonb->>'legalEntityRevision')::integer
              AND p.id=($2::jsonb->>'counterpartyId')::uuid AND p.revision=($2::jsonb->>'counterpartyRevision')::integer)v
            """, [catalog, payload], ct);
        if (rows.Length != 1) throw new MarketDataException("trade_party", "The exact selected parties are unavailable under current authority.");
        return Read(catalog, rows[0])!;
    }
    internal static TradeBookingTerms? Read(Guid catalog, JsonElement row)
    {
        if (row.OptionalText("trade_date") is not {} date) return null;
        string Label(string name) => row.OptionalText(name) ?? throw new MarketDataException("trade_party", "An exact retained party reference is unavailable in this context.");
        return new(DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            new(new(catalog, row.Id("legal_entity_id"), int.Parse(row.Text("legal_entity_revision"), CultureInfo.InvariantCulture)), Label("legal_entity_code"), Label("legal_entity_name")),
            new(new(catalog, row.Id("counterparty_id"), int.Parse(row.Text("counterparty_revision"), CultureInfo.InvariantCulture)), Label("counterparty_code"), Label("counterparty_name")));
    }
    internal static object State(Guid catalog, JsonElement row)
    {
        var terms = Read(catalog, row);
        return new { tradeDate = row.OptionalText("booking_date") is {} bookingDate?DateOnly.ParseExact(bookingDate,"yyyy-MM-dd",CultureInfo.InvariantCulture):terms?.TradeDate, legalEntityId = terms?.LegalEntity.Key.Id, legalEntityRevision = terms?.LegalEntity.Key.Revision,
            legalEntity = terms?.LegalEntity.Name, counterpartyId = terms?.Counterparty.Key.Id, counterpartyRevision = terms?.Counterparty.Key.Revision,
            counterparty = terms?.Counterparty.Name };
    }
    internal static bool Same(JsonElement row, string payload)
    {
        var value = JsonSerializer.Deserialize<JsonElement>(payload);
        return row.OptionalText("trade_date") == value.GetProperty("tradeDate").GetString() &&
            row.OptionalId("legal_entity_id") == value.GetProperty("legalEntityId").GetGuid() &&
            row.OptionalText("legal_entity_revision") == value.GetProperty("legalEntityRevision").GetInt32().ToString(CultureInfo.InvariantCulture) &&
            row.OptionalId("counterparty_id") == value.GetProperty("counterpartyId").GetGuid() &&
            row.OptionalText("counterparty_revision") == value.GetProperty("counterpartyRevision").GetInt32().ToString(CultureInfo.InvariantCulture);
    }
}
