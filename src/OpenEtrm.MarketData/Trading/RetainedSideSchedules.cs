using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Power;

namespace OpenEtrm.MarketData.Trading;

public static class RetainedSideSchedules
{
    public static T Attach<T>(T instrument,IReadOnlyDictionary<Guid,ScheduleDefinition> schedules) where T:SidedInstrument
    {
        if(instrument is null||schedules is null)throw Error("Supply an instrument and its complete retained schedules.");
        var sides=instrument.Sides.Select(m=>m.Side).DistinctBy(s=>s.Id).ToArray();
        if(instrument.Sides.GroupBy(m=>m.Side.Id).Any(group=>group.Select(m=>(DefinitionIdentity.For(m.Side),m.Side.PricingBindingIdentity)).Distinct().Count()!=1))
            throw Error("One side identity cannot reinterpret different economic or pricing definitions.");
        if(!sides.Select(s=>s.Id).ToHashSet().SetEquals(schedules.Keys))throw Error("Every instrument side requires exactly one retained schedule, without missing or extra sides.");
        var retained=sides.ToDictionary(side=>side.Id,side=>
        {
            var definition=schedules[side.Id];
            if(definition.SideId!=side.Id||definition.EconomicIdentity!=DefinitionIdentity.For(side).Canonical||
                definition.PricingBindingIdentity!=(side.PricingBindingIdentity?.Canonical??""))
                throw Error("A retained schedule must belong to the exact side economic definition and pricing bindings.");
            var family=side switch{FixedSide=>"fixed",FloatingSide=>"floating",PowerPhysicalSide=>"power",CertificateDeliverySide=>"certificate",CapacityDeliverySide=>"capacity",_=>throw Error("The side family has no retained schedule adapter.")};
            if(definition.Family!=family)throw Error("A retained schedule cannot reinterpret the side family.");
            return side with{RetainedSchedule=definition,RetainedPlan=new(side,definition)};
        });
        if(instrument is PpaInstrument agreement)RetainedSideSchedule.RequireSiblingRoots(agreement,retained);
        SidedInstrument result=instrument with{Sides=[..instrument.Sides.Select(m=>m with{Side=retained[m.Side.Id]})]};
        if(result is PpaInstrument ppa)result=ppa with{Memberships=[..ppa.Memberships.Select(m=>m with{Side=retained[m.Side.Id]})]};
        return (T)result;
    }
    internal static PpaSideTerms Terms(Side side)=>side.RetainedPlan?.NativeTerms??side.Terms;
    internal static MarketDataException Error(string message)=>new("side_schedule_definition",message);
}

internal sealed record RetainedFixedPrice(ExactUnitPrice Value):FixedPriceTerm
{
    public override PriceUnit Unit=>Value.Unit;
    public override ExactUnitPrice At(DateOnly operatingDate)=>Value;
}
internal enum RetainedScheduleSourceRole { ProfileQuantity=1,ProfileBasis,FixedPrice }
[EconomicDefinition("retained_schedule_source_v1")]
internal sealed record RetainedScheduleSource([property:IdentityField]string Manifest,[property:IdentityField]RetainedScheduleSourceRole Role);

internal sealed record RetainedNativePrice(CurveKey? Curve,ReferenceKey? Location,VolumeSourceKey? Source,AssetKey? Asset,
    ExactDecimal Multiplier,UnitPrice Adder,PriceSettlementSelection? Settlement,PriceUnit OutputUnit);
internal readonly record struct RetainedNativeRow(int Root,ScheduleDefinitionRow Obligation,int Quantity,
    ExactUnitPrice? FixedPrice,ImmutableArray<RetainedNativePrice> Prices)
{
    internal int? CellOrdinal { get; init; }
}

// This adapter admits a closed retained topology into the compact schedule
// evaluator. It reads stored expressions and never runs the side generator.
internal sealed partial class RetainedSideSchedule
{
    private readonly Side side;
    internal ScheduleDefinition Definition { get; }
    private readonly Dictionary<(int,string),int[]> edges;
    private readonly Dictionary<(int,string),ScheduleDefinitionBinding> bindings;
    private readonly Dictionary<int,int[]> incoming;
    private readonly Dictionary<int,ScheduleDefinitionBinding[]> rowBindings;
    private readonly Dictionary<string,ScheduleDefinitionRoot> outputRoots;
    private readonly Dictionary<CurveRequest,(FloatingSidePeriod period,SwapProjection projection,int n)[]> projectionCandidates=[];
    private readonly HashSet<int> activeQuantityRows=[];
    internal IReadOnlyList<RetainedNativeRow> NativeRows { get; }
    private ImmutableArray<RetainedNativeRow> nativeTemplates=[];
    private ImmutableArray<NativeBlock> nativeBlocks=[];
    internal PpaSideTerms? NativeTerms { get; }
    internal ExactDecimal? CertificateConversion { get; }
    internal ExactDecimal? CapacityEntitlementFactor { get; }
    internal DefinitionIdentity SourceIdentity(RetainedScheduleSourceRole role)=>DefinitionIdentity.For(new RetainedScheduleSource(Definition.Manifest,role));

    internal ImmutableArray<DeliveryInterval> CapacityDays { get; }=[];
    private readonly SidePaymentSchedule? dated;
    internal RetainedSideSchedule(Side side,ScheduleDefinition definition)
    {
        this.side=side;Definition=definition;
        outputRoots=definition.Roots.ToDictionary(root=>root.Name,StringComparer.Ordinal);
        edges=definition.Edges.GroupBy(e=>(e.Target,e.Port)).ToDictionary(g=>g.Key,g=>g.OrderBy(e=>e.Ordinal).Select(e=>e.Source).ToArray());
        bindings=definition.Bindings.ToDictionary(b=>(b.Row,b.Port));
        incoming=definition.Edges.GroupBy(e=>e.Target).ToDictionary(g=>g.Key,g=>g.Select(e=>e.Source).ToArray());
        rowBindings=definition.Bindings.GroupBy(b=>b.Row).ToDictionary(g=>g.Key,g=>g.OrderBy(b=>b.Port,StringComparer.Ordinal).ToArray());
        if(side is FloatingSide floating)projectionCandidates=floating.Periods.SelectMany(period=>period.Projections.Select((projection,n)=>(period,projection,n)))
            .GroupBy(p=>p.projection.Market).ToDictionary(g=>g.Key,g=>g.ToArray());
        var obligations=definition.Roots.Where(r=>r.Measure=="contribution_pv").Select(r=>r.Row).ToArray();
        if(obligations.Length==0)throw Error("A retained side requires explicit obligation roots.");
        if(obligations.Select(n=>definition.Rows[n]).Any(row=>row.Operation!="output_guard"||row.Currency is null||row.PaymentOrdinal is null or <0||
            row.ValueAt is null||!TradingInstant.IsSupported(row.ValueAt.Value)))throw Error("Retained obligations require exact supported currency, payment identity and value instant.");
        var native=side is IntervalPhysicalSide||side is FixedSide{IntervalDefinition:not null}||side is FloatingSide{IntervalDefinition:not null};
        if(native&&definition.Grids.Any(grid=>grid.Clock!=side.Terms.Delivery.Clock))
            throw Error("Every retained native output and auxiliary grid must bind the exact clock of this side's pinned delivery contract.");
        if(obligations.Any(n=>(definition.Rows[n].Grid is not null)!=native))throw Error("The retained delivery window kind must match the side's exact contract family.");
        RequireObligations(obligations,native);
        if(!native)
        {
            if(obligations.Any(n=>definition.Rows[n].Interval is not null))throw Error("A side cannot mix dated and native obligations.");
            dated=Dated([..obligations.OrderBy(n=>definition.Rows[n].PaymentOrdinal)]);NativeRows=[];ReleaseAdmissionIndexes();return;
        }
        nativeTemplates=[..obligations.OrderBy(n=>definition.Grids[definition.Rows[n].Grid!.Value].FirstRowOrdinal).Select(Native)];
        nativeBlocks=[..nativeTemplates.Select(template=>new NativeBlock(template,definition.Grids[template.Obligation.Grid!.Value]))];
        var first=0;
        foreach(var block in nativeBlocks)
        {
            if(block.Grid.FirstRowOrdinal!=first)throw Error("Retained output grids require contiguous stable logical cell ordinals.");
            first=checked(first+block.Grid.CellCount);
        }
        if(nativeBlocks.Zip(nativeBlocks.Skip(1)).Any(pair=>pair.First.Intervals[^1].End>pair.Second.Intervals[0].Start))
            throw Error("Retained output grids must contain distinct ordered nonoverlapping intervals.");
        nativeMonths=nativeBlocks.GroupBy(block=>block.Grid.Month).ToImmutableDictionary(group=>group.Key,group=>group.ToImmutableArray());
        NativeRows=new NativeCellRows(nativeBlocks);
        NativeTerms=ReadNativeTerms();RequireNativeQuantities();
        if(NativeTerms.Quantity is AccreditedQuantity)
        {
            CapacityDays=[..definition.Rows.Where(row=>activeQuantityRows.Contains(row.Ordinal)&&row.Operation=="asset_capacity"&&row.Parameters["measure"]=="accredited_capacity_v1")
                .SelectMany(row=>GridIntervals(row.Grid??throw Error("Accredited capacity requires its retained daily grid."))).Distinct().OrderBy(i=>i.Start)];
            var factors=definition.Rows.Where(row=>activeQuantityRows.Contains(row.Ordinal)&&row.Operation=="quantity_transform"&&row.Parameters["implementation"]=="whole_operating_day_entitlement_v1")
                .Select(Decimal).Distinct().ToArray();
            CapacityEntitlementFactor=factors.Length==1?factors[0]:throw Error("A retained capacity side requires one explicit supported daily entitlement factor.");
        }
        if(side is CertificateDeliverySide)
        {
            var conversions=definition.Rows.Where(row=>activeQuantityRows.Contains(row.Ordinal)&&row.Operation=="quantity_transform"&&row.Parameters["implementation"]=="certificate_per_energy_v1")
                .Select(Decimal).Distinct().ToArray();
            CertificateConversion=conversions.Length==1?conversions[0]:throw Error("A retained certificate side requires one explicit supported conversion.");
        }
        ReleaseAdmissionIndexes();
    }
    // Validation/compilation owns these indexes. Runtime reads the immutable
    // compiled rows and references; retaining the scratch maps multiplies book memory.
    private void ReleaseAdmissionIndexes()
    {
        edges.Clear();edges.TrimExcess();bindings.Clear();bindings.TrimExcess();
        incoming.Clear();incoming.TrimExcess();rowBindings.Clear();rowBindings.TrimExcess();
        outputRoots.Clear();outputRoots.TrimExcess();projectionCandidates.Clear();projectionCandidates.TrimExcess();
        activeQuantityRows.Clear();activeQuantityRows.TrimExcess();
    }
    internal SidePaymentSchedule DatedSchedule()=>dated??throw Error("This retained side has native interval obligations.");
    internal ImmutableArray<IntervalScheduleMonth> Months()
    {
        var result=ImmutableArray.CreateBuilder<IntervalScheduleMonth>();
        foreach(var group in nativeBlocks.GroupBy(block=>block.Grid.Month))
        {
            var first=group.First();var obligation=first.Template.Obligation;
            if(group.Any(block=>block.Template.Obligation.ValueAt!=obligation.ValueAt||block.Template.Obligation.PaymentOrdinal!=obligation.PaymentOrdinal||block.Template.Obligation.Currency!=obligation.Currency))
                throw Error("A retained native month requires one exact payment identity.");
            var intervals=group.Count()==1?first.Intervals:group.SelectMany(block=>block.Intervals).ToImmutableArray();
            result.Add(new(group.Key,first.Grid.FirstRowOrdinal!.Value,intervals,new(side.Id,obligation.PaymentOrdinal!.Value,obligation.ValueAt!.Value,obligation.Currency!.Value)));
        }
        return result.ToImmutable();
    }
    private SidePaymentSchedule Dated(int[] obligations)
    {
        var payments=ImmutableArray.CreateBuilder<ScheduleRow>();
        foreach(var root in obligations)
        {
            var guard=Definition.Rows[root];var amount=Definition.Rows[One(root,"amount")];Require(amount,"amount");
            var quantity=Quantity(One(amount.Ordinal,"quantity"));var average=Definition.Rows[One(amount.Ordinal,"price")];Require(average,"weighted_price");
            var applications=Many(average.Ordinal,"input").Select(n=>Definition.Rows[n]).ToArray();
            if(applications.Any(row=>row.Operation!="price_application"||row.ObservationFrom is null||row.ObservationTo is null||row.ObservationFrom>=row.ObservationTo))
                throw Error("A dated price average requires retained observation applications and exact target windows.");
            var totalScaledWeight=ScheduleOperations.Integer(average.Parameters["totalScaledWeight"]);
            if(totalScaledWeight.Sign<=0)throw Error("A retained average requires its positive exact original weight scale.");
            var weights=applications.Select(row=>
            {
                var(n,d)=Ratio(row);var scaled=BigInteger.DivRem(n*totalScaledWeight,d,out var remainder);
                return remainder.IsZero?ExactDecimal.FromScaled(scaled):throw Error("The retained coefficient and original weight scale must resolve exactly.");
            }).ToArray();
            var observationNodes=applications.Select(row=>One(row.Ordinal,"input")).Distinct().ToArray();
            var terms=observationNodes.Select(node=>PriceTerm(node,ExactDecimal.FromScaled(applications.Select((row,n)=>(row,n))
                .Where(p=>One(p.row.Ordinal,"input")==node).Aggregate(BigInteger.Zero,(sum,p)=>sum+weights[p.n].ScaledValue)))).ToImmutableArray();
            var from=guard.DeliveryFrom??throw Error("A dated obligation requires its retained delivery window.");
            var to=guard.DeliveryTo??throw Error("A dated obligation requires its retained delivery window.");
            var payment=guard.ValueAt??throw Error("A dated obligation requires its retained value date.");
            var mapping=applications.Select(row=>row.Parameters["mapping"]).Distinct().ToArray();if(mapping.Length!=1)throw Error("Applications require one declared mapping implementation.");
            var rule=PaymentRule(guard.Parameters["paymentRule"]);
            var unadjusted=guard.Parameters["unadjustedPayment"];
            if(rule is null&&unadjusted!="none")throw Error("An explicit payment instant cannot claim a generated unadjusted date.");
            var generated=rule is null?null:new GeneratedPaymentDate(from,to,
                DateOnly.TryParseExact(unadjusted,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var date)?date:throw Error("The retained generated date requires its exact original unadjusted day."),DateOnly.FromDateTime(payment.UtcDateTime));
            var averagingText=guard.Parameters["averaging"];
            var averaging=averagingText=="none"?(FloatingAveraging?)null:
                Enum.TryParse<FloatingAveraging>(averagingText,out var convention)&&Enum.IsDefined(convention)?convention:throw Error("The retained averaging convention is unsupported.");
            var fixingText=guard.Parameters["firstFixingAt"];
            var firstFixing=fixingText=="none"?(DateTimeOffset?)null:DateTimeOffset.TryParse(fixingText,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out var instant)&&TradingInstant.IsSupported(instant)?instant:
                throw Error("The retained first-fixing marker requires an exact supported UTC instant.");
            var unit=side switch{FixedSide fixedSide=>fixedSide.UnitQuantity,FloatingSide floating=>floating.UnitQuantity,_=>throw Error("The retained dated side family is unsupported.")};
            if(terms.Any(term=>(term.FixedPrice?.Unit??PriceUnit.FromQuote(term.Lookup!.Quote))!=Unit(average))||
                applications.Any(application=>Unit(application)!=Unit(average))||Unit(average).Currency!=guard.Currency||Unit(average).Quantity!=unit.Specification)
                throw Error("A retained dated average requires one exact quote unit matching its amount currency and quantity.");
            SwapObservationCalendar? observationCalendar=null;
            if(side is FloatingSide observed)
            {
                // Geometry and weights remain the stored DAG's. Calendar provenance
                // belongs to its exact immutable side period, already identity-checked.
                var periods=observed.Periods.Where(period=>period.DeliveryFrom==from&&period.DeliveryTo==to).ToArray();
                if(periods.Length!=1)throw Error("A retained dated obligation requires its exact side period for observation-calendar provenance.");
                observationCalendar=periods[0].Calendar;
            }
            var primitive=new ScheduleRow(side.Id,from,to,payment,guard.Currency!.Value,unit,firstFixing,averaging,observationCalendar,terms,
                side is FixedSide?FixedSide.Implementation:terms.Any(term=>term.Lookup?.Fixing is not null)?FloatingSide.IndexedImplementation:FloatingSide.Implementation,
                [..applications.Select((row,n)=>new FloatingObservationApplication(row.ObservationFrom!.Value,row.ObservationTo!.Value,
                    Array.IndexOf(observationNodes,One(row.Ordinal,"input")),weights[n],mapping[0]))],mapping[0],generated,rule);
            payments.Add(new ScheduleRow(primitive,side.Id,guard.PaymentOrdinal!.Value,quantity).WithRetainedReference(Definition,root));
        }
        var first=payments[0];return new(side.Id,first.Currency,first.UnitQuantity,side is FixedSide?FixedSide.Implementation:FloatingSide.Implementation,payments.ToImmutable());
    }
    private SchedulePriceTerm PriceTerm(int node,ExactDecimal weight)
    {
        var row=Definition.Rows[node];
        if(row.Operation=="constant_price")return new(weight,new RetainedFixedPrice(Price(row)),row.DeliveryFrom??DateOnly.MinValue);
        UnitPrice? spread=null;
        if(row.Operation=="price_add")
        {
            var input=Many(node,"input");if(input.Length!=2)throw Error("A retained price addition requires two explicit inputs.");
            spread=Price(Definition.Rows[input[1]]).ToUnitPriceExact();node=input[0];row=Definition.Rows[node];
        }
        Require(row,"price_lookup");var lookup=Lookup(row);
        return new(weight,null,lookup,spread);
    }
    private IndexPriceLookup Lookup(ScheduleDefinitionRow row)
    {
        if(side is not FloatingSide floating)throw Error("Only a floating side may bind retained dated market observations.");
        if(Unit(row)!=PriceUnit.FromQuote(floating.Quote))throw Error("A retained observation must retain its exact selected quote unit.");
        var market=Binding(row.Ordinal,"market");var index=Binding(row.Ordinal,"index");var quote=Binding(row.Ordinal,"quote");
        var candidates=market.Market is {} request&&projectionCandidates.TryGetValue(request,out var matches)?matches:[];
        var selected=candidates
            .Where(p=>p.projection.Market==market.Market&&floating.Index==index.Reference&&floating.Quote.Key==quote.Reference&&
                p.projection.Contract.DeliveryFrom==row.ObservationFrom&&p.projection.Contract.DeliveryTo==row.ObservationTo&&
                DefinitionIdentity.For(floating.Settlement).Canonical==row.Parameters["selection"])
            .Where(p=>!bindings.TryGetValue((row.Ordinal,"fixing"),out var fixing)?p.period.Fixings.IsEmpty:
                !p.period.Fixings.IsEmpty&&p.period.Fixings[p.n].Series==fixing.Series&&p.period.Fixings[p.n].Source==fixing.Reference&&p.period.Fixings[p.n].Implementation==fixing.Coordinate)
            .Where(p=>!bindings.TryGetValue((row.Ordinal,"projection"),out var projection)?p.period.CustomProjection is null:
                p.period.CustomProjection is {} custom&&custom.CatalogId==projection.EntityCatalog&&custom.CurveId==projection.Entity&&custom.Version==projection.Version&&custom.OutputNode==projection.Coordinate&&custom.Position==projection.Position)
            .ToArray();
        if(selected.Length==0)throw Error("A retained observation must bind exact immutable contract, quote, index, fixing and projection definitions.");
        var value=selected[0];return new(floating.Settlement,floating.Quote,value.projection.Market,value.projection.Contract,value.projection.AveragingQuantity,
            value.period.Fixings.IsEmpty?null:value.period.Fixings[value.n],value.period.CustomProjection);
    }
    private ScheduleDefinitionRoot OutputRoot(string name,string measure)=>
        outputRoots.TryGetValue(name,out var root)&&root.Measure==measure?root:
            throw Error("A retained obligation is missing its exact named output root.");
    private PaymentRule? PaymentRule(string identity)
    {
        if(identity=="explicit_payment_date_v1")return null;
        var rules=side switch{FixedSide fixedSide=>fixedSide.Periods.Select(p=>p.PaymentSchedule),FloatingSide floating=>floating.Periods.Select(p=>p.PaymentSchedule),_=>[]};
        return rules.OfType<PaymentRule>().FirstOrDefault(rule=>DefinitionIdentity.CanonicalTerm(rule)==identity)
            ??throw Error("A retained payment convention must bind the side's exact immutable rule.");
    }
    private RetainedNativeRow Native(int root)
    {
        var obligation=Definition.Rows[root];var number=NativeOrdinal(obligation);
        var quantity=OutputRoot("quantity/"+number,"schedule_quantity");
        var cash=Definition.Rows[One(root,"amount")];var components=cash.Operation=="sum"?Many(cash.Ordinal,"input"):[cash.Ordinal];
        var prices=ImmutableArray.CreateBuilder<RetainedNativePrice>();ExactUnitPrice? fixedPrice=null;
        foreach(var component in components)
        {
            var amount=Definition.Rows[component];Require(amount,"amount");var price=Definition.Rows[One(component,"price")];
            if(price.Operation=="constant_price")
            {if(components.Length!=1)throw Error("A fixed native row requires one exact price input.");fixedPrice=Price(price);}
            else prices.Add(NativePrice(price));
        }
        if((side is FixedSide)!=(fixedPrice is not null))
            throw Error("A retained fixed payment requires a constant price; other native side families require their explicit source lookup.");
        var quantityParts=QuantityParts(quantity.Row).Order(StringComparer.Ordinal);
        var amountParts=components.SelectMany(component=>QuantityParts(One(component,"quantity"))).Order(StringComparer.Ordinal);
        if(!quantityParts.SequenceEqual(amountParts))throw Error("Retained amount components must consume the declared complete quantity expression exactly once.");
        return new(root,obligation,quantity.Row,fixedPrice,prices.ToImmutable());
    }
    private IEnumerable<string> QuantityParts(int row)
    {
        var value=Definition.Rows[row];
        if(value.Operation=="quantity_add")return Many(row,"input").SelectMany(QuantityParts);
        string Body(int input)=>string.Join("+",QuantityParts(input).Order(StringComparer.Ordinal));
        var body=value.Operation switch
        {
            "profile_quantity"=>"profile:"+value.Unit,
            "quantity_sum_cells"=>"cells("+Body(One(row,"input"))+")",
            "quantity_literal"=>$"literal:{value.Unit}:{value.Parameters["numerator"]}/{value.Parameters["denominator"]}",
            "quantity_input"=>"input:"+value.Unit+":"+value.Parameters["stage"]+":"+System.Text.Json.JsonSerializer.Serialize(rowBindings[row].Select(b=>b with{Row=0}),ScheduleDefinition.Json),
            "asset_capacity"=>"capacity:"+value.Unit+":"+value.Parameters["measure"]+":"+value.Interval+":"+(Binding(row,"asset") with{Row=0}),
            "quantity_normalize"=>"normalize("+Body(One(row,"input"))+";"+Body(One(row,"basis"))+")",
            "quantity_transform"=>"transform:"+value.Unit+":"+value.Parameters["implementation"]+":"+value.Parameters["numerator"]+"/"+value.Parameters["denominator"]+"("+Body(One(row,"input"))+")",
            _=>throw Error("The retained native quantity operation is outside the supported closed adapter.")
        };
        return [body];
    }
    private RetainedNativePrice NativePrice(ScheduleDefinitionRow row)
    {
        var outputUnit=Unit(row);
        var multiplier=ExactDecimal.Parse("1");var adder=new UnitPrice(default,Unit(row));
        if(row.Operation=="price_add")
        {
            if(side is not FloatingSide)throw Error("Physical retained lookups use exact canonical quote conversion inside their source binding; an extra affine price operation is unsupported.");
            var input=Many(row.Ordinal,"input");adder=Price(Definition.Rows[input[1]]).ToUnitPriceExact();
            var scale=Definition.Rows[input[0]];Require(scale,"price_scale");multiplier=Decimal(scale);row=Definition.Rows[One(scale.Ordinal,"input")];
            if(adder.Unit!=outputUnit||Unit(scale)!=outputUnit||Unit(row)!=outputUnit)throw Error("A retained affine price expression must preserve one exact quote unit.");
        }
        Require(row,"price_lookup");
        if(bindings.TryGetValue((row.Ordinal,"curve"),out var curve))
        {
            if(side is not FloatingSide floating||curve.Definitions is not {} revision||curve.Code is not {} code)
                throw Error("A retained indexed row requires an exact curve binding.");
            var selection=floating.Price.Settlement;
            if(row.Parameters["selection"]!=(selection is null?"interval_attribute_lookup_v1":DefinitionIdentity.For(selection).Canonical))
                throw Error("A retained curve row must bind its exact settlement selection.");
            if(selection is not null&&Binding(row.Ordinal,"index").Reference!=selection.SelectedIndex)
                throw Error("A retained period curve row must bind the explicitly selected exact settlement index.");
            return new(new(revision,code),null,null,null,multiplier,adder,selection,outputUnit);
        }
        if(outputUnit.Currency!=side.Terms.Currency||outputUnit.Quantity!=side.Terms.ScheduleQuantity||outputUnit.Multiplier!=PpaErrors.One)
            throw Error("A physical retained price lookup explicitly returns its exact currency and native quantity in canonical quote units.");
        var location=Binding(row.Ordinal,"location");
        var(kind,attribute)=side switch
        {
            PowerPhysicalSide=>("location","energy"),CertificateDeliverySide=>("quantity_source","certificate"),
            CapacityDeliverySide=>("asset","capacity"),_=>throw Error("This native side family requires an explicit curve price lookup.")
        };
        if(row.Parameters["selection"]!="interval_attribute_lookup_v1"||location.Kind!=kind||location.Coordinate!=attribute)
            throw Error("A retained physical price lookup must select its supported interval policy and exact family attribute and source kind.");
        return location.Kind switch
        {
            "location"=>new(null,location.Reference,null,null,multiplier,adder,null,outputUnit),
            "quantity_source" when location.Definitions is {} revision&&location.Code is {} code=>new(null,null,new(revision,code),null,multiplier,adder,null,outputUnit),
            "asset" when location.Definitions is {} revision&&location.Code is {} code=>new(null,null,null,new(revision,code),multiplier,adder,null,outputUnit),
            _=>throw Error("A retained physical row requires an exact location, asset or quantity-source binding.")
        };
    }
    private PpaSideTerms ReadNativeTerms()
    {
        var terms=side.Terms;PpaQuantity quantity;
        var reachable=new HashSet<int>();var pending=new Stack<int>(nativeTemplates.Select(row=>row.Quantity));
        while(pending.TryPop(out var node)){if(!reachable.Add(node))continue;if(incoming.TryGetValue(node,out var selected))foreach(var input in selected)pending.Push(input);}
        activeQuantityRows.UnionWith(reachable);
        var quantityRows=reachable.Select(n=>Definition.Rows[n]).ToArray();
        foreach(var row in nativeTemplates)
        {
            var parts=QuantityParts(row.Quantity).ToArray();
            if(parts.Distinct(StringComparer.Ordinal).Count()!=parts.Length)throw Error("A retained native quantity cannot count the same declared contribution twice.");
        }
        var normalizers=quantityRows.Where(row=>row.Operation=="quantity_normalize").ToArray();
        var denominatorAssets=normalizers.Select(row=>Leaves(One(row.Ordinal,"basis")).Select(n=>
        {
            var capacity=Definition.Rows[n];Require(capacity,"asset_capacity");
            if(capacity.Parameters["measure"]!="capacity_basis_v1")throw Error("A retained normalization denominator requires explicit capacity-in-force sources.");
            return Asset(Binding(n,"asset"));
        }).OrderBy(asset=>asset.Code,StringComparer.Ordinal).ToArray()).ToArray();
        if(denominatorAssets.Any(set=>set.Distinct().Count()!=set.Length)||denominatorAssets.Skip(1).Any(set=>!set.SequenceEqual(denominatorAssets[0])))
            throw Error("Every retained quantity normalization requires the same exact complete asset denominator.");
        var assets=denominatorAssets.Length==0?ImmutableArray<AssetKey>.Empty:denominatorAssets[0].ToImmutableArray();
        var basis=normalizers.SelectMany(row=>Leaves(One(row.Ordinal,"basis"))).Select(n=>Definition.Rows[n].Unit!.Value).Distinct().ToArray();
        foreach(var normalizer in normalizers)
        {
            var raw=Leaves(One(normalizer.Ordinal,"input")).Select(n=>Definition.Rows[n]).ToArray();
            if(raw.Any(row=>row.Operation is not("quantity_input" or "asset_capacity"))||raw.Select(row=>
                row.Operation=="quantity_input"?(object)Source(Binding(row.Ordinal,"source")):Asset(Binding(row.Ordinal,"asset"))).Distinct().Count()!=raw.Length)
                throw Error("A retained normalization requires distinct explicit source contributions.");
        }
        var sourceRows=quantityRows.Where(row=>row.Operation=="quantity_input"&&bindings.ContainsKey((row.Ordinal,"source"))).ToArray();
        var normalization=assets.IsEmpty?terms.Normalization:
            basis.Length==1?new PpaNormalization(assets,basis[0],PpaNormalizationPolicy.SumCapacityInForce):throw Error("A retained normalization requires one exact capacity basis.");
        var ports=Definition.Ports.Select(port=>terms.InputPorts.SingleOrDefault(p=>p.Slot==port.Slot&&p.Quantity==port.Unit&&
            DefinitionIdentity.For(p.Schedule).Canonical==port.ShapeIdentity)??throw Error("A retained input port must bind an exact declared quantity type and structural schedule.")).ToImmutableArray();
        if(sourceRows.Length>0)
        {
            var stages=sourceRows.Select(row=>row.Parameters["stage"]).Distinct().ToArray();var units=sourceRows.Select(row=>row.Unit!.Value).Distinct().ToArray();
            if(stages.Length!=1||units.Length!=1||!Enum.TryParse<QuantityStage>(stages[0],out var stage)||!Enum.IsDefined(stage))throw Error("A retained volume side requires one supported explicit source stage and energy unit.");
            quantity=new VolumeQuantity([..sourceRows.Select(row=>Source(Binding(row.Ordinal,"source"))).Distinct()],stage,units[0]);
        }
        else if(!ports.IsEmpty)
        {
            var declared=Definition.Ports.Select(port=>port.Name).Order(StringComparer.Ordinal).ToArray();
            foreach(var row in nativeTemplates)
                if(!Leaves(row.Quantity).Select(n=>Binding(n,"quantity").Coordinate!).Order(StringComparer.Ordinal).SequenceEqual(declared))
                    throw Error("Every retained linked quantity must consume each declared typed port exactly once.");
            quantity=new SlotQuantity([..ports.Select(p=>p.Slot)]);
        }
        else if(quantityRows.Any(row=>row.Operation=="asset_capacity"&&row.Parameters["measure"]=="accredited_capacity_v1"))
            quantity=new AccreditedQuantity([..quantityRows.Where(row=>row.Operation=="asset_capacity"&&row.Parameters["measure"]=="accredited_capacity_v1")
                .Select(row=>Asset(Binding(row.Ordinal,"asset"))).Distinct()]);
        else if(terms.Quantity is ProfileQuantity profile)quantity=profile;
        else throw Error("The retained native quantity topology is unsupported.");
        return terms with{Quantity=quantity,Normalization=normalization,InputPorts=ports};
    }
    private IEnumerable<int> Leaves(int node)=>Definition.Rows[node].Operation=="quantity_add"?Many(node,"input").SelectMany(Leaves):[node];
    internal static int NativeOrdinal(ScheduleDefinitionRow row)=>row.Grid??
        throw Error("A retained interval obligation requires its explicit native grid.");
    private static AssetKey Asset(ScheduleDefinitionBinding binding)=>binding.Kind=="asset"&&binding.Definitions is {} revision&&binding.Code is {} code?new(revision,code):throw Error("Select an exact retained asset binding.");
    private static VolumeSourceKey Source(ScheduleDefinitionBinding binding)=>binding.Kind=="quantity_source"&&binding.Definitions is {} revision&&binding.Code is {} code?new(revision,code):throw Error("Select an exact retained volume source binding.");
    private ScheduleDefinitionBinding Binding(int row,string port)=>bindings.TryGetValue((row,port),out var value)?value:throw Error("A retained source binding is missing.");
    private int[] Many(int row,string port)=>edges.TryGetValue((row,port),out var value)?value:throw Error("A retained operation input is missing.");
    private int One(int row,string port)=>Many(row,port) is [var value]?value:throw Error("The retained operation requires exactly one input on this port.");
    private static void Require(ScheduleDefinitionRow row,string operation){if(row.Operation!=operation)throw Error("Unsupported retained operation topology: expected "+operation+".");}
    private ExactQuantity Quantity(int row){var value=Definition.Rows[row];Require(value,"quantity_literal");var(n,d)=Ratio(value);return new(n,d,value.Unit!.Value);}
    private static PriceUnit Unit(ScheduleDefinitionRow row)=>new(row.Currency!.Value,row.Unit!.Value,ExactDecimal.Parse(row.QuoteMultiplier!));
    private static ExactUnitPrice Price(ScheduleDefinitionRow row){Require(row,"constant_price");var(n,d)=Ratio(row);return ExactUnitPrice.FromFraction(n,d,Unit(row));}
    private static (BigInteger N,BigInteger D) Ratio(ScheduleDefinitionRow row)=>
        (BigInteger.Parse(row.Parameters["numerator"],CultureInfo.InvariantCulture),BigInteger.Parse(row.Parameters["denominator"],CultureInfo.InvariantCulture));
    private static ExactDecimal Decimal(ScheduleDefinitionRow row){var(n,d)=Ratio(row);var value=BigInteger.DivRem(n*ExactDecimal.WorkingScale,d,out var remainder);
        return remainder.IsZero?ExactDecimal.FromScaled(value):throw Error("This retained contract requires an exact working-decimal affine factor.");}
    private static MarketDataException Error(string message)=>RetainedSideSchedules.Error(message);
}
