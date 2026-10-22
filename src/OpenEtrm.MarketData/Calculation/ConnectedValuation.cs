using System.Collections.Immutable;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Calculation;

// Managed prototype boundary for an already-authorized, pinned instrument.
// Persisted custom-index booking and hosted portfolio jobs require an adapter
// that materializes these same exact bindings; this is not an admission API.
public sealed class ConnectedValuation
{
    public ConnectedScenario Scenario{get;}
    public MarketSnapshot Snapshot{get;}
    private ConnectedValuation(ConnectedScenario scenario,MarketSnapshot snapshot)
    {Scenario=scenario;Snapshot=snapshot;}

    // Validate the shared source closure once, outside the per-instrument loop.
    public static ConnectedValuation Prepare(ConnectedScenario scenario,MarketSnapshot snapshot,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();
        if(scenario is null||snapshot is null||snapshot.Id==Guid.Empty||snapshot.Definitions.CatalogId==Guid.Empty||
            snapshot.Definitions.RevisionId==Guid.Empty||!TradingInstant.IsSupported(snapshot.ValuationAt)||!TradingInstant.IsSupported(snapshot.MarketCutoff)||
            !snapshot.HasCoherentSelections()||snapshot.SelectionPolicy!=InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering||
            snapshot.ValuationAt!=scenario.Plan.Context.ValuationAt||snapshot.MarketCutoff!=scenario.Plan.Context.MarketAsOf)
            throw new MarketDataException("instrument_context","Supply one pinned graph scenario, compatible snapshot and exact instrument pricing bindings.");
        var selected=snapshot.Selections.ToDictionary(s=>s.Input);
        foreach(var primitive in scenario.Plan.Primitives)
        {
            cancellation.ThrowIfCancellationRequested();var source=primitive.Source;
            if(!selected.TryGetValue(source.Input,out var retained)||retained.Observation!=source.Observation)
                throw new MarketDataException("instrument_context","The instrument snapshot must retain the graph's exact source observations and use roles.");
        }
        return new(scenario,snapshot);
    }

    public ConnectedValuation WithScenario(ConnectedScenario scenario)
    {
        if(scenario is null||!ReferenceEquals(scenario.Plan,Scenario.Plan))
            throw new MarketDataException("instrument_context","Scenario reuse requires the exact prepared plan and retained context.");
        return new(scenario,Snapshot);
    }

    public InstrumentValuation PV(Instrument instrument,ImmutableDictionary<CurveRequest,CurvePointKey> bindings,
        ISideDiscounting? discounting=null,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();
        if(instrument is null||bindings is null)
            throw new MarketDataException("instrument_context","Supply an exact instrument and its declared connected pricing bindings.");
        var scenario=Scenario;var snapshot=Snapshot;
        var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,discounting);
        var declaration=instrument.Declare(new UnitValuationContext(snapshot,setup));
        if(declaration is null||declaration.Id!=instrument.Id||string.IsNullOrWhiteSpace(declaration.ModelVersion)||
            declaration.Currency!=instrument.Currency||declaration.Quantity!=instrument.UnitQuantity.Specification||
            instrument.UnitQuantity.Amount!=ExactDecimal.Parse("1")||declaration.Dependencies.IsDefault||
            declaration.Dependencies.Distinct().Count()!=declaration.Dependencies.Length)
            throw new MarketDataException("risk_contract","The instrument must declare its exact inputs, model, currency and normalized unit basis.");
        if(!declaration.Dependencies.ToHashSet().SetEquals(bindings.Keys))
            throw new MarketDataException("risk_dependency","Connected pricing bindings must match the instrument's complete declared input set.");
        foreach(var point in bindings.Values)
        {cancellation.ThrowIfCancellationRequested();scenario.Plan.GetPoint(point);}
        var context=new SideValuationContext(snapshot,discounting,declaration.Dependencies,
            request=>bindings.TryGetValue(request,out var point)?scenario.Price(point,cancellation):
                throw new MarketDataException("risk_dependency","The requested connected curve input is undeclared."),
            ConnectedCurves.NumericalPolicy,scenario.Id);
        var value=instrument.PV(context);
        cancellation.ThrowIfCancellationRequested();
        if(value is null||value.InstrumentId!=instrument.Id||value.Currency!=instrument.Currency||
            value.UnitQuantity!=instrument.UnitQuantity||!ReferenceEquals(value.Context,context))
            throw new MarketDataException("instrument_context","The returned PV must belong to this instrument and its active connected scenario.");
        return value;
    }
}
