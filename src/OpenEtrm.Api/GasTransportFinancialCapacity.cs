using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal static class GasTransportFinancialCapacity
{
    sealed record Admission(Guid Catalog,Guid Context,DateTimeOffset At,DateTimeOffset Until):ICommitAdmission
    {
        public async Task RequireCommit(NpgsqlConnection c,CancellationToken ct)
        {
            await using var command=ScheduleStore.Command(c,"SELECT security.require_gas_transport_financial_capacity_read($1,$2,$3,$4)",Catalog,Context,At,Until);
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    // Commercial source selection uses the financial instant. Operating plans
    // retain their separate business-date clock and are not read here.
    internal static async Task<ImmutableArray<JsonElement>> Read(NpgsqlConnection c,PreviewDatabase database,LifecycleReadContext context,Guid? entitlement,CancellationToken ct)
    {
        var envelope=await LifecycleNative.Read(c,"SELECT security.gas_transport_financial_capacity_facts($1,$2,$3,$4::uuid)::text",ct,
            database.Manifest.CatalogId,context.ContextId,context.AsOf,(object?)entitlement??DBNull.Value);
        var admission=new Admission(database.Manifest.CatalogId,context.ContextId,context.AsOf,
            TradingInstant.Parse(envelope.Text("authorityUntil"),"gas_transport_financial_source"));
        database.RegisterCommitAdmission(c,admission);
        var known=(await new KnowledgeResolver(database.Manifest.CatalogId).Read(c,context.ContextId,ct)).Context;
        if(PublishedSchedules.ContextDate(known)!=context.BusinessDate)throw GasTransportFinancialStore.Source();
        var rows=Require(envelope,database.Manifest.CatalogId,context,TradingInstant.Parse(known.KnownAt,"gas_transport_financial_source"),entitlement);
        await admission.RequireCommit(c,ct);return rows;
    }

    internal static async Task<JsonElement> ReadOne(NpgsqlConnection c,PreviewDatabase database,LifecycleReadContext context,Guid entitlement,CancellationToken ct)=>
        (await Read(c,database,context,entitlement,ct)).Single();

    internal static ImmutableArray<JsonElement> Require(JsonElement envelope,Guid catalog,LifecycleReadContext context,DateTimeOffset knownAt,Guid? entitlement)
    {
        try
        {
            if(catalog==Guid.Empty||context.ContextId==Guid.Empty||context.BusinessDate==default||entitlement==Guid.Empty||
                !TradingInstant.IsSupported(context.AsOf)||envelope.Id("contextId")!=context.ContextId||
                TradingInstant.Parse(envelope.Text("asOf"),"gas_transport_financial_source")!=context.AsOf||
                TradingInstant.Parse(envelope.Text("knownAt"),"gas_transport_financial_source")!=knownAt||
                DateWorkspace.Date(envelope.Text("effectiveOn"))!=context.BusinessDate)throw GasTransportFinancialStore.Source();
            _=TradingInstant.Parse(envelope.Text("authorityUntil"),"gas_transport_financial_source");
            var values=envelope.GetProperty("capacities");
            if(values.ValueKind!=JsonValueKind.Array||values.GetArrayLength()>256||entitlement is not null&&values.GetArrayLength()!=1)
                throw GasTransportFinancialStore.Source();
            var seen=new HashSet<Guid>();var result=ImmutableArray.CreateBuilder<JsonElement>();
            foreach(var row in values.EnumerateArray())
            {
                var header=row.GetProperty("entitlement");var id=header.Id("id");var future=row.GetProperty("hasFutureHeads").GetBoolean();
                if(id==Guid.Empty||header.Id("catalog_id")!=catalog||!seen.Add(id)||entitlement is {} selected&&id!=selected||
                    row.GetProperty("label").ValueKind!=JsonValueKind.String||row.Text("label").Length==0||
                    TradingInstant.Parse(row.Text("asOf"),"gas_transport_financial_source")!=context.AsOf)throw GasTransportFinancialStore.Source();
                var version=row.GetProperty("version");
                if(version.ValueKind==JsonValueKind.Object)
                {
                    if(future||version.Id("catalog_id")!=catalog||version.Id("entitlement_id")!=id||version.Id("id")==Guid.Empty||
                        version.GetProperty("revision").GetInt32()<1||TradingInstant.Parse(version.Text("effective_at"),"gas_transport_financial_source")>context.AsOf)
                        throw GasTransportFinancialStore.Source();
                }
                else if(version.ValueKind!=JsonValueKind.Null)throw GasTransportFinancialStore.Source();
                result.Add(row.Clone());
            }
            return result.ToImmutable();
        }
        catch(Exception error)when(error is KeyNotFoundException or InvalidOperationException or FormatException or JsonException or ArgumentException or OverflowException)
        {throw GasTransportFinancialStore.Source();}
    }
}
