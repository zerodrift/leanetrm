using System.Collections.Immutable;
using System.Numerics;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Power;

namespace OpenEtrm.MarketData.Trading;

public static partial class ScheduleDefinitionCompiler
{
    public static ScheduleDefinition Generate(Side side,CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(side);
        cancellationToken.ThrowIfCancellationRequested();
        if(side.RetainedSchedule is {} retained)return retained;
        var builder=new Builder(side,cancellationToken);
        switch(side.GenerateSchedule())
        {
            case SidePaymentSchedule dated:builder.Dated(dated);break;
            case IntervalSideSchedule native:builder.Native(native);break;
            default:throw ScheduleDefinition.Error("The selected Side has no supported complete schedule generator.");
        }
        return builder.Finish();
    }
    private sealed class Builder(Side side,CancellationToken cancellationToken)
    {
        private readonly List<ScheduleDefinitionRow> rows=[];
        private readonly List<ScheduleDefinitionEdge> edges=[];
        private readonly List<ScheduleDefinitionBinding> bindings=[];
        private readonly List<ScheduleDefinitionRoot> roots=[];
        private readonly List<ScheduleDefinitionPort> ports=[];
        private readonly List<ScheduleDefinitionGrid> grids=[];
        private int? activeGrid;
        private readonly Dictionary<int,List<ScheduleDefinitionEdge>> inputs=[];
        private readonly Dictionary<int,List<ScheduleDefinitionBinding>> sources=[];
        private readonly List<int> contributions=[];
        private readonly Dictionary<ExactUnitPrice,int> prices=[];
        private readonly Dictionary<DateOnly,ExactUnitPrice> fixedDays=[];
        private readonly Dictionary<PaymentRule,string> paymentRules=[];
        private readonly Dictionary<PriceSettlementSelection,string> selections=[];
        // Reuse immutable contractual metadata within this compilation. Every
        // logical row still serializes its complete parameters into the artifact.
        private readonly Dictionary<(string Key,string Value)[],ImmutableSortedDictionary<string,string>> parameters=new(new ParameterComparer());
        private static readonly BigInteger Scale=ExactDecimal.WorkingScale;
        private ImmutableSortedDictionary<string,string> P(params (string Key,string Value)[] values)
        {
            if(parameters.TryGetValue(values,out var existing))return existing;
            var result=values.ToImmutableSortedDictionary(p=>p.Key,p=>p.Value,StringComparer.Ordinal);
            parameters.Add(values,result);return result;
        }
        private sealed class ParameterComparer:IEqualityComparer<(string Key,string Value)[]>
        {
            public bool Equals((string Key,string Value)[]? x,(string Key,string Value)[]? y)=>
                ReferenceEquals(x,y)||x is not null&&y is not null&&x.AsSpan().SequenceEqual(y);
            public int GetHashCode((string Key,string Value)[] values)
            {
                var hash=new HashCode();
                foreach(var (key,value) in values){hash.Add(key,StringComparer.Ordinal);hash.Add(value,StringComparer.Ordinal);}
                return hash.ToHashCode();
            }
        }
        private string PaymentIdentity(PaymentRule? rule)
        {
            if(rule is null)return "explicit_payment_date_v1";
            if(paymentRules.TryGetValue(rule,out var existing))return existing;
            var identity=DefinitionIdentity.CanonicalTerm(rule);paymentRules.Add(rule,identity);return identity;
        }
        private string SelectionIdentity(PriceSettlementSelection selection)
        {
            if(selections.TryGetValue(selection,out var existing))return existing;
            var identity=DefinitionIdentity.For(selection).Canonical;selections.Add(selection,identity);return identity;
        }
        private ImmutableSortedDictionary<string,string> Ratio(BigInteger n,BigInteger d,params (string Key,string Value)[] extra)
        {
            var gcd=BigInteger.GreatestCommonDivisor(BigInteger.Abs(n),d);n/=gcd;d/=gcd;
            return P([("numerator",n.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("denominator",d.ToString(System.Globalization.CultureInfo.InvariantCulture)),..extra]);
        }
        private int Row(string key,string role,string op,string kind,ScheduleRow? plan,ReferenceKey? unit=null,
            ReferenceKey? currency=null,string? multiplier=null,ImmutableSortedDictionary<string,string>? parameters=null,
            DateOnly? observationFrom=null,DateOnly? observationTo=null,string? occurrence=null,string? digest=null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var p=plan;var window=p?.DeliveryWindow;var dated=window as DatedScheduleWindow;
            var row=new ScheduleDefinitionRow(rows.Count,key,role,op,1,kind,unit,currency,multiplier,
                activeGrid is null?dated?.From:null,activeGrid is null?dated?.To:null,activeGrid is null?p?.Interval:null,observationFrom,observationTo,p?.PaymentAt,p?.Key.Ordinal,occurrence,digest,parameters??P()){Grid=activeGrid};
            rows.Add(row);return row.Ordinal;
        }
        private void Edge(int target,string port,int source,int ordinal=0)
        {var edge=new ScheduleDefinitionEdge(target,port,ordinal,source);edges.Add(edge);if(!inputs.TryGetValue(target,out var list))inputs.Add(target,list=[]);list.Add(edge);}
        private void Binding(int row,string port,string kind,ReferenceKey? reference=null,CurveRequest? market=null,
            SeriesKey? series=null,Guid? entity=null,int? version=null,string? coordinate=null,int? position=null,
            DefinitionRevision? definitions=null,string? code=null,Guid? entityCatalog=null)
        {var binding=new ScheduleDefinitionBinding(row,port,kind,reference,market,series,entity,version,coordinate,position,definitions,code,entityCatalog);
            bindings.Add(binding);if(!sources.TryGetValue(row,out var list))sources.Add(row,list=[]);list.Add(binding);}
        private int Price(string key,ExactUnitPrice price,ScheduleRow? plan)
        {
            if(prices.TryGetValue(price,out var existing))return existing;
            var grid=activeGrid;if(grid is not null)activeGrid=null;
            var row=Row(key,"source","constant_price","price",grid is null?plan:null,price.Unit.Quantity,price.Unit.Currency,
                price.Unit.Multiplier.ToString(),Ratio(price.NumeratorValue,price.DenominatorValue));
            activeGrid=grid;
            prices.Add(price,row);return row;
        }
        private int Quantity(string key,ExactQuantity q,ScheduleRow? plan)
            =>Row(key,"source","quantity_literal","quantity",plan,q.Specification,parameters:Ratio(q.NumeratorValue,q.DenominatorValue));
        private ExactUnitPrice FixedAt(DateOnly day)
        {
            if(fixedDays.TryGetValue(day,out var price))return price;
            price=((FixedSide)side).Price.At(day);fixedDays.Add(day,price);return price;
        }
        private int AddQuantity(string key,IReadOnlyList<int> inputs,ReferenceKey unit,ScheduleRow plan)
        {
            if(inputs.Count==0)throw ScheduleDefinition.Error("An explicit quantity source cannot be empty.");
            if(inputs.Count==1)return inputs[0];var row=Row(key,"intermediate","quantity_add","quantity",plan,unit);
            for(var n=0;n<inputs.Count;n++)Edge(row,"input",inputs[n],n);return row;
        }
        private int Transform(string key,int input,ReferenceKey unit,ScheduleRow plan,ExactDecimal factor,string implementation)
        {
            var row=Row(key,"intermediate","quantity_transform","quantity",plan,unit,
                parameters:Ratio(factor.ScaledValue,Scale,("implementation",implementation)));
            Edge(row,"input",input);return row;
        }
        private int Money(string key,int quantity,int price,ScheduleRow plan)
        {var row=Row(key,"contribution","amount","money",plan,currency:plan.Currency);Edge(row,"quantity",quantity);Edge(row,"price",price);return row;}
        private int Complete(string key,int cash,ScheduleRow plan,bool native)
        {
            var discount=Row(key+"/discount","source","discount","factor",plan,parameters:P(("policy","context_required_v1")));
            var pv=Row(key+"/discounted","intermediate","pv","money",plan,currency:plan.Currency);Edge(pv,"amount",cash);Edge(pv,"discount",discount);
            var occurrence=native?null:Digest(new{Kind="dated_obligation_v1",plan.DeliveryWindow,plan.PaymentAt,
                Rule=PaymentIdentity(plan.PaymentConvention)});
            var terms=native?null:Digest(new{plan.DeliveryWindow,plan.Currency,Quantity=Closure(quantity:cash)});
            var guard=Row(key+"/value","root","output_guard","money",plan,currency:plan.Currency,
                parameters:P(("measure",native?"value_excluding_overdue_v1":"present_value_v1"),
                    ("lifecycle",native?side switch {CertificateDeliverySide=>"certificate_holding_v1",FixedSide or FloatingSide=>"interval_payment_v1",_=>"interval_delivery_v1"}:"undue_payment_v1"),
                    ("pricePolicy","selected_context_v1"),
                    ("averaging",native?"none":plan.Averaging?.ToString()??"none"),
                    ("firstFixingAt",native?"none":plan.FirstFixingAt?.ToString("O",System.Globalization.CultureInfo.InvariantCulture)??"none"),
                    ("paymentRule",PaymentIdentity(plan.PaymentConvention)),
                    ("unadjustedPayment",plan.GeneratedDate?.Unadjusted.ToString("yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture)??"none")),occurrence:occurrence,digest:terms);
            Edge(guard,"amount",cash);Edge(guard,"discounted",pv);
            if(native)
            {
                var selected=activeGrid;activeGrid=null;
                var total=Row(key+"/total","intermediate","sum_cells","money",null,currency:plan.Currency);
                Edge(total,"input",guard);contributions.Add(total);activeGrid=selected;
            }
            else contributions.Add(guard);
            roots.Add(new(key+"/cash",cash,"contractual_amount"));roots.Add(new(key+"/pv",guard,"contribution_pv"));
            // Only price literals are shared with a later obligation. The full
            // rows/edges remain retained; digest traversal scratch can be reused.
            inputs.Clear();sources.Clear();termsHashes.Clear();return guard;
        }
        private readonly Dictionary<int,string> termsHashes=[];
        private readonly Dictionary<int,string> priceHashes=[];
        private string Closure(int quantity)
        {
            if(termsHashes.TryGetValue(quantity,out var retained))return retained;
            if(priceHashes.TryGetValue(quantity,out retained))return retained;
            // Ordered semantic edges refer to child digests, never global or
            // locally renumbered row positions. Sharing a literal with an earlier
            // obligation therefore cannot change this obligation's own terms.
            var row=rows[quantity];
            var value=Digest(new{row.Operation,row.Version,row.OutputKind,row.Unit,row.Currency,row.QuoteMultiplier,
                Parameters=row.Operation=="weighted_price"?P():row.Parameters,row.ObservationFrom,row.ObservationTo,
                Inputs=inputs.TryGetValue(quantity,out var input)?input.OrderBy(e=>e.Port,StringComparer.Ordinal).ThenBy(e=>e.Ordinal)
                    .Select(e=>new{e.Port,e.Ordinal,Source=Closure(e.Source)}).ToArray():[],
                Bindings=sources.TryGetValue(quantity,out var source)?source.OrderBy(b=>b.Port,StringComparer.Ordinal)
                    .Select(b=>b with{Row=0}).ToArray():[]});
            if(row.Operation=="constant_price")priceHashes.Add(quantity,value);
            else termsHashes.Add(quantity,value);
            return value;
        }
        public void Dated(SidePaymentSchedule schedule)
        {
            foreach(var plan in schedule.Payments)
            {
                var prefix="payment/"+plan.Ordinal;var quantity=Quantity(prefix+"/quantity",plan.AllocatedQuantity.Scale(plan.UnitQuantity.Amount),plan);
                var observations=new int[plan.Prices.Length];
                for(var n=0;n<plan.Prices.Length;n++)
                {
                    var term=plan.Prices[n];var key=prefix+"/observation/"+n;int price;
                    if(term.FixedPrice is {} fixedPrice)price=Price(key,fixedPrice.At(term.FixedPriceDate??DateOnly.MinValue),plan);
                    else
                    {
                        var lookup=term.Lookup!;var unit=PriceUnit.FromQuote(lookup.Quote);
                        price=Row(key,"source","price_lookup","price",plan,unit.Quantity,unit.Currency,unit.Multiplier.ToString(),
                            P(("selection",SelectionIdentity(lookup.Settlement))),lookup.Contract.DeliveryFrom,lookup.Contract.DeliveryTo);
                        Binding(price,"market","contract_price",market:lookup.Market);
                        Binding(price,"index","index",reference:lookup.Index);Binding(price,"quote","quote",reference:lookup.Quote.Key);
                        if(lookup.Fixing is {} fixing)Binding(price,"fixing","index_fixing",reference:fixing.Source,series:fixing.Series,
                            coordinate:fixing.Implementation);
                        if(lookup.CustomProjection is {} custom)Binding(price,"projection","custom_output",market:lookup.Market,
                            entity:custom.CurveId,version:custom.Version,coordinate:custom.OutputNode,position:custom.Position,entityCatalog:custom.CatalogId);
                    }
                    if(term.Spread is {} spread)
                    {
                        var adder=Price(key+"/adder",ExactUnitPrice.FromPrice(spread),plan);
                        var add=Row(key+"/all-in","intermediate","price_add","price",plan,spread.Unit.Quantity,spread.Unit.Currency,spread.Unit.Multiplier.ToString());
                        Edge(add,"input",price);Edge(add,"input",adder,1);price=add;
                    }
                    observations[n]=price;
                }
                var total=plan.Prices.Aggregate(BigInteger.Zero,(sum,p)=>sum+p.Weight.ScaledValue);
                var applications=new List<int>();
                for(var n=0;n<plan.Applications.Length;n++)
                {
                    var application=plan.Applications[n];var price=rows[observations[application.PriceOrdinal]];
                    var row=Row(prefix+"/application/"+n,"intermediate","price_application","price",plan,
                        price.Unit,price.Currency,price.QuoteMultiplier,Ratio(application.Weight.ScaledValue,total,("mapping",application.MappingRule)),application.TargetFrom,application.TargetTo);
                    Edge(row,"input",observations[application.PriceOrdinal]);applications.Add(row);
                }
                var priceRow=rows[observations[0]];
                var average=Row(prefix+"/average","intermediate","weighted_price","price",plan,priceRow.Unit,priceRow.Currency,priceRow.QuoteMultiplier,
                    P(("totalScaledWeight",total.ToString(System.Globalization.CultureInfo.InvariantCulture))));
                for(var n=0;n<applications.Count;n++)Edge(average,"input",applications[n],n);
                Complete(prefix,Money(prefix+"/amount",quantity,average,plan),plan,false);
            }
        }
        public void Native(IntervalSideSchedule schedule)
        {
            var terms=side.Terms;
            foreach(var port in terms.InputPorts)ports.Add(new(Port(port.Slot),port.Slot,"quantity",port.Quantity,DefinitionIdentity.For(port.Schedule).Canonical));
            foreach(var month in schedule.Months)
            {
                var offset=0;
                while(offset<month.Intervals.Length)
                {
                    var first=offset;var fixedPrice=side is FixedSide?FixedAt(month.Intervals[offset].OperatingDate):null;
                    offset++;
                    while(offset<month.Intervals.Length&&(side is not FixedSide||FixedAt(month.Intervals[offset].OperatingDate)==fixedPrice))offset++;
                    var intervals=month.Intervals[first..offset];var ordinal=month.FirstRowOrdinal+first;
                    var grid=ScheduleDefinitionGrid.Capture(grids.Count,"delivery/"+ordinal,terms.Delivery.Clock,month.Month,ordinal,intervals);
                    grids.Add(grid);activeGrid=grid.Ordinal;
                    var plan=schedule.Rows[ordinal];var key="block/"+grid.Ordinal;var quantities=new List<int>();var amounts=new List<int>();
                    if(side is CertificateDeliverySide && terms.Quantity is VolumeQuantity volume)
                    {
                        foreach(var source in volume.Sources)
                        {
                            var component=key+"/source/"+source.Code;
                            var q=NativeQuantity(component,plan,source);quantities.Add(q);
                            var p=NativePrice(component,plan,"certificate",source:source);
                            amounts.Add(Money(component+"/amount",q,p,plan));
                        }
                    }
                    else if(side is CapacityDeliverySide && terms.Quantity is AccreditedQuantity accredited)
                    {
                        foreach(var asset in accredited.Assets)
                        {
                            var component=key+"/asset/"+asset.Code;
                            var q=NativeQuantity(component,plan,selectedAsset:asset);quantities.Add(q);
                            var p=NativePrice(component,plan,"capacity",asset:asset);
                            amounts.Add(Money(component+"/amount",q,p,plan));
                        }
                    }
                    else
                    {
                        var quantity=NativeQuantity(key,plan);quantities.Add(quantity);
                        var price=side is FixedSide?Price(key+"/fixed",fixedPrice!,plan)
                            :NativePrice(key,plan,"energy");
                        amounts.Add(Money(key+"/amount",quantity,price,plan));
                    }
                    var totalQuantity=AddQuantity(key+"/quantity",quantities,terms.ScheduleQuantity,plan);
                    roots.Add(new("quantity/"+grid.Ordinal,totalQuantity,"schedule_quantity"));
                    int cash;
                    if(amounts.Count==1)cash=amounts[0];
                    else
                    {
                        cash=Row(key+"/amount","contribution","sum","money",plan,currency:plan.Currency);
                        for(var n=0;n<amounts.Count;n++)Edge(cash,"input",amounts[n],n);
                    }
                    Complete(key,cash,plan,true);
                }
            }
            activeGrid=null;
        }
        private int NativePrice(string key,ScheduleRow plan,string attribute,VolumeSourceKey? source=null,AssetKey? asset=null)
        {
            var terms=side.Terms;
            var unit=side is FloatingSide floating?floating.Price.Adder.Unit:new PriceUnit(terms.Currency,terms.ScheduleQuantity,ExactDecimal.Parse("1"));
            var price=Row(key+"/price","source","price_lookup","price",plan,unit.Quantity,unit.Currency,unit.Multiplier.ToString(),
                P(("selection",side is FloatingSide selected&&selected.Price.Settlement is {} settlement?SelectionIdentity(settlement):"interval_attribute_lookup_v1")));
            if(side is FloatingSide indexedSide)
            {
                Binding(price,"curve","curve_interval",definitions:indexedSide.Price.Curve.Revision,code:indexedSide.Price.Curve.Code);
                if(indexedSide.Price.Settlement is {} selectedIndex)Binding(price,"index","index",reference:selectedIndex.SelectedIndex);
            }
            else if(source is {} selectedSource)Binding(price,"location","quantity_source",definitions:selectedSource.Revision,code:selectedSource.Code,coordinate:attribute);
            else if(asset is {} selectedAsset)Binding(price,"location","asset",definitions:selectedAsset.Revision,code:selectedAsset.Code,coordinate:attribute);
            else if(side is PowerPhysicalSide physical)Binding(price,"location","location",reference:physical.Location,coordinate:attribute);
            else throw ScheduleDefinition.Error("A physical price lookup requires one explicit source, asset or location binding.");
            if(side is FloatingSide indexed)
            {
                var scale=Row(key+"/price-scale","intermediate","price_scale","price",plan,unit.Quantity,unit.Currency,unit.Multiplier.ToString(),Ratio(indexed.Price.Multiplier.ScaledValue,Scale));
                Edge(scale,"input",price);
                var adder=Price(key+"/adder",ExactUnitPrice.FromPrice(indexed.Price.Adder),plan);
                var add=Row(key+"/all-in","intermediate","price_add","price",plan,unit.Quantity,unit.Currency,unit.Multiplier.ToString());
                Edge(add,"input",scale);Edge(add,"input",adder,1);price=add;
            }
            return price;
        }
        private int NativeQuantity(string key,ScheduleRow plan,VolumeSourceKey? selectedSource=null,AssetKey? selectedAsset=null)
        {
            var terms=side.Terms;var interval=plan.Interval!;int quantity;
            switch(terms.Quantity)
            {
                case ProfileQuantity profile:
                    quantity=Row(key+"/profile","source","profile_quantity","quantity",plan,profile.Energy);break;
                case SlotQuantity slots:
                    quantity=AddQuantity(key+"/linked-quantity",slots.Slots.Select(slot=>
                    {
                        var port=terms.InputPorts.Single(p=>p.Slot==slot);
                        var row=Row(key+"/port/"+Port(slot),"source","quantity_input","quantity",plan,port.Quantity,parameters:P(("stage","declared_port_v1")));
                        Binding(row,"quantity","sibling_quantity",reference:port.Quantity,coordinate:Port(slot),position:slot.Ordinal);return row;
                    }).ToArray(),terms.InputPorts[0].Quantity,plan);break;
                case VolumeQuantity volume:
                    var values=new List<int>();
                    foreach(var source in selectedSource is {} oneSource?ImmutableArray.Create(oneSource):volume.Sources)
                    {
                        var input=Row(key+"/source/"+source.Code,"source","quantity_input","quantity",plan,volume.Energy,
                            parameters:P(("stage",volume.Stage.ToString())));
                        Binding(input,"source","quantity_source",definitions:source.Revision,code:source.Code);values.Add(input);
                    }
                    quantity=Normalize(key,AddQuantity(key+"/source-total",values,volume.Energy,plan),volume.Energy,plan);break;
                case AccreditedQuantity accredited:
                    var outputGrid=activeGrid;var dailyIntervals=new List<DeliveryInterval>();
                    for(var day=interval.OperatingDate;day<interval.OperatingDate.AddMonths(1);day=day.AddDays(1))
                        dailyIntervals.Add(new(terms.Schedule.Clock.StartOfDay(day).ToUniversalTime(),terms.Schedule.Clock.StartOfDay(day.AddDays(1)).ToUniversalTime(),day,0,0,1));
                    var dailyGrid=ScheduleDefinitionGrid.Capture(grids.Count,key+"/operating-days",terms.Delivery.Clock,interval.OperatingDate,null,dailyIntervals);
                    grids.Add(dailyGrid);activeGrid=dailyGrid.Ordinal;var assets=new List<int>();
                    foreach(var asset in selectedAsset is {} oneAsset?ImmutableArray.Create(oneAsset):accredited.Assets)
                    {
                        var input=Row(key+"/accredited/"+asset.Code,"source","asset_capacity","quantity",plan,terms.Normalization.Basis,parameters:P(("measure","accredited_capacity_v1")));
                        Binding(input,"asset","asset",definitions:asset.Revision,code:asset.Code);assets.Add(input);
                    }
                    var accreditedTotal=AddQuantity(key+"/accredited-total",assets,terms.Normalization.Basis,plan);
                    var normalized=Normalize(key+"/day",accreditedTotal,terms.Normalization.Basis,plan);
                    var entitlement=Transform(key+"/entitlement",normalized,terms.ScheduleQuantity,plan,ExactDecimal.Parse("1"),"whole_operating_day_entitlement_v1");
                    activeGrid=outputGrid;quantity=Row(key+"/monthly-capacity","intermediate","quantity_sum_cells","quantity",plan,terms.ScheduleQuantity);
                    Edge(quantity,"input",entitlement);break;
                default:throw ScheduleDefinition.Error("The selected Side has no explicit supported quantity expression.");
            }
            if(side is CertificateDeliverySide certificate)quantity=Transform(key+"/certificates",quantity,certificate.CertificateQuantity,plan,certificate.Conversion,"certificate_per_energy_v1");
            return quantity;
        }
        private int Normalize(string key,int quantity,ReferenceKey unit,ScheduleRow plan)
        {
            var terms=side.Terms;var capacities=new List<int>();
            foreach(var asset in terms.Normalization.Assets)
            {
                var input=Row(key+"/basis/"+asset.Code,"source","asset_capacity","quantity",plan,terms.Normalization.Basis,
                    parameters:P(("measure","capacity_basis_v1")));
                Binding(input,"asset","asset",definitions:asset.Revision,code:asset.Code);capacities.Add(input);
            }
            var basis=AddQuantity(key+"/basis-total",capacities,terms.Normalization.Basis,plan);
            var row=Row(key+"/normalized","intermediate","quantity_normalize","quantity",plan,unit);Edge(row,"input",quantity);Edge(row,"basis",basis);return row;
        }
        public ScheduleDefinition Finish()
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currency=rows[contributions[0]].Currency!.Value;
            var sum=Row("side/pv","root","sum","money",null,currency:currency);
            for(var n=0;n<contributions.Count;n++)Edge(sum,"input",contributions[n],n);
            roots.Add(new("pv",sum,"pv"));
            return new(side.Id,side switch {FixedSide=>"fixed",FloatingSide=>"floating",PowerPhysicalSide=>"power",CertificateDeliverySide=>"certificate",CapacityDeliverySide=>"capacity",_=>throw ScheduleDefinition.Error("Unknown Side family.")},
                DefinitionIdentity.For(side).Canonical,side.PricingBindingIdentity?.Canonical??"",ScheduleDefinition.CurrentSchema,ScheduleDefinition.CurrentGenerator,
                rows,edges,bindings,roots,ports,grids);
        }
        private static string Port(SideSlot slot)=>slot.Role.ToString().ToLowerInvariant()+"/"+slot.Ordinal;
        private static string Digest(object value)=>Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value,ScheduleDefinition.Json)));
    }
}
