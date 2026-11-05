using System.Collections.Immutable;

namespace OpenEtrm.Api;

// The caller supplies only versions admitted by the family's own temporal and
// authorization contract. This projection does not decide applicability, revive
// unavailable inputs or change exact-version backend access.
internal static class ContextVersions
{
    internal static Guid? SnapshotId(Microsoft.AspNetCore.Http.IQueryCollection query)=>OptionalId(query,"snapshotId");
    internal static Guid? OptionalId(Microsoft.AspNetCore.Http.IQueryCollection query,string key)
    {
        if(!query.TryGetValue(key,out var value))return null;
        return value.Count==1&&Guid.TryParse(value[0],out var id)&&id!=Guid.Empty?id:
            throw new OpenEtrm.MarketData.MarketDataException("snapshot_input","Supply one exact context or proposal identity.");
    }

    internal static ImmutableArray<T> Latest<T,TIdentity>(IEnumerable<T> eligible,Func<T,TIdentity> identity,Func<T,int> version) where TIdentity:notnull =>
        [..eligible.GroupBy(identity).Select(group=>group.OrderByDescending(version).First())];
}
