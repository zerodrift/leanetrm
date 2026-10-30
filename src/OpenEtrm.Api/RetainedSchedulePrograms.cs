using System.Collections.Immutable;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal sealed record RetainedSideProgram(int Slot,Guid SideId,Guid ScheduleId,BookedSideProgramIdentity Identity,BookedSideProgram Program);

internal sealed partial class RetainedScheduleStore
{
    // This adapter takes no Side, definition or parameter source. The authorized
    // read supplies the complete published instructions and their trusted identity.
    internal async Task<ImmutableArray<RetainedSideProgram>> LoadPrograms(NpgsqlConnection connection,Guid instrument,
        Guid? context,DateTimeOffset? asOf,CancellationToken cancellation)
    {
        var result=ImmutableArray.CreateBuilder<RetainedSideProgram>();
        await using var query=ScheduleStore.Command(connection,
            "SELECT * FROM security.read_instrument_side_programs($1,$2,$3,$4)",Catalog,instrument,
            (object?)context??DBNull.Value,(object?)asOf??DBNull.Value);
        try
        {
            await using var reader=await query.ExecuteReaderAsync(cancellation);
            while(await reader.ReadAsync(cancellation))
            {
                cancellation.ThrowIfCancellationRequested();
                if(reader.GetString(3)!=BookedSideProgram.CurrentVersion||reader.GetString(10)!=ScheduleDefinition.CurrentSchema||
                    reader.GetString(11)!=ScheduleDefinition.CurrentGenerator)
                    throw new MarketDataException("side_schedule_unsupported","The retained executable schedule uses an unsupported version and must be rebooked.");
                var identity=new BookedSideProgramIdentity(reader.GetString(5),reader.GetString(6),reader.GetString(7),reader.GetString(8),reader.GetString(9));
                var program=BookedSideProgram.Read(reader.GetFieldValue<byte[]>(4),identity);
                var slot=reader.GetInt32(2);
                if(slot!=result.Count)throw Error("The executable schedules require complete ordered instrument memberships.");
                result.Add(new(slot,reader.GetGuid(0),reader.GetGuid(1),identity,program));
            }
        }
        catch(PostgresException error)when(error.SqlState=="P0001"&&error.MessageText is "side_schedule_missing" or "side_schedule_unsupported")
        {
            throw new MarketDataException(error.MessageText,"The booked instrument has no supported complete executable schedule and must be rebooked.");
        }
        if(result.Count==0)throw new MarketDataException("side_schedule_missing","The booked instrument has no executable schedules and must be rebooked.");
        // Deserialization can outlast a source grant's expiry. Recheck the same
        // membership/source authority after consuming the protected byte columns.
        await using var authority=ScheduleStore.Command(connection,
            "SELECT count(*) FROM security.read_instrument_side_schedule_headers($1,$2,$3,$4)",Catalog,instrument,
            (object?)context??DBNull.Value,(object?)asOf??DBNull.Value);
        if((long)(await authority.ExecuteScalarAsync(cancellation))! !=result.Count)
            throw Error("The executable membership set changed during its authorized read.");
        cancellation.ThrowIfCancellationRequested();return result.ToImmutable();
    }
}
