using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Power;

namespace OpenEtrm.Api;

internal sealed partial class PricePublicationStore
{
    internal async Task<object> PowerProducts(NpgsqlConnection connection, PinnedContext pin, CancellationToken ct)
    {
        ScheduleReads.RequireCurrent(pin);
        var products = await new ScheduleReads(database).PublishedProducts(connection, pin, ct);
        return new
        {
            products = products.Available.Select(p => new { p.Code, p.Name, p.Version }),
            blocks = PowerPreview.Blocks.Select(b => new { code = b.Code, name = b.Name }),
            averaging = new[] { new { code = IntervalPricing.AggregatePolicy, name = "Native-hour duration-weighted mean (diagnostic)" } }
        };
    }

    internal async Task<object> InspectPowerProduct(NpgsqlConnection connection, PinnedContext pin,
        PowerProductPreviewRequest request, CancellationToken ct)
    {
        ScheduleReads.RequireCurrent(pin);
        if (request.Capture is null || string.IsNullOrWhiteSpace(request.Product) ||
            request.Averaging != IntervalPricing.AggregatePolicy)
            throw new MarketDataException("power_price_average", "Select an exact published TOU product, membership and supported diagnostic averaging policy.");
        var read = await ReadPowerCapture(connection, pin, request.Capture, ct, calendarDays: true);
        var products = await new ScheduleReads(database).PublishedProducts(connection, pin, ct);
        var product = PowerPreview.SelectProduct(products, request.Product, request.ProductVersion);
        if (product.Clock != read.Metadata.Clock)
            throw new MarketDataException("power_source_clock", "The product and captured source must use the same exact market clock.");
        var indexRow = PowerPreview.Index(read.Data, read.Index.Key.Code);
        var calendar = PowerPreview.Calendar(read.Data, indexRow);
        if (calendar.Text("clock_code") != product.Clock.Code)
            throw new MarketDataException("power_source_clock", "The index delivery calendar must use the selected product's exact clock.");
        var from = request.Capture.FromDate;
        var to = request.Capture.ThroughDate.AddDays(1); // Capture admission already checked supported date bounds.
        var holidays = PowerPreview.Holidays(read.Data, calendar, from, to);
        var clock = PowerClock.FromRule(product.Clock, TimeZoneDatabase.Installed());
        var mask = IntervalBlocks.Mask(PowerPreview.Membership(request.Block), read.Metadata.Minutes, product.Peak);
        var selected = PowerHours.Compile(from, to, clock, mask, holidays, read.Metadata.Minutes);
        var quantity = PpaQuantitiesStore.Quantity(read.Data, read.Index.Unit.Quantity.Code);
        var index = new PowerIndex(read.Index.Key, read.Index.Unit.Currency, quantity.Unit, quantity.Key, "Collected", request.Capture.Market);
        var prices = read.History.Rows.ToDictionary(r => r.Start);
        (int Missing, bool Complete, string State, ExactUnitPrice? Average) Summary(PowerInterval[] cells)
        {
            var missing = cells.Count(c => !prices.ContainsKey(c.Start));
            var complete = cells.Length > 0 && missing == 0;
            var fixings = complete ? cells.Select(c => new PowerFixing(c.Start, c.End, index, prices[c.Start].Price.Value, null)).ToArray() : [];
            return (missing, complete, cells.Length == 0 ? "outside_product" : missing > 0 ? "incomplete_capture" : "complete_capture",
                complete ? PowerSettlement.Average(cells, fixings, index, request.Averaging) : null);
        }
        var summary = Summary(selected);
        var byDay = selected.GroupBy(c => c.OperatingDate).ToDictionary(g => g.Key, g => g.ToArray());
        return new
        {
            capture = read.Receipt, product, request.Block, request.Averaging,
            calendar = new { reference = read.Data.ReferenceRevision, code = calendar.Text("code"), holidays,
                coverageFrom = calendar.Text("coverage_from"), coverageTo = calendar.Text("coverage_to") },
            expectedIntervals = selected.Length, missingIntervals = summary.Missing, complete = summary.Complete, state = summary.State,
            average = summary.Average, formattedAverage = summary.Average?.ToExactString(), selectedStarts = selected.Select(c => c.Start),
            days = read.Window.GroupBy(c => c.OperatingDate).Select(day =>
            {
                var cells = byDay.GetValueOrDefault(day.Key, []); var daily = Summary(cells);
                return new { operatingDate = day.Key, expectedIntervals = cells.Length, missingIntervals = daily.Missing,
                    complete = daily.Complete, state = daily.State, average = daily.Average, formattedAverage = daily.Average?.ToExactString() };
            }).ToArray()
        };
    }
}
