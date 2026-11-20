using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StructuredPowerBookRequest(
    [property:JsonRequired]Guid OperationId,
    [property:JsonRequired]Guid? TradeId,
    [property:JsonRequired]int ExpectedRevision,
    [property:JsonRequired]Guid BookId,
    [property:JsonRequired]Guid LegalEntityId,
    [property:JsonRequired]int LegalEntityRevision,
    [property:JsonRequired]Guid CounterpartyId,
    [property:JsonRequired]int CounterpartyRevision,
    [property:JsonRequired]string Reason,
    [property:JsonRequired]string Convention,
    [property:JsonRequired]StructuredPowerPreviewRequest Plan,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]Guid? FacilityProfileId=null);

public sealed record StructuredPowerBookingReceipt(Guid OperationId,Guid TradeId,int Revision,Guid InstrumentId,Guid BindingId,bool Changed);
public sealed record StructuredPowerBookedSide(int Slot,Guid SideId,Guid ScheduleId,string Name,string Coefficient,string Manifest);
public sealed record StructuredPowerBookedTrade(Guid TradeId,int Revision,Guid InstrumentId,Guid BindingId,Guid BookId,
    Guid FacilityId,int FacilityVersion,string EffectiveAt,string TradeReference,string ResourceKind,string FacilityCode,string FacilityName,
    string Book,StructuredPowerBookRequest Terms,ImmutableArray<StructuredPowerBookedSide> Sides)
{public ImmutableArray<InstrumentSideGroup> SideGroups{get;init;}=[];public RetainedSideSource? RetainedSource{get;init;}}
