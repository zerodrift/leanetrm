using System.Collections.Immutable;
using System.Numerics;

namespace OpenEtrm.MarketData.Trading;

public sealed record SwapNetTerms(PartyKey LegalEntity,PartyKey Counterparty,SwapSettlementRounding Rounding,
    string DuePolicy,string RecognitionPolicy,string CorrectionPolicy);
public sealed record SwapNetCandidate(Guid TradeId,SettlementContribution Contribution,SwapNetTerms Terms);
public sealed record SwapNetAllocation(Guid DeterminationId,ExactMoney Amount);
public sealed record SwapNetPlan(ExactMoney Cash,ExactMoney GrossPaid,ExactMoney GrossReceived,ImmutableArray<SettlementContribution> Remaining)
{
    public bool IsNoncashDischarge=>Cash==ExactMoney.Parse("0",Cash.Currency);
}
public static class SwapNetSettlement
{
    public const string Policy="explicit_same_due_swap_net_v1";
    public static SwapNetPlan Calculate(ImmutableArray<SwapNetCandidate> candidates,ImmutableArray<SwapNetAllocation> allocations,ExactMoney cash,string policy)
    {
        static MarketDataException Invalid()=>new("settlement_net_allocation","Select explicit eligible gross balances in one trade, exact currency, parties and due instant, with conserved signed allocations.");
        if(policy!=Policy||cash is null||candidates.IsDefault||allocations.IsDefault||candidates.Length is <2 or >256||
            candidates.Length!=allocations.Length||allocations.Any(a=>a is null)||candidates.Any(c=>c is null||c.Contribution is null||c.Terms is null)||
            candidates.Select(c=>c.Contribution.Id).Distinct().Count()!=candidates.Length||
            !candidates.Select(c=>c.Contribution.Id).ToHashSet().SetEquals(allocations.Select(a=>a.DeterminationId))||
            allocations.Select(a=>a.DeterminationId).Distinct().Count()!=allocations.Length)throw Invalid();
        var first=candidates[0];var terms=first.Terms;
        if(first.TradeId==Guid.Empty||terms.LegalEntity.Id==Guid.Empty||terms.Counterparty.Id==Guid.Empty||terms.LegalEntity.CatalogId==Guid.Empty||
            terms.Counterparty.CatalogId!=terms.LegalEntity.CatalogId||terms.LegalEntity.Revision<=0||terms.Counterparty.Revision<=0||terms.Rounding is null||
            terms.DuePolicy!="swap_due_nominal_v1"||terms.RecognitionPolicy!="swap_completed_period_trading_pnl_v1"||
            terms.CorrectionPolicy!="swap_explicit_correction_due_v1"||terms.Rounding.Currency!=cash.Currency)throw Invalid();
        var zero=ExactMoney.Parse("0",cash.Currency);var sum=zero;var paid=zero;var received=zero;
        var remaining=ImmutableArray.CreateBuilder<SettlementContribution>();
        static int Sign(ExactMoney m)=>BigInteger.Parse(m.Numerator,System.Globalization.CultureInfo.InvariantCulture).Sign;
        foreach(var candidate in candidates)
        {
            var c=candidate.Contribution;var amount=allocations.Single(a=>a.DeterminationId==c.Id).Amount;
            if(candidate.TradeId!=first.TradeId||candidate.Terms!=terms||c.DueAt!=first.Contribution.DueAt||
                c.Id==Guid.Empty||amount is null||amount.Currency!=cash.Currency||c.Amount.Currency!=cash.Currency||c.Applied.Currency!=cash.Currency||
                amount!=ExactMoney.FromMoney(amount.ToMoney(terms.Rounding.Format))||Sign(amount)==0||Sign(amount)!=Sign(c.Outstanding)||
                Sign(c.Outstanding.Subtract(amount))!=0&&Sign(c.Outstanding.Subtract(amount))!=Sign(c.Outstanding))throw Invalid();
            sum=sum.Add(amount);if(Sign(amount)<0)paid=paid.Add(amount);else received=received.Add(amount);
            remaining.Add(c with{Applied=c.Applied.Add(amount)});
        }
        if(sum!=cash)throw Invalid();
        return new(cash,paid,received,remaining.ToImmutable());
    }
}
