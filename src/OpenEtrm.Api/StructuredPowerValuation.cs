using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.Api;

public static partial class StructuredPowerPreview
{
    private sealed record ValuedPlan(StructuredPowerValuationResult Value,OperatingRightInstrument Instrument,ImmutableDictionary<Guid,string> Names);
    static ValuedPlan Value(PreviewData data,InputFrame frame,DateTimeOffset knownAt,Guid id,
        StructuredPowerResolvedTerms resolved,DeliveryInterval[] grid,IReadOnlyDictionary<string,OperatingOutputPort> ports,
        ImmutableArray<OperatingScenarioQuantityCell> cells,StructuredPowerValuationInput input)
    {
        ValidateValuationProfile(input.Perspective,input.Measure,input.Discounting);
        if(input.EnergyPrice is null||input.PaymentRule is null)
            throw Error("structured_power_policy","Select the explicit right-holder perspective, remaining-value measure, energy price and payment rule.");
        if(input.StartupPrice is not null||input.StartupQuoteCode is not null||input.StartupFeeBasis is not null)
            throw Error("structured_power_unavailable","Startup fees need an admitted start-count quote; the complete resource-right unit cannot be reused as a start.");
        var definitions=new List<StructuredPowerOperatingSideInput>();
        void Add(string name,string source,StructuredPowerPriceInput price,ExactDecimal sign,bool once=false)=>
            definitions.Add(new(name,name,once?"horizon_right":source,sign.ToString(),"payment",price,input.PaymentRule));
        var kind=resolved.CanonicalTerms.Kind;
        Add(kind=="battery"?"discharging_energy":kind=="thermal"?"energy_output":"payment_energy",kind=="solar"?"payment":"energy",input.EnergyPrice,One);
        if(kind=="battery")
        {
            if(input.ChargingPrice is null||input.FuelPrice is not null||input.FuelQuantityBasis is not null)throw Error("structured_power_price","Battery valuation needs an explicit charging price and no fuel terms.");
            Add("charging_energy","charge",input.ChargingPrice,MinusOne);
        }
        else if(input.ChargingPrice is not null)throw Error("structured_power_price","A charging price belongs only to a battery preview.");
        if(kind=="thermal")
        {
            if(input.FuelPrice is null||input.FuelQuantityBasis is not("guaranteed" or "consumption"))throw Error("structured_power_price","Thermal valuation needs an explicit fuel price and guaranteed or consumption basis.");
            Add("fuel_input","fuel",input.FuelPrice,MinusOne);
        }
        else if(input.FuelPrice is not null||input.FuelQuantityBasis is not null)throw Error("structured_power_price","Fuel price and basis belong only to thermal previews.");
        if(input.VariablePrice is {} variable)
        {
            var expected=kind switch{"battery"=>"grid_charge_plus_discharge","thermal"=>"energy_output",_=>"payment_energy"};
            if(input.VariableQuantityBasis!=expected)throw Error("structured_power_policy","Select the implemented variable-fee quantity basis explicitly.");
            Add("variable_fee",kind=="battery"?"variable":kind=="solar"?"payment":"energy",new("fixed",input.EnergyPrice.QuoteCode,variable,null,null),MinusOne);
        }
        else if(input.VariableQuantityBasis is not null)throw Error("structured_power_policy","A variable-fee quantity basis requires an explicit variable price.");
        if(input.AccessPrice is {} access)
        {
            if(input.AccessQuoteCode is null||input.AccessFeeBasis!="preview_horizon_once_v1")throw Error("structured_power_policy","A horizon access fee requires its exact complete-right quote and explicit once-per-preview-horizon basis.");
            Add("access_fee","",new("fixed",input.AccessQuoteCode,access,null,null),MinusOne,true);
        }
        else if(input.AccessQuoteCode is not null||input.AccessFeeBasis is not null)throw Error("structured_power_policy","Access fee reference and basis require an explicit fee amount.");
        return Assemble(data,frame,knownAt,id,resolved,grid,ports,cells,input.CurrencyCode,input.RightQuantityCode,
            definitions.ToArray(),legacy:true);
    }

    internal const string CompositionImplementation="operating_side_composition_v2";
    internal const int MaximumOperatingSides=8;
    static ValuedPlan ValueComposition(PreviewData data,InputFrame frame,DateTimeOffset knownAt,Guid id,
        StructuredPowerResolvedTerms resolved,DeliveryInterval[] grid,IReadOnlyDictionary<string,OperatingOutputPort> ports,
        ImmutableArray<OperatingScenarioQuantityCell> cells,StructuredPowerCompositionInput input)
    {
        ValidateComposition(input,resolved.CanonicalTerms.Kind);
        return Assemble(data,frame,knownAt,id,resolved,grid,ports,cells,input.CurrencyCode,input.RightQuantityCode,input.Sides,legacy:false);
    }

    internal static void ValidateValuationProfile(string perspective,string measure,bool discounting)
    {
        if(perspective!=RightHolderPerspective||measure!=RemainingValueMeasure)
            throw Error("structured_power_policy","Select the explicit right-holder perspective and remaining-value measure.");
        if(discounting)throw Error("structured_power_unavailable","Discounting requires a separately admitted currency-rate operation; select explicitly undiscounted.");
    }

    internal static void ValidateComposition(StructuredPowerCompositionInput input,string kind)
    {
        if(input is null||input.Implementation!=CompositionImplementation||input.Sides is not {Length:>=1 and <=MaximumOperatingSides} sides||
            sides.Any(s=>s is null||s.Key is null||!System.Text.RegularExpressions.Regex.IsMatch(s.Key,"^[a-z][a-z0-9_]{0,47}$")||
                string.IsNullOrWhiteSpace(s.Name)||s.Name!=s.Name.Trim()||s.Name.Length>80||s.Coefficient is not("1" or "-1")||
                s.Kind is not("payment" or "delivery")||(s.Kind=="payment")!=(s.PaymentRule is not null)||s.Price is null||
                !SideChoices.Any(c=>c.Kind==kind&&c.Code==s.QuantitySource&&c.AllowedKinds.Contains(s.Kind)))||
            sides.Select(s=>s.Key).Distinct(StringComparer.Ordinal).Count()!=sides.Length||sides.All(s=>s.QuantitySource=="horizon_right"))
            throw Error("operating_composition","Supply one to eight distinctly keyed, explicitly signed concrete Side definitions with supported native sources and independent delivery/payment rules. A whole-horizon right is a payment basis.");
        ValidateValuationProfile(input.Perspective,input.Measure,input.Discounting);
    }

    static ValuedPlan Assemble(PreviewData data,InputFrame frame,DateTimeOffset knownAt,Guid id,
        StructuredPowerResolvedTerms resolved,DeliveryInterval[] grid,IReadOnlyDictionary<string,OperatingOutputPort> ports,
        ImmutableArray<OperatingScenarioQuantityCell> cells,string currencyCode,string rightCode,StructuredPowerOperatingSideInput[] definitions,bool legacy)
    {
        Reference(data,"currencies",currencyCode);var currency=new ReferenceKey(data.ReferenceRevision,currencyCode);
        var right=Quantity(data,rightCode);
        if(right.Unit.Code!="resource_right"||!CompleteRightDimension(data,right.Dimension.Code)||right.Commodity!=resolved.Energy.Commodity)
            throw Error("structured_power_reference","Select an exact compatible complete resource-right quantity for normalization.");
        var unit=new Quantity(One,right.Key);
        var memberships=ImmutableArray.CreateBuilder<SideMembership>();var names=new Dictionary<Guid,string>();var quotes=new Dictionary<Guid,ReferenceKey>();
        QuoteSpecification Quote(string code,QuantitySpecification quantity)
        {
            var matches=data.QuoteRows(code).Take(2).ToArray();if(matches.Length!=1||matches[0].Key.Revision!=data.ReferenceRevision||matches[0].Kind!=QuoteKind.Outright||matches[0].Currency!=currency||matches[0].Quantity!=quantity.Key)
                throw Error("structured_power_price","Select an authorized outright quote in the exact native quantity and preview currency.");
            return matches[0];
        }
        void Add(StructuredPowerOperatingSideInput definition,string portName,QuantitySpecification quantity,bool once)
        {
            var price=definition.Price;var name=definition.Name;var sign=Decimal(definition.Coefficient);
            var payment=definition.Kind=="payment"?OperatingPayment(definition.PaymentRule!):null;
            if(price is null)throw Error("structured_power_price","Supply an explicit price for every valued side.");
            var quote=Quote(price.QuoteCode,quantity);CurveRequest? projection=null;FixedPriceTerm? fixedPrice=null;
            switch(price.Mode)
            {
                case "fixed" when price.FixedPrice is {} amount&&price.CurveCode is null&&price.ContractId is null:
                    fixedPrice=new FlatPrice(new(Decimal(amount),PriceUnit.FromQuote(quote)));break;
                case "selected_contract_flat_v1" when price.FixedPrice is null&&price.CurveCode is {} curve&&price.ContractId is {} contract:
                {
                    var requests=data.Requests(frame,curve);var selected=requests.Where(x=>x.Contract.Id==contract).Take(2).ToArray();
                    var definitions=data.Definitions["curves"].Where(x=>x.Text("code")==curve).Take(2).ToArray();
                    var contracts=data.ContractRows(contract).Take(2).ToArray();
                    if(selected.Length!=1||definitions.Length!=1||definitions[0].Text("quote_code")!=quote.Key.Code||contracts.Length!=1||
                        contracts[0].DeliveryFrom>grid[0].OperatingDate||contracts[0].DeliveryTo<=grid[^1].OperatingDate)
                        throw Error("structured_power_price","The explicitly selected price contract must be authorized and cover every operating date in this flat-contract model.");
                    projection=selected[0];break;
                }
                default:throw Error("structured_power_price","Select exactly one fixed price or an explicit selected-contract flat projection.");
            }
            var sideId=legacy?Identity(new{id,name}):Identity(new{id,key=definition.Key});var selectedGrid=once?grid.Take(1):grid;
            var periods=selectedGrid.Select(interval=>
            {
                var month=new DateOnly(interval.OperatingDate.Year,interval.OperatingDate.Month,1);
                return new OperatingSidePeriod(interval,month,month.AddMonths(1),once?new(null,new(One,right.Key)):new(ports[portName],null),projection);
            }).ToImmutableArray();
            var terms=new OperatingSideTerms(unit,quote,payment,definition.Kind=="payment"?ComponentKind.Payment:ComponentKind.Delivery,periods,OperatingSideTerms.SupportedImplementation,
                new SideCalendarPolicy(CalendarBinding.NotApplicable(),CalendarBinding.NotApplicable(),CalendarBinding.NotApplicable(),SideCalendarPolicy.Implementation));
            Side side=fixedPrice is {} fixedTerm?new FixedSide(sideId,terms,fixedTerm):new FloatingSide(sideId,terms);
            memberships.Add(new(side,sign));names.Add(sideId,name);quotes.Add(sideId,quote.Key);
        }
        foreach(var definition in definitions)
        {
            var once=definition.QuantitySource=="horizon_right";
            var portName=definition.QuantitySource;
            if(!legacy&&!once)portName=SourcePort(definition.QuantitySource);
            var quantity=once?right:portName is "fuel" or "guaranteed_fuel" or "consumption_fuel"?resolved.Fuel!:resolved.Energy;
            Add(definition,portName,quantity,once);
        }
        var instrument=new OperatingRightInstrument(id,resolved.Resource.Asset,unit,currency,memberships.ToImmutable());
        var dependencies=instrument.Dependencies;var snapshot=data.Snapshot(frame);
        if(frame.ContextId is {} contextId)snapshot=snapshot.WithKnowledge(contextId,knownAt);
        var discounting=new ExplicitDiscounting(One,DiscountFactorSource.Undiscounted);
        var context=dependencies.IsEmpty?new SideValuationContext(snapshot,discounting):new SideValuationContext(data.Model.Build(snapshot,dependencies),discounting);
        context=context.WithOperatingQuantities(OperatingQuantityInputs.Scenario(snapshot,cells,OperatingQuantityInputs.ScenarioImplementation));
        var value=instrument.PV(context);
        var sides=value.Sides.Select(contribution=>
        {
            var sideId=contribution.SideId;var evaluated=(OperatingSideValuation)contribution.Value;
            var rows=evaluated.Rows.Select(row=>new StructuredPowerSideRow(row.Interval!,row.ValueAt,row.Quantity,row.PriceComponents[0].Price,row.Amount,row.RequirePresentValue(),row.QuantityState,
                row.OperatingEvidence?.EvidenceId,row.OperatingEvidence?.KnownAt){PriceUnitLabel=data.UnitLabel(row.PriceComponents[0].Price.Unit)}).ToImmutableArray();
            return new StructuredPowerSideResult(sideId,names[sideId],contribution.Coefficient.ToString(),quotes[sideId],evaluated.PresentValue,contribution.Contribution,rows);
        }).ToImmutableArray();
        return new(new(RemainingValueMeasure,RightHolderPerspective,currency,unit,value.PresentValue,sides,value.NumericalPolicy),instrument,names.ToImmutableDictionary());
    }
    internal static PaymentRule OperatingPayment(PpaPaymentInput input)
    {
        if(input.Frequency!="monthly"||input.Rule!="days_after_period_end"||input.Days is not >=0 or >366||input.DayOfMonth is not null||
            input.DayBasis!="calendar_days"||input.ShortMonth is not null||input.Adjustment!="unadjusted"||input.Calendar is not null||
            input.Implementation!=PaymentRule.Implementation)
            throw Error("structured_power_payment","Select the supported explicit monthly calendar-day payment rule without holiday adjustment or an external calendar.");
        return new(PaymentFrequency.Monthly,PaymentDateRule.DaysAfterPeriodEnd,input.Days,null,PaymentDayBasis.CalendarDays,null,DateRoll.Unadjusted,null,input.Implementation);
    }

}
