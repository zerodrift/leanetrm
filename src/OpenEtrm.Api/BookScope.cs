using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

// Trading-directory reads remain under the caller's authorized connection.
// Selection never caches authority or interprets product-specific positions.
internal static class BookScope
{
    internal static Task<ImmutableArray<JsonElement>> Read(NpgsqlConnection connection, Guid catalog, CancellationToken ct) =>
        PreviewDatabase.Rows(connection,"""
            SELECT row_to_json(r)::text FROM(SELECT b.id,b.name,b.portfolio_id,p.name AS portfolio
            FROM trading.books b JOIN trading.portfolios p ON(p.catalog_id,p.id)=(b.catalog_id,b.portfolio_id)
            WHERE b.catalog_id=$1 ORDER BY b.name,b.id)r
            """, [catalog], ct);

    internal static void Validate(ImmutableArray<JsonElement> books, ImmutableArray<Guid> bookIds, ImmutableArray<Guid> portfolioIds)
    {
        if (books.IsDefault || bookIds.IsDefault || portfolioIds.IsDefault ||
            bookIds.Distinct().Count() != bookIds.Length || portfolioIds.Distinct().Count() != portfolioIds.Length ||
            bookIds.Any(id => !books.Any(book => book.Id("id") == id)) ||
            portfolioIds.Any(id => !books.Any(book => book.Id("portfolio_id") == id)))
            throw new MarketDataException("blotter_view", "Choose explicit available exact books and portfolios.");
    }

    internal static ImmutableArray<JsonElement> Select(ImmutableArray<JsonElement> books, ImmutableArray<Guid> bookIds, ImmutableArray<Guid> portfolioIds)
    {
        Validate(books, bookIds, portfolioIds);
        return [..books.Where(book => (bookIds.IsEmpty || bookIds.Contains(book.Id("id"))) &&
            (portfolioIds.IsEmpty || portfolioIds.Contains(book.Id("portfolio_id"))))];
    }
}
