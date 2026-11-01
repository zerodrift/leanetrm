using System.Collections.Immutable;
using System.Globalization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Crude;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal sealed record CrudePortfolioInput(PreviewData Data, InputFrame Frame, MarketSnapshot Snapshot,
    ValuationPortfolio Portfolio, ImmutableArray<PortfolioPositionEvidence> Evidence)
{
    internal ImmutableArray<SchedulePriceTerm> ActivePrices => [..Portfolio.Instruments.Cast<RetainedCrudeInstrument>()
        .SelectMany(i => i.ActivePrices).Distinct()];
}

// Authorized retained programs enter the same portfolio/scenario engine as financial holdings.
internal static class CrudePortfolio
{
    internal static async Task<ImmutableArray<CrudePortfolioInput>> Prepare(NpgsqlConnection connection,
        PreviewDatabase database, InputFrame frame, ImmutableArray<CrudePosition> positions,
        ImmutableArray<(PreviewData Data, InputFrame Frame)> supplied, bool requirePinnedSources, CancellationToken ct)
    {
        if (positions.IsDefault || supplied.IsDefault || frame.ContextId is not {} contextId)
            throw Invalid("Select explicit positions and source frames under one certified calculation context.");
        if (positions.IsEmpty) return [];
        var context = PinnedContext.Exact((await new KnowledgeStore(database.Manifest.CatalogId).Read(connection, contextId, ct)).Context);
        var result = ImmutableArray.CreateBuilder<CrudePortfolioInput>();
        var store = new PhysicalCrudeStore(database);
        var retained = new RetainedScheduleStore(database);
        foreach (var group in positions.GroupBy(p => p.Definition).OrderBy(g => g.Key))
        {
            var candidates = supplied.Where(s => s.Data.DefinitionRevision.RevisionId == group.Key).ToArray();
            if (candidates.Length > 1 || candidates.Length == 0 && requirePinnedSources)
                throw Invalid("Every crude market requires exactly one admitted source frame; retained jobs cannot select a replacement market.");
            var source = candidates.Length == 1 ? candidates[0] :
                await PhysicalMarketSelection.Crude.Read(database, connection, frame, ct, group.First().Trade);
            if (source.Data.DefinitionRevision.RevisionId != group.Key || source.Frame.At != frame.At || source.Frame.ContextId != contextId ||
                group.Any(p => p.Reference != source.Data.ReferenceRevision))
                throw Invalid("Crude holdings require their exact source/reference revisions, valuation instant and certified context.");
            var snapshot = MarketSnapshotBinding.Bind(source.Data.Snapshot(source.Frame), contextId, SchedulePreview.ParseAt(context.KnownAt));
            var instruments = new Dictionary<Guid, RetainedCrudeInstrument>();
            var trades = ImmutableArray.CreateBuilder<Trade>();
            var evidence = ImmutableArray.CreateBuilder<PortfolioPositionEvidence>();
            foreach (var position in group)
            {
                if (!instruments.TryGetValue(position.Binding, out var instrument))
                {
                    var assignment = await store.Metadata(connection, source.Data, position.Terms(source.Frame.Id), ct);
                    var programs = await retained.LoadPrograms(connection, position.Binding, contextId, frame.At, ct);
                    if (programs.Length != (position.Head.OptionalText("handling_fee") is null ? 2 : 3) ||
                        !programs.Select(p => p.Slot).Order().SequenceEqual(Enumerable.Range(0, programs.Length)))
                        throw Invalid("Every held crude instrument requires its complete retained delivery and cash programs.");
                    var members = programs.OrderBy(p => p.Slot).Select(p => new RetainedCrudeMember(p.SideId,
                        ExactDecimal.Parse(position.Head.Text(p.Slot switch { 0 => "direction", 1 => "consideration_direction", _ => "handling_fee_direction" })), p.Program)).ToImmutableArray();
                    instrument = new(position.Binding, assignment, members);
                    instruments.Add(position.Binding, instrument);
                }
                var signs = new[] { "direction", "consideration_direction", "handling_fee_direction" };
                if (instrument.Members.Where((member, slot) => member.Coefficient != ExactDecimal.Parse(position.Head.Text(signs[slot]))).Any())
                    throw Invalid("A retained instrument identity cannot be reused with different membership directions.");
                if (instrument.Currency != position.Currency || instrument.UnitQuantity.Specification != position.Quantity)
                    throw Invalid("The held quantity and currency must match the exact retained assignment.");
                var quantity = new Quantity(position.TotalQuantity, position.Quantity);
                var book = position.Book.Id("id");
                trades.Add(new(position.Trade, book, [new(position.Binding, instrument, quantity)]));
                evidence.Add(new(position.Trade, int.Parse(position.Head.Text("revision"), CultureInfo.InvariantCulture),
                    position.Binding, position.Binding, position.Binding, book, CrudePositions.Measure, group.Key,
                    int.Parse(position.Head.Text("publication_order"), CultureInfo.InvariantCulture), quantity)
                {
                    TradeReference = TradingReference.Trade(position.Head),
                    InstrumentReference = TradingReference.Instrument(position.Head), InstrumentType = CrudePositions.Type
                });
            }
            var portfolio = ValuationPortfolio.FromTrades(trades.ToImmutable(), new(ValuationMeasure.PresentValue,
                ImmutableDictionary<Guid, FuturesMarketBinding>.Empty, new ExplicitDiscounting(ExactDecimal.Parse("1"), DiscountFactorSource.Undiscounted)));
            result.Add(new(source.Data, source.Frame, snapshot, portfolio, evidence.ToImmutable()));
        }
        return result.ToImmutable();
    }

    internal static async Task<ImmutableDictionary<Guid, ExactMoney>> Values(NpgsqlConnection connection,
        PreviewDatabase database, InputFrame frame, ImmutableArray<CrudePosition> positions, CancellationToken ct)
    {
        var groups = await Prepare(connection, database, frame, positions, [], false, ct);
        var values = ImmutableDictionary.CreateBuilder<Guid, ExactMoney>();
        if (groups.IsEmpty) return values.ToImmutable();
        var context = PinnedContext.Exact((await new KnowledgeStore(database.Manifest.CatalogId).Read(connection, frame.ContextId!.Value, ct)).Context);
        foreach (var group in groups)
        {
            var dependencies = group.Portfolio.Declare(group.Snapshot).SelectMany(c => c.Dependencies).Distinct().ToImmutableArray();
            var market = await CustomCurves.PrepareMarket(connection, database, group.Data, group.Frame, group.Snapshot,
                context, group.Portfolio.Instruments, dependencies, CustomCurves.MarketPreparation.ValuationDependencies, ct, activePrices: group.ActivePrices);
            var prepared = RiskEngine.PrepareConnected(market.Valuation, market.Bindings, [group.Portfolio], market.Authority, ct);
            var run = RiskEngine.CalculateExact(prepared, [], RiskEngine.ExactNumericalPolicy, ct, RiskDetail.Holdings);
            foreach (var value in run.Groups.Single().BasePositions) values.Add(value.BindingId, value.Value);
        }
        return values.ToImmutable();
    }

    private static MarketDataException Invalid(string message) => new("crude_market_context", message);
}
