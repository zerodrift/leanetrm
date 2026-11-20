using System.Collections.Immutable;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

public sealed record StructuredPowerPriceContract(string CurveCode,Guid ContractId,string QuoteCode,
    string QuantityCode,string CurrencyCode,string DeliveryFrom,string DeliveryTo,string Label,Guid DefinitionRevisionId);

internal static class StructuredPowerTradeCatalog
{
    internal static ImmutableArray<StructuredPowerPriceContract> Prices(PreviewData data,InputFrame frame)
    {
        var result=ImmutableArray.CreateBuilder<StructuredPowerPriceContract>();
        foreach(var curve in data.Definitions["curves"].OrderBy(r=>r.Text("code"),StringComparer.Ordinal))
        {
            var code=curve.Text("code");
            if(!frame.CurveContracts.ContainsKey(code))continue;
            var quotes=data.QuoteRows(curve.Text("quote_code")).Take(2).ToArray();
            if(quotes.Length!=1)throw new MarketDataException("structured_power_price","An available price choice needs one exact native quote.");
            var quote=quotes[0];if(quote.Kind!=QuoteKind.Outright)continue;
            foreach(var request in data.Requests(frame,code))
            {
                var contracts=data.ContractRows(request.Contract.Id).Take(2).ToArray();
                if(contracts.Length!=1)throw new MarketDataException("structured_power_price","An available price choice needs one exact dated contract.");
                var contract=contracts[0];
                var from=contract.DeliveryFrom.ToString("yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture);
                var to=contract.DeliveryTo.ToString("yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture);
                var name=curve.TryGetProperty("name",out var label)&&label.ValueKind==System.Text.Json.JsonValueKind.String?label.GetString()!:code;
                result.Add(new(code,request.Contract.Id,quote.Key.Code,quote.Quantity.Code,quote.Currency.Code,from,to,
                    $"{name} · {from} to {to} · {data.PriceUnit(code)}",data.DefinitionRevision.RevisionId));
            }
        }
        return result.ToImmutable();
    }

    internal static async Task<object> Read(NpgsqlConnection connection,PreviewData data,InputFrame frame,
        string? knownAt,bool current,CancellationToken ct)
    {
        if(knownAt is null)throw new MarketDataException("structured_power_context","Price choices require certified knowledge.");
        var trades=await PreviewDatabase.HasPermission(connection,"ReadTrades",ct);
        if(!trades)throw new PreviewDeniedException();
        var parties=await PreviewDatabase.HasPermission(connection,"ReadTradeParties",ct);
        var author=await PreviewDatabase.HasPermission(connection,"BookTrade",ct);
        var facilityProfiles=await LifecycleNative.Read(connection,"SELECT desk.profiles($1,$2)::text",ct,data.DefinitionRevision.CatalogId,data.DefinitionRevision.RevisionId);
        var result=new{facilityProfiles,snapshotId=frame.Id,contextId=frame.ContextId,knownAt,
            operating=StructuredPowerPreview.Catalog(data,current&&await PreviewDatabase.HasPermission(connection,"ManagePhysicalSetup",ct),
                await PreviewDatabase.HasPermission(connection,"RunScenario",ct)) with{PriceContracts=Prices(data,frame)},
            books=trades?await BookScope.Read(connection,data.DefinitionRevision.CatalogId,ct):[],
            legalEntities=parties?await TradeParties.Catalog(connection,data.DefinitionRevision.CatalogId,true,ct):[],
            counterparties=parties?await TradeParties.Catalog(connection,data.DefinitionRevision.CatalogId,false,ct):[],
            convention=StructuredPowerBookingStore.Convention,canBook=current&&trades&&parties&&author};
        if(parties&&!await PreviewDatabase.HasPermission(connection,"ReadTradeParties",ct))throw new PreviewDeniedException();
        return result;
    }
}
