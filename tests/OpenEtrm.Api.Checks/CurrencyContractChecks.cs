using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

internal static class CurrencyContractChecks
{
    internal static void Run()
    {
        var plan=AssistantOperations.Catalog.Single(o=>o.Path=="settlement/payment-lineages/plan");
        var read=AssistantOperations.Catalog.Single(o=>o.Path=="settlement/payment-lineages/read");
        if(!plan.RequiresApproval||!plan.Permissions.Contains("ProposeSettlementDeterminations")||read.RequiresApproval||!read.Permissions.Contains("ProposeSettlementDeterminations"))
            throw new Exception("Payment continuity writes and retained reads must expose their actual authorization and effect.");
        var currency=new ReferenceKey(new(Guid.NewGuid(),Guid.NewGuid()),"synthetic_gbp");
        var labels=new ReferenceDisplay(new Dictionary<(string,ReferenceKey),string>
            {[("currency",currency)]="GBP",[("quantity",currency)]="GJ"}.ToImmutableDictionary());
        if(labels.Label(currency,ScheduleReferenceKinds.Currency)!="GBP"||labels.Label(currency,ScheduleReferenceKinds.Quantity)!="GJ")
            throw new Exception("Currency and physical quantity namespaces must remain distinct even when their exact keys coincide.");
        try{labels.Label(currency,"unspecified");throw new Exception("An unspecified reference kind must refuse.");}
        catch(MarketDataException error)when(error.Code=="reference_display"){}
        foreach(var increment in new[]{"0.05","0.001","0.000000000000000001"})
        {
            var terms=new PaymentRoundingTerms(currency,ExactDecimal.Parse(increment),"to_even","payment_total");
            var json=JsonSerializer.Serialize(terms,SettlementPreview.JsonOptions);
            using var document=JsonDocument.Parse(json);
            if(document.RootElement.GetProperty("increment").ValueKind!=JsonValueKind.String||
                JsonSerializer.Deserialize<PaymentRoundingTerms>(json,SettlementPreview.JsonOptions)!=terms)
                throw new Exception("Exact rounding terms must round trip as decimal text, with their full currency reference.");
        }
        var old=JsonSerializer.SerializeToElement(new{reference_catalog_id=currency.Revision.CatalogId,reference_revision_id=currency.Revision.RevisionId,currency_code=currency.Code,
            rounding="cash_2dp_half_even_per_occurrence_v1"});
        var convention=SettlementPreview.Rounding(old);
        foreach(var (input,expected) in new[]{("1.005","1"),("-1.015","-1.02"),("10000000000000000000000000/1","10000000000000000000000000/1")})
            if(ExactMoney.Parse(input,currency).RoundExact(convention,CashRoundingStage.PaymentTotal).ToExactString()!=expected)
                throw new Exception("Retained two-decimal policy must preserve its exact declared behavior and range.");
        Console.WriteLine("PASS exact currency-bound rounding JSON and unchanged retained two-decimal replay");
    }
}
