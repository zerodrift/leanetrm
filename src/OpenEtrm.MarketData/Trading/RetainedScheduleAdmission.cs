using System.Collections.Immutable;
using System.Numerics;
using OpenEtrm.MarketData.Power;

namespace OpenEtrm.MarketData.Trading;

internal sealed partial class RetainedSideSchedule
{
    private void RequireObligations(int[] obligations,bool native)
    {
        if(obligations.Distinct().Count()!=obligations.Length)throw Error("Each retained obligation must contribute exactly once.");
        var ordered=obligations.OrderBy(n=>native?Definition.Grids[Definition.Rows[n].Grid!.Value].FirstRowOrdinal:Definition.Rows[n].PaymentOrdinal).ToArray();
        var pv=OutputRoot("pv","pv").Row;
        Require(Definition.Rows[pv],"sum");
        int Reduced(int root)
        {
            var candidates=Definition.Rows.Where(row=>row.Operation=="sum_cells"&&One(row.Ordinal,"input")==root).ToArray();
            return candidates.Length==1?candidates[0].Ordinal:throw Error("Every vector obligation requires exactly one explicit cell reduction into side PV.");
        }
        if(!Many(pv,"input").SequenceEqual(native?ordered.Select(Reduced):ordered))
            throw Error("The retained side PV must sum every declared obligation exactly once in schedule order.");
        if(Definition.Roots.Count!=1+obligations.Length*(native?3:2))
            throw Error("A retained schedule can expose only its side PV, obligation cash/PV and native quantity roots.");
        var lifecycle=native?side switch{CertificateDeliverySide=>"certificate_holding_v1",FixedSide or FloatingSide=>"interval_payment_v1",_=>"interval_delivery_v1"}:"undue_payment_v1";
        var nativePaymentRule=native?DefinitionIdentity.CanonicalTerm(side.Terms.DateRule):null;
        foreach(var root in ordered)
        {
            var guard=Definition.Rows[root];var ordinal=native?NativeOrdinal(guard):guard.PaymentOrdinal!.Value;
            var key=(native?"block/":"payment/")+ordinal;
            if(native?(guard.OccurrenceKey is not null||guard.TermsDigest is not null):(guard.OccurrenceKey is null||guard.TermsDigest is null))
                throw Error("Scalar obligations retain their own identity; vector obligations identify each selected cell through its functional reference.");
            if(guard.Key!=key+"/value"||guard.Role!="root"||
                guard.Parameters["lifecycle"]!=lifecycle||guard.Parameters["pricePolicy"]!="selected_context_v1"||
                guard.Parameters["measure"]!=(native?"value_excluding_overdue_v1":"present_value_v1"))
                throw Error("A retained output guard must select its supported explicit lifecycle, price and valuation policies.");
            if(native&&(guard.Parameters["averaging"]!="none"||guard.Parameters["firstFixingAt"]!="none"||
                guard.Parameters["paymentRule"]!=nativePaymentRule||guard.Parameters["unadjustedPayment"]!="none"))
                throw Error("Native output guards cannot introduce unconsumed dated pricing or payment conventions.");
            var amount=One(root,"amount");var discounted=Definition.Rows[One(root,"discounted")];Require(discounted,"pv");
            if(One(discounted.Ordinal,"amount")!=amount)
                throw Error("An obligation's cash and discounted branches must consume the same exact retained amount.");
            var discount=Definition.Rows[One(discounted.Ordinal,"discount")];Require(discount,"discount");
            if(discount.Parameters["policy"]!="context_required_v1"||rowBindings.ContainsKey(discount.Ordinal))
                throw Error("The compact retained evaluator requires the explicit selected discount context without an unconsumed rate binding.");
            foreach(var value in new[]{Definition.Rows[amount],discounted,discount})RequireWindow(value,guard);
            if(OutputRoot(key+"/cash","contractual_amount").Row!=amount||
                OutputRoot(key+"/pv","contribution_pv").Row!=root)
                throw Error("Every obligation must declare its own exact contractual cash and contribution PV roots.");
            if(native)
            {
                _=OutputRoot("quantity/"+ordinal,"schedule_quantity");
            }
            else
            {
                foreach(var node in Closure(amount))
                    if(Definition.Rows[node].Operation!="constant_price")RequireWindow(Definition.Rows[node],guard);
                var average=Definition.Rows[One(amount,"price")];Require(average,"weighted_price");
                BigInteger numerator=0,denominator=1;
                foreach(var application in Many(average.Ordinal,"input"))
                {
                    var(n,d)=Ratio(Definition.Rows[application]);
                    if(n<=0)throw Error("Retained observation weights must be positive.");
                    numerator=numerator*d+n*denominator;denominator*=d;
                    var gcd=BigInteger.GreatestCommonDivisor(numerator,denominator);numerator/=gcd;denominator/=gcd;
                }
                if(numerator!=denominator)throw Error("Retained observation coefficients must sum exactly to one; they cannot be silently renormalized.");
            }
        }
        var consumed=Closure(pv);
        if(native)
            foreach(var root in Definition.Roots.Where(root=>root.Measure=="schedule_quantity"))consumed.UnionWith(Closure(root.Row));
        if(consumed.Count!=Definition.Rows.Count)
            throw Error("Every retained row must belong to the admitted PV or quantity output expressions; extra roots cannot preserve ignored branches.");
    }

    private HashSet<int> Closure(int node)
    {
        var result=new HashSet<int>();var pending=new Stack<int>();pending.Push(node);
        while(pending.TryPop(out var selected))
            if(result.Add(selected)&&incoming.TryGetValue(selected,out var inputs))foreach(var input in inputs)pending.Push(input);
        return result;
    }
    private static void RequireWindow(ScheduleDefinitionRow row,ScheduleDefinitionRow obligation)
    {
        if(row.Grid!=obligation.Grid||row.DeliveryFrom!=obligation.DeliveryFrom||row.DeliveryTo!=obligation.DeliveryTo||row.Interval!=obligation.Interval||
            row.ValueAt!=obligation.ValueAt||row.PaymentOrdinal!=obligation.PaymentOrdinal)
            throw Error("Every retained contribution must consume its exact declared delivery window and payment identity.");
    }
    private void RequireGrid(ScheduleDefinitionRow row,int grid,ScheduleDefinitionRow obligation)
    {
        if(row.Grid!=grid||row.Interval is not null||row.DeliveryFrom is not null||row.DeliveryTo is not null||
            row.ValueAt!=obligation.ValueAt||row.PaymentOrdinal!=obligation.PaymentOrdinal)
            throw Error("A retained quantity operation must consume its exact declared grid and payment identity.");
    }

    // The native adapter admits the complete vector expression once per block.
    // Geometry is checked cellwise, but no per-cell DAG or binding index is built.
    private void RequireNativeQuantities()
    {
        var terms=NativeTerms!;
        if(Definition.Ports.Count!=side.Terms.InputPorts.Length||Definition.Ports.Select(port=>port.Slot).Distinct().Count()!=Definition.Ports.Count||
            Definition.Ports.Any(port=>port.Slot is not {} slot||port.Name!=slot.Role.ToString().ToLowerInvariant()+"/"+slot.Ordinal))
            throw Error("Retained schedules must declare every typed input port of the side exactly once.");
        foreach(var native in nativeTemplates)
        {
            var guard=native.Obligation;var grid=guard.Grid!.Value;var geometry=Definition.Grids[grid];var root=Definition.Rows[native.Quantity];
            var delivery=terms.Delivery;var clock=terms.Schedule.Clock;
            if(geometry.FirstRowOrdinal is null||geometry.Clock!=delivery.Clock)throw Error("An output grid must bind the exact delivery clock and logical output ordinals.");
            foreach(var interval in GridIntervals(grid))
            {
                if(interval.Start<clock.StartOfDay(delivery.From).ToUniversalTime()||interval.End>clock.StartOfDay(delivery.To).ToUniversalTime()||
                    interval.Start>=interval.End||interval.OperatingDate<delivery.From||interval.OperatingDate>=delivery.To||
                    terms.Schedule.Cadence==IntervalScheduleCadence.NativeIntervals&&interval.End-interval.Start!=TimeSpan.FromMinutes(delivery.IntervalMinutes)||
                    terms.Schedule.Cadence==IntervalScheduleCadence.OperatingMonths&&(interval.OperatingDate.Day!=1||
                        interval.Start!=clock.StartOfDay(interval.OperatingDate).ToUniversalTime()||interval.End!=clock.StartOfDay(interval.OperatingDate.AddMonths(1)).ToUniversalTime()))
                    throw Error("Every retained output cell must preserve its explicit whole-term native or operating-month cadence.");
                var local=TimeZoneInfo.ConvertTime(interval.Start,clock.Zone);
                var occurrence=clock.Zone.IsAmbiguousTime(local.DateTime)&&local.Offset!=clock.Zone.GetAmbiguousTimeOffsets(local.DateTime).Max()?2:1;
                if(interval.OperatingDate!=DateOnly.FromDateTime(local.DateTime)||interval.HourBeginning!=local.Hour||interval.Minute!=local.Minute||interval.Occurrence!=occurrence)
                    throw Error("Retained local interval labels must agree with the exact captured reference clock, including repeated hours.");
            }
            if(root.Unit!=terms.ScheduleQuantity)throw Error("A retained quantity output must preserve its exact declared native unit.");
            RequireGrid(root,grid,guard);
            var cash=Definition.Rows[One(native.Root,"amount")];var components=cash.Operation=="sum"?Many(cash.Ordinal,"input"):[cash.Ordinal];
            if((side is FixedSide or FloatingSide or PowerPhysicalSide)&&components.Length!=1)
                throw Error("A retained fixed, floating or power column requires one amount consuming its complete quantity output.");
            if(terms.Quantity is AccreditedQuantity&&side is not CapacityDeliverySide)
                throw Error("Only a capacity delivery can consume the supported accredited-capacity topology.");
            var quantities=components.Select(component=>One(component,"quantity")).ToArray();
            var declared=root.Operation=="quantity_add"&&components.Length>1?Many(root.Ordinal,"input"):[root.Ordinal];
            if(!declared.SequenceEqual(quantities))throw Error("Every amount component must consume the exact quantity output contribution once, in declared order.");
            var sources=new HashSet<VolumeSourceKey>();var assets=new HashSet<AssetKey>();var ports=new HashSet<string>(StringComparer.Ordinal);
            foreach(var component in components)
            {
                RequireGrid(Definition.Rows[component],grid,guard);var quantity=One(component,"quantity");
                if(terms.Quantity is AccreditedQuantity)
                {
                    var selected=RequireCapacityComponent(quantity,guard);
                    if(!assets.Add(selected))throw Error("A retained capacity obligation cannot repeat an asset component.");
                    var lookup=Definition.Rows[One(component,"price")];Require(lookup,"price_lookup");RequireGrid(lookup,grid,guard);
                    if(Asset(Binding(lookup.Ordinal,"location"))!=selected)throw Error("A retained capacity amount must price the same exact asset as its quantity contribution.");
                }
                else
                {
                    var componentSources=RequireIntervalQuantity(quantity,grid,guard,ports);
                    foreach(var source in componentSources)if(!sources.Add(source))throw Error("A retained obligation cannot repeat a source contribution across amounts.");
                    var price=Definition.Rows[One(component,"price")];
                    foreach(var node in Closure(price.Ordinal))if(Definition.Rows[node].Operation!="constant_price")RequireGrid(Definition.Rows[node],grid,guard);
                    if(side is CertificateDeliverySide&&terms.Quantity is VolumeQuantity)
                    {
                        Require(price,"price_lookup");
                        if(componentSources.Count!=1||Source(Binding(price.Ordinal,"location"))!=componentSources.Single())
                            throw Error("A retained certificate amount must price the exact source of its quantity contribution.");
                    }
                }
            }
            if(terms.Quantity is VolumeQuantity volume&&!sources.SetEquals(volume.Sources))throw Error("Every retained block must consume the same complete explicit volume-source set.");
            if(terms.Quantity is AccreditedQuantity capacity&&!assets.SetEquals(capacity.Assets))throw Error("Every retained capacity block must consume the same complete explicit asset set.");
            if(terms.Quantity is SlotQuantity&&!ports.SetEquals(Definition.Ports.Select(port=>port.Name)))throw Error("Every retained linked block must consume all declared typed ports exactly once.");
        }
    }
    private HashSet<VolumeSourceKey> RequireIntervalQuantity(int node,int grid,ScheduleDefinitionRow obligation,HashSet<string> ports)
    {
        var terms=NativeTerms!;var value=Definition.Rows[node];RequireGrid(value,grid,obligation);
        if(side is CertificateDeliverySide)
        {
            Require(value,"quantity_transform");
            if(value.Parameters["implementation"]!="certificate_per_energy_v1"||value.Unit!=terms.ScheduleQuantity)
                throw Error("A retained certificate quantity requires its explicit supported conversion and output unit.");
            node=One(node,"input");value=Definition.Rows[node];RequireGrid(value,grid,obligation);
        }
        var sources=new HashSet<VolumeSourceKey>();
        switch(terms.Quantity)
        {
            case ProfileQuantity profile:
                if(value.Operation is not("profile_quantity" or "quantity_literal")||value.Unit!=profile.Energy)
                    throw Error("A retained profile requires an explicit exact literal or retained-duration energy quantity.");
                break;
            case SlotQuantity:
                foreach(var leaf in QuantitySum(node,grid,obligation))
                {
                    var input=Definition.Rows[leaf];Require(input,"quantity_input");var binding=Binding(leaf,"quantity");
                    var port=Definition.Ports.SingleOrDefault(port=>port.Name==binding.Coordinate&&port.Unit==input.Unit&&port.Unit==binding.Reference&&port.Slot?.Ordinal==binding.Position);
                    if(input.Parameters["stage"]!="declared_port_v1"||port.Name is null||!ports.Add(port.Name))throw Error("Every retained linked contribution requires one exact, unrepeated typed sibling port.");
                }
                break;
            case VolumeQuantity volume:
                Require(value,"quantity_normalize");
                if(value.Unit!=volume.Energy)throw Error("A retained normalized volume must preserve its exact energy unit.");
                RequireBasis(One(node,"basis"),grid,obligation);
                foreach(var leaf in QuantitySum(One(node,"input"),grid,obligation))
                {
                    var input=Definition.Rows[leaf];Require(input,"quantity_input");
                    if(input.Unit!=volume.Energy||input.Parameters["stage"]!=volume.Stage.ToString()||!sources.Add(Source(Binding(leaf,"source"))))
                        throw Error("Retained volume inputs require distinct exact sources with the declared stage, unit and grid.");
                }
                break;
            default:throw Error("The retained interval quantity is outside the supported closed contract.");
        }
        return sources;
    }
    private int[] QuantitySum(int node,int grid,ScheduleDefinitionRow obligation)
    {
        var row=Definition.Rows[node];RequireGrid(row,grid,obligation);
        if(row.Operation!="quantity_add")return [node];
        var inputs=Many(node,"input");
        if(inputs.Any(input=>Definition.Rows[input].Operation=="quantity_add"))throw Error("The retained compact quantity sum requires direct explicit contributions.");
        foreach(var input in inputs)RequireGrid(Definition.Rows[input],grid,obligation);return inputs;
    }
    private void RequireBasis(int node,int grid,ScheduleDefinitionRow obligation)
    {
        var normalization=NativeTerms!.Normalization;var assets=new HashSet<AssetKey>();
        foreach(var leaf in QuantitySum(node,grid,obligation))
        {
            var row=Definition.Rows[leaf];Require(row,"asset_capacity");
            if(row.Unit!=normalization.Basis||row.Parameters["measure"]!="capacity_basis_v1"||!assets.Add(Asset(Binding(leaf,"asset"))))
                throw Error("Retained normalization requires distinct capacity-in-force sources of its exact basis and grid.");
        }
        if(normalization.Policy!=PpaNormalizationPolicy.SumCapacityInForce||!assets.SetEquals(normalization.Assets))
            throw Error("Every retained normalization must consume the complete declared exact asset denominator.");
    }
    private AssetKey RequireCapacityComponent(int node,ScheduleDefinitionRow obligation)
    {
        var terms=NativeTerms!;var output=Definition.Grids[obligation.Grid!.Value];
        if(output.CellCount!=1)throw Error("A retained capacity output must select exactly one whole operating month.");
        var interval=output.Interval(0);var month=interval.OperatingDate;var clock=terms.Schedule.Clock;
        var monthly=Definition.Rows[node];Require(monthly,"quantity_sum_cells");RequireGrid(monthly,output.Ordinal,obligation);
        var transform=Definition.Rows[One(node,"input")];Require(transform,"quantity_transform");
        var grid=transform.Grid??throw Error("Daily capacity entitlements require their own retained input grid.");var days=Definition.Grids[grid];
        if(days.FirstRowOrdinal is not null||days.Clock!=terms.Delivery.Clock||days.Month!=month||days.CellCount!=month.AddMonths(1).DayNumber-month.DayNumber||
            transform.Unit!=terms.ScheduleQuantity||transform.Parameters["implementation"]!="whole_operating_day_entitlement_v1")
            throw Error("A monthly capacity reduction requires its complete original operating-day input grid and explicit entitlement rule.");
        var date=month;
        foreach(var day in GridIntervals(grid))
        {
            if(day.OperatingDate!=date||day.Start!=clock.StartOfDay(date).ToUniversalTime()||day.End!=clock.StartOfDay(date.AddDays(1)).ToUniversalTime()||day.HourBeginning!=0||day.Minute!=0||day.Occurrence!=1)
                throw Error("A capacity reduction must include each whole operating day exactly once, including clock transitions.");
            date=date.AddDays(1);
        }
        RequireGrid(transform,grid,obligation);
        var normalizer=Definition.Rows[One(transform.Ordinal,"input")];Require(normalizer,"quantity_normalize");RequireGrid(normalizer,grid,obligation);
        if(normalizer.Unit!=terms.Normalization.Basis)throw Error("An accredited normalization requires the exact declared capacity unit.");
        RequireBasis(One(normalizer.Ordinal,"basis"),grid,obligation);
        var input=Definition.Rows[One(normalizer.Ordinal,"input")];Require(input,"asset_capacity");RequireGrid(input,grid,obligation);
        if(input.Unit!=terms.Normalization.Basis||input.Parameters["measure"]!="accredited_capacity_v1")throw Error("A capacity component must consume its exact asset's accredited capacity on every retained day.");
        return Asset(Binding(input.Ordinal,"asset"));
    }
    internal static void RequireSiblingRoots(PpaInstrument instrument,IReadOnlyDictionary<Guid,Side> retained)
    {
        foreach(var membership in instrument.Memberships)
        {
            var plan=retained[membership.Side.Id].RetainedPlan!;
            foreach(var port in plan.Definition.Ports)
            {
                var target=instrument.Memberships.SingleOrDefault(member=>member.Slot==port.Slot)??throw Error("A retained typed port requires an explicitly bound instrument membership.");
                var source=retained[target.Side.Id].RetainedPlan!;
                if(source.nativeTemplates.Any(row=>source.Definition.Rows[row.Quantity].Unit!=port.Unit))
                    throw Error("A retained sibling quantity must preserve the port's exact unit.");
                ScheduleDefinitionGrid.RequireCoverage(plan.nativeBlocks.Select(block=>block.Grid),source.nativeBlocks.Select(block=>block.Grid));
            }
        }
    }
}
