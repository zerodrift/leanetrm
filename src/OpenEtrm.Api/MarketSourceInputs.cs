using System.Collections.Immutable;
using OpenEtrm.MarketData;
using System.Text.Json.Serialization;

namespace OpenEtrm.Api;

// One exact part of an input selection. Source identity and frozen use belong
// to this part, independently of the snapshot's primary source.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MarketSourceInputs([property:JsonRequired] CatalogueSource Source,[property:JsonRequired] ImmutableArray<Guid> ObservationIds,
    [property:JsonRequired] ImmutableArray<Guid> FrozenObservationIds,[property:JsonRequired] ImmutableDictionary<string,ImmutableArray<Guid>> CurveContracts,
    [property:JsonRequired] ImmutableArray<string> RootCurves)
{
    internal InputFrame Project(InputFrame root)
    {
        var frame=root.ForMarketSource(ObservationIds,CurveContracts,FrozenObservationIds) with{Source=Source};
        // A recorded matrix attests all its parts. A working selection makes no
        // recorded-close claim until the complete selection is registered.
        return root.OriginCloseId is not null&&root.SourceInputs?.GetValueOrDefault(Source.DefinitionRevisionId) is {} declared&&declared.Matches(this)
            ?frame with{OriginCloseId=root.OriginCloseId,Close=root.Close}:frame;
    }
    internal bool Matches(InputFrame frame)=>Matches(frame.ObservationIds,
        frame.FrozenObservationIds.IsDefault?[]:frame.FrozenObservationIds,frame.CurveContracts);
    internal bool Matches(MarketSourceInputs other)=>!RootCurves.IsDefault&&!other.RootCurves.IsDefault&&Source==other.Source&&RootCurves.ToHashSet(StringComparer.Ordinal).SetEquals(other.RootCurves)&&
        Matches(other.ObservationIds,other.FrozenObservationIds,other.CurveContracts);
    private bool Matches(ImmutableArray<Guid> pins,ImmutableArray<Guid> frozen,ImmutableDictionary<string,ImmutableArray<Guid>> members)=>
        SamePins(ObservationIds,pins)&&SamePins(FrozenObservationIds,frozen)&&CurveContracts.Count==members.Count&&
        CurveContracts.All(p=>members.TryGetValue(p.Key,out var ids)&&p.Value.SequenceEqual(ids));
    internal MarketSourceInputs SelectRevision(Guid previous,Guid selected)
    {
        if(previous==selected||!ObservationIds.Contains(previous)||ObservationIds.Contains(selected))
            throw new MarketDataException("snapshot_inputs","A revision replaces exactly one selected input with a distinct observation.");
        return this with{ObservationIds=ObservationIds.Replace(previous,selected),
            FrozenObservationIds=FrozenObservationIds.Contains(previous)?FrozenObservationIds.Replace(previous,selected):FrozenObservationIds};
    }
    private static bool SamePins(ImmutableArray<Guid> left,ImmutableArray<Guid> right)=>
        !left.IsDefault&&!right.IsDefault&&left.Length==right.Length&&left.Distinct().Count()==left.Length&&
        right.Distinct().Count()==right.Length&&left.ToHashSet().SetEquals(right);
}
