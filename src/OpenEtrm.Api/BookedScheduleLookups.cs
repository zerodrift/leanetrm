using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

// Materialize source metadata from the authorized exact market/reference read.
// In particular, fixing publication times come from index_fixing_observations,
// never Side terms, contract expiry, observations or the schedule's first fixing.
internal static class BookedScheduleLookups
{
    internal static ImmutableArray<IndexPriceLookup> Dated(PreviewData data,BookedSideProgram program)
    {
        var result=ImmutableArray.CreateBuilder<IndexPriceLookup>();
        foreach(var instruction in program.Document.Instructions.Where(instruction=>instruction.Output is{Operation:"price_lookup",Grid:null}))
        {
            var row=instruction.Output;var bindings=instruction.Bindings.ToDictionary(binding=>binding.Port);
            if(!bindings.TryGetValue("market",out var market)||market.Market is not {} request||request.Curve.Revision!=data.DefinitionRevision||
                !bindings.TryGetValue("quote",out var quoteBinding)||!bindings.TryGetValue("index",out var indexBinding))
                throw Invalid("The dated price source requires its exact market, quote and index bindings.");
            var contracts=data.Contracts.Where(contract=>contract.Key==request.Contract&&contract.Kind==ContractKind.Forward&&
                contract.DeliveryFrom==row.ObservationFrom&&contract.DeliveryTo==row.ObservationTo).ToArray();
            var quotes=data.Quotes.Where(quote=>quote.Key==quoteBinding.Reference).ToArray();
            var curves=data.DefinitionRows("curves","code",request.Curve.Code).ToArray();
            if(contracts.Length!=1||quotes.Length!=1||curves.Length!=1||indexBinding.Reference is not {} index||index.Revision!=data.ReferenceRevision)
                throw Invalid("A dated indexed lookup requires one exact forward contract, quote and curve definition.");
            var targets=data.ReferenceRows("settlement_targets","code",curves[0].Text("settlement_target_code")).ToArray();
            if(targets.Length!=1||targets[0].Text("kind")!="index"||targets[0].OptionalText("index_b_code") is not null)
                throw Invalid("The stored forward price requires an outright index target.");
            var target=new ReferenceKey(data.ReferenceRevision,targets[0].Text("index_a_code"));
            // Decode the closed versioned contract by matching its stored canonical
            // identity. A differing index alone does not authorize an override.
            var selection=index==target?PriceSettlementSelection.CurveTarget(target):
                new PriceSettlementSelection(PriceSettlementMode.Override,target,index,PriceSettlementSelection.ForwardCurveNoBasis);
            if(DefinitionIdentity.For(selection).Canonical!=row.Parameters["selection"])
                throw Invalid("The stored settlement policy must match its exact supported canonical contract.");
            IndexFixing? fixing=null;
            if(bindings.TryGetValue("fixing",out var pinned))
            {
                var matching=PriceSettlementInputs.Fixings(data,selection,quotes[0],[contracts[0]])
                    .Where(value=>value.Series==pinned.Series&&value.Source==pinned.Reference&&value.Implementation==pinned.Coordinate).ToArray();
                if(matching.Length!=1)throw Invalid("The retained fixing source requires one exact qualified publication definition.");
                fixing=matching[0];
            }
            CustomCurveProjectionBinding? custom=null;
            if(bindings.TryGetValue("projection",out var projection))
            {
                if(projection.EntityCatalog is not {} catalog||projection.Entity is not {} curve||projection.Version is not {} version||projection.Coordinate is not {} output)
                    throw Invalid("The custom projection requires its exact published identity and output coordinate.");
                custom=new(catalog,curve,version,output,projection.Position,
                    projection.Position is null?CustomCurveProjectionBinding.DeliverySelection:CustomCurveProjectionBinding.SupportedSelection,
                    CustomCurveProjectionBinding.SupportedImplementation);
            }
            result.Add(new(selection,quotes[0],request,contracts[0],null,fixing,custom));
        }
        return result.Distinct().ToImmutableArray();
    }
    private static MarketDataException Invalid(string message)=>new("side_market_sources",message);
}
