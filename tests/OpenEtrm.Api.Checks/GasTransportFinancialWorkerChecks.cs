using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

// An ordinary selected-trade PRICE report is the oracle for its retained job.
// Independent signed-money and native-membership assertions precede admission.
internal static class GasTransportFinancialWorkerChecks
{
    internal static async Task Run(string path,bool verify)
    {
        using var document=JsonDocument.Parse(await File.ReadAllTextAsync(path));var packet=document.RootElement;
        var host=Path.GetFullPath(packet.Text("host"));
        if(!host.StartsWith("/private/tmp/oetrm-ref-",StringComparison.Ordinal)&&!host.StartsWith("/tmp/oetrm-ref-",StringComparison.Ordinal))
            throw new Exception("Financial Transport worker qualification requires its owned disposable PostgreSQL cluster.");
        var manifest=packet.GetProperty("manifest").Deserialize<PreviewManifest>(RiskJobs.Json)!;
        var login=new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="preview_runtime",Timezone="UTC",Pooling=false};
        await using var database=new PreviewDatabase(manifest,new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="preview_authenticator"},login);
        var receiptPath=Path.ChangeExtension(path,"financial-price-worker.json");
        await database.WithIdentity("trader",null,async(c,ct)=>
        {
            if(verify)
            {
                using var receipt=JsonDocument.Parse(await File.ReadAllTextAsync(receiptPath,ct));var saved=receipt.RootElement;
                var result=JsonSerializer.SerializeToElement(await RiskJobs.Read(c,manifest.CatalogId,saved.Id("id"),ct),RiskJobs.Json);
                if(result.Text("state")!="completed"||result.Id("requestId")!=saved.Id("requestId")||
                    !JsonElement.DeepEquals(result.GetProperty("result"),saved.GetProperty("report")))
                    throw new Exception("The retained financial PRICE report changed its original request or ordinary complete report.");
                return true;
            }
            if(packet.Text("pricingPolicy")!="base")throw new Exception("Select the installed contractual pricing policy explicitly.");
            var at=TradingInstant.Parse(packet.Text("asOf"),"gas_transport_financial_input");
            var context=packet.Id("contextId");var trade=packet.Id("tradeId");var book=packet.Id("bookId");
            var source=(await new KnowledgeStore(manifest.CatalogId).Read(c,context,ct)).Context;
            await PinnedContext.BindExact(c,manifest.CatalogId,source,ct);await TradingContext.Bind(c,manifest.CatalogId,source,at,ct);
            var day=PublishedSchedules.ContextDate(source);
            var raw=await GasTransportFinancialStore.Read(c,database,new(context,at,day),trade,ct);
            var agreement=GasTransportFinancialAdapter.Read(manifest.CatalogId,raw);
            if(agreement.BookId!=book||agreement.State is not {Status:"live"}||agreement.Mark is not {} mark||agreement.UnpricedReason is not null)
                throw new Exception("The chosen worker baseline requires its exact current priced financial agreement.");
            var pins=packet.GetProperty("observationIds").EnumerateArray().Select(p=>p.GetGuid()).ToImmutableArray();
            var contracts=packet.GetProperty("curveContracts").EnumerateObject().ToImmutableDictionary(p=>p.Name,
                p=>p.Value.EnumerateArray().Select(id=>id.GetGuid()).ToImmutableArray(),StringComparer.Ordinal);
            var reference=agreement.Terms.Rights.Currency.Revision;
            var frame=new InputFrame(Guid.NewGuid(),"Synthetic retained financial Transport PRICE",at,day,pins,contracts,context)
                {Source=new(agreement.Terms.Definitions.RevisionId,reference.CatalogId,reference.RevisionId)};
            var data=await MarketInputSelection.Root(database,c,frame,ct);
            var factor=new CurveRiskAxisRequest("market_quote",new("contract",mark.Market.Curve,Contract:mark.Market.Contract));
            var request=new PortfolioRiskRequest(new(frame.Id,TradingBlotter.Convention,"retained",context),[book],[],
                [new(factor,packet.Text("bumpSize"),ConnectedRiskBump.Convention)],PortfolioRiskPreview.Convention,RiskEngine.ExactNumericalPolicy,
                RiskPricingPolicies.Base,"holdings",[],TradeIds:[trade]);
            var calculator=new PortfolioRiskPreview(database);var prepared=await calculator.Prepare(c,data,frame,request,ct);
            var evidence=prepared.Portfolio.Positions;
            var expectedBindings=agreement.Cash is {} fee?new[]{agreement.InstrumentId,fee.BindingId}:new[]{agreement.InstrumentId};
            if(prepared.Portfolio.Policy.GasOwned is not null||evidence.Length!=expectedBindings.Length||
                !evidence.Select(p=>p.BindingId).ToHashSet().SetEquals(expectedBindings)||evidence.Any(p=>p.Owner!=new AccountingOwnerKey(AccountingOwnerKind.Trade,trade)||
                    p.TradeId!=trade||p.BookId!=book||p.InstrumentId!=agreement.InstrumentId||p.InstrumentType!=GasTransportFinancialTicket.Product||
                    p.Measure!=GasTransportFinancialTicket.Measure||p.DefinitionRevisionId!=agreement.Terms.Definitions.RevisionId))
                throw new Exception("The financial report must retain exactly its service-origin rights and independent fee memberships, without a gas owner.");
            var rights=evidence.Single(p=>p.BindingId==agreement.InstrumentId);
            if(rights.ValuationId==agreement.InstrumentId||rights.Quantity!=new Quantity(ExactDecimal.Parse("1"),agreement.Terms.Rights.PackageQuantity))
                throw new Exception("The retained rights binding must identify one package and its separate accepted-state valuation definition.");
            if(agreement.Cash is {} cash)
            {
                var paid=evidence.Single(p=>p.BindingId==cash.BindingId);
                if(paid.ValuationId!=cash.Instrument.Id||paid.Quantity!=cash.Quantity)
                    throw new Exception("The service-origin fee evidence must retain its actual native cash binding, currency quantity and cash valuation instrument.");
            }
            if(prepared.Portfolio.InstantDiscountInputs.Any(p=>p.Input.DiscountFactor!=ExactDecimal.Parse("1")))
                throw new Exception("The independent financial worker oracle requires its explicitly selected synthetic zero-rate observations.");
            var envelope=JsonSerializer.SerializeToNode(PortfolioRiskPreview.Calculate(prepared,ct),RiskJobs.Json)!.AsObject();
            envelope["context"]=JsonSerializer.SerializeToNode(source,RiskJobs.Json);envelope["asOf"]=JsonSerializer.SerializeToNode(at,RiskJobs.Json);
            var report=JsonSerializer.SerializeToElement(envelope,RiskJobs.Json);
            var currency=packet.GetProperty("expectedCurrency");var key=new ReferenceKey(new(currency.Id("catalogId"),currency.Id("revisionId")),currency.Text("code"));
            if(key!=agreement.Terms.Rights.Currency)throw new Exception("Do not net equal currency codes from different reference catalogs or revisions.");
            var totals=report.GetProperty("factors").EnumerateArray().Single().GetProperty("totals").EnumerateArray().ToArray();
            if(totals.Length!=1||totals[0].Id("bookId")!=book||totals[0].Text("measure")!=GasTransportFinancialTicket.Measure||
                totals[0].GetProperty("values").GetProperty("currencyReference").Deserialize<ReferenceKey>(RiskJobs.Json)!=key)
                throw new Exception("The complete selected financial report must retain its exact book, canonical measure and R2 currency reference.");
            foreach(var(field,expected)in new[]{("base","expectedBase"),("up","expectedUp"),("down","expectedDown")})
                if(ExactMoney.Parse(totals[0].GetProperty("values").Text(field),key)!=ExactMoney.Parse(packet.Text(expected),key))
                    throw new Exception("The independent signed financial PRICE oracle differs for "+field+".");
            var contractsInReport=report.GetProperty("modelContracts").EnumerateArray().Select(p=>p.Id("id")).ToHashSet();
            if(!contractsInReport.SetEquals(evidence.Select(p=>p.ValuationId)))
                throw new Exception("Every retained rights/cash valuation identity must have its exact model declaration.");
            var requestId=Guid.NewGuid();var command=new SubmitPortfolioRiskJob(requestId,request);
            var first=JsonSerializer.SerializeToElement(await RiskJobs.Submit(c,manifest,calculator,data,frame,command,ct),RiskJobs.Json);
            var retry=JsonSerializer.SerializeToElement(await RiskJobs.Submit(c,manifest,calculator,data,frame,command,ct),RiskJobs.Json);
            if(first.Text("state")!="queued"||first.Id("id")!=retry.Id("id"))
                throw new Exception("The financial PRICE job must retain its idempotent original queued outcome.");
            await File.WriteAllTextAsync(receiptPath,JsonSerializer.Serialize(new{id=first.Id("id"),requestId,engineVersion=RiskJobs.EngineVersion,report},RiskJobs.Json),ct);
            return true;
        },default);
        Console.WriteLine("PASS financial Transport PRICE "+(verify?"retained worker parity":"ordinary oracle, native memberships and idempotent admission")+": "+packet.Text("checkpoint"));
    }
}
