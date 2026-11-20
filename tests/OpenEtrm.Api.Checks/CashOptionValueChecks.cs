using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

internal static class CashOptionValueChecks
{
    internal static async Task Run(string path)
    {
        using var document=JsonDocument.Parse(File.ReadAllText(path));var fixture=document.RootElement;
        var manifest=fixture.GetProperty("manifest").Deserialize<PreviewManifest>(RiskJobs.Json)!;var host=Path.GetFullPath(fixture.Text("host"));
        if(!host.StartsWith("/private/tmp/oetrm-ref-",StringComparison.Ordinal)&&!host.StartsWith("/tmp/oetrm-ref-",StringComparison.Ordinal))
            throw new Exception("Cash option checks require their owned disposable native cluster.");
        var login=new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="preview_runtime",Timezone="UTC",Pooling=false};
        await using var database=new PreviewDatabase(manifest,new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="preview_authenticator"},login);
        await database.WithIdentity("trader","RunScenario",async(connection,ct)=>
        {
            var context=(await new KnowledgeResolver(manifest.CatalogId).Read(connection,fixture.Id("contextId"),ct)).Context;
            await PinnedContext.BindExact(connection,manifest.CatalogId,context,ct);await TradingContext.Bind(connection,manifest.CatalogId,context,fixture.Instant("at"),ct);
            if(await PreviewDatabase.HasPermission(connection,"ReadGasOwnedHoldings",ct))throw new Exception("This exact cash consumer must be qualified without gas authority.");
            var pins=fixture.GetProperty("pins").EnumerateArray().Select(p=>p.GetGuid()).ToImmutableArray();
            var contracts=ImmutableDictionary<string,ImmutableArray<Guid>>.Empty.Add("ice_cfb",[fixture.Id("contractId")])
                .Add("synthetic_cash_option",[fixture.Id("optionContractId")]);
            var frame=new InputFrame(Guid.NewGuid(),"Synthetic funded cash option",fixture.Instant("at"),PublishedSchedules.ContextDate(context),pins,contracts,context.ContextId){
                Source=new(manifest.RevisionId,manifest.ReferenceCatalogId,manifest.ReferenceRevisionId)};
            var data=await MarketInputSelection.Root(database,connection,frame,ct);var part=new MarketSourceInputs(CatalogueSource.From(data),pins,[],contracts,
                ["ice_cfb","synthetic_cash_option"]);
            frame=frame with{SourceInputs=ImmutableDictionary<Guid,MarketSourceInputs>.Empty.Add(manifest.RevisionId,part)};
            var snapshot=MarketSnapshotBinding.Bind(data.Snapshot(frame),context.ContextId!.Value,SchedulePreview.ParseAt(context.KnownAt));
            var books=ImmutableArray.Create(fixture.Id("bookId"));var trades=ImmutableArray.Create(fixture.Id("tradeId"));
            var scope=await RiskTradeScope.Resolve(connection,manifest.CatalogId,books,trades,ct);
            var model=new CashFuturesOptionPolicy(CashFuturesOptionPolicy.Version,CashFuturesOptionPolicy.ReferenceStopping,CashFuturesOptionPolicy.ReferenceCalibration,
                CashFuturesOptionPolicy.IntrinsicStrategy,FuturesMarginValueBinding.FlatReference);
            var selection=new FuturesOptionCashValuationSelection(fixture.Id("tradeId"),fixture.Id("bindingId"),model);
            var policy=PortfolioRiskPreview.AccountingPricingPolicy with{FuturesOptions=FuturesOptionValuationPolicy.Release1 with{
                CashFunding=new(FuturesOptionCashValuationPolicy.Version,[selection])}};
            var economic=fixture.GetProperty("economic").GetBoolean();
            if(economic)policy=policy with{FuturesConvention=FuturesEconomicValuationPolicy.Version,
                NativeFutures=new(FuturesMarginValuationPolicy.CurrencyDefault,RateDiscountTiming.Version){
                    Economic=new(FuturesEconomicValuationPolicy.Version,FuturesMarginValueBinding.FlatReference,[])}};
            var prepared=await new PortfolioValuation(database).Prepare(connection,data,frame,snapshot,context,books,policy,[],[],ct,selection:scope);
            var source=prepared.Sources.Single();var binding=source.Portfolio.Setup.CashOptionValueBindings.Single();
            if(binding.Key!=new MeasureBinding(selection.TradeId,selection.BindingId)||!source.Portfolio.Setup.PhysicalOptionValueBindings.IsEmpty||
                !prepared.InputMarkets.IsEmpty||prepared.Markets.Length!=1||prepared.Factors.Length!=1||prepared.Positions.Length!=2)
                throw new Exception("Native funded cash options must preserve exact option/premium owners and one real source without an invented gas or asset input.");
            var report=PortfolioValuation.Calculate(prepared,[],[],RiskDetail.Holdings,ct);
            var option=source.Portfolio.Instruments.OfType<FuturesOptionInstrument>().Single();var a=prepared.Factors.Single().BaseValue;
            var first=binding.Value.Margin.Occurrences.OrderBy(o=>o.At).First();var currency=option.Currency;
            var factor=binding.Value.MarginDiscounting.Resolve([new(first.Id,0,first.DueAt,currency)],snapshot).Single().DiscountFactor;
            var unit=ExactMoney.FromMoney(new(a-option.Series.Terms.Strike,currency)).Scale(factor)
                .Round(new(currency,ExactDecimal.Parse("0.000000000000000001"),CashTieRule.ToEven,CashRoundingStage.PaymentTotal),CashRoundingStage.PaymentTotal);
            var expected=ExactMoney.FromMoney(unit).Scale(option.Series.Multiplier*ExactDecimal.Parse("2"));
            var solved=fixture.GetProperty("solved").GetBoolean();
            if(solved!=(source.Portfolio.Setup.OptionBindings.Single().Value.Calibration is SolvedVolatility)||
                !solved&&report.BasePositions.Single(v=>v.Position.BindingId==selection.BindingId).Position.Value!=expected||
                report.ModelContracts.Single(c=>c.Id==option.Id).ModelVersion!=CashFuturesOptionPolicy.Version)
                throw new Exception("Native cash-option opening must match independent next-saved-due funding rather than vanilla intrinsic.");
            var axis=CurveRiskAxes.Quote(prepared,prepared.Factors.Single(),ct);
            var shifted=PortfolioValuation.Calculate(prepared,[],[new(axis,ExactDecimal.Parse("0.1"))],RiskDetail.Holdings,ct);
            if(shifted.Totals.Single().Value==report.Totals.Single().Value)
                throw new Exception("The selected cash option must revalue under its actual native A scenario.");
            try{_=PortfolioValuation.Calculate(prepared,[new(axis,ExactDecimal.Parse("0.1"))],[],RiskDetail.Holdings,ct);throw new Exception("The selected cash model returned unqualified static derivatives.");}
            catch(MarketDataException error)when(error.Code=="risk_static_derivatives_unavailable"){}
            foreach(var changed in new[]{policy with{FuturesOptions=policy.FuturesOptions! with{CashFunding=new(FuturesOptionCashValuationPolicy.Version,[])}},
                policy with{FuturesOptions=policy.FuturesOptions! with{CashFunding=new(FuturesOptionCashValuationPolicy.Version,[selection with{BindingId=Guid.NewGuid()}])}}})
            {
                try{_=await new PortfolioValuation(database).Prepare(connection,data,frame,snapshot,context,books,changed,[],[],ct,selection:scope);throw new Exception("A missing or foreign cash selection reached successful preparation.");}
                catch(MarketDataException error)when(error.Code=="cash_option_source"){}
            }
            var vanilla=await new PortfolioValuation(database).Prepare(connection,data,frame,snapshot,context,books,policy with{
                FuturesOptions=policy.FuturesOptions! with{CashFunding=null}},[],[],ct,selection:scope);
            if(!vanilla.Sources.Single().Portfolio.Setup.CashOptionValueBindings.IsEmpty||
                PortfolioValuation.Calculate(vanilla,[],[],RiskDetail.Holdings,ct).BasePositions.Single(p=>p.Position.BindingId==selection.BindingId).Position.Value==
                    report.BasePositions.Single(p=>p.Position.BindingId==selection.BindingId).Position.Value)
                throw new Exception("Legacy vanilla value must retain its distinct explicit diagnostic measure.");
            var scopeRequest=new PortfolioRiskCatalogRequest(new(frame.Id,TradingBlotter.Convention,"retained",context.ContextId),books,[],trades);
            var setup=JsonSerializer.SerializeToElement(await new PortfolioRiskPreview(database).EconomicSetup(connection,data,frame,scopeRequest,ct),RiskJobs.Json);
            if(!setup.TryGetProperty("cashOptionBindings",out var groups)||groups.GetArrayLength()!=1)
                throw new Exception("Setup must offer a separate exact cash option binding group without requiring Gas authority.");
            var group=groups.EnumerateArray().Single();var offered=group.GetProperty("choices").EnumerateArray().Single().GetProperty("selection")
                .Deserialize<FuturesOptionCashValuationSelection>(RiskJobs.Json)!;
            if(group.Id("tradeId")!=selection.TradeId||group.Id("bindingId")!=selection.BindingId||offered!=selection||
                !setup.Text("optionModelHelp").Contains("decision or time P&L",StringComparison.Ordinal)||
                setup.GetProperty("pricing").EnumerateArray().Any(p=>!p.TryGetProperty("requiresCashOptionBindings",out var required)||required.ValueKind is not(JsonValueKind.True or JsonValueKind.False))||
                !setup.GetProperty("pricing").EnumerateArray().Any(p=>p.GetProperty("requiresCashOptionBindings").GetBoolean()&&
                    p.GetProperty("policy").GetProperty("futuresOptions").GetProperty("cashFunding").GetProperty("options").GetArrayLength()==0&&
                    !p.GetProperty("policy").GetProperty("futuresOptions").TryGetProperty("physical",out _)))
                throw new Exception("Cash model metadata must retain exact opaque choices, explicit full templates and the distinct reference-stopping/held-event assumption.");
            if(economic)
            {
                var projection=new PortfolioEconomicProjectionRequest(PortfolioEconomicProjectionRequest.Version2,solved?"synthetic_cash_option_following":"synthetic_cash_option_economic",1,
                    PortfolioEconomicProjectionRequest.SourceKnownSchedule,RateDiscountTiming.Version,PortfolioEconomicProjectionRequest.SelectedContractMarkets,
                    new(LifecycleProjectionPolicy.Version,LifecycleProjectionPolicy.Hold,LifecycleProjectionPolicy.Outstanding,LifecycleProjectionPolicy.Nominal,
                        LifecycleProjectionPolicy.Numerics,LifecycleProjectionPolicy.HeldNonPrice,LifecycleProjectionPolicy.DeclaredCurveExpiry,LifecycleProjectionPolicy.RetainPhysical),[])
                    {CashAccumulationSelections=[]};
                var projected=await LifecyclePortfolioProjection.Prepare(connection,database,prepared,projection,null,ct);
                var values=projected.Projections.SelectMany(p=>p.Evaluate("cash_no_shock",[],ct).Values).ToArray();
                if(!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(projected.CalibrationReceipts.Single(),RiskJobs.Json),
                    JsonSerializer.SerializeToElement(source.Portfolio.Setup.OptionBindings.Single().Value.Receipt,RiskJobs.Json)))
                    throw new Exception("Economic preparation silently replaced the original selected option reference calibration, provider or receipt at the same source clock.");
                foreach(var original in report.BasePositions)
                {
                    var key=new AccountingPositionKey(original.Position.Owner,original.Position.BindingId,original.Position.BookId);
                    if(values.Single(v=>v.Owner==key).OpeningValue!=original.Position.Value)
                        throw new Exception("Full cash option economic opening must preserve its original "+
                            (original.Position.BindingId==selection.BindingId?"option reference/funding":"premium carrying")+" value.");
                }
                var owners=prepared.Positions.Select(p=>new AccountingPositionKey(p.Owner,p.BindingId,p.BookId)).ToHashSet();
                if(fixture.TryGetProperty("child",out var child)&&child.ValueKind==JsonValueKind.Object)
                {
                    var childOwner=new AccountingPositionKey(child.Id("futuresTradeId"),child.Id("bindingId"),fixture.Id("bookId"));owners.Add(childOwner);
                    var parentOwner=new AccountingPositionKey(selection.TradeId,selection.BindingId,fixture.Id("bookId"));
                    var zero=ExactMoney.Parse("0",currency);var nominal=ExactMoney.Parse(child.Text("variation"),currency);
                    if(values.Single(v=>v.Owner==childOwner).OpeningValue!=zero||values.Single(v=>v.Owner==parentOwner).ResidualValue!=zero)
                        throw new Exception("Native cash exercise must activate its own exact zero-opening future and end the entire option continuation.");
                    foreach(var suffix in new[]{"before","exercise","margin","economic"})
                    {
                        var boundary=await LifecyclePortfolioProjection.Prepare(connection,database,prepared,projection with{Session="synthetic_cash_option_"+suffix},null,ct);
                        var at=boundary.Projections[0].Horizon;var result=boundary.Projections.SelectMany(p=>p.Evaluate("cash_native_boundary",[],ct).Values).ToArray();
                        var active=at>=DateTimeOffset.Parse("2026-10-16T16:30:00Z",System.Globalization.CultureInfo.InvariantCulture);
                        var parentValue=result.Single(v=>v.Owner==parentOwner);var childValue=result.Single(v=>v.Owner==childOwner);
                        var boundaryExpected=nominal.Scale(binding.Value.MarginDiscounting.ResolveForValuation([new(option.Id,0,child.Instant("dueAt"),currency)],snapshot,at,ClockChange.FrozenInputs).Single().DiscountFactor);
                        var measured=active?childValue.ResidualValue:parentValue.ResidualValue;
                        var difference=measured.Subtract(boundaryExpected);if(difference.CompareTo(zero)<0)difference=difference.Scale(ExactDecimal.Parse("-1"));
                        var rounding=ExactMoney.Parse("1/2000000000000000000",currency).Scale(option.Series.Multiplier*ExactDecimal.Parse("2"));
                        if(!owners.SetEquals(result.Select(v=>v.Owner))||result.Length!=owners.Count||
                            (active?parentValue.ResidualValue:childValue.ResidualValue)!=zero||childValue.PeriodCash!=zero||difference.CompareTo(rounding)>0)
                            throw new Exception("Native cash exercise and accepted daily VM must preserve exactly one funded option/future/cash measure at each original event time, with disclosed working rounding only.");
                    }
                }
                if(!owners.SetEquals(values.Select(v=>v.Owner))||values.Length!=owners.Count||projected.Factors.Length!=1||projected.HistoryPortfolio.Markets.Length!=1)
                    throw new Exception("Cash economic risk must retain exactly original/native child owners and one A source, without physical dependencies.");
                var history=new PortfolioHistoryRequest(scopeRequest,policy,new(RiskHistoryPolicy.Version1,CurveModel.NumericalPolicy,RiskHistoryPolicy.Raw,
                    RiskHistoryPolicy.Heads,RiskHistoryPolicy.ExactCloses,RiskHistoryPolicy.Absolute,RiskHistoryPolicy.Complete,RiskHistoryPolicy.Fixed,2),
                    new(data.ReferenceRevision,"source_business_days"),"2026-10-14","2026-10-17","16:00:00","reject","reject");
                await EconomicWorkerChecks.Submit(database,connection,data,frame,scopeRequest,policy,projection,projected,path,ct,history);
            }
            else await PhysicalOptionWorkerChecks.Submit(database,connection,data,frame,scopeRequest,policy,path,ct);
            return true;
        },default);
        Console.WriteLine("PASS actual funded cash option: exact native option/margin profile, independent saved-due PV and no gas authority or asset graph");
    }
}
