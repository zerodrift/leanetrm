using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

internal static class GasKnownCashoutChecks
{
    internal static void Contract()
    {
        var id=Guid.NewGuid();
        if(GasOwnedKnownHistoryStore.SettlementCashId(JsonSerializer.SerializeToElement(new{cash_id=(Guid?)null})) is not null||
            GasOwnedKnownHistoryStore.SettlementCashId(JsonSerializer.SerializeToElement(new{cash_id=id}))!=id)
            throw new Exception("An explicit zero-cash settlement differs from an exact native cash membership.");
        foreach(var json in new[]{"{}","{\"cash_id\":\"\"}","{\"cash_id\":false}","{\"cash_id\":[]}","{\"cash_id\":\"00000000-0000-0000-0000-000000000000\"}"})
        {
            using var value=JsonDocument.Parse(json);
            try{GasOwnedKnownHistoryStore.SettlementCashId(value.RootElement);}
            catch(MarketDataException error)when(error.Code=="gas_owned_known_context"){continue;}
            throw new Exception("Missing or malformed native cash identity became a successful zero-cash settlement.");
        }
        Console.WriteLine("PASS accepted cashout source contract: explicit null and exact cash identity; missing or malformed membership refuses");
    }
    internal static async Task Run(string path)
    {
        using var document=JsonDocument.Parse(File.ReadAllText(path));var root=document.RootElement;
        var manifest=root.GetProperty("manifest").Deserialize<PreviewManifest>(GasOwnedOperations.Json)!;
        var host=Path.GetFullPath(root.Text("host"));
        if(!host.StartsWith("/private/tmp/oetrm-ref-",StringComparison.Ordinal)&&!host.StartsWith("/tmp/oetrm-ref-",StringComparison.Ordinal))
            throw new Exception("Known cashout checks require an owned disposable PostgreSQL cluster.");
        var login=new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="preview_runtime",Timezone="UTC",Pooling=false};
        await using var database=new PreviewDatabase(manifest,new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="preview_authenticator"},login);
        var failures=new List<Exception>();
        foreach(var f in root.GetProperty("cases").EnumerateArray().Append(root))
            try
            {
                await database.WithIdentity("trader",null,async(connection,ct)=>
                {
                    var context=f.Id("contextId");var at=f.Instant("at");
                    var source=(await new KnowledgeResolver(manifest.CatalogId).Read(connection,context,ct)).Context;
                    await PinnedContext.BindExact(connection,manifest.CatalogId,source,ct);await TradingContext.Bind(connection,manifest.CatalogId,source,at,ct);
                    var pins=ImmutableArray.Create(f.Id("markId")).AddRange(f.GetProperty("rateIds").EnumerateArray().Select(row=>row.GetGuid()));
                    var frame=new InputFrame(Guid.NewGuid(),"Synthetic accepted future cashout",at,new(2020,12,20),pins,
                        ImmutableDictionary<string,ImmutableArray<Guid>>.Empty.Add("future",[f.Id("contractId")]),context);
                    var data=await MarketInputSelection.Root(database,connection,frame,ct);
                    var snapshot=data.Snapshot(frame).WithKnowledge(context,SchedulePreview.ParseAt(source.KnownAt));
                    var pricing=new PortfolioValuationPolicy(CurveModel.NumericalPolicy,RiskEngine.ExactNumericalPolicy,FuturesMarginValuationPolicy.Convention,
                        SwapStore.Convention,RateCurveMath.Policy,DiscountInterpolation.Policy,QuoteBump.Convention,"trading_tick",1,
                        NativeFutures:new(FuturesMarginValuationPolicy.CurrencyDefault,RateDiscountTiming.Version),
                        GasOwned:new(GasOwnedValuationPolicy.Version,GasOwnedValuationPolicy.WholeBooks,GasOwnedValuationPolicy.CurrencyDefault,RateDiscountTiming.Version));
                    var opening=await new PortfolioValuation(database).Prepare(connection,data,frame,snapshot,source,[f.Id("bookId")],pricing,[],[],ct);
                    var ordinary=PortfolioValuation.Calculate(opening,[],[],RiskDetail.Holdings,ct);
                    var parent=new AccountingOwnerKey(AccountingOwnerKind.NativeHolding,f.Id("positionId"));
                    if(ordinary.Positions is not [{} position]||position.Owner!=parent||position.BindingId!=f.Id("positionId")||position.BookId!=f.Id("bookId")||
                        position.Quantity.Amount!=ExactDecimal.Parse("3")||!opening.Excluded.IsEmpty||ordinary.BasePositions is not [{} ordinaryValue]||
                        ordinaryValue.Position.Value!=ExactMoney.Parse(f.Text("expectedOpening"),ordinaryValue.Position.Value.Currency))
                        throw new Exception("The complete ordinary opening must retain only the original commodity claim with its independent marked value.");
                    var request=new PortfolioEconomicProjectionRequest(PortfolioEconomicProjectionRequest.Version,f.Text("economicSession"),1,
                        PortfolioEconomicProjectionRequest.SourceKnownSchedule,RateDiscountTiming.Version,PortfolioEconomicProjectionRequest.SelectedContractMarkets,
                        new(LifecycleProjectionPolicy.Version,LifecycleProjectionPolicy.Hold,LifecycleProjectionPolicy.Outstanding,LifecycleProjectionPolicy.Nominal,
                            LifecycleProjectionPolicy.Numerics,LifecycleProjectionPolicy.HeldNonPrice,LifecycleProjectionPolicy.DeclaredCurveExpiry,LifecycleProjectionPolicy.RetainPhysical),
                        [new(f.Id("accountId"),f.Id("profileId"),1)]);
                    var prepared=await LifecyclePortfolioProjection.Prepare(connection,database,opening,request,null,ct);
                    var results=prepared.Projections.Select(p=>p.Evaluate("accepted_future_cashout",[],ct)).ToArray();
                    if(results.Length==0||results.Any(result=>result.Horizon!=TradingInstant.Parse("2020-12-23T16:00:00Z","fixture")))
                        throw new Exception("Accepted cashout economics must reach the exact stated next close H23.");
                    var values=results.SelectMany(p=>p.Values).ToArray();
                    var currency=values[0].Currency;
                    ExactMoney Sum(Func<LifecycleProjectionValue,ExactMoney> select)=>values.Select(select).Aggregate((a,b)=>a.Add(b));
                    if(Sum(v=>v.OpeningValue)!=ExactMoney.Parse(f.Text("expectedOpening"),currency)||
                        Sum(v=>v.ResidualValue)!=ExactMoney.Parse(f.Text("expectedResidual"),currency)||Sum(v=>v.PeriodCash)!=ExactMoney.Parse(f.Text("expectedCash"),currency)||
                        Sum(v=>v.EconomicPnl)!=ExactMoney.Parse(f.Text("expectedPnl"),currency))
                        throw new Exception("The accepted future cashout must replace remaining commodity value with exact unpaid native cash, preserving the original bank payment.");
                    var owners=values.Select(v=>v.Owner).ToHashSet();
                    var cashIds=f.GetProperty("cashIds").EnumerateArray().Select(id=>id.GetGuid()).ToArray();
                    var expected=cashIds.Append(f.Id("positionId")).Select(id=>new AccountingPositionKey(parent,id,f.Id("bookId"))).ToHashSet();
                    if(values.Length!=expected.Count||!owners.SetEquals(expected)||!prepared.AdditionalOwners.Select(o=>o.Owner.BindingId).ToHashSet().SetEquals(cashIds))
                        throw new Exception("Known cashout projection lost or fabricated an independent native owner/binding.");
                    foreach(var value in values)
                    {
                        var index=Array.IndexOf(cashIds,value.Owner.BindingId);
                        var openingValue=index<0?f.Text("expectedOpening"):"0";
                        var residual=index<0?"0":f.GetProperty("cashResiduals")[index].GetString()!;
                        var cash=index<0?"0":f.GetProperty("bankAmounts")[index].GetString()!;
                        if(value.OpeningValue!=ExactMoney.Parse(openingValue,currency)||value.ResidualValue!=ExactMoney.Parse(residual,currency)||
                            value.PeriodCash!=ExactMoney.Parse(cash,currency)||value.EconomicPnl!=value.ResidualValue.Add(value.PeriodCash).Subtract(value.OpeningValue))
                            throw new Exception("Each native commodity/cash owner must retain its independently stated opening wealth, unpaid cash and exact own bank payment.");
                    }
                    if(f.Text("mode")!="normal")
                    {
                        if(prepared.Projections.Any(p=>!p.CashCurveIdentities.IsEmpty)||f.GetProperty("rateIds").GetArrayLength()!=0)
                            throw new Exception("A zero, overdue or fully paid endpoint demanded an unused captured cash curve.");
                        Console.WriteLine("PASS native future cashout endpoint without rate inputs: "+f.Text("mode"));
                        return true;
                    }
                    if(prepared.Projections.SelectMany(p=>p.CashCurveIdentities).Count()!=1)
                        throw new Exception("The unpaid future-due cash endpoint must retain its one exact captured currency curve.");
                    await EconomicRiskNativeChecks.VerifyGasSamples(database,connection,data,frame,[f.Id("bookId")],pricing,request,f.Text("expectedHistoricalVar"),ct,
                        [..expected.Where(owner=>owner.BindingId!=f.Id("positionId"))]);
                    await EconomicWorkerChecks.Submit(database,connection,data,frame,new(new(frame.Id,TradingBlotter.Convention,"retained",context),[f.Id("bookId")],[],null),
                        pricing,request,prepared,path,ct,includeCancellation:true);
                    Console.WriteLine("PASS source-known future cashout: two future cash memberships and a known bank payment, complete native commodity/cash owner set, explicit H23 and unpaid cash 2.77");
                    return true;
                },default);
            }
            catch(MarketDataException error){failures.Add(new Exception(f.Text("mode")+": "+error.Message,error));}
        if(failures.Count>0)throw new AggregateException("Native future cashout continuation cases failed.",failures);
    }
}
