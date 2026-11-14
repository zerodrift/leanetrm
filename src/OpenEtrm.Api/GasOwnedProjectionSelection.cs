using System.Text.Json.Serialization;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasOwnedProjectionSelection(Guid AccountId,Guid ProfileId,int Revision);
