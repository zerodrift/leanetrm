using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Power;

namespace OpenEtrm.MarketData.Trading;

public sealed partial class ScheduleMarketSources
{
    private (ExactUnitPrice Price,SchedulePriceFacts State) Dated(BookedSideInstruction instruction,CurveBuild market)
    {
        var row=instruction.Output;var bindings=instruction.Bindings.ToDictionary(binding=>binding.Port);
        if(!bindings.ContainsKey("market")||!bindings.ContainsKey("index")||!bindings.ContainsKey("quote"))
            throw Invalid("A scalar price requires the supported exact dated market, index and quote bindings.");
        var candidates=dated.Where(lookup=>lookup.Market==bindings["market"].Market&&lookup.Index==bindings["index"].Reference&&
            lookup.Quote.Key==bindings["quote"].Reference&&lookup.Contract.DeliveryFrom==row.ObservationFrom&&lookup.Contract.DeliveryTo==row.ObservationTo&&
            DefinitionIdentity.For(lookup.Settlement).Canonical==row.Parameters["selection"]&&
            (bindings.TryGetValue("fixing",out var fixing)?lookup.Fixing is {} f&&f.Series==fixing.Series&&f.Source==fixing.Reference&&f.Implementation==fixing.Coordinate:lookup.Fixing is null)&&
            (bindings.TryGetValue("projection",out var projection)?lookup.CustomProjection is {} p&&p.CatalogId==projection.EntityCatalog&&p.CurveId==projection.Entity&&
                p.Version==projection.Version&&p.OutputNode==projection.Coordinate&&p.Position==projection.Position:lookup.CustomProjection is null)).Distinct().ToArray();
        if(candidates.Length!=1)throw Invalid("A stored dated lookup requires one exact admitted contract, quote, fixing and projection definition.");
        var resolved=PeriodPriceSelector.Resolve(market,candidates[0],request=>market.Pricing.Price(request,default),request=>market.Pricing.Lineage(request,default));
        RequireUnit(resolved.Price,row);
        return(resolved.Price,resolved.State==SchedulePriceState.Actual?SchedulePriceFacts.PublishedFixing:SchedulePriceFacts.Projected);
    }

    private (ExactUnitPrice Price,SchedulePriceFacts State) Native(BookedSideInstruction instruction,ScheduleDefinitionGrid grid,int cell,string family,IntervalPriceResolver resolver,ValuedAttribute? nativePriceAttribute)
    {
        RequireNative(grid);var row=instruction.Output;var interval=grid.Interval(cell);var binding=instruction.Bindings.Single(value=>value.Port is "curve" or "location");
        CurveKey curve;PriceSettlementSelection? selection=null;LocationAssignment? assignment=null;
        if(binding.Kind=="curve_interval")
        {
            curve=new(binding.Definitions!.Value,binding.Code!);
            if(row.Parameters["selection"]!="interval_attribute_lookup_v1")
            {
                var matches=selections.Where(value=>DefinitionIdentity.For(value).Canonical==row.Parameters["selection"]&&
                    value.SelectedIndex==instruction.Bindings.Single(value=>value.Port=="index").Reference).Distinct().ToArray();
                if(matches.Length!=1)throw Invalid("The stored indexed lookup requires its exact explicit settlement selection.");
                selection=matches[0];
            }
        }
        else
        {
            ReferenceKey location;
            if(binding.Kind=="location")location=binding.Reference!.Value;
            else
            {
                AssetKey asset;
                if(binding.Kind=="asset")asset=new(binding.Definitions!.Value,binding.Code!);
                else if(binding.Kind=="quantity_source"&&reference!.Sources.TryGetValue(new(binding.Definitions!.Value,binding.Code!),out var source))asset=source.Asset;
                else throw Invalid("The physical price requires its exact asset or source location.");
                if(!reference!.Assets.TryGetValue(asset,out var definition))throw Invalid("The physical price asset is unavailable.");
                location=definition.Location;
            }
            var attribute=binding.Coordinate switch{"energy"=>ValuedAttribute.Energy,"certificate"=>ValuedAttribute.Certificate,"capacity"=>ValuedAttribute.Capacity,
                _=>throw Invalid("The physical price attribute is unsupported.")};
            assignment=LocationValuation.Partition(reference!.Assignments,location,attribute,[interval],reference.Definitions)[0];curve=assignment.Curve;
        }
        if(!read!.Curves.TryGetValue(curve,out var prepared))throw Invalid("The exact native price curve is unavailable.");
        if(assignment is not null&&prepared.Curve is PeriodContractCurve period)selection=PriceSettlementSelection.CurveTarget(period.SettlementIndex);
        if((prepared.Curve is PeriodContractCurve)!=(selection is not null))throw Invalid("Period prices require an explicit settlement selection.");
        var selectedAttribute=binding.Kind=="curve_interval"?nativePriceAttribute??
            throw Invalid("An indexed native source requires its explicit authorized instrument attribute."):
            binding.Coordinate switch{"energy"=>ValuedAttribute.Energy,"certificate"=>ValuedAttribute.Certificate,"capacity"=>ValuedAttribute.Capacity,
                _=>throw Invalid("The physical price attribute is unsupported.")};
        if(!Enum.IsDefined(selectedAttribute)||nativePriceAttribute is {} supplied&&supplied!=selectedAttribute)
            throw Invalid("Native source classification must agree with the authorized instrument binding.");
        if(!reference!.Quantities.TryGetValue(row.Unit!.Value,out var specification))
            throw new MarketDataException("ppa_quantity_unit","The native price requires its exact quantity specification.");
        AssetDefinitions.Specification(specification,reference.Reference);
        var validUnit=selectedAttribute switch
        {
            ValuedAttribute.Energy=>specification.Unit.Code=="megawatt_hour"&&specification.Dimension.Code=="energy",
            ValuedAttribute.Capacity=>specification.Unit.Code=="megawatt_day"&&specification.Dimension.Code=="energy",
            ValuedAttribute.Certificate=>specification.Dimension.Code=="certificate",
            _=>false
        };
        if(!validUnit)throw new MarketDataException("ppa_quantity_unit","The selected attribute must match its supported exact quantity dimension and unit.");
        if((selectedAttribute is ValuedAttribute.Certificate or ValuedAttribute.Capacity)!=(prepared.Curve is PeriodContractCurve))
            throw new MarketDataException("ppa_cadence","The selected attribute requires its declared native or whole-period price cadence.");
        var values=resolver.Prices(curve,grid.Month,selection);
        var contracts=prepared.Period(grid.Month).Contracts;
        var matchesPrice=values.Where((value,n)=>prepared.Curve is PeriodContractCurve?
            contracts[n].DeliveryFrom<=interval.OperatingDate&&interval.OperatingDate<contracts[n].DeliveryTo:value.Interval==interval).ToArray();
        if(selectedAttribute==ValuedAttribute.Capacity)
        {
            var matching=contracts.Where(contract=>contract.DeliveryFrom<=interval.OperatingDate&&interval.OperatingDate<contract.DeliveryTo).ToArray();
            if(matching.Length!=1||matching[0].DeliveryFrom!=grid.Month||matching[0].DeliveryTo!=grid.Month.AddMonths(1))
                throw new MarketDataException("ppa_cadence","Capacity pricing requires one exact whole-month contract.");
        }
        if(matchesPrice.Length!=1)throw Invalid("The price must cover exactly the stored interval or its selected whole-period contract.");
        var resolved=matchesPrice[0];var price=PpaPriceGraphCompiler.Required(resolved);
        var state=resolved.State switch{IntervalPriceState.Actual=>SchedulePriceFacts.Actual,IntervalPriceState.PublishedFixing=>SchedulePriceFacts.PublishedFixing,
            IntervalPriceState.Modeled=>SchedulePriceFacts.Modeled,IntervalPriceState.Unresolved=>SchedulePriceFacts.Estimated,_=>SchedulePriceFacts.Projected};
        if(prepared.Curve is PeriodContractCurve&&family!="certificate"&&interval.End<=context.ValuationAt&&state==SchedulePriceFacts.Projected)state=SchedulePriceFacts.Estimated;
        if(state==SchedulePriceFacts.Estimated&&pending!=PendingActualsPolicy.EstimateUntilActual)
            throw new MarketDataException("actuals_required","Delivered prices require eligible actuals under the selected policy.");
        if(assignment is not null)
        {
            if(price.Unit!=assignment.Unit||row.Currency!=price.Unit.Currency||row.Unit!=price.Unit.Quantity||row.QuoteMultiplier!="1")
                throw Invalid("Physical assignments require the exact currency, quantity and canonical output quote scale.");
            price=price.Add(ExactUnitPrice.FromPrice(new(assignment.Adjustment,assignment.Unit)));
            price=ExactUnitPrice.FromFraction(price.NumeratorValue*price.Unit.Multiplier.ScaledValue,
                price.DenominatorValue*ExactDecimal.WorkingScale,new(price.Unit.Currency,price.Unit.Quantity,ExactDecimal.Parse("1")));
        }
        RequireUnit(price,row);return(price,state);
    }
}
