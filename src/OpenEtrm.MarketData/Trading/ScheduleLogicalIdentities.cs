using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;

namespace OpenEtrm.MarketData.Trading;

public sealed record ScheduleObligationIdentity(string OccurrenceKey,string TermsDigest);

// Logical obligation identity uses its own coordinates and semantic inputs.
// Physical block partitioning, global node numbers and manifests are absent.
internal sealed class ScheduleLogicalIdentities
{
    private readonly ScheduleDefinition definition;
    private readonly Dictionary<int,ScheduleDefinitionEdge[]> inputs;
    private readonly Dictionary<int,ScheduleDefinitionBinding[]> bindings;
    internal ScheduleLogicalIdentities(ScheduleDefinition definition)
    {
        this.definition=definition;
        inputs=definition.Edges.GroupBy(edge=>edge.Target).ToDictionary(group=>group.Key,group=>group.OrderBy(e=>e.Port,StringComparer.Ordinal).ThenBy(e=>e.Ordinal).ToArray());
        bindings=definition.Bindings.GroupBy(binding=>binding.Row).ToDictionary(group=>group.Key,group=>group.OrderBy(binding=>binding.Port,StringComparer.Ordinal).Select(binding=>binding with{Row=0}).ToArray());
    }
    internal ScheduleObligationIdentity For(int root,int? cell)
    {
        if((uint)root>=(uint)definition.Rows.Count||definition.Rows[root].Operation!="output_guard")throw ScheduleDefinition.Error("An obligation reference requires a retained output guard.");
        var row=definition.Rows[root];
        if(row.Grid is null)
        {
            if(cell is not null||row.OccurrenceKey is null||row.TermsDigest is null)throw ScheduleDefinition.Error("A scalar obligation requires its own retained identity and no cell coordinate.");
            return new(row.OccurrenceKey,row.TermsDigest);
        }
        var grid=definition.Grids[row.Grid.Value];
        if(cell is null||(uint)cell.Value>=(uint)grid.CellCount)throw ScheduleDefinition.Error("A vector obligation reference requires one exact retained cell coordinate.");
        var interval=grid.Interval(cell.Value);
        var occurrence=Hash(new{Kind="interval_obligation_v1",interval.Start,interval.End,interval.OperatingDate,interval.HourBeginning,interval.Minute,interval.Occurrence,
            PaymentAt=row.ValueAt,Rule=row.Parameters["paymentRule"]});
        var cache=new Dictionary<(int,int?),string>();
        string Node(int ordinal,int? selected)
        {
            if(cache.TryGetValue((ordinal,selected),out var cached))return cached;
            var value=definition.Rows[ordinal];var operation=value.Operation;var parameters=value.Parameters;
            var edges=inputs.GetValueOrDefault(ordinal)??[];
            object? window=null;
            if(value.Grid is {} gridOrdinal)
            {
                if(selected is null)throw ScheduleDefinition.Error("A vector term requires its selected source cell.");
                var grid=definition.Grids[gridOrdinal];var interval=grid.Interval(selected.Value);
                window=new{grid.Clock,interval.Start,interval.End,interval.OperatingDate,interval.HourBeginning,interval.Minute,interval.Occurrence};
            }
            if(operation=="profile_quantity")
            {
                if(value.Grid is not {} profileGrid||selected is null)throw ScheduleDefinition.Error("Profile terms require their retained selected interval.");
                var period=definition.Grids[profileGrid].Interval(selected.Value);var numerator=new BigInteger((period.End-period.Start).Ticks);var denominator=new BigInteger(TimeSpan.TicksPerHour);
                var gcd=BigInteger.GreatestCommonDivisor(numerator,denominator);operation="quantity_literal";
                parameters=ImmutableSortedDictionary<string,string>.Empty.Add("numerator",(numerator/gcd).ToString(System.Globalization.CultureInfo.InvariantCulture)).Add("denominator",(denominator/gcd).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            object[] arguments;
            if(operation=="quantity_sum_cells")
            {
                operation="quantity_add";var input=edges.Single().Source;var source=definition.Rows[input];
                var sourceGrid=definition.Grids[source.Grid??throw ScheduleDefinition.Error("A reduction must retain its input grid.")];
                arguments=[..Enumerable.Range(0,sourceGrid.CellCount).Select(n=>(object)new{Port="input",Ordinal=n,Source=Node(input,n)})];
            }
            else arguments=[..edges.Select(edge=>(object)new{edge.Port,edge.Ordinal,Source=Node(edge.Source,definition.Rows[edge.Source].Grid is null?null:selected)})];
            var digest=Hash(new{Operation=operation,value.Version,value.OutputKind,value.Unit,value.Currency,value.QuoteMultiplier,
                Parameters=operation=="weighted_price"?ImmutableSortedDictionary<string,string>.Empty:parameters,value.ObservationFrom,value.ObservationTo,
                Window=window,Inputs=arguments,Bindings=bindings.GetValueOrDefault(ordinal)??[]});
            cache.Add((ordinal,selected),digest);return digest;
        }
        var amount=inputs[root].Single(edge=>edge.Port=="amount").Source;
        return new(occurrence,Hash(new{interval.Start,interval.End,interval.OperatingDate,interval.HourBeginning,interval.Minute,interval.Occurrence,row.Currency,Quantity=Node(amount,cell)}));
    }
    private static string Hash(object value)=>Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value,ScheduleDefinition.Json)));
}
