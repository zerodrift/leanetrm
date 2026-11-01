using System.Collections.Immutable;

namespace OpenEtrm.MarketData.Trading;

// Admission checks the complete typed DAG before storage or execution. The
// operation registry is deliberately small and closed to supported consumers.
internal static class ScheduleTopology
{
    internal static void Require(ScheduleDefinition definition)
    {
        RequirePorts(definition.Ports);
        var edges=definition.Edges.GroupBy(e=>e.Target).ToDictionary(g=>g.Key,g=>g.ToArray());
        var bindings=definition.Bindings.GroupBy(b=>b.Row).ToDictionary(g=>g.Key,g=>g.ToArray());
        foreach(var row in definition.Rows)
        {
            var selected=edges.GetValueOrDefault(row.Ordinal)??[];
            var sources=bindings.GetValueOrDefault(row.Ordinal)??[];
            var shape=Shape(row.Operation);
            if(row.OutputKind!=shape.Item1)throw ScheduleDefinition.Error("An operation has the wrong output type.");
            var ports=shape.Item2.Length==0?[]:shape.Item2.Split(',');
            if(selected.Any(e=>!ports.Contains(e.Port)&&!ports.Contains(e.Port+"+")||
                e.Ordinal!=0&&!ports.Contains(e.Port+"+")))
                throw ScheduleDefinition.Error("An operation has an unknown or repeated scalar input port.");
            foreach(var port in ports)
            {
                var many=port.EndsWith('+');var name=many?port[..^1]:port;
                var count=selected.Count(e=>e.Port==name);
                if(count==0||!many&&count!=1)throw ScheduleDefinition.Error("An operation is missing its required input ports.");
            }
            if(row.Operation=="price_add"&&selected.Length!=2)
                throw ScheduleDefinition.Error("The supported affine price addition requires exactly two explicit inputs.");
            foreach(var edge in selected)
                RequireOperand(row,definition.Rows[edge.Source],edge.Port,definition.Grids);
            if(row.Operation=="amount")
            {
                var quantity=definition.Rows[selected.Single(e=>e.Port=="quantity").Source];
                var price=definition.Rows[selected.Single(e=>e.Port=="price").Source];
                if(quantity.Unit!=price.Unit)throw ScheduleDefinition.Error("Price times quantity requires the same exact quantity specification.");
            }
            RequireSources(row,sources,definition.Ports);
        }
        var seen=new HashSet<int>();var pending=new Stack<int>(definition.Roots.Select(r=>r.Row));
        while(pending.Count>0)
        {
            var n=pending.Pop();if(!seen.Add(n))continue;
            if(edges.TryGetValue(n,out var input))foreach(var edge in input)pending.Push(edge.Source);
        }
        if(seen.Count!=definition.Rows.Count)throw ScheduleDefinition.Error("Every retained pricing row must belong to an explicit output dependency closure.");
        if(definition.Roots.Count(r=>r.Name=="pv"&&r.Measure=="pv"&&definition.Rows[r.Row].OutputKind=="money")!=1)
            throw ScheduleDefinition.Error("A Side must declare one monetary PV root.");
    }
    internal static void RequirePorts(IReadOnlyList<ScheduleDefinitionPort> ports)
    {
        if(ports.Select(port=>port.Name).Distinct(StringComparer.Ordinal).Count()!=ports.Count)
            throw ScheduleDefinition.Error("External ports require distinct names.");
        foreach(var port in ports)
        {
            if(port.Slot is not{} slot||!Enum.IsDefined(slot.Role)||slot.Ordinal<0||port.Kind!="quantity"||string.IsNullOrWhiteSpace(port.Name)||string.IsNullOrWhiteSpace(port.ShapeIdentity))
                throw ScheduleDefinition.Error("A sibling input port requires an exact slot, unit and retained structural shape.");
            Money.RequireKey(port.Unit);
        }
    }
    internal static void RequireOperand(ScheduleDefinitionRow row,ScheduleDefinitionRow source,string port,
        IReadOnlyList<ScheduleDefinitionGrid> grids)
    {
        var expected=row.Operation switch
        {
            "amount" when port=="price" => "price",
            "amount" => "quantity",
            "pv" when port=="discount" => "factor",
            "pv" or "sum" or "sum_cells" or "output_guard" => "money",
            _ => row.OutputKind
        };
        if(source.OutputKind!=expected)throw ScheduleDefinition.Error("An input port references a row with an incompatible value type.");
        if(row.Operation is "sum_cells" or "quantity_sum_cells")
        {
            if(source.Grid is null||row.Operation=="sum_cells"&&row.Grid is not null||
                row.Operation=="quantity_sum_cells"&&(row.Grid is not {} targetGrid||grids[targetGrid].CellCount!=1))
                throw ScheduleDefinition.Error("A cell reduction requires an explicit vector input and a scalar or one-cell output.");
        }
        else if(source.Grid!=row.Grid&&source.Grid is not null)
            throw ScheduleDefinition.Error("Vector inputs must share the exact retained grid unless the operator explicitly reduces cells.");
        if(expected==row.OutputKind&&source.Currency!=row.Currency)
            throw ScheduleDefinition.Error("Arithmetic input currencies must match exactly.");
        if(expected==row.OutputKind&&row.Operation is not("quantity_normalize" or "quantity_transform")&&
            (source.Unit!=row.Unit||source.QuoteMultiplier!=row.QuoteMultiplier))
            throw ScheduleDefinition.Error("Arithmetic input units and quote scales must match exactly.");
        if(row.Operation=="amount"&&port=="price"&&source.Currency!=row.Currency)
            throw ScheduleDefinition.Error("A cash amount must retain its price currency.");
    }
    internal static (string Kind,string Ports) Shape(string operation)=>operation switch
    {
        "constant_price" or "price_lookup" or ContractObservationScheduleSource.Operation => ("price", ""),
        "quantity_input" or "quantity_literal" or "asset_capacity" or "profile_quantity" => ("quantity", ""),
        "quantity_normalize" => ("quantity", "input,basis"),
        "quantity_add" => ("quantity", "input+"),
        "quantity_scale" or "quantity_transform" or "quantity_sum_cells" => ("quantity", "input"),
        "price_add" or "weighted_price" => ("price", "input+"),
        "price_scale" or "price_application" => ("price", "input"),
        "amount" => ("money", "quantity,price"),
        "discount" => ("factor", ""),
        "pv" => ("money", "amount,discount"),
        "sum" => ("money", "input+"),
        "sum_cells" => ("money", "input"),
        "output_guard" => ("money", "amount,discounted"),
        _ => throw ScheduleDefinition.Error("Unsupported schedule operation.")
    };
    internal static void RequireSources(ScheduleDefinitionRow row,ScheduleDefinitionBinding[] sources,IReadOnlyList<ScheduleDefinitionPort> ports)
    {
        switch(row.Operation)
        {
            case ContractObservationScheduleSource.Operation:
                if(sources.Length!=3||sources.Any(b=>b.Kind!=(b.Port switch
                    {"market"=>"contract_price","quote"=>"quote","settlement"=>"settlement_source",_=>null})))
                    throw ScheduleDefinition.Error("A dated-contract observation requires exactly its market, quote and settlement source bindings.");
                break;
            case "price_lookup":
                if(sources.Any(b=>b.Kind=="contract_price"))
                {
                    if(!sources.Any(b=>b.Port=="market")||!sources.Any(b=>b.Port=="index")||!sources.Any(b=>b.Port=="quote")||!sources.Any(b=>b.Port=="fixing")||
                        sources.Any(b=>b.Kind!=(b.Port switch
                        {
                            "market"=>"contract_price","index"=>"index","quote"=>"quote",
                            "fixing"=>"index_fixing","projection"=>"custom_output",
                            "assignment"=>"gas_assignment","point"=>"location","product"=>"gas_product",
                            "crude"=>"crude_assignment","facility" or "custody"=>"location","grade"=>"crude_grade","commodity"=>"crude_product",_=>null
                        })))
                        throw ScheduleDefinition.Error("A dated lookup requires its exact contract, index, quote and fixing bindings; a custom projection is optional.");
                    if(sources.Any(b=>b.Port is "assignment" or "point" or "product")&&
                        new[]{"assignment","point","product"}.Any(port=>!sources.Any(b=>b.Port==port)))
                        throw ScheduleDefinition.Error("A gas market lookup requires its complete assignment, point and product bindings.");
                    if(sources.Any(b=>b.Port is "crude" or "facility" or "custody" or "grade" or "commodity")&&
                        (new[]{"crude","facility","custody","grade","commodity"}.Any(port=>!sources.Any(b=>b.Port==port))||
                         sources.Any(b=>b.Port is "assignment" or "point" or "product")))
                        throw ScheduleDefinition.Error("A crude lookup requires complete grade, facility, custody and commodity bindings without gas semantics.");
                    if(sources.SingleOrDefault(b=>b.Port=="projection") is {Kind:"custom_output"} projection&&
                        projection.Market!=sources.Single(b=>b.Port=="market").Market)
                        throw ScheduleDefinition.Error("A custom projection must retain the same exact dated observation contract.");
                }
                else if(sources.Any(b=>b.Kind=="curve_interval"))
                {
                    var period=row.Parameters["selection"]!="interval_attribute_lookup_v1";
                    if(!sources.Any(b=>b.Port=="curve"&&b.Kind=="curve_interval")||
                        period&&!sources.Any(b=>b.Port=="index"&&b.Kind=="index")||
                        sources.Length!=(period?2:1)||sources.Any(b=>b.Port is not("curve" or "index")))
                        throw ScheduleDefinition.Error("A native period curve lookup requires its separately pinned settlement index.");
                }
                else if(sources.Length!=1||sources[0].Port!="location"||sources[0].Kind is not("quantity_source" or "asset" or "location"))
                    throw ScheduleDefinition.Error("A native lookup requires one exact curve or location-assignment input.");
                break;
            case "quantity_input":
                if(sources.Length!=1||sources[0].Kind is not("quantity_source" or "sibling_quantity")||
                    sources[0].Port!=(sources[0].Kind=="quantity_source"?"source":"quantity")||
                    sources[0].Kind=="quantity_source"&&sources[0].Coordinate is not null)
                    throw ScheduleDefinition.Error("A quantity input requires one exact declared source or sibling port.");
                if(sources[0].Kind=="sibling_quantity"&&!ports.Any(p=>p.Name==sources[0].Coordinate&&p.Unit==row.Unit&&p.Slot!.Ordinal==sources[0].Position))
                    throw ScheduleDefinition.Error("A sibling quantity input must reference a declared compatible port.");
                break;
            case "asset_capacity":
                if(sources.Length!=1||sources[0].Kind!="asset"||sources[0].Port!="asset"||sources[0].Coordinate is not null)
                    throw ScheduleDefinition.Error("Capacity inputs require an exact asset binding.");
                break;
            case "discount":
                if(sources.Length!=0)throw ScheduleDefinition.Error("The supported discount operator reads its explicit valuation context and cannot ignore additional curve bindings.");
                break;
            default:
                if(sources.Length!=0)throw ScheduleDefinition.Error("Arithmetic and literal rows cannot hide additional data-source bindings.");
                break;
        }
        foreach(var binding in sources)
        {
            static bool Invalid(DefinitionRevision revision)=>revision.CatalogId==Guid.Empty||revision.RevisionId==Guid.Empty;
            if(binding.Reference is {} reference)Money.RequireKey(reference);
            if(binding.Definitions is {} definitions&&Invalid(definitions)||
                binding.Series is {} series&&(Invalid(series.Revision)||series.Id==Guid.Empty)||
                binding.Market is {} market&&(Invalid(market.Curve.Revision)||Invalid(market.Contract.Revision)||
                    string.IsNullOrWhiteSpace(market.Curve.Code)||market.Contract.Id==Guid.Empty))
                throw ScheduleDefinition.Error("A source binding requires complete exact definition keys.");
            var empty=new ScheduleDefinitionBinding(binding.Row,binding.Port,binding.Kind,null,null,null,null,null,null,null);
            var shape=binding.Kind switch
            {
                "contract_price"=>empty with{Market=binding.Market},
                "index_fixing" or "settlement_source"=>empty with{Reference=binding.Reference,Series=binding.Series,Coordinate=binding.Coordinate},
                "index" or "quote" or "gas_product" or "crude_grade" or "crude_product"=>empty with{Reference=binding.Reference},
                "gas_assignment" or "crude_assignment"=>empty with{Reference=binding.Reference,Coordinate=binding.Coordinate,Definitions=binding.Definitions,Code=binding.Code},
                "location"=>empty with{Reference=binding.Reference,Coordinate=binding.Coordinate},
                "sibling_quantity"=>empty with{Reference=binding.Reference,Coordinate=binding.Coordinate,Position=binding.Position},
                "quantity_source" or "asset"=>empty with{Definitions=binding.Definitions,Code=binding.Code,Coordinate=binding.Coordinate},
                "curve_interval"=>empty with{Definitions=binding.Definitions,Code=binding.Code},
                "custom_output"=>empty with{Market=binding.Market,EntityCatalog=binding.EntityCatalog,Entity=binding.Entity,Version=binding.Version,
                    Coordinate=binding.Coordinate,Position=binding.Position},
                _=>throw ScheduleDefinition.Error("The selected operator has no supported binding shape.")
            };
            if(binding!=shape)throw ScheduleDefinition.Error("A typed binding cannot retain unrelated or unconsumed reference fields.");
            var valid=binding.Kind switch
            {
                "contract_price" => binding.Market is not null,
                "index_fixing" or "settlement_source" => binding.Reference is not null&&binding.Series is not null&&!string.IsNullOrWhiteSpace(binding.Coordinate),
                "index" or "quote" or "location" or "gas_product" or "crude_grade" or "crude_product" or "sibling_quantity" => binding.Reference is not null,
                "gas_assignment" or "crude_assignment" => binding.Reference is not null&&binding.Definitions is not null&&binding.Code==binding.Reference.Value.Code&&!string.IsNullOrWhiteSpace(binding.Coordinate),
                "curve_interval" or "quantity_source" or "asset" => binding.Definitions is not null&&!string.IsNullOrWhiteSpace(binding.Code),
                "custom_output" => binding.EntityCatalog is {} catalog&&catalog!=Guid.Empty&&binding.Entity is {} entity&&entity!=Guid.Empty&&binding.Version>0&&
                    (binding.Position is null||binding.Position>=0)&&!string.IsNullOrWhiteSpace(binding.Coordinate),
                "rate_curve" => binding.Reference is not null,
                _ => false
            };
            if(!valid)throw ScheduleDefinition.Error("A data-source binding is missing its exact typed key.");
        }
    }
}
