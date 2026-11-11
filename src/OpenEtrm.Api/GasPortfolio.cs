using System.Collections.Immutable;
using System.Globalization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

// Measure: started-delivery trades and older programs are separate groups, each under its own measure code.
internal sealed record GasPortfolioInput(PreviewData Data,InputFrame Frame,MarketSnapshot Snapshot,
 ValuationPortfolio Portfolio,ImmutableArray<PortfolioPositionEvidence> Evidence,string Measure)
{
 internal ImmutableArray<SchedulePriceTerm> ActivePrices=>[..Portfolio.Instruments.Cast<RetainedGasInstrument>().SelectMany(i=>i.ActivePrices).Distinct()];
}

// Persistence supplies retained programs; pricing and factor discovery remain instrument operations.
internal static class GasPortfolio
{
 internal static async Task<ImmutableArray<GasPortfolioInput>> Prepare(NpgsqlConnection connection,PreviewDatabase database,
  InputFrame frame,ImmutableArray<GasPosition> positions,ImmutableArray<(PreviewData Data,InputFrame Frame)> supplied,
  bool requirePinnedSources,CancellationToken ct)
 {
  if(positions.IsDefault||supplied.IsDefault||frame.ContextId is not {} contextId)
   throw Invalid("Select explicit held positions and market frames under a retained context.");
  if(positions.IsEmpty)return [];
  var context=PinnedContext.Exact((await new KnowledgeStore(database.Manifest.CatalogId).Read(connection,contextId,ct)).Context);
  var store=new GasCompositionStore(database);var result=ImmutableArray.CreateBuilder<GasPortfolioInput>();
  foreach(var group in positions.GroupBy(p=>p.Definition).OrderBy(g=>g.Key))
  {
   var source = await PhysicalMarketSelection.Gas.PortfolioSource(database, connection, frame, group.Key,
       [..group.Select(p => p.Reference)], supplied, requirePinnedSources, ct);
   var snapshot=MarketSnapshotBinding.Bind(source.Data.Snapshot(source.Frame),contextId,SchedulePreview.ParseAt(context.KnownAt));
   var held=new List<(string Measure,Trade Trade,PortfolioPositionEvidence Evidence)>();
   var states=ImmutableDictionary.CreateBuilder<MeasureBinding,IBindingValuationState>();
   foreach(var position in group)
   {
    var execution=position.Head.GetProperty("composition").GetBoolean()
     ?await store.Load(connection,source.Data,source.Frame,position.Trade,ct)
     :await store.LoadSinglePoint(connection,source.Data,source.Frame,position.Trade,ct);
    var instrument=execution.Instrument([..execution.Programs.SelectMany(p=>BookedScheduleLookups.Dated(source.Data,p.Program)).Distinct()]);
    if(instrument.Id!=position.Binding||instrument.Currency!=position.Currency||instrument.UnitQuantity.Specification!=position.Quantity||
     execution.Quantity!=ExactDecimal.Parse(position.Head.Text("quantity")))
     throw Invalid("Held quantity, currency and instrument must match the retained gas programs.");
    var quantity=new Quantity(execution.Quantity,position.Quantity);var book=position.Book.Id("id");
    // A started-delivery trade is valued with its recorded cash; older programs keep their own refusals.
    if(execution.Started)states.Add(new(position.Trade,position.Binding),await GasCashPaymentStore.Read(connection,database.Manifest.CatalogId,position.Trade,position.Binding,quantity,GasCashPaymentStore.LatestKnownRevision,ct));
    var measure=execution.Started?GasPositions.LifecycleMeasure:GasPositions.Measure;
    held.Add((measure,new(position.Trade,book,[new(position.Binding,instrument,quantity)]),new(position.Trade,int.Parse(position.Head.Text("revision"),CultureInfo.InvariantCulture),position.Binding,
     instrument.Id,instrument.Id,book,measure,group.Key,int.Parse(position.Head.Text("publication_order"),CultureInfo.InvariantCulture),quantity)
     {TradeReference=TradingReference.Trade(position.Head),InstrumentReference=TradingReference.Instrument(position.Head),InstrumentType=GasPositions.Type}));
   }
   // Both admitted gas profiles explicitly select no discounting; each measure code is its own portfolio group.
   foreach(var measured in held.GroupBy(h=>h.Measure).OrderBy(g=>g.Key,StringComparer.Ordinal))
   {
    var portfolio=ValuationPortfolio.FromTrades([..measured.Select(h=>h.Trade)],new(ValuationMeasure.PresentValue,
     ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted)){BindingStates=states.Where(s=>measured.Any(h=>h.Trade.Id==s.Key.TradeId)).ToImmutableDictionary()});
    result.Add(new(source.Data,source.Frame,snapshot,portfolio,[..measured.Select(h=>h.Evidence)],measured.Key));
   }
  }
  return result.ToImmutable();
 }
 private static MarketDataException Invalid(string message)=>new("gas_market_context",message);
}
