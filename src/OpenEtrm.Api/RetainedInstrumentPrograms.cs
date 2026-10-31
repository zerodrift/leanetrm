using System.Collections.Immutable;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal sealed record RetainedInstrumentPrograms(ImmutableArray<RetainedSideProgram> Members,
    ImmutableArray<InstrumentQuantityConnection> Connections);

internal sealed partial class RetainedScheduleStore
{
    internal async Task<RetainedInstrumentPrograms> LoadInstrumentPrograms(NpgsqlConnection connection,Guid instrument,
        Guid? context,DateTimeOffset? asOf,CancellationToken cancellation)
    {
        var members=await LoadPrograms(connection,instrument,context,asOf,cancellation);
        var links=ImmutableArray.CreateBuilder<InstrumentQuantityConnection>();
        await using(var command=ScheduleStore.Command(connection,
            "SELECT * FROM security.read_instrument_schedule_cells($1,$2,$3,$4)",Catalog,instrument,
            (object?)context??DBNull.Value,(object?)asOf??DBNull.Value))
        try
        {
            await using var reader=await command.ExecuteReaderAsync(cancellation);
            while(await reader.ReadAsync(cancellation))
            {
                cancellation.ThrowIfCancellationRequested();
                links.Add(new(reader.GetGuid(0),reader.GetInt32(1),reader.GetGuid(2),reader.GetString(3),
                    [..reader.GetFieldValue<int[]>(4)],[..reader.GetFieldValue<int[]>(5)]));
            }
        }
        catch(PostgresException error)when(error.SqlState=="P0001"&&error.MessageText=="instrument_schedule_missing")
        {throw new MarketDataException("instrument_schedule_missing","The instrument has no retained executable connections and requires explicit rebooking.");}
        // Recheck every member/source after decoding both protected read results.
        await using var authority=ScheduleStore.Command(connection,
            "SELECT (h->>'side_id')::uuid FROM security.read_instrument_side_schedule_headers($1,$2,$3,$4) h",Catalog,instrument,
            (object?)context??DBNull.Value,(object?)asOf??DBNull.Value);
        var visible=new List<Guid>();
        await using(var reader=await authority.ExecuteReaderAsync(cancellation))
            while(await reader.ReadAsync(cancellation))visible.Add(reader.GetGuid(0));
        if(visible.Count!=members.Length||!visible.ToHashSet().SetEquals(members.Select(member=>member.SideId))||
            links.Any(link=>!visible.Contains(link.InputSide)||!visible.Contains(link.OutputSide)))
            throw new MarketDataException("instrument_schedule_cells","Executable connections must retain their exact authorized membership set.");
        cancellation.ThrowIfCancellationRequested();return new(members,links.ToImmutable());
    }
}
