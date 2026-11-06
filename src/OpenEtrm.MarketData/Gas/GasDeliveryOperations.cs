using System.Collections.Immutable;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Gas;

public sealed record GasSchedulingDecision(bool Approved,ExactQuantity? Quantity,DateTimeOffset ResponseAt,string Evidence);
public sealed record GasAcceptedSchedule(ExactQuantity Confirmed,ExactQuantity Scheduled);
public static class GasNominationWorkflow
{
    public const string Profile="manual_confirmed_ceiling_v1";
    public static string Phase(GasDailyContract day,DateTimeOffset submittedAt,DateTimeOffset recordedAt)
    {
        if(day is null||day.Start>=day.End||!TradingInstant.IsSupported(submittedAt)||!TradingInstant.IsSupported(recordedAt)||submittedAt>recordedAt)
            throw Error("Supply retained native bounds and an actual submission instant no later than the recording context.");
        return submittedAt<day.Start?"before_flow":submittedAt<day.End?"intraday":"retro";
    }
    public static GasAcceptedSchedule Accept(GasDailyContract day,ExactQuantity requested,GasSchedulingDecision counterparty,
        GasSchedulingDecision pipeline,bool withdrawn,DateTimeOffset at,string profile)
    {
        if(profile!=Profile||withdrawn||day is null||requested is null||requested.NumeratorValue.Sign<0||
            counterparty is null||pipeline is null||!counterparty.Approved||!pipeline.Approved||
            counterparty.Quantity is not {} confirmed||pipeline.Quantity is not {} scheduled||
            confirmed.Specification!=day.Quantity.Specification||scheduled.Specification!=requested.Specification||
            requested.Specification!=day.Quantity.Specification||confirmed.NumeratorValue.Sign<0||scheduled.NumeratorValue.Sign<0||
            !TradingInstant.IsSupported(at)||!TradingInstant.IsSupported(counterparty.ResponseAt)||!TradingInstant.IsSupported(pipeline.ResponseAt)||
            counterparty.ResponseAt>at||pipeline.ResponseAt>at||string.IsNullOrWhiteSpace(counterparty.Evidence)||string.IsNullOrWhiteSpace(pipeline.Evidence)||
            Compare(scheduled,confirmed)>0||Compare(scheduled,requested)>0)
            throw Error("The selected profile requires compatible approved counterpart/pipeline quantities, no withdrawal and a schedule within both confirmed and requested totals.");
        return new(confirmed,scheduled);
    }
    internal static int Compare(ExactQuantity a,ExactQuantity b)=>
        (a.NumeratorValue*b.DenominatorValue).CompareTo(b.NumeratorValue*a.DenominatorValue);
    static MarketDataException Error(string message)=>new("gas_operations_nomination",message);
}

public sealed record GasBalanceScope(ReferenceKey Pipeline,ReferenceKey Zone,ReferenceKey Product,ReferenceKey Quantity,
    DateTimeOffset Start,DateTimeOffset End);
public sealed record GasBalanceStatement(Guid Id,ExactQuantity? Balance,string Status,Guid? IncludedMovementId);
public sealed record GasBalanceMovement(Guid Id,int Ordinal,ExactQuantity Quantity);
public sealed record GasImbalanceCashTerms(UnitPrice Price,string OwnedRole,int DecimalPlaces,string RoundingPolicy,
    DateTimeOffset AgreedAt,DateTimeOffset DueAt);
public sealed record GasImbalanceTransfer(ExactQuantity BuyerMovement,ExactQuantity SellerMovement,ExactQuantity BuyerBalance,
    ExactQuantity SellerBalance,ExactMoney ExactCash,Money Cash);

public static class GasImbalanceTrading
{
    public const string Profile="paired_offsetting_imbalance_v1";
    public const string FinalPolicy="final_statement_balance_v1",ReportedPolicy="reported_statement_balance_v1";
    public static ExactQuantity Balance(GasBalanceStatement statement,ImmutableArray<GasBalanceMovement> movements,string policy)
    {
        if(statement is null||statement.Id==Guid.Empty||statement.Balance is not {} reported||
            policy is not(FinalPolicy or ReportedPolicy)||statement.Status is not("final" or "provisional")||
            policy==FinalPolicy&&statement.Status!="final"||movements.IsDefault||
            movements.Any(m=>m is null||m.Id==Guid.Empty||m.Quantity is null||m.Quantity.Specification!=reported.Specification)||
            movements.Select(m=>m.Id).Distinct().Count()!=movements.Length||
            !movements.Select(m=>m.Ordinal).Order().SequenceEqual(Enumerable.Range(1,movements.Length)))
            throw Error("Supply an eligible statement and complete accepted movement lineage under an explicit source policy.");
        var included=statement.IncludedMovementId is {} id?
            movements.SingleOrDefault(m=>m.Id==id)?.Ordinal??throw Error("The statement's exact included movement is unavailable."):0;
        return movements.Where(m=>m.Ordinal>included).Aggregate(reported,(balance,m)=>balance.Add(m.Quantity));
    }
    public static GasImbalanceTransfer Transfer(GasBalanceScope buyer,GasBalanceScope seller,ExactQuantity buyerBalance,
        ExactQuantity sellerBalance,ExactQuantity quantity,GasImbalanceCashTerms terms,DateTimeOffset at,string profile)
    {
        if(buyer is null||seller is null||buyer!=seller||buyer.Start>=buyer.End||buyerBalance is null||sellerBalance is null||
            quantity is null||terms is null||terms.Price is null||profile!=Profile||
            buyerBalance.Specification!=buyer.Quantity||sellerBalance.Specification!=buyer.Quantity||quantity.Specification!=buyer.Quantity||
            terms.Price.Unit.Quantity!=buyer.Quantity||buyerBalance.NumeratorValue.Sign>=0||sellerBalance.NumeratorValue.Sign<=0||quantity.NumeratorValue.Sign<=0||
            terms.OwnedRole is not("buyer" or "seller")||terms.RoundingPolicy!=GasCashBilling.RoundingImplementation||terms.DecimalPlaces is <0 or >12||
            !TradingInstant.IsSupported(at)||!TradingInstant.IsSupported(terms.AgreedAt)||!TradingInstant.IsSupported(terms.DueAt)||
            terms.AgreedAt<buyer.End||terms.AgreedAt>at||terms.DueAt<terms.AgreedAt||
            GasNominationWorkflow.Compare(quantity,sellerBalance)>0||GasNominationWorkflow.Compare(quantity,buyerBalance.Scale(ExactDecimal.Parse("-1")))>0)
            throw Error("Select compatible after-flow accounts, positive offsetting capacity and explicit fixed cash terms; quantities cannot cross zero.");
        var outgoing=quantity.Scale(ExactDecimal.Parse("-1"));
        var exact=quantity.Times(terms.Price).Scale(ExactDecimal.Parse(terms.OwnedRole=="buyer"?"-1":"1"));
        var cash=exact.ToMoney(new AmountFormat(terms.DecimalPlaces,CashTieRule.ToEven,false,false));
        return new(quantity,outgoing,buyerBalance.Add(quantity),sellerBalance.Add(outgoing),exact,cash);
    }
    static MarketDataException Error(string message)=>new("gas_operations_balance",message);
}
