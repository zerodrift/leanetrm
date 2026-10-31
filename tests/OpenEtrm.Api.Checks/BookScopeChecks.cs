using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

static class BookScopeChecks
{
    internal static void Run()
    {
        var first = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var second = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var third = Guid.Parse("00000000-0000-0000-0000-000000000003");
        var portfolio = Guid.Parse("00000000-0000-0000-0000-000000000004");
        var other = Guid.Parse("00000000-0000-0000-0000-000000000005");
        JsonElement Book(Guid id, Guid parent) => JsonSerializer.SerializeToElement(new { id, portfolio_id = parent, name = "Synthetic book" });
        ImmutableArray<JsonElement> visible = [Book(third, other), Book(first, portfolio), Book(second, portfolio)];
        Expect(BookScope.Select(visible, [], []), third, first, second);
        Expect(BookScope.Select(visible, [second, first], [portfolio]), first, second);
        Expect(BookScope.Select(visible, [third], [portfolio]));
        Expect(BookScope.Select(visible, [], [other]), third);
        Expect(BookScope.Select([], [], []));
        Reject(() => BookScope.Select(visible, default, []));
        Reject(() => BookScope.Select(visible, [], default));
        Reject(() => BookScope.Select(visible, [first, first], []));
        Reject(() => BookScope.Select(visible, [], [portfolio, portfolio]));
        Reject(() => BookScope.Select(visible, [Guid.NewGuid()], []));
        Reject(() => BookScope.Select(visible, [], [Guid.NewGuid()]));
        // A later call must resolve its own authorized directory, not reuse prior scope.
        Reject(() => BookScope.Select([visible[0]], [first], []));
        Console.WriteLine("PASS shared book scope: authorized directory, explicit filters, intersection and caller ordering");
    }

    private static void Expect(ImmutableArray<JsonElement> actual, params Guid[] expected)
    {
        if (!actual.Select(row => row.GetProperty("id").GetGuid()).SequenceEqual(expected))
            throw new Exception("Selected books must be the exact intersection in directory order.");
    }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (MarketDataException error) when (error.Code == "blotter_view") { return; }
        throw new Exception("Unavailable or ambiguous book scope was accepted.");
    }
}
