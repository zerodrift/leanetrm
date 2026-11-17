using System.Text.Json;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

internal sealed partial class GasParkCustodyActions
{
    internal const string Handler="gas_park_custody";
    internal const string Accept="gas.park_custody.accept",Return="gas.park_custody.return";

    internal static LifecycleActionRequest Normalize(LifecycleActionRequest request)
    {
        LifecycleNative.Require(request,Handler);
        if(request.Automatic)throw new MarketDataException("lifecycle_policy","This custody profile requires an explicitly reviewed manual action.");
        try
        {
            object selection=request.Action switch
            {
                Accept=>NormalizeAccept(request.Selection.Deserialize<GasParkCustodyAcceptSelection>(GasOwnedOperations.Json)??throw new JsonException()),
                Return=>NormalizeReturn(request.Selection.Deserialize<GasParkCustodyReturnSelection>(GasOwnedOperations.Json)??throw new JsonException()),
                _=>throw Input("Select an installed custody acceptance or return action.")
            };
            return request with{Selection=JsonSerializer.SerializeToElement(selection,selection.GetType(),GasOwnedOperations.Json)};
        }
        catch(Exception error)when(error is JsonException or FormatException or OverflowException or ArgumentException)
        {throw Input("Supply this custody action's exact typed fields, observed times and explicit lot allocations.",error);}
        catch(MarketDataException error)when(error.Code!="gas_custody_input")
        {throw Input("Supply supported observed times and exact positive quantities in the saved custody basis.",error);}
    }

    static GasParkCustodyAcceptSelection NormalizeAccept(GasParkCustodyAcceptSelection value)
    {
        if(value.ExpectedVersion<1||value.AgreementEvidenceId==Guid.Empty||value.ReceiptEvidenceId==Guid.Empty||
            value.Allocations is null||value.Allocations.Length>128||value.Allocations.Any(a=>a is null||a.LotId==Guid.Empty)||
            value.Allocations.Select(a=>a.LotId).Distinct().Count()!=value.Allocations.Length)
            throw Input("Retain the captured agreement head, exact agreement/receipt evidence and distinct explicit customer lots, or an explicit empty provider allocation.");
        return value with{Allocations=[..value.Allocations.OrderBy(a=>a.LotId).Select(a=>a with{Quantity=Positive(a.Quantity)})]};
    }

    static GasParkCustodyReturnSelection NormalizeReturn(GasParkCustodyReturnSelection value)
    {
        var key=GasOperationText.Key(value.BusinessKey,"gas_custody_input");
        if(value.ExpectedVersion<1||value.EvidenceId==Guid.Empty||
            value.Allocations is null||value.Allocations.Length>128||value.Allocations.Any(a=>a is null||a.ReservationId==Guid.Empty)||
            value.Allocations.Select(a=>a.ReservationId).Distinct().Count()!=value.Allocations.Length)
            throw Input("Retain the captured custody head, observed return, business reference and distinct active reservations, or an explicit empty provider allocation.");
        return value with{BusinessKey=key,Quantity=Positive(value.Quantity),
            Allocations=[..value.Allocations.OrderBy(a=>a.ReservationId).Select(a=>a with{Quantity=Positive(a.Quantity)})]};
    }

    static string Positive(string text)
    {
        var quantity=ExactDecimal.Parse(text);
        if(quantity.CompareTo(default)<=0)throw Input("Custody performance and each selected lot quantity must be positive.");
        return quantity.ToString();
    }

    // Outcome identities come from the native receipt seal. Recovery must bind
    // the same immutable target before disclosure, without a new source read.
    internal static void RequireOutcomeTarget(LifecycleTarget target,JsonElement outcome)
    {
        try
        {
            if(target is null||target.Handler!=Handler||target.ScopeId==Guid.Empty||target.BindingId==Guid.Empty||
                outcome.ValueKind!=JsonValueKind.Object||outcome.Id("tradeId")!=target.ScopeId||outcome.Id("instrumentId")!=target.BindingId)
                throw Source();
        }
        catch(Exception error)when(error is KeyNotFoundException or InvalidOperationException or FormatException or JsonException)
        {throw new MarketDataException("gas_custody_source","The native custody outcome must attest its exact trade and instrument target.",error);}
    }

    internal static MarketDataException Source()=>new("gas_custody_source","Retain the exact native custody target, accepted source and independently checked operation effects.");
    static MarketDataException Input(string message,Exception? inner=null)=>new("gas_custody_input",message,inner);
}
