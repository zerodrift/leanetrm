using System.Collections.Immutable;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Curves;
using OpenEtrm.MarketData.Publications;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData;

public sealed partial class CurveModel
{
    // The selected custom source owns this transition. Native curve definitions
    // stay immutable; both actuals and projections remain original graph inputs.
    public ConnectedCurveExport CompileContractTerms(MarketSnapshot snapshot,ImmutableArray<CurveRequest> requests,
        IReadOnlyDictionary<CurveRequest,ContractPriceTermsBinding> bindings,bool freezeHistorical,
        ResolvedCalculationContext context,CancellationToken cancellation=default)
    {
        var inputs=ValidateSnapshot(snapshot,cancellation);
        _=CompileValidated(snapshot,[],freezeHistorical,context,inputs,cancellation);
        if(bindings is null||requests.IsDefault||requests.Distinct().Count()!=requests.Length||bindings.Keys.Any(k=>!requests.Contains(k)))
            throw ContractPriceTerms.Invalid();
        var live=ImmutableArray.CreateBuilder<CurveRequest>();
        var points=new Dictionary<(CurveRequest,InputUse),CurvePointKey>();
        var declarations=new List<ConnectedCurvePoint>();
        foreach(var request in requests)
        {
            cancellation.ThrowIfCancellationRequested();
            if(!bindings.TryGetValue(request,out var binding)){live.Add(request);continue;}
            if(!curves.TryGetValue(request.Curve,out var curve)||!curveMembers[curve.Key].Contains(request.Contract))
                throw new MarketDataException("request_member","The terms require an exact native curve member.");
            var contract=contracts[request.Contract];var quote=Quote(curve);var terms=binding.Terms;
            if(terms is null||Quantity(quote).Commodity!=binding.Commodity)throw ContractPriceTerms.Invalid();terms.Validate();
            InputKey input;MarketContract actualContract;
            if(terms.Settlement==ContractPriceTerms.Derived)
            {
                if(curve.Rule==CurveRule.Direct||binding.Publication is not null||binding.Fixing is not null)throw ContractPriceTerms.Invalid();
                live.Add(request);continue;
            }
            if(terms.Settlement==ContractPriceTerms.ExchangeFinal)
            {
                if(contract.Kind!=ContractKind.Future||curve.Rule!=CurveRule.Direct||curve.Settlement.Kind!=SettlementKind.FutureSeries||
                    binding.Publication is not null||binding.Fixing is not null)throw ContractPriceTerms.Invalid();
                if(snapshot.ValuationAt<contract.Expiry){live.Add(request);continue;}
                input=new(priceSeries[(request.Contract,curve.Quote)],InputUse.FrozenFact);actualContract=contract;
                if(!inputs.TryGetValue(input,out var selected))throw new MarketDataException("missing_observation","The exact official final exchange settlement is required.");
                var observation=selected.Observation;
                if(observation.Role!=ObservationRole.FinalContractSettlement||observation.Source!=curve.DirectSource||observation.ObservedAt<contract.Expiry)
                    throw new MarketDataException("selection_mismatch","The pinned final exchange settlement must retain its official source and be observed at or after expiry.");
            }
            else
            {
                var publication=binding.Publication;
                if(publication is null||publication.Key!=terms.Publication||publication.References!=quote.Key.Revision||binding.Fixing is null)
                    throw ContractPriceTerms.Invalid();
                if(PricePublicationBehavior.Create(publication) is UnsupportedPricePublication)
                    throw new MarketDataException("publication_unsupported","This publication is a non-executable placeholder.");
                var index=publication.Indices.SingleOrDefault(i=>i.Key==terms.Index);
                if(index is null||index.Commodity!=binding.Commodity||index.Quote!=quote.Key||index.Unit!=PriceUnit.FromQuote(quote)||
                    curve.Settlement.Kind!=SettlementKind.Index||curve.Settlement.IndexA is not {} target)throw ContractPriceTerms.Invalid();
                var selection=terms.Index==target?new PriceSettlementSelection(PriceSettlementMode.CurveTarget,target,target,terms.ProjectionPolicy):
                    new PriceSettlementSelection(PriceSettlementMode.Override,target,terms.Index!.Value,terms.ProjectionPolicy);
                var lookup=PeriodLookup(request,contract,selection,binding.Fixing);
                var admission=PeriodPriceSelector.Admit(Build(snapshot,[],cancellation),lookup);
                if(admission.Projection is not null){live.Add(request);continue;}
                input=new(binding.Fixing.Series,InputUse.FrozenFact);actualContract=contracts[series[input.Series].Contract!.Value];
            }
            var sample=new ConnectedSourceSample(snapshot.Id,snapshot.ValuationAt,snapshot.MarketCutoff,snapshot.SelectionPolicy,InputUse.FrozenFact,freezeHistorical);
            var actualRequest=new CurveRequest(request.Curve,actualContract.Key);
            var actualKey=new CurvePointKey(request.Curve,ConnectedIdentity.Id(new{Version="contract_price_terms_actual_v1",request,Binding=binding,Sample=sample}));
            declarations.Add(new(actualKey,actualContract.DeliveryFrom,actualContract.DeliveryTo,PriceUnit.FromQuote(quote),
                new ConnectedSource(actualRequest,input,inputs[input].Observation,quote,series[input.Series]){Sample=sample},actualContract));
            // A distinct actual series may use its own exact dated contract. The
            // output keeps the authored native contract identity and coverage.
            var output=actualKey;
            if(actualContract.Key!=contract.Key)
            {
                output=new(request.Curve,ConnectedIdentity.Id(new{Version="contract_price_terms_output_v1",request,Actual=actualKey,Binding=binding}));
                declarations.Add(new(output,contract.DeliveryFrom,contract.DeliveryTo,PriceUnit.FromQuote(quote),
                    new ConnectedWeightedSum([new(CurvePointReference.Exact(actualKey),ExactDecimal.Parse("1"))]),contract));
            }
            points.Add((request,InputUse.Variable),output);
        }
        var native=CompileValidated(snapshot,live.ToImmutable(),freezeHistorical,context,inputs,cancellation);
        foreach(var pair in native.Points)points.Add(pair.Key,pair.Value);
        return new(ConnectedCurves.MergeDefinitions(native.Definitions.Concat(declarations.GroupBy(p=>p.Key.Curve)
            .Select(g=>new ConnectedCurveDefinition(g.Key,g.ToImmutableArray())))),points.ToImmutableDictionary());
    }
}
