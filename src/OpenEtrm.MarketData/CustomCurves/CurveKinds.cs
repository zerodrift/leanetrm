namespace OpenEtrm.MarketData.CustomCurves;

// Names what a curve is from its exact saved definition. Only the output step and the inputs a rule names are
// read; node ids carry no meaning. Anything no rule matches is "custom", never a guessed kind.
public static class CurveKinds
{
    public const string Version="curve_kinds_v1";
    // A stored interest-rate curve is not built from a rule or a graph; its market type names it.
    public const string InterestRate="interest_rate";
    public static string Of(RecipeDefinition definition)
    {
        RecipeNode? Node(string? id)=>id is null?null:definition.Nodes.FirstOrDefault(n=>n.Id==id);
        RecipeNode? Input(RecipeNode? node,string port)=>Node(node?.Inputs.GetValueOrDefault(port));
        var feed=Input(Node(definition.Output),"values");
        switch(feed?.Kind)
        {
            case "weighted_panels" or "weighted_contracts":return "weighted_blend";
            case "mean":
                var axis=feed.Parameters.GetValueOrDefault("axis");
                var dates=Input(Input(feed,"values"),"dates");
                var mode=dates?.Kind=="dates"?dates.Parameters.GetValueOrDefault("mode"):null;
                return axis=="dates"&&mode is "prior_calendar" or "prior_business"?"rolling_average":
                    axis=="contracts"&&mode=="balance_month"?"balance_of_month":"custom";
            case "sample":
                var selection=Input(feed,"selection");
                return selection?.Kind=="select"&&selection.Parameters.GetValueOrDefault("mode")=="positions"
                    &&selection.Parameters.GetValueOrDefault("count")=="1"?"prompt_contract":"custom";
            default:return "custom";
        }
    }
    // A market curve's kind follows its rule; a direct curve must say which observations it carries.
    public static string Of(CurveRule rule,ObservationRole? directRole)=>rule switch
    {
        CurveRule.Direct when directRole==ObservationRole.DailySettlement=>"market_settlement",
        CurveRule.Direct when directRole==ObservationRole.TraderMark=>"trader_mark",
        // Unreachable for stored curves: db/migrations/0002_market_data.sql limits direct_role to trader_mark/daily_settlement and requires one for direct.
        CurveRule.Direct=>throw new MarketDataException("curve_kind_undefined","A direct curve needs a daily-settlement or trader-mark role to have a kind."),
        CurveRule.Add=>"basis_spread",
        CurveRule.ScaleAdd=>"scaled",
        _=>throw new MarketDataException("curve_kind_undefined","The curve rule has no kind.")
    };
}
