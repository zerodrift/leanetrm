using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

internal static class GasLoanWithdrawalChecks
{
    internal static async Task Run(string path)
    {
        using var packet=JsonDocument.Parse(File.ReadAllText(path));var fixture=packet.RootElement;
        var manifest=fixture.GetProperty("manifest").Deserialize<PreviewManifest>(LifecycleNative.Json)!;
        var host=Path.GetFullPath(fixture.Text("host"));
        if(!host.StartsWith("/private/tmp/oetrm-ref-",StringComparison.Ordinal)&&!host.StartsWith("/tmp/oetrm-ref-",StringComparison.Ordinal))
            throw new Exception("Loan withdrawal checks require an owned disposable PostgreSQL cluster.");
        var login=new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="preview_runtime",Timezone="UTC",Pooling=false};
        await using var database=new PreviewDatabase(manifest,new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="preview_authenticator"},login);
        await database.WithIdentity("trader",null,async(connection,ct)=>
        {
            var context=fixture.Id("contextId");var at=fixture.Instant("at");
            var source=(await new KnowledgeResolver(manifest.CatalogId).Read(connection,context,ct)).Context;
            await PinnedContext.BindExact(connection,manifest.CatalogId,source,ct);
            await TradingContext.Bind(connection,manifest.CatalogId,source,at,ct);
            var pins=ImmutableArray.Create(fixture.Id("markId")).AddRange(fixture.GetProperty("rateIds").EnumerateArray().Select(p=>p.GetGuid()));
            var frame=new InputFrame(Guid.NewGuid(),"Synthetic withdrawn loan",at,PublishedSchedules.ContextDate(source),pins,
                ImmutableDictionary<string,ImmutableArray<Guid>>.Empty.Add("future",[fixture.Id("contractId")]),context);
            var data=await MarketInputSelection.Root(database,connection,frame,ct);
            var snapshot=data.Snapshot(frame).WithKnowledge(context,SchedulePreview.ParseAt(source.KnownAt));
            var books=ImmutableArray.Create(fixture.Id("bookId"));
            var known=await GasOwnedKnownHistoryStore.Read(connection,database,frame,snapshot,books,ct);
            var loan=fixture.Id("loanId");var stock=fixture.Id("stockId");var debt=fixture.Id("returnId");
            if(known.Loans.Any(l=>l.Id==loan)||known.Repayments.Any(r=>r.LoanId==loan)||
                known.Consumptions.Any(c=>c.Id==fixture.Id("dispatchId")||c.Id==fixture.Id("repaymentConsumptionId"))||
                known.Links.LoanReturnPositions[loan]!=debt||known.Links.RepaymentConsumptions[fixture.Id("repaymentId")]!=fixture.Id("repaymentConsumptionId")||
                !known.Quantities.Positions[stock].Retracted||!known.Quantities.Positions[debt].Retracted||
                !known.Quantities.Consumptions[fixture.Id("dispatchId")].Retracted||!known.Quantities.Consumptions[fixture.Id("repaymentConsumptionId")].Retracted||
                known.Fees.Count(f=>f.LoanId==loan)!=1||known.Positions[stock].Quantity.Amount!=default||known.Positions[debt].Quantity.Amount!=default)
                throw new Exception("Withdrawn loan principal must leave active quantity facts while exact native links, heads and independent fixed cash survive.");
            var policy=new PortfolioValuationPolicy(CurveModel.NumericalPolicy,RiskEngine.ExactNumericalPolicy,FuturesMarginValuationPolicy.Convention,
                SwapStore.Convention,RateCurveMath.Policy,DiscountInterpolation.Policy,QuoteBump.Convention,"trading_tick",1,
                FuturesOptions:FuturesOptionValuationPolicy.Release1,NativeFutures:new(FuturesMarginValuationPolicy.CurrencyDefault,RateDiscountTiming.Version),
                GasOwned:new(GasOwnedValuationPolicy.Version,GasOwnedValuationPolicy.WholeBooks,GasOwnedValuationPolicy.CurrencyDefault,RateDiscountTiming.Version));
            var selection=await RiskTradeScope.Resolve(connection,manifest.CatalogId,books,null,ct);
            var prepared=await new PortfolioValuation(database).Prepare(connection,data,frame,snapshot,source,books,policy,[],[],ct,selection:selection);
            var result=PortfolioValuation.Calculate(prepared,[],[],RiskDetail.Holdings,ct);
            var pv=result.Totals.Single().Value;var feeOnly=fixture.GetProperty("feeOnly").GetBoolean();
            var expectedOwners=feeOnly?new[]{debt}:new[]{fixture.Id("fundingId"),debt};
            if(result.Positions.Length!=expectedOwners.Length||pv!=ExactMoney.Parse(fixture.Text("expectedPv"),pv.Currency)||
                !result.Positions.Select(p=>p.Owner.Id).ToHashSet().SetEquals(expectedOwners))
                throw new Exception("Withdrawn loan PV must retain only independently owned funding stock and the unpaid fixed fee, with bank history unchanged.");
            if(feeOnly)
            {
                var request=new PortfolioEconomicProjectionRequest(PortfolioEconomicProjectionRequest.Version,fixture.Text("economicSession"),1,
                    PortfolioEconomicProjectionRequest.SourceKnownSchedule,RateDiscountTiming.Version,PortfolioEconomicProjectionRequest.SelectedContractMarkets,
                    new(LifecycleProjectionPolicy.Version,LifecycleProjectionPolicy.Hold,LifecycleProjectionPolicy.Outstanding,LifecycleProjectionPolicy.Nominal,
                        LifecycleProjectionPolicy.Numerics,LifecycleProjectionPolicy.HeldNonPrice,LifecycleProjectionPolicy.DeclaredCurveExpiry,LifecycleProjectionPolicy.RetainPhysical),[]);
                var economic=await LifecyclePortfolioProjection.Prepare(connection,database,prepared,request,null,ct);
                var values=economic.Projections.SelectMany(p=>p.Evaluate("native_fee_only_no_shock",[],ct).Values).ToArray();
                if(values.Length!=1||values[0].Owner.Owner.Id!=debt||values[0].ResidualValue!=pv||values[0].EconomicPnl.Numerator!="0")
                    throw new Exception("A fee-only native loan source requires its independently owned cash component and no fabricated quantity-profile member.");
            }
            return true;
        },default);
        Console.WriteLine("PASS withdrawn native loan: active principal/repayment facts retire, exact proof links survive, ordinary whole-book PV retains fixed fee/bank and independent funding stock");
    }
}
