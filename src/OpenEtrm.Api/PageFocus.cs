using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenEtrm.Api;

internal sealed record PivotCellFocus(JsonObject Keys,string Measure);
internal sealed record PageFocus(ImmutableArray<Guid> TradeIds,PivotCellFocus? PivotCell)
{
    internal static readonly string[] IdentityFields=["groupId","bookId","portfolioId","market","contractId","commodity","instrumentType","quantitySpecification","currencyReference","definitionRevisionId"];
    internal static PageFocus Parse(JsonElement value)
    {
        var m=new ToolInput(value);m.Exactly("tradeIds","pivotCell");
        var ids=m.Ids("tradeIds");PivotCellFocus? cell=null;
        if(m.Has("pivotCell"))
        {
            var selected=m.Members("pivotCell");selected.Exactly("keys","measure");
            var keys=value.GetProperty("pivotCell").GetProperty("keys");
            if(keys.ValueKind!=JsonValueKind.Object||!keys.EnumerateObject().Any()||keys.EnumerateObject().Select(p=>p.Name).Distinct().Count()!=keys.EnumerateObject().Count())throw ToolInput.Invalid();
            foreach(var key in keys.EnumerateObject())
            {
                if(!IdentityFields.Contains(key.Name))throw ToolInput.Invalid();
                if(key.Name=="groupId")
                {
                    if(key.Value.ValueKind!=JsonValueKind.String||key.Value.GetString() is not {Length:64} id||id.Any(c=>!char.IsAsciiHexDigit(c)))throw ToolInput.Invalid();
                }
                else if(key.Name is "quantitySpecification" or "currencyReference")
                {
                    var reference=new ToolInput(key.Value);reference.Exactly("revision","code");_=reference.Text("code",1,64);
                    var revision=reference.Members("revision");revision.Exactly("catalogId","revisionId");_=revision.Id("catalogId");_=revision.Id("revisionId");
                }
                else if(key.Value.ValueKind!=JsonValueKind.Null)
                {
                    var member=new ToolInput(JsonSerializer.SerializeToElement(new{key=key.Value}));_=member.Text("key",1,1024);
                }
            }
            cell=new(JsonNode.Parse(keys.GetRawText())!.AsObject(),selected.Choice("measure",["tradeCount",..BlotterDigestView.MeasureKeys,..TradingBlotter.PriceMeasures.Keys]));
        }
        if(!ids.IsEmpty&&cell is not null)throw ToolInput.Invalid();
        return new(ids,cell);
    }
    internal static PageFocus? From(JsonElement report)=>report.TryGetProperty("focus",out var focus)?Parse(focus):null;
}
