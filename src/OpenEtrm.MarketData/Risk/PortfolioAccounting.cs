using System.Collections.Immutable;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Risk;

// Complete, authorized ledger facts for one held occurrence at an exact context.
// An explicit empty ledger attests no cash; a missing capability attests nothing.
public sealed record BindingAccountingFacts(AccountingPositionKey Position,Guid ContextId,DateTimeOffset KnownAt,
    ImmutableArray<AccountingFlow> Flows);

public static partial class RiskEngine
{
    public static PnlAccountingPoint CaptureAccounting(PreparedConnectedPortfolioRisk prepared,PnlGreekRun greeks,CancellationToken cancellation=default)
    {
        if(greeks is null||!ReferenceEquals(prepared,greeks.Prepared))
            throw Error("pnl_greek_context","Accounting and Greeks must share the exact prepared valuation source.");
        return greeks.OpeningPoint(AccountingFlows(prepared,cancellation),PnlAccountingCoverage.Complete);
    }
    public static PnlAccountingPoint CaptureAccounting(PreparedConnectedPortfolioRisk prepared,CancellationToken cancellation=default)
    {
        var flows=AccountingFlows(prepared,cancellation);var context=prepared.Scenario.Plan.Context;
        var values=CalculateExact(prepared,[],ExactNumericalPolicy,cancellation,RiskDetail.Holdings);
        return new(context.ContextId,values.SnapshotId,context.ValuationAt,context.KnownAt,
            [..values.Groups.SelectMany(g=>g.BasePositions).Select(p=>new PnlPositionValue(new(p.TradeId,p.BindingId,p.BookId),p.Value))],
            flows,PnlAccountingCoverage.Complete){ValueSources=[prepared]};
    }
    internal static void RequireActualAccounting(PreparedConnectedPortfolioRisk prepared)
    {
        if(prepared.Scenario.ParentId is not null)
            throw Error("pnl_actual_context","Actual accounting requires the retained source base, not a hypothetical valuation layer.");
    }
    private static ImmutableArray<AccountingFlow> AccountingFlows(PreparedConnectedPortfolioRisk prepared,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if(prepared is null)throw Error("pnl_context","Supply an exact prepared valuation context.");
        var context=prepared.Scenario.Plan.Context;
        RequireActualAccounting(prepared);
        var flows=ImmutableArray.CreateBuilder<AccountingFlow>();
        foreach(var portfolio in prepared.Portfolios)
        {
            if(portfolio.Setup.Measure!=ValuationMeasure.PresentValue)
                throw Error("pnl_pv_basis","Trading P&L requires remaining PV. An execution-P&L or overdue-excluding measure cannot substitute for it.");
            foreach(var held in portfolio.Positions)
            {
                cancellation.ThrowIfCancellationRequested();
                var owner=new AccountingPositionKey(held.TradeId,held.BindingId,held.BookId);
                if(!portfolio.Setup.AccountingFacts.TryGetValue(new(held.TradeId,held.BindingId),out var facts)||facts.Position!=owner||
                    facts.ContextId!=context.ContextId||facts.KnownAt!=context.KnownAt||facts.Flows.IsDefault||
                    facts.Flows.Any(f=>f.Position!=owner||f.KnownAt>context.KnownAt||f.Amount.Currency!=held.FixedBasis.Currency))
                    throw Error("pnl_accounting_coverage","Every held occurrence requires complete authorized cash and transfer facts at its exact retained context.");
            }
            foreach(var (key,facts) in portfolio.Setup.AccountingFacts)
            {
                // Retired bindings may still carry cash in the interval. Ledger coverage is not limited to endpoint PV rows.
                if(facts is null||facts.Position.TradeId!=key.TradeId||facts.Position.BindingId!=key.BindingId||facts.Position.BookId==Guid.Empty||
                    facts.ContextId!=context.ContextId||facts.KnownAt!=context.KnownAt||facts.Flows.IsDefault||
                    facts.Flows.Any(f=>f.Position!=facts.Position||f.KnownAt>context.KnownAt))
                    throw Error("pnl_accounting_coverage","Every ledger-only contribution requires exact owned binding and knowledge coverage.");
                flows.AddRange(facts.Flows);
            }
        }
        return flows.ToImmutable();
    }
}
