using System.Collections.Immutable;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

// Known-at-source native payments remain actual facts even when their cash
// instant lies after the opening economic instant. No hypothetical payment is
// admitted here and no effective-time filter silently removes their evidence.
internal static class SwapKnownPaymentStore
{
    internal static async Task<SwapKnownPaymentHistory> Read(NpgsqlConnection connection,PreviewDatabase database,InputFrame frame,
        ScheduleContext context,AccountingPositionKey owner,int revision,CancellationToken ct)
    {
        var source=PinnedContext.Exact(context);var date=PublishedSchedules.ContextDate(source);
        if(owner.Owner.Kind!=AccountingOwnerKind.Trade||!owner.IsValid||revision<1||frame.ContextId!=source.ContextId||frame.Time.Date!=date)
            throw new MarketDataException("lifecycle_swap_source","Supply the exact held native owner, revision and certified source frame.");
        var value=await LifecycleNative.Read(connection,"SELECT lifecycle.read_swap_payment_history($1,$2,$3,$4,$5,$6,$7)::text",ct,
            database.Manifest.CatalogId,owner.TradeId,owner.BindingId,revision,source.ContextId!.Value,frame.At,date);
        var until=value.Instant("authorityUntil");database.RegisterCommitAdmission(connection,new LifecycleReadAdmission(database.Manifest.CatalogId,until));
        var known=SchedulePreview.ParseAt(source.KnownAt);
        if(value.Id("contextId")!=source.ContextId||value.Instant("knownAt")!=known||value.Instant("asOf")!=frame.At||DateWorkspace.Date(value.Text("businessDate"))!=date||
            value.Id("tradeId")!=owner.TradeId||value.Id("bindingId")!=owner.BindingId||value.Id("bookId")!=owner.BookId||value.GetProperty("revision").GetInt32()!=revision)
            throw new MarketDataException("lifecycle_swap_source","Native history must retain this exact source owner, business date and knowledge cutoff.");
        var payments=value.GetProperty("payments").EnumerateArray().Select(p=>
        {
            var currency=new ReferenceKey(new(p.Id("referenceCatalogId"),p.Id("referenceRevisionId")),p.Text("currency"));
            var allocations=p.GetProperty("allocations").EnumerateArray().Select(a=>new SwapKnownPaymentAllocation(a.Id("contributionId"),a.GetProperty("membershipOrdinal").GetInt32(),
                DateWorkspace.Date(a.Text("deliveryFrom")),DateWorkspace.Date(a.Text("deliveryTo")),ExactMoney.Parse(a.Text("amount"),currency))).ToImmutableArray();
            return new SwapKnownPayment(p.Id("paymentId"),p.OptionalId("reversesId"),p.Instant("effectiveAt"),p.Instant("knownAt"),p.GetProperty("publicationOrder").GetInt32(),
                ExactMoney.Parse(p.Text("amount"),currency),allocations);
        }).ToImmutableArray();
        var determinations=value.GetProperty("determinations").EnumerateArray().Select(d=>
        {
            var currency=new ReferenceKey(new(d.Id("referenceCatalogId"),d.Id("referenceRevisionId")),d.Text("currency"));
            return new SwapKnownDetermination(d.Id("contributionId"),d.OptionalId("previousId"),d.GetProperty("membershipOrdinal").GetInt32(),
                DateWorkspace.Date(d.Text("deliveryFrom")),DateWorkspace.Date(d.Text("deliveryTo")),d.Instant("effectiveAt"),d.Instant("dueAt"),d.Instant("knownAt"),
                d.GetProperty("publicationOrder").GetInt32(),ExactMoney.Parse(d.Text("roundedTotal"),currency),ExactMoney.Parse(d.Text("contribution"),currency));
        }).ToImmutableArray();
        return new(owner,source.ContextId.Value,known,payments){Determinations=determinations};
    }
}
