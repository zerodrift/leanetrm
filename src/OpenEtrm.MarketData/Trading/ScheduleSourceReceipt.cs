using System.Collections.Immutable;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Power;

namespace OpenEtrm.MarketData.Trading;

public readonly record struct ScheduleSourceCell(int Instruction,int Cell,string ScenarioId);
public abstract record ScheduleSourceEvidence;
public sealed record ScheduleVolumeEvidence(AssetVolume Volume):ScheduleSourceEvidence;
public sealed record ScheduleCapacityEvidence(AssetKey Asset,AssetVersion Version,DeliveryInterval Interval,string Measure):ScheduleSourceEvidence;
public sealed record ScheduleDiscountEvidence(SidePaymentFactor Factor):ScheduleSourceEvidence;
public sealed record ScheduleContractObservationEvidence(ContractObservationLookup Lookup,ContractObservationPrice Resolution):ScheduleSourceEvidence;
public sealed record ScheduleDatedPriceEvidence(IndexPriceLookup Lookup,ResolvedPeriodPrice Resolution):ScheduleSourceEvidence;
public sealed record ScheduleNativePriceEvidence(PreparedCurve Curve,IntervalPriceResolution Resolution,
    LocationAssignment? Assignment,ExactUnitPrice Price,SchedulePriceFacts State):ScheduleSourceEvidence;
// A port records the received value only. The instrument retains its provider mapping.
public sealed record SchedulePortEvidence(ScheduleNumber Value,ScheduleCellState State):ScheduleSourceEvidence;
public sealed record ScheduleSourceReceipt(Guid SideId,BookedSideProgramIdentity Program,
    ImmutableDictionary<ScheduleSourceCell,ScheduleSourceEvidence> Cells);
