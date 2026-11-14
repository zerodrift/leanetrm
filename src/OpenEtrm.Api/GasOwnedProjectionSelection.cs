using System.Text.Json.Serialization;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasOwnedProjectionSelection(Guid AccountId,Guid ProfileId,int Revision);
public sealed record GasOwnedProjectionProfileChoice(Guid ProfileId,int Revision,string Implementation,string QuantityDisposition,DateTimeOffset EffectiveAt);
public sealed record GasOwnedProjectionAccountChoice(Guid AccountId,string Code,Guid BookId,string Book,DateTimeOffset EffectiveAt,
    System.Collections.Immutable.ImmutableArray<GasOwnedProjectionProfileChoice> Profiles);
