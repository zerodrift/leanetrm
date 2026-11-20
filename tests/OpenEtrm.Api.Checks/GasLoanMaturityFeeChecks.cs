using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

internal static class GasLoanMaturityFeeChecks
{
    internal static async Task Run(string path)
    {
        using var packet=JsonDocument.Parse(File.ReadAllText(path));var fixture=packet.RootElement;
        var manifest=fixture.GetProperty("manifest").Deserialize<PreviewManifest>(LifecycleNative.Json)!;
        var host=Path.GetFullPath(fixture.Text("host"));
        if(!host.StartsWith("/private/tmp/oetrm-ref-",StringComparison.Ordinal)&&!host.StartsWith("/tmp/oetrm-ref-",StringComparison.Ordinal))
            throw new Exception("Loan maturity checks require an owned disposable PostgreSQL cluster.");
        var login=new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="preview_runtime",Timezone="UTC",Pooling=false};
        await using var database=new PreviewDatabase(manifest,new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="preview_authenticator"},login);
        await database.WithIdentity("trader",null,async(connection,ct)=>
        {
            var context=fixture.Id("contextId");var at=fixture.Instant("at");
            var source=(await new KnowledgeResolver(manifest.CatalogId).Read(connection,context,ct)).Context;
            await PinnedContext.BindExact(connection,manifest.CatalogId,source,ct);
            await TradingContext.Bind(connection,manifest.CatalogId,source,at,ct);
            var rates=fixture.GetProperty("rateIds").EnumerateArray().Select(row=>row.GetGuid()).ToImmutableArray();
            // Cash needs its declared rate terms. No commodity observation or
            // contract selection is retained for this quantity-free opening.
            var frame=new InputFrame(Guid.NewGuid(),"Synthetic fully repaid loan cash",at,PublishedSchedules.ContextDate(source),rates,
                ImmutableDictionary<string,ImmutableArray<Guid>>.Empty,context);
            var data=await MarketInputSelection.Root(database,connection,frame,ct);
            var snapshot=data.Snapshot(frame).WithKnowledge(context,SchedulePreview.ParseAt(source.KnownAt));
            var books=ImmutableArray.Create(fixture.Id("bookId"));
            var known=await GasOwnedKnownHistoryStore.Read(connection,database,frame,snapshot,books,ct);
            var loan=known.Loans.Single(row=>row.Id==fixture.Id("loanId"));
            var repayments=known.Repayments.Where(row=>row.LoanId==loan.Id).ToImmutableArray();
            if(loan.Quantity.Amount!=ExactDecimal.Parse("0.375")||repayments.Length!=2||
                GasOwnedLedger.ReturnOutstanding(loan,repayments,at,known.Source.Knowledge!.KnownAt).Amount!=default||
                known.Quantities.Positions[fixture.Id("stockId")].Retracted||known.Quantities.Positions[fixture.Id("returnId")].Retracted||
                known.Fees.Count(row=>row.LoanId==loan.Id)!=1)
                throw new Exception("Normal complete repayment must retain positive original loan/stock/debt heads and exact repayments, independently of fixed cash.");
            var policy=new PortfolioValuationPolicy(CurveModel.NumericalPolicy,RiskEngine.ExactNumericalPolicy,FuturesMarginValuationPolicy.Convention,
                SwapStore.Convention,RateCurveMath.Policy,DiscountInterpolation.Policy,QuoteBump.Convention,"trading_tick",1,
                FuturesOptions:FuturesOptionValuationPolicy.Release1,NativeFutures:new(FuturesMarginValuationPolicy.CurrencyDefault,RateDiscountTiming.Version),
                GasOwned:new(GasOwnedValuationPolicy.Version,GasOwnedValuationPolicy.WholeBooks,GasOwnedValuationPolicy.CurrencyDefault,RateDiscountTiming.Version));
            var scope=await RiskTradeScope.Resolve(connection,manifest.CatalogId,books,null,ct);
            var prepared=await new PortfolioValuation(database).Prepare(connection,data,frame,snapshot,source,books,policy,[],[],ct,selection:scope);
            var result=PortfolioValuation.Calculate(prepared,[],[],RiskDetail.Holdings,ct);
            var pv=result.Totals.Single().Value;
            if(result.Positions.Length!=1||result.Positions[0].Owner.Id!=fixture.Id("returnId")||pv!=ExactMoney.Parse(fixture.Text("expectedPv"),pv.Currency))
                throw new Exception("Only the independent unpaid fixed fee must survive full physical repayment, without any commodity mark input.");
            var request=new PortfolioEconomicProjectionRequest(PortfolioEconomicProjectionRequest.Version,fixture.Text("economicSession"),1,
                PortfolioEconomicProjectionRequest.SourceKnownSchedule,RateDiscountTiming.Version,PortfolioEconomicProjectionRequest.SelectedContractMarkets,
                new(LifecycleProjectionPolicy.Version,LifecycleProjectionPolicy.Hold,LifecycleProjectionPolicy.Outstanding,LifecycleProjectionPolicy.Nominal,
                    LifecycleProjectionPolicy.Numerics,LifecycleProjectionPolicy.HeldNonPrice,LifecycleProjectionPolicy.DeclaredCurveExpiry,LifecycleProjectionPolicy.RetainPhysical),[]);
            var economic=await LifecyclePortfolioProjection.Prepare(connection,database,prepared,request,null,ct);
            var values=economic.Projections.SelectMany(projection=>projection.Evaluate("native_repaid_fee_only",[],ct).Values).ToArray();
            if(values.Length!=1||values[0].Owner.Owner.Id!=fixture.Id("returnId")||values[0].ResidualValue!=pv||values[0].EconomicPnl.Numerator!="0")
                throw new Exception("Positive original principal must not require a fabricated quantity profile or mark in a cash-only economic source.");
            return true;
        },default);
        Console.WriteLine("PASS positive-gross fully repaid native loan: no commodity mark or quantity profile, independent fixed fee/bank remain in ordinary PV and cash-only economic continuation");
    }
}
