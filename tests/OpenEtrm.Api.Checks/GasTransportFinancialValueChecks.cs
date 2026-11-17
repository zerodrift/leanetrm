using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

// The owned native fixture supplies exact observations and an independent signed
// money oracle. Valuation uses the ordinary reader and connected market path.
internal static class GasTransportFinancialValueChecks
{
    internal static async Task Run(string path)
    {
        using var document=JsonDocument.Parse(await File.ReadAllTextAsync(path));var packet=document.RootElement;
        var host=Path.GetFullPath(packet.Text("host"));
        if(!host.StartsWith("/private/tmp/oetrm-ref-",StringComparison.Ordinal)&&!host.StartsWith("/tmp/oetrm-ref-",StringComparison.Ordinal))
            throw new Exception("Financial Transport valuation requires its isolated owned PostgreSQL cluster.");
        var priced=packet.TryGetProperty("expectedValue",out var expectedValue);
        var unpriced=packet.TryGetProperty("expectedUnpricedReason",out var expectedReason);
        var refused=packet.TryGetProperty("expectedRefusalCode",out var expectedRefusal);
        if((priced?1:0)+(unpriced?1:0)+(refused?1:0)!=1||refused&&expectedRefusal.GetString()!="rate_context")
            throw new Exception("Supply exactly one independent signed value, explicit unpriced reason or installed rate-context refusal.");
        var pins=packet.GetProperty("observationIds").EnumerateArray().Select(p=>p.GetGuid()).ToImmutableArray();
        var contracts=packet.GetProperty("curveContracts").EnumerateObject().ToImmutableDictionary(p=>p.Name,
            p=>p.Value.EnumerateArray().Select(id=>id.GetGuid()).ToImmutableArray(),StringComparer.Ordinal);
        if(pins.Any(id=>id==Guid.Empty)||pins.Distinct().Count()!=pins.Length||contracts.Any(p=>string.IsNullOrWhiteSpace(p.Key)||
            p.Value.Any(id=>id==Guid.Empty)||p.Value.Distinct().Count()!=p.Value.Length))
            throw new Exception("Retain every explicitly selected native observation and exact curve contract once.");
        var manifest=packet.GetProperty("manifest").Deserialize<PreviewManifest>(LifecycleNative.Json)!;
        var login=new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="preview_runtime",Timezone="UTC",Pooling=false};
        await using var database=new PreviewDatabase(manifest,new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="preview_authenticator"},login);
        await database.WithIdentity("trader","ReadTrades",async(c,ct)=>
        {
            var context=packet.Id("contextId");var at=TradingInstant.Parse(packet.Text("asOf"),"gas_transport_financial_input");
            var source=(await new KnowledgeStore(manifest.CatalogId).Read(c,context,ct)).Context;
            await PinnedContext.BindExact(c,manifest.CatalogId,source,ct);
            await TradingContext.Bind(c,manifest.CatalogId,source,at,ct);
            var day=PublishedSchedules.ContextDate(source);
            var agreement=await GasTransportFinancialStore.Read(c,database,new(context,at,day),packet.Id("tradeId"),ct);
            var terms=agreement.GetProperty("terms");
            var frame=new InputFrame(Guid.NewGuid(),"Synthetic current whole-rights valuation",at,day,pins,contracts,context)
                {Source=new(terms.Id("definitionRevisionId"),terms.Id("valuationReferenceCatalogId"),terms.Id("valuationReferenceRevisionId"))};
            var data=await MarketInputSelection.Root(database,c,frame,ct);
            var snapshot=data.Snapshot(frame).WithKnowledge(context,SchedulePreview.ParseAt(source.KnownAt));
            if(unpriced||refused)
            {
                var reason=unpriced?expectedReason.GetString():null;
                if(unpriced&&string.IsNullOrWhiteSpace(reason)||agreement.OptionalText("unpricedReason")!=reason)
                    throw new Exception("The retained native source lost its independently expected unpriced reason.");
                try
                {
                    _=await GasTransportFinancialPortfolio.Value(c,database,data,frame,snapshot,agreement,false,ct);
                }
                catch(MarketDataException error)when(error.Code==(unpriced?"gas_transport_financial_unpriced":"rate_context")){return true;}
                throw new Exception("An unavailable complete financial value became a successful partial total.");
            }
            var currency=packet.GetProperty("expectedCurrency");
            var key=new ReferenceKey(new(currency.Id("catalogId"),currency.Id("revisionId")),currency.Text("code"));
            if(GasTransportFinancialAdapter.Terms(manifest.CatalogId,agreement).Rights.Currency!=key)
                throw new Exception("The money oracle requires the exact valuation currency catalog and revision, not an equal currency code.");
            var prepared=await GasTransportFinancialPortfolio.PrepareAgreements(c,database,frame,snapshot,[agreement],[(data,frame)],false,false,ct);
            if(prepared.Length!=1||prepared[0].Measure!=GasTransportFinancialTicket.Measure||prepared[0].Evidence.IsDefaultOrEmpty||
                prepared[0].Evidence.Any(p=>p.Measure!=GasTransportFinancialTicket.Measure))
                throw new Exception("The prepared financial value and every retained position must identify the same public Transport measure as report totals.");
            var retained=agreement.Id("instrumentId");
            var evidence=prepared[0].Evidence.Where(p=>p.BindingId==retained).ToArray();
            var positions=prepared[0].Portfolio.Positions.Where(p=>p.BindingId==retained).ToArray();
            if(evidence.Length!=1||positions.Length!=1||evidence[0].InstrumentId!=retained||evidence[0].ValuationId==retained||
                evidence[0].TradeId!=agreement.Id("tradeId")||evidence[0].BookId!=agreement.Id("bookId")||
                positions[0].TradeId!=evidence[0].TradeId||positions[0].BookId!=evidence[0].BookId||
                positions[0].ValuationId!=evidence[0].ValuationId||positions[0].Quantity!=evidence[0].Quantity)
                throw new Exception("The current rights valuation must retain its native agreement binding and separately identify the exact valued financial state.");
            var value=await GasTransportFinancialPortfolio.Value(c,database,data,frame,snapshot,agreement,false,ct);
            var expected=ExactMoney.Parse(expectedValue.GetString()!,key);
            if(value!=expected)
                throw new Exception($"The independent whole-rights and separate fee oracle differs: expected {expected.ToExactString()}, actual {value.ToExactString()}.");
            return true;
        },default);
        Console.WriteLine(refused?"PASS managed native financial Transport value: valid rights preserve the explicit unsupported rate-clock refusal":
            unpriced?"PASS managed native financial Transport value: explicit unpriced source refuses a fee-only total":
            "PASS managed native financial Transport value: exact signed whole-rights PV and independent fee cash in one pinned currency reference");
    }
}
