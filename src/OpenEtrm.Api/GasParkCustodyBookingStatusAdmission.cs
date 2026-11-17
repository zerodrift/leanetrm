using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

// Receipt recovery retains the authority captured by its readonly lookup. An
// absent lookup never acquires inventory authority over a later new receipt.
internal sealed record GasParkCustodyBookingStatusAdmission(Guid Catalog,Guid Operation,DateTimeOffset Until,bool Inventory,bool Found):ICommitAdmission
{
    internal static GasParkCustodyBookingStatusAdmission Read(Guid catalog,Guid operation,JsonElement response)
    {
        try
        {
            var found=response.GetProperty("found").GetBoolean();
            var inventory=response.GetProperty("inventory").GetBoolean();
            var until=TradingInstant.Parse(response.Text("authorityUntil"),"gas_custody_source");
            if(catalog==Guid.Empty||operation==Guid.Empty||!found&&inventory)throw Invalid();
            return new(catalog,operation,until,inventory,found);
        }
        catch(Exception error)when(error is KeyNotFoundException or InvalidOperationException or FormatException or JsonException)
        {throw Invalid();}
    }

    public async Task RequireCommit(NpgsqlConnection connection,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,
            "SELECT security.require_gas_park_custody_booking_status($1,$2,$3,$4,$5)",Catalog,Operation,Until,Inventory,Found);
        await command.ExecuteNonQueryAsync(ct);
    }
    static MarketDataException Invalid()=>new("gas_custody_source","Retain the native booking receipt lookup's original authority deadline and exact read state.");
}
