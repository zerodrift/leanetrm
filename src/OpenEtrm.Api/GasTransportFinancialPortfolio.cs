using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

namespace OpenEtrm.Api;

internal static class GasTransportFinancialPortfolio
{
    internal const string Measure=GasTransportFinancialTicket.Measure;

    internal static async Task<ImmutableArray<GasOwnedPortfolioInput>> PrepareAgreements(NpgsqlConnection connection,PreviewDatabase database,
        InputFrame frame,MarketSnapshot snapshot,ImmutableArray<JsonElement> agreements,ImmutableArray<(PreviewData Data,InputFrame Frame)> supplied,
        bool requirePinnedSources,bool draft,CancellationToken ct)
    {
        if(frame.ContextId is null||snapshot.Knowledge is not {} knowledge||knowledge.ContextId!=frame.ContextId||snapshot.ValuationAt!=frame.At||
            agreements.IsDefault||supplied.IsDefault||agreements.Select(r=>r.Id("tradeId")).Distinct().Count()!=agreements.Length)
            throw GasTransportFinancialStore.Source();
        var known=(await new KnowledgeStore(database.Manifest.CatalogId).Read(connection,frame.ContextId.Value,ct)).Context;
        if(SchedulePreview.ParseAt(known.KnownAt)!=knowledge.KnownAt)throw GasTransportFinancialStore.Source();
        var result=ImmutableArray.CreateBuilder<GasOwnedPortfolioInput>();
        foreach(var raw in agreements)
        {
            if(draft)
            {
                _=GasTransportFinancialAdapter.Terms(database.Manifest.CatalogId,raw);
                throw GasTransportFinancialAdapter.Unpriced("rights_mark_required");
            }
            var agreement=GasTransportFinancialAdapter.Read(database.Manifest.CatalogId,raw,draft);
            if(agreement.ContextId!=frame.ContextId||agreement.At!=frame.At||agreement.KnownAt!=knowledge.KnownAt||
                DateWorkspace.Date(raw.Text("effectiveOn"))!=frame.BusinessDate)throw GasTransportFinancialStore.Source();
            if(agreement.State is null||agreement.UnpricedReason is not null)
                throw GasTransportFinancialAdapter.Unpriced(agreement.UnpricedReason??"rights_mark_required");
            var reference=agreement.Terms.Rights.Currency.Revision;
            var selected=new CatalogueSource(agreement.Terms.Definitions.RevisionId,reference.CatalogId,reference.RevisionId);
            var input=await MarketInputSelection.Resolve(database,connection,frame,selected,supplied,requirePinnedSources,ct);
            if(input.Data.DefinitionRevision!=agreement.Terms.Definitions)throw GasTransportFinancialStore.Source();
            var market=input.Data.Snapshot(input.Frame).WithKnowledge(frame.ContextId.Value,knowledge.KnownAt);
            var dependencies=agreement.Mark is {} adopted?ImmutableArray.Create(adopted.Market):[];
            if(dependencies.Any(d=>d.Curve.Revision!=agreement.Terms.Definitions))throw GasTransportFinancialStore.Source();
            CapturedPriceSource? captured=null;GasTransportDirectRightsEvaluation? evaluation=null;
            if(agreement.Expiry is null)
            {
                if(agreement.Mark is not {} mark)throw GasTransportFinancialAdapter.Unpriced("rights_mark_required");
                var prepared=await CustomCurves.PrepareMarket(connection,database,input.Data,input.Frame,market,known,[],dependencies,
                    CustomCurves.MarketPreparation.ValuationDependencies,ct);
                if(!prepared.Bindings.TryGetValue(mark.Market,out var point))throw GasTransportFinancialStore.Source();
                captured=new(mark.Market,prepared.Valuation,point);
                evaluation=new(prepared.Valuation.Scenario.Plan.Context,prepared.Valuation.Scenario.Id,ConnectedCurves.NumericalPolicy);
            }
            var rights=GasTransportFinancialAdapter.Instrument(agreement,captured,evaluation);
            var bindings=ImmutableArray.CreateBuilder<TradeInstrument>();var states=ImmutableDictionary.CreateBuilder<MeasureBinding,IBindingValuationState>();
            var evidence=ImmutableArray.CreateBuilder<PortfolioPositionEvidence>();
            void Evidence(Guid binding,Instrument instrument,Quantity quantity)=>evidence.Add(new(agreement.TradeId,1,binding,agreement.InstrumentId,
                instrument.Id,agreement.BookId,Measure,agreement.Terms.Definitions.RevisionId,raw.GetProperty("publicationOrder").GetInt32(),quantity)
                {InstrumentType=GasTransportFinancialTicket.Product,TradeReference=TradingReference.Trade(raw.GetProperty("tradeReference").GetInt32()),
                    InstrumentReference=TradingReference.Instrument(raw,"instrumentReference")});
            bindings.Add(new(agreement.InstrumentId,rights,rights.UnitQuantity,retainedInstrumentId:agreement.InstrumentId));Evidence(agreement.InstrumentId,rights,rights.UnitQuantity);
            var providers=ImmutableDictionary.CreateBuilder<Guid,ISideDiscounting>();var discounts=ImmutableArray.CreateBuilder<PortfolioInstantDiscountEvidence>();
            if(agreement.Cash is {} cash)
            {
                bindings.Add(new(cash.BindingId,cash.Instrument,cash.Quantity));states.Add(new(agreement.TradeId,cash.BindingId),cash.State);
                Evidence(cash.BindingId,cash.Instrument,cash.Quantity);
                if(!cash.Instrument.Due(market.ValuationAt)&&cash.State.Outstanding(market).Numerator!="0")
                {
                    var mapping=await SwapStore.CurrencyDefault(connection,database.Manifest.CatalogId,input.Data,input.Frame,rights.Currency,ct)??
                        throw new MarketDataException("discount_curve","The independent Transport fee requires its exact captured currency discount mapping.");
                    var provider=new InstantCurrencyDiscounting([cash.Instrument.Payment],market,mapping,RateDiscountTiming.Version);
                    providers.Add(cash.Instrument.Id,provider);
                    discounts.AddRange(provider.Inputs.Select(p=>new PortfolioInstantDiscountEvidence(p.Key.SideId,p.Value)));
                }
            }
            var trade=new Trade(agreement.TradeId,agreement.BookId,bindings.ToImmutable());
            var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null)
                {BindingStates=states.ToImmutable(),InstrumentDiscounting=providers.ToImmutable()};
            result.Add(new(input.Data,input.Frame,market,ValuationPortfolio.FromTrades([trade],setup),evidence.ToImmutable(),discounts.ToImmutable()){Measure=Measure});
        }
        return result.ToImmutable();
    }

    internal static async Task<ExactMoney> Value(NpgsqlConnection connection,PreviewDatabase database,PreviewData data,InputFrame frame,
        MarketSnapshot snapshot,JsonElement agreement,bool draft,CancellationToken ct)
    {
        var inputs=await PrepareAgreements(connection,database,frame,snapshot,[agreement],[(data,frame)],false,draft,ct);
        var known=(await new KnowledgeStore(database.Manifest.CatalogId).Read(connection,frame.ContextId!.Value,ct)).Context;
        var total=ExactMoney.Parse("0",GasTransportFinancialAdapter.Terms(database.Manifest.CatalogId,agreement).Rights.Currency);
        foreach(var input in inputs)
        {
            var dependencies=input.Portfolio.Declare(input.Snapshot).SelectMany(c=>c.Dependencies).Distinct().ToImmutableArray();
            var prepared=await CustomCurves.PrepareMarket(connection,database,input.Data,input.Frame,input.Snapshot,known,input.Portfolio.Instruments,
                dependencies,CustomCurves.MarketPreparation.ValuationDependencies,ct);
            var portfolio=RiskEngine.PrepareConnected(prepared.Valuation,prepared.Bindings,[input.Portfolio],prepared.Authority,ct);
            var values=RiskEngine.CalculateExact(portfolio,[],RiskEngine.ExactNumericalPolicy,ct,RiskDetail.Books);
            foreach(var value in values.Groups.SelectMany(g=>g.BaseValues))total=total.Add(value.Value);
        }
        return total;
    }
}
