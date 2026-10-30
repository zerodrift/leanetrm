using OpenEtrm.Shared.Calculation;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.MarketData.Calculation;

public readonly record struct CurvePointKey(CurveKey Curve,Guid PointId);
public abstract record ConnectedCurveReference;
public sealed record ExactCurveReference(CurveKey Curve) : ConnectedCurveReference;
public sealed record LogicalCurveReference(LogicalCurveKey Curve) : ConnectedCurveReference;
public sealed record CurvePointReference(ConnectedCurveReference Curve,Guid PointId)
{
    public static CurvePointReference Exact(CurvePointKey point)=>new(new ExactCurveReference(point.Curve),point.PointId);
}

public abstract record ConnectedCurveExpression;
public sealed record ConnectedSource(CurveRequest Request,InputKey Input,MarketObservation Observation,
    QuoteSpecification Quote,MarketSeries Series) : ConnectedCurveExpression
{
    public ConnectedSourceSample? Sample{get;init;}
}
public sealed record ConnectedWeightedTerm(CurvePointReference Point,ExactDecimal? Weight);
public sealed record ConnectedWeightedSum(ImmutableArray<ConnectedWeightedTerm> Terms) : ConnectedCurveExpression;
public sealed record ConnectedWeightedMean(ImmutableArray<ConnectedWeightedTerm> Terms) : ConnectedCurveExpression;
public sealed record ConnectedMean(ImmutableArray<CurvePointReference> Points,string Convention) : ConnectedCurveExpression;
public sealed record ConnectedRollingPoint(RecipeContract Contract,Guid PointId);
public sealed record ConnectedRollingMean(ConnectedCurveReference Source,ImmutableArray<ConnectedRollingPoint> Universe,
    string Mode,int First,int Count,DateOnly DeliveryDate,string Convention) : ConnectedCurveExpression;
// Supplied by a provider that has already resolved and authorized this exact
// schedule version in Context. This value is not publication or access proof.
public sealed record ResolvedCurveSchedule(Guid CatalogId,Guid ScheduleId,int Version,string Engine,DateTimeOffset KnownFrom,
    ResolvedCalculationContext Context,DateSet Dates);
public sealed record ConnectedScheduledPoint(DateOnly Date,CurvePointReference Point);
public sealed record ConnectedScheduledMean(ResolvedCurveSchedule Schedule,ImmutableArray<ConnectedScheduledPoint> Points,
    string Convention) : ConnectedCurveExpression;

public sealed record ConnectedCurvePoint(CurvePointKey Key,DateOnly DeliveryFrom,DateOnly DeliveryTo,
    PriceUnit Unit,ConnectedCurveExpression Expression,MarketContract? Contract=null)
{
    public DateOnly? PointDate{get;init;}
}
public sealed record ConnectedCurveDefinition(CurveKey Key,ImmutableArray<ConnectedCurvePoint> Points);

public static class ConnectedCurves
{
    public const string NumericalPolicy="exact_rational_connected_curves_v1";
    public const string UniformMean="uniform_arithmetic_mean_v1";
    public const string ManagedDateSet="resolved_managed_date_set_v1";

    public static ConnectedCurvePlan Compile(IEnumerable<ConnectedCurveDefinition> definitions,
        ImmutableArray<CurvePointKey> roots,ResolvedCalculationContext context,ICurveBindingResolver resolver,
        CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();
        if(definitions is null||context is null||resolver is null||roots.IsDefault||roots.Distinct().Count()!=roots.Length)
            throw Invalid("Supply explicit definitions, unique roots, one resolved context and a binding resolver.");
        var directory=new Dictionary<CurvePointKey,ConnectedCurvePoint>();var curveKeys=new HashSet<CurveKey>();
        foreach(var definition in definitions)
        {
            cancellation.ThrowIfCancellationRequested();
            if(definition is null||!Valid(definition.Key)||!curveKeys.Add(definition.Key)||definition.Points.IsDefaultOrEmpty)
                throw Invalid("Each curve definition requires a unique exact version and explicit points.");
            foreach(var point in definition.Points)
            {
                cancellation.ThrowIfCancellationRequested();
                if(point is null||point.Key.Curve!=definition.Key||point.Key.PointId==Guid.Empty||!directory.TryAdd(point.Key,point))
                    throw Invalid("Points require unique exact identities in their declared curve.");
            }
        }
        var logicalBindings=new Dictionary<LogicalCurveKey,CurveKey>();var visible=new HashSet<CurveKey>();
        void RequireVisible(CurveKey curve)
        {
            if(!Valid(curve))throw Invalid("An exact curve reference is incomplete.");
            if(visible.Add(curve))resolver.RequireVisible(curve,context);
        }
        CurveKey Resolve(ConnectedCurveReference reference)
        {
            CurveKey curve;
            switch(reference)
            {
                case ExactCurveReference exact:curve=exact.Curve;break;
                case LogicalCurveReference logical:
                    if(!logicalBindings.TryGetValue(logical.Curve,out curve))
                    {curve=resolver.Resolve(logical.Curve,context);logicalBindings.Add(logical.Curve,curve);}
                    if(curve.Revision.CatalogId!=logical.Curve.CatalogId)throw Invalid("A logical binding returned a different catalog.");
                    break;
                default:throw Invalid("Select an exact or logical curve reference explicitly.");
            }
            RequireVisible(curve);return curve;
        }
        CurvePointKey ResolvePoint(CurvePointReference reference)
        {
            if(reference is null||reference.PointId==Guid.Empty)throw Invalid("A dependency requires an exact point identity.");
            return new(Resolve(reference.Curve),reference.PointId);
        }

        var operations=new Dictionary<CurvePointKey,Operation>();var pending=new Queue<CurvePointKey>(roots);
        var contractEvidence=new Dictionary<ContractKey,MarketContract>();
        var scheduleEvidence=new Dictionary<(Guid Catalog,Guid Schedule,int Version),List<ResolvedCurveSchedule>>();
        var admittedSchedules=new HashSet<ResolvedCurveSchedule>();
        void RecordContract(MarketContract contract)
        {
            if(contract.Key.Id==Guid.Empty||contract.Key.Revision.CatalogId==Guid.Empty||contract.Key.Revision.RevisionId==Guid.Empty||
                !Enum.IsDefined(contract.Kind)||contract.DeliveryFrom>=contract.DeliveryTo||contract.Expiry.Offset!=TimeSpan.Zero)
                throw Invalid("A retained contract requires an exact identity, supported kind, delivery interval and UTC expiry.");
            if(contractEvidence.TryGetValue(contract.Key,out var prior)&&prior!=contract)
                throw Invalid("An exact contract identity cannot carry conflicting metadata in one calculation.");
            contractEvidence[contract.Key]=contract;
        }
        var one=ExactDecimal.Parse("1");
        while(pending.TryDequeue(out var key))
        {
            cancellation.ThrowIfCancellationRequested();if(operations.ContainsKey(key))continue;
            RequireVisible(key.Curve);
            if(!directory.TryGetValue(key,out var point))throw Missing("A required exact curve point is absent.");
            if((point.PointDate is {} date?point.Contract is not null||point.DeliveryFrom!=default||point.DeliveryTo!=default||date.Year is <1901 or >2199:
                point.DeliveryFrom>=point.DeliveryTo)||point.Unit is null||point.Expression is null)
                throw Invalid("A curve point requires an explicit delivery interval, price unit and expression.");
            if(point.Contract is {} contract&&(contract.DeliveryFrom!=point.DeliveryFrom||contract.DeliveryTo!=point.DeliveryTo))
                throw Invalid("Point delivery metadata conflicts with its declared contract.");
            if(point.Contract is {} exactContract)
                RecordContract(exactContract);
            Operation operation;
            switch(point.Expression)
            {
                case ConnectedSource source:
                    ValidateSource(point,source,context);operation=new(source,[],[],one);break;
                case ConnectedBuiltCurve built:
                    ValidateSample(built.Sample,context);
                    if(built.NumericalPolicy!=CurveModel.NumericalPolicy||built.Rule is not (CurveRule.Add or CurveRule.ScaleAdd)||
                        built.Quote is null||built.Rule==CurveRule.Add&&(built.Multiplier is not null||built.MultiplierInput is not null)||
                        built.Rule==CurveRule.ScaleAdd&&built.Multiplier is null||point.Unit!=PriceUnit.FromQuote(built.Quote))
                        throw Invalid("A source calculation must retain its typed rule, quote and working numerical policy.");
                    if(built.MultiplierInput is {} numeric)
                    {
                        if(built.Multiplier is null||numeric.Series is null||numeric.Observation is null||numeric.Input.Use!=InputUse.Variable||
                            numeric.Input.Series!=numeric.Series.Key||numeric.Observation.Series!=numeric.Series.Key||numeric.Series.Quote is not null||
                            numeric.Series.ValueSpecification!=built.Multiplier.Specification.Key||numeric.Observation.Role!=ObservationRole.Parameter||
                            numeric.Observation.State!=PublicationState.Final||numeric.Observation.Value!=built.Multiplier.Value||
                            numeric.Observation.ObservedAt.Offset!=TimeSpan.Zero||numeric.Observation.ObservedAt>built.Sample.MarketCutoff||
                            numeric.Observation.Key.CatalogId!=numeric.Series.Key.Revision.CatalogId||numeric.Observation.Key.Id==Guid.Empty||
                            numeric.Observation.Source.Revision!=built.Multiplier.Specification.Key.Revision)
                            throw Invalid("A source multiplier requires its exact numeric series, final observation and specification.");
                        Money.RequireKey(numeric.Observation.Source);
                    }
                    operation=new(null,[ResolvePoint(built.Base),ResolvePoint(built.Spread)],[one,one],one,built);break;
                case ConnectedRecipeProjection projection:
                    if(projection.Declaration is null||projection.Position<0)throw Invalid("A saved-recipe output requires its retained declaration and nonnegative output position.");
                    projection.Declaration.Validate(context);
                    if(point.Key.Curve!=projection.Declaration.Curve)
                        throw Invalid("A saved-recipe output must retain its exact adapter identity.");
                    operation=new(null,[ResolvePoint(projection.Value)],[one],one);break;
                case ConnectedWeightedSum sum:operation=Weighted(sum.Terms,false);break;
                case ConnectedWeightedMean mean:operation=Weighted(mean.Terms,true);break;
                case ConnectedMean mean:
                    RequireMean(mean.Convention);
                    if(mean.Points.IsDefaultOrEmpty)throw Invalid("A uniform mean requires explicit point references.");
                    operation=Uniform(mean.Points.Select(ResolvePoint).ToImmutableArray());break;
                case ConnectedRollingMean rolling:
                    RequireMean(rolling.Convention);var sourceCurve=Resolve(rolling.Source);
                    if(rolling.Universe.IsDefaultOrEmpty||rolling.Universe.Any(p=>p is null||p.Contract is null||p.Contract.Contract is null||p.PointId==Guid.Empty))
                        throw Invalid("A rolling mean requires an explicit contract universe and point mapping.");
                    var universe=rolling.Universe;
                    foreach(var member in universe){cancellation.ThrowIfCancellationRequested();RecordContract(member.Contract.Contract);}
                    if(universe.Select(p=>p.PointId).Distinct().Count()!=universe.Length||universe.Select(p=>p.Contract.Contract.Key).Distinct().Count()!=universe.Length||
                        universe.Any(p=>p.Contract.EligibleFrom.Offset!=TimeSpan.Zero)||
                        universe.Zip(universe.Skip(1)).Any(pair=>pair.First.Contract.Contract.DeliveryFrom>=pair.Second.Contract.Contract.DeliveryFrom||pair.First.Contract.Contract.Expiry>pair.Second.Contract.Contract.Expiry))
                        throw Invalid("Rolling contract identities and point mappings must be unique in increasing delivery and expiry order.");
                    if(rolling.Mode=="daily_delivery"&&(rolling.First!=1||rolling.Count!=1))
                        throw Invalid("Daily delivery selects one exact day; conflicting rank parameters are not supported.");
                    var selected=RecipeEngine.ResolveContracts(universe.Select(p=>p.Contract).ToImmutableArray(),rolling.Mode,
                        rolling.First,rolling.Count,context.ValuationAt,rolling.DeliveryDate);
                    var mapped=universe.ToDictionary(p=>p.Contract.Contract.Key);
                    var selectedPoints=selected.Select(contract=>
                    {
                        var pointKey=new CurvePointKey(sourceCurve,mapped[contract.Contract.Key].PointId);
                        if(!directory.TryGetValue(pointKey,out var item)||item.DeliveryFrom!=contract.Contract.DeliveryFrom||item.DeliveryTo!=contract.Contract.DeliveryTo||
                            item.Contract is {} declared&&declared!=contract.Contract)
                            throw Invalid("A rolling selection must map each exact contract to a matching curve point.");
                        return pointKey;
                    }).ToImmutableArray();
                    operation=Uniform(selectedPoints);break;
                case ConnectedScheduledMean scheduled:
                    RequireMean(scheduled.Convention);
                    var schedule=scheduled.Schedule;
                    if(schedule is null||schedule.CatalogId==Guid.Empty||schedule.ScheduleId==Guid.Empty||schedule.Version<1||
                        schedule.Engine!=ManagedDateSet||schedule.Context!=context||!TemporalCurveBindings.IsInstant(schedule.KnownFrom)||schedule.KnownFrom>context.KnownAt||
                        schedule.Dates is null||schedule.Dates.Count==0)
                        throw Invalid("A schedule requires its exact published version, managed date-set engine and the same complete resolved context.");
                    if(scheduled.Points.IsDefault||scheduled.Points.Length!=schedule.Dates.Count||scheduled.Points.Any(p=>p is null)||
                        scheduled.Points.Select(p=>p.Date).Distinct().Count()!=scheduled.Points.Length||
                        scheduled.Points.Any(p=>p.Date<schedule.Dates.From||p.Date>=schedule.Dates.To||!schedule.Dates.Contains(p.Date)))
                        throw Invalid("Map every selected schedule occurrence to exactly one explicit curve point.");
                    if(admittedSchedules.Add(schedule))
                    {
                        var scheduleKey=(schedule.CatalogId,schedule.ScheduleId,schedule.Version);
                        if(!scheduleEvidence.TryGetValue(scheduleKey,out var windows))scheduleEvidence.Add(scheduleKey,windows=[]);
                        foreach(var previous in windows)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            if(previous.Engine!=schedule.Engine||previous.KnownFrom!=schedule.KnownFrom)
                                throw Invalid("An exact schedule version cannot carry conflicting publication or engine evidence.");
                            var from=previous.Dates.From>schedule.Dates.From?previous.Dates.From:schedule.Dates.From;
                            var to=previous.Dates.To<schedule.Dates.To?previous.Dates.To:schedule.Dates.To;
                            if(from<to&&!previous.Dates.Dates.Where(date=>date>=from&&date<to)
                                .SequenceEqual(schedule.Dates.Dates.Where(date=>date>=from&&date<to)))
                                throw Invalid("Overlapping coverage of an exact schedule must agree on its occurrence dates.");
                        }
                        windows.Add(schedule);
                    }
                    var datedPoints=scheduled.Points.ToDictionary(p=>p.Date);
                    operation=Uniform(schedule.Dates.Dates.Select(date=>ResolvePoint(datedPoints[date].Point)).ToImmutableArray());break;
                default:throw Invalid("The connected-curve expression is unsupported.");
            }
            // Different dated occurrences may deliberately consume the same
            // point; each occurrence contributes its own uniform weight.
            if(point.Expression is not ConnectedScheduledMean&&operation.Dependencies.Distinct().Count()!=operation.Dependencies.Length)
                throw Invalid("Declare a referenced point once per operator; put its full explicit weight in that term.");
            operations.Add(key,operation);
            foreach(var dependency in operation.Dependencies)
            {
                if(!directory.TryGetValue(dependency,out var input))throw Missing("A required exact curve point is absent.");
                var expected=input.Unit;
                if(operation.Built is {} built&&dependency==operation.Dependencies[0]&&built.Multiplier is {} multiplier)
                    expected=new UnitPrice(default,input.Unit).Times(multiplier).Unit;
                if(expected!=point.Unit)throw new MarketDataException("unit_mismatch","Connected curve operators require their exact declared price units.");
                pending.Enqueue(dependency);
            }
        }
        Operation Uniform(ImmutableArray<CurvePointKey> points)=>new(null,points,Enumerable.Repeat(one,points.Length).ToImmutableArray(),ExactDecimal.Parse(points.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Operation Weighted(ImmutableArray<ConnectedWeightedTerm> terms,bool mean)
        {
            if(terms.IsDefaultOrEmpty||terms.Any(t=>t is null||t.Weight is null))
                throw Invalid("Every weighted term requires an explicit dimensionless weight.");
            var weights=terms.Select(t=>t.Weight!.Value).ToImmutableArray();
            if(mean&&weights.Any(w=>w.CompareTo(default)<0))throw Invalid("Weighted means require nonnegative weights.");
            var divisor=mean?weights.Aggregate(default(ExactDecimal),(total,w)=>total+w):one;
            if(mean&&divisor.CompareTo(default)<=0)throw Invalid("A weighted mean requires a positive weight sum.");
            return new(null,terms.Select(t=>ResolvePoint(t.Point)).ToImmutableArray(),weights,divisor);
        }

        var points=operations.Keys.OrderBy(k=>k.Curve.Revision.CatalogId).ThenBy(k=>k.Curve.Revision.RevisionId)
            .ThenBy(k=>k.Curve.Code,StringComparer.Ordinal).ThenBy(k=>k.PointId).Select(k=>directory[k]).ToImmutableArray();
        var indexes=new Dictionary<CurvePointKey,int>();var sources=new Dictionary<(InputKey,ObservationKey),(int,ConnectedSource,PriceUnit)>();
        var variableSources=new Dictionary<InputKey,ObservationKey>();var observationEvidence=new Dictionary<ObservationKey,MarketObservation>();
        var quoteEvidence=new Dictionary<ReferenceKey,QuoteSpecification>();var seriesEvidence=new Dictionary<SeriesKey,MarketSeries>();
        var nodeOperations=new List<Operation>();var primitives=ImmutableArray.CreateBuilder<ConnectedPrimitive>();
        foreach(var point in points)
        {
            cancellation.ThrowIfCancellationRequested();var operation=operations[point.Key];
            if(operation.Source is {} source)
            {
                if(quoteEvidence.TryGetValue(source.Quote.Key,out var quote)&&quote!=source.Quote)
                    throw Invalid("An exact quote identity cannot carry conflicting specifications in one calculation.");
                quoteEvidence[source.Quote.Key]=source.Quote;
                if(seriesEvidence.TryGetValue(source.Series.Key,out var series)&&series!=source.Series)
                    throw Invalid("An exact series identity cannot carry conflicting registrations in one calculation.");
                seriesEvidence[source.Series.Key]=source.Series;
                if(observationEvidence.TryGetValue(source.Observation.Key,out var observation)&&observation!=source.Observation)
                    throw Invalid("One observation identity cannot contain conflicting values or provenance.");
                observationEvidence[source.Observation.Key]=source.Observation;
                if(source.Input.Use==InputUse.Variable)
                {
                    if(variableSources.TryGetValue(source.Input,out var selected)&&selected!=source.Observation.Key)
                        throw Invalid("A variable input has conflicting selected observations in this context.");
                    variableSources[source.Input]=source.Observation.Key;
                }
                var identity=(source.Input,source.Observation.Key);
                if(sources.TryGetValue(identity,out var prior))
                {
                    if(prior.Item2.Observation!=source.Observation||prior.Item2.Quote!=source.Quote||prior.Item2.Series!=source.Series||prior.Item2.Request.Contract!=source.Request.Contract||prior.Item3!=point.Unit)
                        throw Invalid("A shared source observation has conflicting financial bindings.");
                    indexes.Add(point.Key,prior.Item1);continue;
                }
                sources.Add(identity,(nodeOperations.Count,source,point.Unit));
                primitives.Add(new(nodeOperations.Count,source,point.Unit));
            }
            indexes.Add(point.Key,nodeOperations.Count);nodeOperations.Add(operation);
        }
        var numericSpecifications=new Dictionary<ReferenceKey,ValueSpecification>();
        var currentNumericSelections=new Dictionary<InputKey,ObservationKey>();
        var recipeDeclarations=new Dictionary<ConnectedRecipeIdentity,ConnectedRecipeDeclaration>();
        var recipeCalendars=new Dictionary<ReferenceKey,DateSet>();
        var recipeSchedules=new Dictionary<(Guid,Guid,int),string>();
        foreach(var point in points)
        {
            if(point.Expression is ConnectedBuiltCurve built)
            {
                if(quoteEvidence.TryGetValue(built.Quote.Key,out var quote)&&quote!=built.Quote)
                    throw Invalid("One exact quote cannot carry conflicting source or computed domains.");
                quoteEvidence[built.Quote.Key]=built.Quote;
                if(built.Multiplier is {} multiplier)
                {
                    if(numericSpecifications.TryGetValue(multiplier.Specification.Key,out var prior)&&prior!=multiplier.Specification)
                        throw Invalid("One exact multiplier specification cannot carry conflicting metadata.");
                    numericSpecifications[multiplier.Specification.Key]=multiplier.Specification;
                }
                if(built.MultiplierInput is {} numeric)
                {
                    if(seriesEvidence.TryGetValue(numeric.Series.Key,out var priorSeries)&&priorSeries!=numeric.Series||
                        observationEvidence.TryGetValue(numeric.Observation.Key,out var priorObservation)&&priorObservation!=numeric.Observation)
                        throw Invalid("One exact numeric input cannot carry conflicting series or observation evidence.");
                    seriesEvidence[numeric.Series.Key]=numeric.Series;observationEvidence[numeric.Observation.Key]=numeric.Observation;
                    if(!built.Sample.FreezeHistorical)
                    {
                        if(currentNumericSelections.TryGetValue(numeric.Input,out var selected)&&selected!=numeric.Observation.Key)
                            throw Invalid("A current numeric input has conflicting selected observations in this context.");
                        currentNumericSelections[numeric.Input]=numeric.Observation.Key;
                    }
                }
            }
            if(point.Expression is ConnectedRecipeProjection projection)
            {
                var declaration=projection.Declaration;
                if(recipeDeclarations.TryGetValue(declaration.Identity,out var prior)&&prior.ManifestId!=declaration.ManifestId)
                    throw Invalid("One exact saved recipe version cannot carry conflicting declarations.");
                recipeDeclarations[declaration.Identity]=declaration;
                foreach(var calendar in declaration.References.Calendars.Concat(declaration.References.Schedules.Where(s=>s.Calendar is not null).Select(s=>s.Calendar!)))
                {
                    if(recipeCalendars.TryGetValue(calendar.Reference,out var dates)&&!dates.Equals(calendar.BusinessDates))
                        throw Invalid("One exact calendar version cannot carry conflicting complete business-day coverage.");
                    recipeCalendars[calendar.Reference]=calendar.BusinessDates;
                }
                foreach(var schedule in declaration.References.Schedules)
                {
                    var identity=(schedule.CatalogId,schedule.ScheduleId,schedule.Version);var evidence=ConnectedRecipeDeclaration.ScheduleEvidence(schedule);
                    if(recipeSchedules.TryGetValue(identity,out var priorSchedule)&&priorSchedule!=evidence)
                        throw Invalid("One exact schedule version cannot carry conflicting publication, calendar or occurrence evidence.");
                    recipeSchedules[identity]=evidence;
                }
            }
        }
        foreach(var declaration in points.Select(p=>p.Expression).OfType<ConnectedRecipeProjection>().Select(p=>p.Declaration).DistinctBy(d=>d.Identity))
        foreach(var published in declaration.PublishedSources.IsDefault?[]:declaration.PublishedSources)
        {
            var identity=new ConnectedRecipeIdentity(published.Reference.CatalogId,published.Reference.CurveId,published.Reference.Version,RecipeEngine.Version);
            if(!recipeDeclarations.TryGetValue(identity,out var imported)||imported.ManifestId!=published.ManifestId||imported.Curve!=published.Curve||
                imported.Definition.Output!=published.Reference.OutputNode)
                throw Invalid("A published source must retain its exact imported declaration and graph coordinates.");
        }
        var nodes=nodeOperations.Select(op=>new ConnectedNode(op.Source,op.Dependencies.Select(p=>indexes[p]).ToImmutableArray(),op.Weights,op.Divisor,op.Built)).ToImmutableArray();
        var edges=nodes.SelectMany((node,index)=>node.Dependencies.Distinct().Select(dependency=>new DependencyEdge(index,dependency)));
        DependencyPlan plan;
        try{plan=DependencyPlan.Compile(nodes.Length,edges,cancellation);}
        catch(CalculationException error){throw new MarketDataException(error.Code,error.Message);}
        return new(context,points.ToImmutableDictionary(p=>p.Key),indexes.ToImmutableDictionary(),primitives.ToImmutable(),nodes,plan,roots);
    }

    private sealed record Operation(ConnectedSource? Source,ImmutableArray<CurvePointKey> Dependencies,ImmutableArray<ExactDecimal> Weights,ExactDecimal Divisor,ConnectedBuiltCurve? Built=null);
    private static bool Valid(CurveKey curve)=>curve.Revision.CatalogId!=Guid.Empty&&curve.Revision.RevisionId!=Guid.Empty&&!string.IsNullOrWhiteSpace(curve.Code)&&curve.Code.Trim()==curve.Code;
    private static void RequireMean(string convention)
    {if(convention!=UniformMean)throw Invalid("Select the explicit uniform arithmetic mean convention.");}
    private static MarketDataException Invalid(string message)=>new("connected_curve_definition",message);
    private static MarketDataException Missing(string message)=>new("connected_curve_point",message);

    private static void ValidateSource(ConnectedCurvePoint point,ConnectedSource source,ResolvedCalculationContext context)
    {
        if(source.Observation is not {} observation||source.Quote is null||source.Series is not {} series||point.Contract is not {} contract||
            source.Request.Curve!=point.Key.Curve||source.Request.Contract!=contract.Key||contract.Key.Revision!=point.Key.Curve.Revision||contract.Key.Id==Guid.Empty||!Enum.IsDefined(contract.Kind)||
            source.Input.Series!=observation.Series||source.Input.Series.Revision!=point.Key.Curve.Revision||source.Input.Series.Id==Guid.Empty||
            series.Key!=source.Input.Series||series.Contract!=contract.Key||series.Quote!=source.Quote.Key||series.ValueSpecification is not null||
            observation.Key.CatalogId!=point.Key.Curve.Revision.CatalogId||observation.Key.Id==Guid.Empty||
            !Enum.IsDefined(source.Input.Use)||!Enum.IsDefined(observation.Role)||observation.Role==ObservationRole.Parameter||observation.State!=PublicationState.Final||
            observation.ObservedAt.Offset!=TimeSpan.Zero||observation.ObservedAt>context.MarketAsOf||contract.Expiry.Offset!=TimeSpan.Zero)
            throw Invalid("A source requires a coherent exact contract, input use, final observation and resolved market cutoff.");
        Money.RequireKey(observation.Source);
        if(observation.Source.Revision!=source.Quote.Key.Revision||!Enum.IsDefined(source.Quote.Kind)||source.Quote.TradingTick.CompareTo(default)<=0||source.Quote.ClearingIncrement.CompareTo(default)<=0)
            throw Invalid("The source requires coherent exact quote references and positive trading and clearing increments.");
        if(source.Input.Use==InputUse.Variable&&(observation.Role is not (ObservationRole.TraderMark or ObservationRole.DailySettlement)||contract.Expiry<=context.ValuationAt))
            throw Invalid("Only live selected market quotes may be variable inputs; fixings and expired contract facts stay frozen.");
        if(source.Sample is {} sample)
        {
            ValidateSample(sample,context);
            if(source.Input.Use!=(sample.FreezeHistorical||sample.FreezeObservation?InputUse.FrozenFact:sample.OriginalUse)||observation.ObservedAt>sample.MarketCutoff||
                sample.OriginalUse==InputUse.Variable&&contract.Expiry<=sample.ValuationAt||
                sample.FreezeObservation&&(observation.ObservedAt!=sample.ValuationAt||observation.Role is not(ObservationRole.DailySettlement or ObservationRole.TraderMark)))
                throw Invalid("A sampled source must preserve its original selected use and explicit historical freezing.");
        }
        var value=new UnitPrice(observation.Value,source.Quote);
        if(value.Unit!=point.Unit)throw new MarketDataException("unit_mismatch","The source price must retain its exact selected quote unit.");
    }
    private static void ValidateSample(ConnectedSourceSample sample,ResolvedCalculationContext context)
    {
        if(sample is null||sample.SnapshotId==Guid.Empty||!TemporalCurveBindings.IsInstant(sample.ValuationAt)||
            !TemporalCurveBindings.IsInstant(sample.MarketCutoff)||sample.MarketCutoff>sample.ValuationAt||sample.MarketCutoff>context.MarketAsOf||
            sample.ValuationAt>context.ValuationAt||sample.SelectionPolicy!=InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering||
            sample.FreezeObservation&&(sample.ValuationAt!=context.ValuationAt||sample.OriginalUse!=InputUse.Variable||sample.FreezeHistorical)||
            !Enum.IsDefined(sample.OriginalUse)||sample.FreezeHistorical!=(sample.ValuationAt<context.ValuationAt)||
            !sample.FreezeHistorical&&sample.MarketCutoff!=context.MarketAsOf)
            throw Invalid("A source sample requires exact snapshot and UTC coordinates, selection policy and historical-freeze semantics.");
    }

    public static ImmutableArray<ConnectedCurveDefinition> MergeDefinitions(IEnumerable<ConnectedCurveDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var points=new Dictionary<CurvePointKey,ConnectedCurvePoint>();
        foreach(var definition in definitions)
        {
            if(definition is null||!Valid(definition.Key)||definition.Points.IsDefaultOrEmpty)
                throw Invalid("Merge complete exact curve definitions with explicit points.");
            foreach(var point in definition.Points)
            {
                if(point is null||point.Key.Curve!=definition.Key||point.Key.PointId==Guid.Empty)
                    throw Invalid("Merged points must belong to their exact curve definition.");
                if(points.TryGetValue(point.Key,out var prior)&&ConnectedIdentity.Digest(prior)!=ConnectedIdentity.Digest(point))
                    throw Invalid("A shared exact connected point has conflicting declarations.");
                points[point.Key]=point;
            }
        }
        return points.Values.GroupBy(p=>p.Key.Curve).OrderBy(g=>g.Key.Revision.CatalogId).ThenBy(g=>g.Key.Revision.RevisionId)
            .ThenBy(g=>g.Key.Code,StringComparer.Ordinal).Select(g=>new ConnectedCurveDefinition(g.Key,g.OrderBy(p=>p.Key.PointId).ToImmutableArray())).ToImmutableArray();
    }
}

public sealed record ConnectedPrimitive(int Node,ConnectedSource Source,PriceUnit Unit);
internal sealed record ConnectedNode(ConnectedSource? Source,ImmutableArray<int> Dependencies,ImmutableArray<ExactDecimal> Weights,ExactDecimal Divisor,ConnectedBuiltCurve? Built);

public sealed partial class ConnectedCurvePlan
{
    private readonly ImmutableDictionary<CurvePointKey,int> indexes;
    private readonly ImmutableArray<ConnectedNode> nodes;
    public ResolvedCalculationContext Context {get;}
    public string NumericalPolicy=>ConnectedCurves.NumericalPolicy;
    public string ManifestId {get;}
    public ImmutableDictionary<CurvePointKey,ConnectedCurvePoint> Points {get;}
    public ImmutableArray<ConnectedPrimitive> Primitives {get;}
    public DependencyPlan DependencyPlan {get;}
    internal ConnectedCurvePlan(ResolvedCalculationContext context,ImmutableDictionary<CurvePointKey,ConnectedCurvePoint> points,
        ImmutableDictionary<CurvePointKey,int> indexes,ImmutableArray<ConnectedPrimitive> primitives,ImmutableArray<ConnectedNode> nodes,
        DependencyPlan dependencyPlan,ImmutableArray<CurvePointKey> roots)
    {
        Context=context;Points=points;this.indexes=indexes;Primitives=primitives;this.nodes=nodes;DependencyPlan=dependencyPlan;
        // Canonical resolved evidence, including exact scalar text. Dictionary
        // iteration order and display rounding cannot change a plan's identity.
        object Unit(PriceUnit unit)=>new{unit.Currency,unit.Quantity,Multiplier=unit.Multiplier.ToString()};
        object Source(ConnectedSource source)=>new{source.Request,source.Input,source.Sample,SeriesDefinition=source.Series,source.Observation.Key,source.Observation.Series,
            source.Observation.Source,source.Observation.Role,source.Observation.State,source.Observation.ObservedAt,
            Value=source.Observation.Value.ToString(),Quote=new{source.Quote.Key,source.Quote.Kind,source.Quote.Currency,source.Quote.Quantity,
                Multiplier=source.Quote.Multiplier.ToString(),TradingTick=source.Quote.TradingTick.ToString(),ClearingIncrement=source.Quote.ClearingIncrement.ToString(),
                Minimum=source.Quote.Minimum.ToString(),Maximum=source.Quote.Maximum.ToString()}};
        object Reference(ConnectedCurveReference reference)=>reference switch
        {
            ExactCurveReference exact=>new{Kind="exact",exact.Curve},
            LogicalCurveReference logical=>new{Kind="logical",logical.Curve},
            _=>throw new InvalidOperationException("An uncompiled curve reference entered a resolved plan.")
        };
        object PointReference(CurvePointReference point)=>new{Curve=Reference(point.Curve),point.PointId};
        object Terms(ImmutableArray<ConnectedWeightedTerm> terms)=>terms.Select(t=>new{Point=PointReference(t.Point),Weight=t.Weight!.Value.ToString()}).ToArray();
        object Expression(ConnectedCurveExpression expression)=>expression switch
        {
            ConnectedSource source=>Source(source),
            ConnectedBuiltCurve built=>ConnectedIdentity.Evidence(built),
            ConnectedRecipeProjection projection=>new{Value=PointReference(projection.Value),projection.Position,projection.Declaration.ManifestId,projection.Declaration.Identity,
                Declaration=projection.Declaration.Canonical},
            ConnectedWeightedSum sum=>new{Terms=Terms(sum.Terms)},
            ConnectedWeightedMean mean=>new{Terms=Terms(mean.Terms)},
            ConnectedMean mean=>new{Points=mean.Points.Select(PointReference).ToArray(),mean.Convention},
            ConnectedRollingMean rolling=>new{Source=Reference(rolling.Source),rolling.Universe,rolling.Mode,rolling.First,rolling.Count,rolling.DeliveryDate,rolling.Convention},
            ConnectedScheduledMean scheduled=>new{Schedule=new{scheduled.Schedule.CatalogId,scheduled.Schedule.ScheduleId,scheduled.Schedule.Version,
                scheduled.Schedule.Engine,scheduled.Schedule.KnownFrom,scheduled.Schedule.Context,
                Dates=new{scheduled.Schedule.Dates.From,scheduled.Schedule.Dates.To,Values=scheduled.Schedule.Dates.Dates.ToArray()}},
                Points=scheduled.Points.OrderBy(p=>p.Date).Select(p=>new{p.Date,Point=PointReference(p.Point)}).ToArray(),scheduled.Convention},
            _=>throw new InvalidOperationException("An uncompiled curve expression entered a resolved plan.")
        };
        object PointEvidence(ConnectedCurvePoint p)
        {
            var original=new{p.Key,p.DeliveryFrom,p.DeliveryTo,Unit=Unit(p.Unit),p.Contract,Operator=p.Expression.GetType().Name,Declaration=Expression(p.Expression),Node=IndexOf(p.Key)};
            if(p.PointDate is null)return original;
            var node=JsonSerializer.SerializeToNode(original)!.AsObject();
            if(p.PointDate is {} date)node["PointDate"]=JsonSerializer.SerializeToNode(date);
            return node;
        }
        var evidence=new{NumericalPolicy,Context,Roots=roots.OrderBy(p=>p.Curve.Revision.CatalogId).ThenBy(p=>p.Curve.Revision.RevisionId).ThenBy(p=>p.Curve.Code,StringComparer.Ordinal).ThenBy(p=>p.PointId).ToArray(),
            Points=points.Values.OrderBy(p=>p.Key.Curve.Revision.CatalogId).ThenBy(p=>p.Key.Curve.Revision.RevisionId).ThenBy(p=>p.Key.Curve.Code,StringComparer.Ordinal).ThenBy(p=>p.Key.PointId)
                .Select(PointEvidence).ToArray(),
            Nodes=nodes.Select(n=>new{Source=n.Source is {} source?Source(source):null,n.Dependencies,Weights=n.Weights.Select(w=>w.ToString()).ToArray(),Divisor=n.Divisor.ToString()}).ToArray()};
        ManifestId=Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(evidence)));
    }
    public ConnectedCurvePoint GetPoint(CurvePointKey point)=>Points.TryGetValue(point,out var value)?value:throw new MarketDataException("connected_curve_point","The point does not belong to this compiled graph.");
    public int IndexOf(CurvePointKey point)=>indexes.TryGetValue(point,out var index)?index:throw new MarketDataException("connected_curve_point","The point does not belong to this compiled graph.");
    public GraphSession<ExactUnitPrice> CreateSession()=>new(DependencyPlan,EvaluateNode,(a,b)=>a.Add(b),ValidateNode);
    public ExactUnitPrice Evaluate(CurvePointKey point,CancellationToken cancellation=default)
        =>CreateSession().Get(IndexOf(point),cancellation);
    private ExactUnitPrice EvaluateNode(int node,GraphInputs<ExactUnitPrice> inputs,CancellationToken cancellation)
        =>EvaluateNode(node,inputs,static price=>price,cancellation);
    internal ExactUnitPrice EvaluateNode<T>(int node,GraphInputs<T> inputs,Func<T,ExactUnitPrice> price,CancellationToken cancellation) where T:notnull
    {
        cancellation.ThrowIfCancellationRequested();var operation=nodes[node];
        if(operation.Source is {} source)return ExactUnitPrice.FromPrice(new(source.Observation.Value,source.Quote));
        if(operation.Built is {} built)
        {
            var value=price(inputs.Get(operation.Dependencies[0])).ToUnitPriceExact();
            if(built.Multiplier is {} multiplier)value=value.Times(multiplier);
            value=value.Add(price(inputs.Get(operation.Dependencies[1])).ToUnitPriceExact());
            return ExactUnitPrice.FromPrice(value);
        }
        var sum=price(inputs.Get(operation.Dependencies[0])).Scale(operation.Weights[0]);
        for(var i=1;i<operation.Dependencies.Length;i++)
        {cancellation.ThrowIfCancellationRequested();sum=sum.Add(price(inputs.Get(operation.Dependencies[i])).Scale(operation.Weights[i]));}
        return sum.Divide(operation.Divisor);
    }
    internal void ValidateNode(int node,ExactUnitPrice value)
    {
        var operation=nodes[node];var quote=operation.Source?.Quote??operation.Built?.Quote;
        if(quote is not null)value.ToUnitPriceExact().RequireQuote(quote);
    }
}
