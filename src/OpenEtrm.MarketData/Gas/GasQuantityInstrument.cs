using System.Text.Json.Serialization;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Gas;

// Quantity continuation uses the real ordinary valuation definition. A fixed
// delivery exchange keeps its principal leg; it is not replaced by a gas mark.
public interface IGasQuantityInstrument
{
    Guid Id{get;}
    CurveRequest Mark{get;}
    ReferenceKey QuantitySpecification{get;}
    ReferenceKey Currency{get;}
    DateTimeOffset? ReturnAt{get;}
    GasHoldingCarrying Carrying{get;}
    [JsonIgnore]Instrument ValuationInstrument{get;}
}
