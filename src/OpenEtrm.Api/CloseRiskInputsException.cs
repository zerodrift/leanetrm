using System.Collections.Immutable;
using System.Text.Json;

namespace OpenEtrm.Api;

public sealed record CloseRiskAffectedBinding(Guid TradeId,Guid BindingId,string Reason);
public sealed class CloseRiskInputsException(string message,ImmutableArray<CloseRiskAffectedBinding> affected):Exception(message)
{
    public const string Code="pilot_close_inputs";
    public ImmutableArray<CloseRiskAffectedBinding> AffectedBindings{get;}=affected;
    internal static CloseRiskInputsException For(IEnumerable<JsonElement> rows,string reason)=>new(
        "The complete selected scope contains unsupported or incompatible inputs; no partial report was admitted.",
        [..rows.Select(r=>new CloseRiskAffectedBinding(r.Id("trade_id"),r.Id("binding_id"),reason)).OrderBy(r=>r.TradeId).ThenBy(r=>r.BindingId)]);
}
