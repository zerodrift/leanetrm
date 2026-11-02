using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

// Each option trade's option and premium cash rows, valued together as one unit. An option the close cannot value keeps
// its structured reason and no value; its premium cash is still valued.
internal static class FuturesOptionBlotter
{
    internal static async Task<OptionRun> Run(NpgsqlConnection connection,PreviewDatabase database,PreviewData data,InputFrame frame,
        ImmutableArray<Guid> books,ImmutableArray<Guid> portfolios,CancellationToken ct,TradePositionSelection? position=null)=>
        new(connection,database,data,frame,await FuturesOptionPositions.Read(connection,database.Manifest,books,portfolios,ct,position));

    internal sealed class OptionRun(NpgsqlConnection connection,PreviewDatabase database,PreviewData data,InputFrame frame,
        ImmutableArray<FuturesOptionPosition> positions):IBlotterProductRun
    {
        public int Positions=>positions.Length*2;
        public async IAsyncEnumerable<ImmutableArray<JsonObject>> Units([EnumeratorCancellation]CancellationToken ct)
        {
            if(positions.IsEmpty)yield break;
            var (market,values)=await FuturesOptionPortfolio.Values(connection,database,data,frame,positions,ct);
            foreach(var position in positions)
            {
                var option=position.Row(true);var premium=position.Row(false);
                if(market.Bindings.TryGetValue(position.OptionBinding,out var binding))
                {
                    var receipt=binding.Receipt;
                    option["settlement"]=market.Settlements[position.OptionBinding].ToString();option["impliedVolatility"]=receipt.Volatility;
                    option["calibrationMode"]=receipt.Mode;option["numericalPolicy"]=receipt.NumericalPolicy;
                }
                Value(option,market.Reason(position),position.OptionBinding);
                // Premium cash is pending until its payment date and settled from then on, when it adds no further value.
                premium["cashState"]=market.PremiumState(position.PremiumBinding);
                Value(premium,market.PremiumReason(position),position.PremiumBinding);
                yield return [option,premium];
            }
            void Value(JsonObject row,string? reason,Guid binding)
            {
                var trade=Guid.Parse(row["tradeId"]!.GetValue<string>());
                if(reason is not null)row["unavailableReason"]=reason;
                // A closed option binding holds no lots and is worth nothing; every valued binding has its value.
                else if(values.TryGetValue((trade,binding),out var value))
                {
                    row["presentValue"]=value.ToExactString();
                    // An option is valued per contract and scaled by its signed open lots, so its value per contract is that value over them.
                    if(row["component"]!.GetValue<string>()=="option")
                    {
                        var lots=ExactDecimal.Parse(row["openLots"]!.GetValue<string>());
                        row["unitPresentValue"]=(lots.CompareTo(default)<0?value.Scale(ExactDecimal.Parse("-1")).DivideBy(-lots):value.DivideBy(lots)).ToExactString();
                    }
                }
                else row["presentValue"]=
                    row["component"]!.GetValue<string>()=="option"&&row["openLots"]!.GetValue<string>()=="0"?"0":
                    throw new MarketDataException("option_portfolio","Every held option and premium binding must have exactly one value.");
            }
        }
    }
}
