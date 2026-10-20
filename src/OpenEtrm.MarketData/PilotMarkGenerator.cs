using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace OpenEtrm.MarketData;

public sealed record PilotPriceAnchor(SeriesKey Series,ObservationKey Observation,ReferenceKey Source,string SourceKind,
    DateTimeOffset ObservedAt,ObservationRole Role,ExactDecimal Value,QuoteSpecification Quote);
public sealed record PilotGeneratedPrice(PilotPriceAnchor Anchor,int MoveTicks,ExactDecimal Value);

public static class PilotMarkGenerator
{
    public const string Implementation="seeded_clearing_tick_move_v1";
    public const int MaximumTickMove=10000;
    public static PilotGeneratedPrice Generate(PilotPriceAnchor anchor,DateTimeOffset closeAt,int seed,int maximumTicks,string implementation)
    {
        if(anchor is null||anchor.Quote is not {} quote||implementation!=Implementation||seed<0||maximumTicks is <1 or >MaximumTickMove||
            anchor.Series.Revision.CatalogId==Guid.Empty||anchor.Series.Revision.RevisionId==Guid.Empty||anchor.Series.Id==Guid.Empty||
            anchor.Observation.CatalogId!=anchor.Series.Revision.CatalogId||anchor.Observation.Id==Guid.Empty||
            anchor.SourceKind!="synthetic"||anchor.Source.Revision!=quote.Key.Revision||string.IsNullOrWhiteSpace(anchor.Source.Code)||
            anchor.Role is not(ObservationRole.DailySettlement or ObservationRole.TraderMark)||
            anchor.ObservedAt.Offset!=TimeSpan.Zero||closeAt.Offset!=TimeSpan.Zero||closeAt.Year is <1901 or >2199||
            closeAt.Ticks%10!=0||anchor.ObservedAt>=closeAt||quote.ClearingIncrement.CompareTo(default)<=0||
            quote.Minimum.CompareTo(quote.Maximum)>0||anchor.Value.CompareTo(quote.Minimum)<0||anchor.Value.CompareTo(quote.Maximum)>0||
            anchor.Role==ObservationRole.DailySettlement&&!anchor.Value.IsMultipleOf(quote.ClearingIncrement))throw Invalid();
        _=PriceUnit.FromQuote(quote);
        // SHA256 is a reproducible pilot-data generator, not a market model or
        // random risk scenario. Each close retains this exact encoding/version.
        var encoded=string.Join('\n',Implementation,seed.ToString(CultureInfo.InvariantCulture),closeAt.ToString("O",CultureInfo.InvariantCulture),
            anchor.Series.Revision.CatalogId.ToString("D"),anchor.Series.Revision.RevisionId.ToString("D"),anchor.Series.Id.ToString("D"),anchor.Observation.Id.ToString("D"));
        var hash=SHA256.HashData(Encoding.UTF8.GetBytes(encoded));
        var move=(int)(BinaryPrimitives.ReadUInt64BigEndian(hash)%(ulong)(2*maximumTicks+1))-maximumTicks;
        var scaled=anchor.Value.ScaledValue+quote.ClearingIncrement.ScaledValue*move;
        if(scaled<quote.Minimum.ScaledValue||scaled>quote.Maximum.ScaledValue)throw Invalid();
        return new(anchor,move,ExactDecimal.FromScaled(scaled));
    }
    private static MarketDataException Invalid()=>new("pilot_generation","Choose a synthetic price source, exact prior mark, supported explicit generator settings and a later covered UTC close. Generated prices must remain within the quote domain.");
}
