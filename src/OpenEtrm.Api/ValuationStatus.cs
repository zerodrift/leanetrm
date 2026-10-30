using System.Collections.Immutable;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

// D220: the valuation failures that concern one position at the selected close. A trading read
// returns such a position with this code instead of failing, and a portfolio risk run leaves it out
// and lists it; every other failure still fails them.
static class ValuationStatus
{
    public static bool PositionSpecific(string? code)=>code is "source_expired" or "missing_observation" or "index_fixing_missing";
    public static bool PositionSpecific(MarketDataException error)=>PositionSpecific(error.Code);

    // One build values every request the close can value (D220). Only when it fails for a position-specific reason are the
    // requests that fail alone isolated, against the same snapshot: requests that build are passed in blocks that double in
    // size, and a block that fails is halved until one request fails alone. The builds therefore grow with the requests that
    // fail and not with those that do not, and never exceed three for each request. The rest are then built together. When no
    // request fails alone, or one fails alone for another reason, the failure is not one position's and the build fails.
    internal static (CurveBuild? Build,ImmutableDictionary<CurveRequest,string> Unvalued) BuildValued(Func<ImmutableArray<CurveRequest>,CurveBuild> build,ImmutableArray<CurveRequest> requests)
    {
        if(requests.IsEmpty)return(null,ImmutableDictionary<CurveRequest,string>.Empty);
        try{return(build(requests),ImmutableDictionary<CurveRequest,string>.Empty);}
        catch(MarketDataException error) when(PositionSpecific(error)){}
        var unvalued=ImmutableDictionary.CreateBuilder<CurveRequest,string>();
        var narrowing=false;
        for(int next=0,size=1;next<requests.Length;)
        {
            var block=ImmutableArray.Create(requests,next,Math.Min(size,requests.Length-next));
            try{build(block);next+=block.Length;if(!narrowing)size*=2;}
            catch(MarketDataException error) when(PositionSpecific(error))
            {
                if(block.Length==1){unvalued.Add(block[0],error.Code);next++;size=1;narrowing=false;}
                else{size=block.Length/2;narrowing=true;}
            }
        }
        var valued=requests.RemoveAll(unvalued.ContainsKey);
        return(valued.IsEmpty?null:build(valued),unvalued.ToImmutable());
    }
}
