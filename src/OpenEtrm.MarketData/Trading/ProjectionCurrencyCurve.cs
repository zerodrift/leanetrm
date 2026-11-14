namespace OpenEtrm.MarketData.Trading;

public sealed record ProjectionCurrencyCurveKey(CurveKey Curve,Guid MappingId,Guid DefinitionId,int DefinitionVersion);

// A scalar payment factor cannot define accumulation or an intraday roll.
// Providers implement this capability only from a captured native term structure.
public interface IProjectionCurrencyCurve:ISideDiscounting
{
    ReferenceKey Currency{get;}
    MarketSnapshot SourceSnapshot{get;}
    ProjectionCurrencyCurveKey CurveIdentity{get;}
    string TimingPolicy{get;}
}
