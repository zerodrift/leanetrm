using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Power.Structured;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.Api;

public sealed record StructuredPowerQuantityResult(string Name,ExactQuantity Quantity);
public sealed record StructuredPowerIntervalResult(DeliveryInterval Interval,ImmutableArray<StructuredPowerQuantityResult> Quantities);
public sealed record StructuredPowerStateResult(string At,string? Commitment,int? ElapsedMinutes,int? StartsUsed,
    ImmutableArray<StructuredPowerQuantityResult> Quantities);
public sealed record StructuredPowerSideRow(DeliveryInterval Interval,DateTimeOffset PaymentAt,ExactQuantity Quantity,
    ExactUnitPrice Price,ExactMoney Amount,ExactMoney PresentValue,VolumeState State,Guid? EvidenceId,DateTimeOffset? KnownAt)
{public string PriceUnitLabel{get;init;}="";}
public sealed record StructuredPowerSideResult(Guid Id,string Name,string Coefficient,ReferenceKey Quote,
    ExactMoney GrossValue,ExactMoney Contribution,ImmutableArray<StructuredPowerSideRow> Rows);
public sealed record StructuredPowerValuationResult(string Measure,string Perspective,ReferenceKey Currency,
    Quantity RightQuantity,ExactMoney Value,ImmutableArray<StructuredPowerSideResult> Sides,string NumericalPolicy);
public sealed record StructuredPowerPreviewResult(Guid PreviewId,Guid SnapshotId,Guid? ContextId,Guid FacilityId,int FacilityVersion,
    string Kind,string Implementation,string ClockFingerprint,DateTimeOffset KnownAt,
    ImmutableArray<StructuredPowerIntervalResult> Intervals,StructuredPowerStateResult? ClosingState,
    StructuredPowerValuationResult? Valuation,ImmutableArray<string> Limitations);

public sealed record StructuredPowerPreparedPlan(StructuredPowerPreviewResult Preview,StructuredPowerResolvedTerms Resolved,
    OperatingRightInstrument? Instrument,OperatingQuantityInputs Quantities,ImmutableArray<DeliveryInterval> Grid,ImmutableDictionary<Guid,string> SideNames);

public static partial class StructuredPowerPreview
{
    public const string PreviewImplementation="structured_power_supplied_plan_preview_v1";
    public const string RightHolderPerspective="resource_right_holder_v1";
    public const string RemainingValueMeasure="remaining_operating_value_v1";
    private static readonly ExactDecimal One=ExactDecimal.Parse("1"),MinusOne=ExactDecimal.Parse("-1");

    // All inputs, including the cutoff, are captured by the caller's authorized
    // operation. This preview records no dispatch, facility revision or booking.
    public static StructuredPowerPreviewResult Calculate(PreviewData data,InputFrame frame,
        StructuredPowerFacilityVersion saved,DateTimeOffset knownAt,StructuredPowerPreviewRequest request)
        =>Prepare(data,frame,saved,knownAt,request).Preview;

    public static StructuredPowerPreparedPlan Prepare(PreviewData data,InputFrame frame,
        StructuredPowerFacilityVersion saved,DateTimeOffset knownAt,StructuredPowerPreviewRequest request,Guid? contractIdentity=null)
    {
        if(request is null||frame is null||saved is null||request.SnapshotId==Guid.Empty||request.SnapshotId!=frame.Id||request.FacilityId!=saved.Id||
            saved.Id==Guid.Empty||request.FacilityVersion!=saved.Version||saved.Version<1||!Utc(knownAt)||!Utc(frame.At))
            throw Error("structured_power_context","Retain the selected snapshot, saved facility version and authorized knowledge cutoff.");
        var from=Instant(saved.EffectiveFrom);var to=Instant(saved.EffectiveTo);var resolved=ResolveTerms(data,saved.Terms,from,to);
        if(request.Composition is not null&&request.Valuation is not null)throw Error("operating_composition","Select exactly one versioned Side composition; legacy and composed terms cannot coexist.");
        if(request.Composition is {} composition)ValidateComposition(composition,saved.Terms.Kind);
        if(request.BatteryDispatch is null||request.ThermalDispatch is null||request.SolarDispatch is null)throw PreviewUnion();
        var count=saved.Terms.Kind switch{"battery"=>request.BatteryDispatch.Length,"thermal"=>request.ThermalDispatch.Length,"solar"=>request.SolarDispatch.Length,_=>0};
        var grid=Grid(resolved,request.Grid,count);if(grid[0].Start<from||grid[^1].End>to)throw Error("structured_power_time","Every interval must belong to the saved facility version's effective period.");
        var id=contractIdentity??Identity(new{saved,frame.Id,frame.At,frame.ContextId,knownAt,request});var plan=new OperatingPlanReference(data.DefinitionRevision,id,1);
        var cells=ImmutableArray.CreateBuilder<OperatingScenarioQuantityCell>();
        var ports=new Dictionary<string,OperatingOutputPort>(StringComparer.Ordinal);
        void Port(string name,OperatingOutputKind kind,QuantitySpecification spec,IEnumerable<ExactQuantity> quantities)
        {
            var port=new OperatingOutputPort(plan,resolved.Resource.Asset,kind,spec.Key);ports.Add(name,port);
            var values=quantities.ToArray();if(values.Length!=grid.Length)throw PreviewUnion();
            for(var n=0;n<grid.Length;n++)cells.Add(new(port,grid[n],values[n]));
        }
        var result=ImmutableArray.CreateBuilder<StructuredPowerIntervalResult>(grid.Length);StructuredPowerStateResult? closing=null;
        switch(saved.Terms.Kind)
        {
            case "battery":
            {
                if(request.BatteryState is not {} state||request.ThermalState is not null||request.SolarRight is not null||request.ThermalDispatch.Length!=0||request.SolarDispatch.Length!=0||request.BatteryDispatch.Any(x=>x is null))throw PreviewUnion();
                var opening=new BatteryState(Instant(state.At),ExactQuantity.FromQuantity(Q(state.StoredMwh,resolved.Energy)),ExactQuantity.FromQuantity(Q(state.CumulativeGridThroughputMwh,resolved.Energy)));
                var evaluation=BatteryOperatingPlans.Evaluate(resolved.Battery!,opening,new(Q(state.TerminalMinimumMwh,resolved.Energy),Q(state.TerminalMaximumMwh,resolved.Energy)),resolved.Throughput!,
                    [..request.BatteryDispatch.Select((x,n)=>new BatteryDispatchInterval(grid[n],Q(x.ChargeMw,resolved.Power!),Q(x.DischargeMw,resolved.Power!),Decimal(x.AvailablePowerFraction),BatteryChargingOrigin.Grid))],BatteryOperatingPlans.Implementation);
                Port("charge",OperatingOutputKind.ChargingEnergy,resolved.Energy,evaluation.Intervals.Select(x=>x.GridChargeEnergy));
                Port("energy",OperatingOutputKind.DischargingEnergy,resolved.Energy,evaluation.Intervals.Select(x=>x.GridDischargeEnergy));
                Port("variable",OperatingOutputKind.PaymentEnergy,resolved.Energy,evaluation.Intervals.Select(x=>x.GridChargeEnergy.Add(x.GridDischargeEnergy)));
                foreach(var row in evaluation.Intervals)result.Add(new(row.Dispatch.Interval,[new("grid_charge",row.GridChargeEnergy),new("grid_discharge",row.GridDischargeEnergy),new("opening_stored_energy",row.OpeningStoredEnergy),new("closing_stored_energy",row.ClosingStoredEnergy),new("cumulative_grid_throughput",row.CumulativeGridThroughput)]));
                closing=new(evaluation.ClosingState.At.ToString("O"),null,null,null,[new("stored_energy",evaluation.ClosingState.StoredEnergy),new("cumulative_grid_throughput",evaluation.ClosingState.CumulativeGridThroughput)]);break;
            }
            case "thermal":
            {
                if(request.ThermalState is not {} state||request.BatteryState is not null||request.SolarRight is not null||request.BatteryDispatch.Length!=0||request.SolarDispatch.Length!=0||request.ThermalDispatch.Any(x=>x is null))throw PreviewUnion();
                var terminal=state.TerminalPolicy=="require_minimum_times_satisfied"?ThermalTerminalPolicy.RequireMinimumTimesSatisfied:throw Error("structured_power_policy","Select the implemented terminal minimum-time policy.");
                var terms=new ThermalTollTerms(resolved.Thermal!,Fuel(state.GuaranteedHeatRate,state.GuaranteedNoLoadFuelPerHour,state.GuaranteedStartupFuel,resolved.Fuel!,resolved.Energy),terminal,state.NumericalPolicy);
                var opening=new ThermalInitialState(Instant(state.At),Commitment(state.Commitment),Minutes(state.ElapsedMinutes),Q(state.PreviousOutputMw,resolved.Power!),state.StartsUsed);
                var evaluation=ThermalToll.Evaluate(terms,opening,[..request.ThermalDispatch.Select((x,n)=>new ThermalDispatchInterval(grid[n],Commitment(x.Commitment),Q(x.OutputMw,resolved.Power!),Q(x.AvailableMw,resolved.Power!),x.StartupInstructionAt is {} at?Instant(at):null))]);
                Port("energy",OperatingOutputKind.EnergyOutput,resolved.Energy,evaluation.Rows.Select(x=>x.Energy));
                var fuelBasis=request.Valuation?.FuelQuantityBasis;
                if(request.Valuation is not null&&fuelBasis is not("guaranteed" or "consumption"))throw Error("structured_power_policy","Select the guaranteed or consumption fuel quantity basis explicitly.");
                if(request.Composition is not null)
                {
                    Port("guaranteed_fuel",OperatingOutputKind.GuaranteedFuelInput,resolved.Fuel!,evaluation.Rows.Select(x=>x.GuaranteedFuel));
                    Port("consumption_fuel",OperatingOutputKind.ConsumptionFuelInput,resolved.Fuel!,evaluation.Rows.Select(x=>x.ConsumptionFuel));
                }
                else Port("fuel",OperatingOutputKind.FuelInput,resolved.Fuel!,evaluation.Rows.Select(x=>fuelBasis=="guaranteed"?x.GuaranteedFuel:x.ConsumptionFuel));
                foreach(var row in evaluation.Rows)result.Add(new(row.Interval,[new("energy_output",row.Energy),new("guaranteed_fuel",row.GuaranteedFuel),new("consumption_fuel",row.ConsumptionFuel)]));
                closing=new(evaluation.FinalState.At.ToString("O"),evaluation.FinalState.Commitment==ThermalCommitment.Online?"online":"offline",checked((int)(evaluation.FinalState.ElapsedInState.Ticks/TimeSpan.TicksPerMinute)),evaluation.FinalState.StartsUsed,[new("previous_output",ExactQuantity.FromQuantity(evaluation.FinalState.PreviousOutput))]);break;
            }
            case "solar":
            {
                if(request.SolarRight is not {} right||request.BatteryState is not null||request.ThermalState is not null||request.BatteryDispatch.Length!=0||request.ThermalDispatch.Length!=0||request.SolarDispatch.Any(x=>x is null))throw PreviewUnion();
                var delivery=right.DeliveryBasis switch{"as_produced"=>SolarDeliveryBasis.AsProduced,"fixed_profile"=>SolarDeliveryBasis.FixedProfile,_=>throw Error("structured_power_policy","Select an implemented solar delivery basis.")};
                if(delivery==SolarDeliveryBasis.FixedProfile&&(request.Valuation is not null||request.Composition is not null))throw Error("structured_power_unavailable","Fixed-profile quantities and replacement exposure are available; full valuation requires explicit replacement and surplus price sides.");
                var payment=right.PaymentBasis switch{"produced_only"=>SolarPaymentBasis.ProducedOnly,"delivery_obligation"=>SolarPaymentBasis.DeliveryObligation,_=>throw Error("structured_power_policy","This supplied forecast preview supports produced-only or delivery-obligation payments; metered/deemed evidence requires an admitted source.")};
                var terms=new SolarOutputTerms(new(data.DefinitionRevision,id),resolved.Resource.Asset,resolved.Clock,saved.Terms.IntervalMinutes,resolved.Energy,resolved.SolarStage!.Value,
                    Ratio(right.EntitlementNumerator,right.EntitlementDenominator),delivery,payment,SolarOutputRights.Implementation);
                var evaluation=SolarOutputRights.EvaluateScenario(terms,new(data.DefinitionRevision,data.ReferenceRevision,frame.At,knownAt,PendingActualsPolicy.RequireActuals),
                    [..request.SolarDispatch.Select((x,n)=>new SolarScenarioInput(grid[n],Q(x.ProductionMwh,resolved.Energy),x.RequiredDeliveryMwh is {} required?Q(required,resolved.Energy):null,resolved.SolarStage.Value))],right.ForecastAssumption);
                Port("energy",OperatingOutputKind.EnergyOutput,resolved.Energy,evaluation.Rows.Select(x=>x.PhysicalEnergy));
                Port("payment",OperatingOutputKind.PaymentEnergy,resolved.Energy,evaluation.Rows.Select(x=>x.PaymentEnergy));
                foreach(var row in evaluation.Rows)result.Add(new(row.Input.Interval,[new("physical_energy",row.PhysicalEnergy),new("payment_energy",row.PaymentEnergy),new("delivery_obligation",row.DeliveryObligation),new("replacement_energy",row.ReplacementEnergy),new("surplus_energy",row.SurplusEnergy),new("eligible_actual_energy",row.EligibleActualEnergy)]));break;
            }
        }
        var valued=request.Composition is {} composed?ValueComposition(data,frame,knownAt,id,resolved,grid,ports,cells.ToImmutable(),composed):
            request.Valuation is {} legacy?Value(data,frame,knownAt,id,resolved,grid,ports,cells.ToImmutable(),legacy):null;
        var snapshot=data.Snapshot(frame);if(frame.ContextId is {} contextId)snapshot=snapshot.WithKnowledge(contextId,knownAt);
        var quantities=OperatingQuantityInputs.Scenario(snapshot,cells.ToImmutable(),OperatingQuantityInputs.ScenarioImplementation);
        var preview=new StructuredPowerPreviewResult(id,frame.Id,frame.ContextId,saved.Id,saved.Version,saved.Terms.Kind,PreviewImplementation,resolved.OperatingClock.Fingerprint,knownAt,result.MoveToImmutable(),closing,valued?.Value,
            ["Hypothetical supplied-plan assumptions under the selected market; no historical publication assertion, optimization, operator instruction or booking.","Remaining operating value excludes historical operating P&L and opening inventory cost basis.","Forecast quantities create no certificates; metered/deemed evidence and registry transitions require separately admitted sources."]);
        return new(preview,resolved,valued?.Instrument,quantities,[..grid],valued?.Names??ImmutableDictionary<Guid,string>.Empty);
    }

    static DeliveryInterval[] Grid(StructuredPowerResolvedTerms resolved,StructuredPowerGridInput input,int count)
    {
        if(input is null||count<1||count>MaximumPreviewIntervals||!DateOnly.TryParseExact(input.OperatingDate,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var day)||day.Year is <2000 or >2099||input.FirstInterval<1)
            throw Error("structured_power_grid","Supply a supported operating date, one-based first interval and a bounded nonempty plan.");
        var first=PowerHours.Compile(day,day.AddDays(1),resolved.OperatingClock,TimeOfUse.All(resolved.CanonicalTerms.IntervalMinutes),[],resolved.CanonicalTerms.IntervalMinutes);
        if(input.FirstInterval>first.Length)throw Error("structured_power_grid","The selected first interval must exist on the native operating day.");
        var intervals=first.Skip(input.FirstInterval-1).Select(x=>x.Delivery).Take(count).ToList();
        while(intervals.Count<count)
        {
            day=day.AddDays(1);if(day.Year>2099)throw Error("structured_power_grid","The native grid must remain within the supported operating dates.");
            intervals.AddRange(PowerHours.Compile(day,day.AddDays(1),resolved.OperatingClock,TimeOfUse.All(resolved.CanonicalTerms.IntervalMinutes),[],resolved.CanonicalTerms.IntervalMinutes).Select(x=>x.Delivery).Take(count-intervals.Count));
        }
        return intervals.ToArray();
    }
    static ThermalCommitment Commitment(string text)=>text switch{"offline"=>ThermalCommitment.Offline,"online"=>ThermalCommitment.Online,_=>throw Error("structured_power_policy","Select an explicit online or offline commitment state.")};
    internal static DateTimeOffset Instant(string text)=>text is not null&&text.Length<=40&&DateTimeOffset.TryParseExact(text,["yyyy-MM-dd'T'HH:mm:ss'Z'","yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'","yyyy-MM-dd'T'HH:mm:sszzz","yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"],CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var at)&&Utc(at)?at:throw Error("structured_power_time","Supply an exact UTC instant ending in Z.");
    static ExactRatio Ratio(string numerator,string denominator)
    {
        if(numerator is null||denominator is null||numerator.Length>80||denominator.Length>80||!BigInteger.TryParse(numerator,NumberStyles.None,CultureInfo.InvariantCulture,out var n)||!BigInteger.TryParse(denominator,NumberStyles.None,CultureInfo.InvariantCulture,out var d)||n<=0||d<=0||n>d)
            throw Error("structured_power_number","Supply an exact positive entitlement numerator and denominator with fraction no greater than one.");
        return new(n,d);
    }
    static Guid Identity(object value)=>new(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)).AsSpan(0,16));
    static MarketDataException PreviewUnion()=>Error("structured_power_union","Supply only the selected kind's nonempty dispatch array and matching explicit state/right inputs.");
}
