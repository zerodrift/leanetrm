using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

internal static class GasParkLoanNativeChecks
{
    internal static async Task Run(string path)
    {
        using var document=JsonDocument.Parse(File.ReadAllText(path));var fixture=document.RootElement;
        var manifest=fixture.GetProperty("manifest").Deserialize<PreviewManifest>(LifecycleNative.Json)!;
        var host=Path.GetFullPath(fixture.Text("host"));
        if(!host.StartsWith("/private/tmp/oetrm-ref-",StringComparison.Ordinal)&&!host.StartsWith("/tmp/oetrm-ref-",StringComparison.Ordinal))
            throw new Exception("Service qualification requires an owned disposable PostgreSQL cluster.");
        var login=new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="preview_runtime",Timezone="UTC",Pooling=false};
        await using var database=new PreviewDatabase(manifest,new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="preview_authenticator"},login);
        if(fixture.Text("checkpoint")=="loan_customer booked before advance")
            foreach(var retainOriginal in new[]{false,true})await PresenceRollback(database,fixture,retainOriginal);
        await database.WithIdentity("trader",null,async(connection,ct)=>
        {
            var at=fixture.Instant("at");var context=fixture.Id("contextId");var book=fixture.Id("bookId");var trade=fixture.Id("tradeId");
            var day=DateWorkspace.Date(fixture.Text("businessDate"));
            var source=(await new KnowledgeResolver(manifest.CatalogId).Read(connection,context,ct)).Context;
            await PinnedContext.BindExact(connection,manifest.CatalogId,source,ct);
            await TradingContext.Bind(connection,manifest.CatalogId,source,at,ct);
            var agreement=await GasParkLoanTicketStore.Read(connection,database,new(context,at,day),trade,ct);
            var terms=GasParkLoanPortfolio.Terms(manifest.CatalogId,agreement);
            var pins=ImmutableArray.Create(fixture.Id("markId")).AddRange(fixture.GetProperty("rateIds").EnumerateArray().Select(p=>p.GetGuid()));
            if(terms.Mark.Contract.Id!=fixture.Id("contractId"))throw new Exception("Retain the exact native service mark contract in the checkpoint.");
            var frame=new InputFrame(Guid.NewGuid(),"Synthetic service whole-book checkpoint",at,day,pins,
                ImmutableDictionary<string,ImmutableArray<Guid>>.Empty.Add(terms.Mark.Curve.Code,[terms.Mark.Contract.Id]),context);
            var data=await MarketInputSelection.Root(database,connection,frame,ct);
            var snapshot=data.Snapshot(frame).WithKnowledge(context,SchedulePreview.ParseAt(source.KnownAt));
            var policy=new PortfolioValuationPolicy(CurveModel.NumericalPolicy,RiskEngine.ExactNumericalPolicy,FuturesMarginValuationPolicy.Convention,
                SwapStore.Convention,RateCurveMath.Policy,DiscountInterpolation.Policy,QuoteBump.Convention,"trading_tick",1,
                FuturesOptions:FuturesOptionValuationPolicy.Release1,NativeFutures:new(FuturesMarginValuationPolicy.CurrencyDefault,RateDiscountTiming.Version),
                GasOwned:new(GasOwnedValuationPolicy.Version,GasOwnedValuationPolicy.WholeBooks,GasOwnedValuationPolicy.CurrencyDefault,RateDiscountTiming.Version));
            var prepared=await new PortfolioValuation(database).Prepare(connection,data,frame,snapshot,source,[book],policy,[],[],ct);
            var services=prepared.Positions.Where(p=>p.InstrumentType==GasParkLoanTicket.Product).ToArray();
            if(services.Length!=fixture.GetProperty("expectedServiceComponents").GetInt32()||
                services.Any(p=>p.TradeId!=trade||p.InstrumentId!=agreement.Id("instrumentId")||p.Owner.Kind!=AccountingOwnerKind.Trade)||
                prepared.Positions.Any(p=>p.InstrumentType!=GasParkLoanTicket.Product&&p.Owner.Kind!=AccountingOwnerKind.NativeHolding))
                throw new Exception("The whole book duplicated service/native ownership or lost the exact service binding handoff.");
            // The disposable fixture pins actual zero-rate observations. The
            // independent oracle therefore uses DF=1 without a pricing fallback.
            if(prepared.InstantDiscountInputs.Any(p=>p.Input.DiscountFactor!=ExactDecimal.Parse("1")))
                throw new Exception("This exact independent checkpoint requires its selected synthetic zero-rate observations.");
            var axis=CurveRiskAxes.Resolve(prepared,new("market_quote",new("contract",terms.Mark.Curve,Contract:terms.Mark.Contract)),ct);
            var run=PortfolioValuation.Calculate(prepared,[new(axis,ExactDecimal.Parse(fixture.Text("bumpSize")))],[],RiskDetail.Holdings,ct);
            var currency=terms.Currency;
            ExactMoney Sum(IEnumerable<ExactMoney> values)=>values.Aggregate(ExactMoney.Parse("0",currency),(a,b)=>a.Add(b));
            var basis=Sum(run.Totals.Select(t=>t.Value));var up=Sum(run.Risk.Select(t=>t.Up));var down=Sum(run.Risk.Select(t=>t.Down));
            if(basis!=ExactMoney.Parse(fixture.Text("expectedBase"),currency)||up!=ExactMoney.Parse(fixture.Text("expectedUp"),currency)||
                down!=ExactMoney.Parse(fixture.Text("expectedDown"),currency)||run.Work.ScenarioCount!=3)
                throw new Exception($"Native whole-book service conservation failed: base {basis.ToExactString()}, up {up.ToExactString()}, down {down.ToExactString()}.");
            var calculator=new PortfolioRiskPreview(database);
            var catalogScope=new PortfolioRiskCatalogRequest(new(frame.Id,TradingBlotter.Convention,"retained",context),[book],[],PricingPolicy:RiskPricingPolicies.NativeGas);
            var setup=JsonSerializer.SerializeToElement(await calculator.Setup(connection,frame,catalogScope,ct),RiskJobs.Json);
            if(setup.GetProperty("pricingPolicies").GetArrayLength()!=2||setup.Text("selectedPricingPolicyId")!="native-gas-v1")
                throw new Exception("Authorized whole-book Risk setup must expose its explicitly selected independent-gas policy without first pricing the base policy.");
            var catalog=JsonSerializer.SerializeToElement(await calculator.Catalog(connection,data,frame,catalogScope,ct),RiskJobs.Json);
            if(!RiskPricingPolicies.Same(catalog.GetProperty("pricingPolicy").Deserialize<PortfolioValuationPolicy>(RiskJobs.Json)!,RiskPricingPolicies.NativeGas))
                throw new Exception("The ordinary catalog did not prepare and retain the explicitly chosen whole-book gas policy.");
            try
            {
                await calculator.Catalog(connection,data,frame,catalogScope with{TradeIds=[trade]},ct);
                throw new Exception("A selected service trade inferred independent native ownership.");
            }
            catch(MarketDataException error)when(error.Code=="gas_owned_scope"){}
            if(prepared.Positions.Any(position=>position.Owner.Kind==AccountingOwnerKind.NativeHolding))
            {
                await using(var save=ScheduleStore.Command(connection,"SAVEPOINT risk_missing_ownership_policy"))await save.ExecuteNonQueryAsync(ct);
                try
                {
                    await calculator.Catalog(connection,data,frame,catalogScope with{PricingPolicy=null},ct);
                    throw new Exception("Ordinary Risk inferred a missing independent-ownership policy after accepted performance.");
                }
                catch(MarketDataException error)when(error.Code=="gas_owned_policy_required"){}
                finally
                {
                    await using var restore=ScheduleStore.Command(connection,"ROLLBACK TO SAVEPOINT risk_missing_ownership_policy;RELEASE SAVEPOINT risk_missing_ownership_policy");
                    await restore.ExecuteNonQueryAsync(ct);
                }
            }
            await database.WithIdentity("risk",null,async(other,token)=>
            {
                await PinnedContext.BindExact(other,manifest.CatalogId,source,token);
                await TradingContext.Bind(other,manifest.CatalogId,source,at,token);
                var available=JsonSerializer.SerializeToElement(await calculator.Setup(other,frame,catalogScope with{PricingPolicy=null},token),RiskJobs.Json);
                if(available.GetProperty("pricingPolicies").GetArrayLength()!=1||available.Text("selectedPricingPolicyId")!="base")
                    throw new Exception("A Risk reader without current gas authority was offered independent gas ownership.");
                try
                {
                    await calculator.Catalog(other,data,frame,catalogScope,token);
                    throw new Exception("An explicit native policy replaced missing current gas authority.");
                }
                catch(PreviewDeniedException){}
                return true;
            },ct);
            var positions=await GasParkLoanPositions.Read(connection,database,frame,[book],[],ct,new(trade,agreement.Id("instrumentId"),1,GasParkLoanTicket.Product));
            if(positions.Length!=1||positions[0].Fact().BookedQuantity!=agreement.Text("quantity"))
                throw new Exception("The selected service position lost native principal or exact visible metadata.");
            Console.WriteLine($"PASS native service whole book: {fixture.Text("checkpoint")}, {services.Length} service contributions, exact base/up/down {basis.ToExactString()}/{up.ToExactString()}/{down.ToExactString()}");
            return true;
        },default);
    }

    private static async Task PresenceRollback(PreviewDatabase database,JsonElement fixture,bool retainOriginal)
    {
        await database.WithIdentity("trader",null,async(connection,ct)=>
        {
            var catalog=database.Manifest.CatalogId;var at=fixture.Instant("at");var context=fixture.Id("contextId");
            var source=(await new KnowledgeResolver(catalog).Read(connection,context,ct)).Context;
            var original=(await new KnowledgeResolver(catalog).Capture(connection,new(fixture.Text("businessDate"),null,"current"),ct)).Context;
            if(original.ContextId==context)throw new Exception("The presence restoration fixture needs distinct certified source identities.");
            DateTimeOffset? until=retainOriginal?await TradingContext.AdmitSource(database,connection,original,at,ct):null;
            await using(var save=ScheduleStore.Command(connection,"SAVEPOINT presence_source"))await save.ExecuteNonQueryAsync(ct);
            try
            {
                await PinnedContext.BindExact(connection,catalog,source,ct);
                await TradingContext.Bind(connection,catalog,source,at,ct);
                var frame=new InputFrame(Guid.NewGuid(),"Synthetic temporary presence source",at,PublishedSchedules.ContextDate(source),[],
                    ImmutableDictionary<string,ImmutableArray<Guid>>.Empty,context);
                if(!await GasParkLoanPositions.Present(connection,database,frame,[fixture.Id("bookId")],[fixture.Id("tradeId")],ct))
                    throw new Exception("The exact service membership disappeared from its immutable presence cut.");
            }
            finally
            {
                await using var restore=ScheduleStore.Command(connection,"ROLLBACK TO SAVEPOINT presence_source;RELEASE SAVEPOINT presence_source");
                await restore.ExecuteNonQueryAsync(ct);
            }
            if(retainOriginal&&until!=await TradingContext.AdmitSource(database,connection,original,at,ct))
                throw new Exception("A temporary presence read replaced or extended the original command source.");
            return true;
        },default);
        Console.WriteLine(retainOriginal?"PASS presence admission preserves a different original source across read rollback":
            "PASS presence admission commits after its temporary bound source is rolled back");
    }
}
