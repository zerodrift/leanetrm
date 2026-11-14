using System.Collections.Immutable;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

public static partial class MarketCloses
{
    public const string ExactCapturePolicy="exact_selected_inputs_v1";
    public static object[] CaptureChoices=>[
        new{value=CurveModel.LiveOutputsPolicy,label="Eligible live outputs; retain all source evidence"},
        new{value=ExactCapturePolicy,label="Exact selected outputs and source evidence"}];
    internal static MarketSourceInputs LivePart(PreviewData data,InputFrame frame,ImmutableArray<string> roots)
    {
        if(roots.IsDefault||roots.Distinct(StringComparer.Ordinal).Count()!=roots.Length||roots.Any(code=>!frame.CurveContracts.ContainsKey(code)))
            throw new MarketDataException("pilot_close_inputs","Every selected root requires an exact ordered candidate window.");
        var members=frame.CurveContracts;
        foreach(var code in roots)
        {
            var live=data.Model.SelectLiveOutputs(frame.At,data.Requests(frame,code),CurveModel.LiveOutputsPolicy);
            if(live.Selected.IsEmpty)
                throw new MarketDataException("empty_live_curve","A required close-session curve has no eligible output in the selected window. Choose an explicit new contract window.");
            members=members.SetItem(code,[..live.Selected.Select(r=>r.Contract.Id)]);
        }
        return new(CatalogueSource.From(data),frame.ObservationIds,frame.FrozenObservationIds.IsDefault?[]:frame.FrozenObservationIds,members,roots);
    }

    internal static InputFrame FreshFrame(InputFrame original,Guid id,ImmutableDictionary<Guid,MarketSourceInputs> parts,CatalogueSource source)
    {
        var primary=parts[source.DefinitionRevisionId];
        return original with{Id=id,Name="Selected live close inputs",OriginCloseId=null,Close=null,OriginInputId=null,
            ObservationIds=primary.ObservationIds,FrozenObservationIds=primary.FrozenObservationIds,CurveContracts=primary.CurveContracts,
            Source=source,SourceInputs=parts};
    }

    internal static void QualifyPart(PreviewData data,InputFrame frame,ImmutableArray<string> roots)
    {
        if(roots.IsDefault||roots.Distinct(StringComparer.Ordinal).Count()!=roots.Length)
            throw new MarketDataException("pilot_close_inputs","Declare the distinct exact output roots, or an explicit empty retained-source scope.");
        // Retained sources are checked by the selected roots' dependency graph;
        // they are not independently promoted to requested live outputs.
        if(!roots.IsEmpty)_=data.Build(frame,roots);
    }
}
