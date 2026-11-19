using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.Api;

public static partial class CustomCurves
{
    private static IEnumerable<ObservationWindow> HistoryWindows(InputFrame frame,ImmutableArray<SourceLeaf> leaves)=>
        leaves.Where(leaf=>leaf.ExactObservation is null).Select(leaf=>new ObservationWindow(leaf.Series.Id,leaf.Source.Code,
            PreviewData.RoleCode(leaf.Role),leaf.FinalFrom??frame.At,frame.At));

    // One full preview, on its authorized connection and projected revision. Exact frame and publication pins
    // still take their own admission paths. A preview without historical reads never plans or queries history.
    internal sealed class PreviewHistory(Func<Action<ObservationWindow>,CancellationToken,Task> plan,
        Func<ImmutableArray<ObservationWindow>,CancellationToken,Task<PreviewData>> read)
    {
        private readonly HashSet<ObservationWindow> windows=[];
        private PreviewData? selected;
        private bool attempted;

        internal async Task<PreviewData?> Find(ImmutableArray<ObservationWindow> requested,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if(requested.IsEmpty||!PreviewDatabase.ValidObservationWindows(requested))return null;
            if(!attempted)
            {
                attempted=true;
                var fits=true;
                void Collect(ObservationWindow window)
                {
                    if(!fits)return;
                    windows.Add(window);
                    fits=window.From<=window.To&&windows.Count<=PreviewDatabase.MaximumObservationWindows;
                }
                // Planning can encounter an invalid later date. Let the sequential consumer report its own first refusal.
                try{await plan(Collect,ct);}
                catch(Exception error)when(error is MarketDataException or TemporalException){fits=false;}
                ct.ThrowIfCancellationRequested();
                if(fits&&windows.Count>0)
                    selected=await read([..windows],ct);
                else windows.Clear();
            }
            return selected is not null&&requested.All(windows.Contains)?selected:null;
        }
    }
}
