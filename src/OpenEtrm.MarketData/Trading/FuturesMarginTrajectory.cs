using System.Collections.Immutable;

namespace OpenEtrm.MarketData.Trading;

public enum FuturesMarginPhase { Daily=1, Final }
public enum FuturesMarginOpening { AfterOpeningInstant=1, IncludeOpeningInstant }
public sealed record FuturesMarginOccurrence(Guid Id,DateTimeOffset At,DateTimeOffset DueAt,FuturesMarginPhase Phase);
public sealed record FuturesMarginPolicy(Guid Id,int Revision,ContractKey Contract,CurveKey SettlementCurve,DateTimeOffset From,DateTimeOffset Until,
    ImmutableArray<FuturesMarginOccurrence> Occurrences,CashRoundingConvention Rounding,string RoundingScope,
    FuturesMarginOpening Opening,string Implementation)
{
    public const string Version="dated_futures_margin_policy_v1";
}
// The latest visible native determination of an occurrence, selected at the
// operation's certified cutoff. Payments are not part of this input or proposal.
public sealed record RecordedFuturesMargin(Guid OccurrenceId,Guid DeterminationId,Guid? BasisDeterminationId,ExactUnitPrice PreviousBasis,
    FuturesSettlementFact Evidence,ExactMoney ExactVariation,Money Variation,DateTimeOffset KnownAt,int PublicationOrder)
{
    public ExactUnitPrice Price=>Evidence.Price;
}
public sealed record FuturesMarginTerms(MeasureBinding Owner,Guid BookId,Quantity Quantity,UnitPrice ExecutionPrice,
    DateTimeOffset ExecutedAt,FuturesMarginPolicy Policy);
public sealed record FuturesMarginHistory(FuturesMarginTerms Terms,Guid SourceContextId,DateTimeOffset KnownThrough,
    ImmutableArray<RecordedFuturesMargin> Determinations);
public sealed record FuturesMarginStep(FuturesMarginOccurrence Occurrence,Guid? PreviousDetermination,Guid? BasisOccurrence,
    ExactUnitPrice PreviousBasis,FuturesSettlementFact Evidence,ExactMoney ExactVariation,Money Variation,
    ExactMoney Adjustment,DateTimeOffset? AdjustmentDue,ExactMoney RoundingEffect,bool NoChange);
public sealed record FuturesMarginTrajectory(MeasureBinding Owner,Guid BookId,FuturesMarginPolicy Policy,
    ProposedFuturesMarginBasis Basis,ImmutableArray<FuturesMarginStep> Steps);

public sealed partial record FuturesContract
{
    public FuturesSettlementSource ValidateMarginPolicy(FuturesMarketBinding market,FuturesMarginPolicy policy,CurveModel model,MarketSnapshot context)
    {
        RequireSameDefinition(market.Instrument);
        RequireMarginContext(context,market.Contract.Key.Revision,"futures_margin_policy");
        RequireMarginPolicy(policy,market,context);
        return SettlementSource(market,policy.SettlementCurve,model);
    }
    public FuturesMarginTerms ProposeMarginTerms(Trade trade,Guid bindingId,FuturesMarginPolicy policy,CurveModel model,MarketSnapshot context)
    {
        var(holding,execution)=MarginExecution(trade,bindingId,context,"futures_margin_policy");
        _=MarginEvents(policy,execution.MarketBinding,execution.ExecutedAt,context);
        _=SettlementSource(execution.MarketBinding,policy.SettlementCurve,model);
        return new(new(trade!.Id,bindingId),trade.BookId,holding.Quantity,execution.ExecutionPrice,execution.ExecutedAt,policy);
    }
    // A committed future-effective execution is source evidence, not an opening
    // holding. Its native causal event is proved by the source adapter.
    public FuturesMarginTerms RetainKnownMarginTerms(Trade trade,Guid bindingId,FuturesMarginPolicy policy,CurveModel model,MarketSnapshot context)
    {
        var(holding,execution)=MarginExecution(trade,bindingId,context,"futures_margin_policy");
        RequireMarginExecutionPolicy(policy,execution.MarketBinding,execution.ExecutedAt,context);
        if(execution.ExecutedAt<=context.ValuationAt)
            throw TimelineError("futures_margin_policy","Knowledge-only future terms require execution after the opening instant.");
        _=SettlementSource(execution.MarketBinding,policy.SettlementCurve,model);
        return new(new(trade.Id,bindingId),trade.BookId,holding.Quantity,execution.ExecutionPrice,execution.ExecutedAt,policy);
    }
    public ImmutableArray<FuturesMarginOccurrence> RequiredMarginOccurrences(Trade trade,Guid bindingId,FuturesMarginPolicy policy,
        MarketSnapshot context,Guid throughOccurrence,FuturesMarginHistory history)
    {
        var(_,execution)=MarginExecution(trade,bindingId,context,"futures_margin_coverage");
        var events=MarginEvents(policy,execution.MarketBinding,execution.ExecutedAt,context);
        if(history is null||history.Determinations.IsDefault)throw TimelineError("futures_margin_coverage","Supply the exact native accepted prefix.");
        var index=events.Select((e,i)=>(e.Id,i)).ToDictionary(e=>e.Id,e=>e.i);
        return [..events.Take(MarginLast(events,index,throughOccurrence,history.Determinations,context)+1)];
    }
    // Recompute the required prefix, retaining both original and successor
    // effects. Correcting yesterday's price must not reset today's basis.
    public FuturesMarginTrajectory PlanMarginTrajectory(Trade trade,Guid bindingId,FuturesMarginPolicy policy,
        MarketSnapshot context,Guid throughOccurrence,ImmutableDictionary<Guid,FuturesSettlementFact> facts,
        FuturesMarginHistory history,ImmutableDictionary<Guid,DateTimeOffset> correctionDue)
    {
        if(history?.Terms is null)
            throw TimelineError("futures_margin_history","Supply complete retained terms and certified native determination history.");
        var recorded=history.Determinations;
        if(recorded.IsDefault||facts is null||correctionDue is null)
            throw TimelineError("futures_margin_coverage","Supply the exact held binding, captured context and complete native evidence.");
        var(holding,execution)=MarginExecution(trade,bindingId,context,"futures_margin_coverage");
        var market=execution.MarketBinding;
        var events=MarginEvents(policy,market,execution.ExecutedAt,context);
        var terms=history.Terms;var adopted=terms.Policy;
        if(terms.Owner!=new MeasureBinding(trade!.Id,bindingId)||terms.BookId!=trade.BookId||terms.Quantity!=holding.Quantity||
            terms.ExecutionPrice!=execution.ExecutionPrice||terms.ExecutedAt!=execution.ExecutedAt||
            !TradingInstant.IsSupported(terms.ExecutedAt)||!TradingInstant.IsSupported(history.KnownThrough)||
            history.SourceContextId!=context.Knowledge!.ContextId||history.KnownThrough!=context.Knowledge.KnownAt||
            adopted is null||adopted.Occurrences.IsDefault||policy with{Occurrences=adopted.Occurrences}!=adopted||
            !policy.Occurrences.ToHashSet().SetEquals(adopted.Occurrences))
            throw TimelineError("futures_margin_history","Native history must retain this exact owner, execution terms, adopted policy and certified read context.");
        _=MarginEvents(adopted,market,execution.ExecutedAt,context);
        var index=events.Select((e,i)=>(e.Id,i)).ToDictionary(e=>e.Id,e=>e.i);
        var last=MarginLast(events,index,throughOccurrence,recorded,context);
        var accepted=recorded.OrderBy(r=>index[r.OccurrenceId]).ToImmutableArray();
        if(!accepted.Select(r=>r.OccurrenceId).SequenceEqual(events.Take(accepted.Length).Select(e=>e.Id))||
            accepted.Select(r=>r.DeterminationId).Distinct().Count()!=accepted.Length)
            throw TimelineError("futures_margin_coverage","Accepted determinations must form a complete, distinct causal prefix.");
        var acceptedBasis=ExactUnitPrice.FromPrice(execution.ExecutionPrice);Guid? acceptedPredecessor=null;
        var acceptedKnown=DateTimeOffset.MinValue;var acceptedPublication=0;
        foreach(var existing in accepted)
        {
            if(!TradingInstant.IsSupported(existing.KnownAt)||existing.KnownAt>context.Knowledge.KnownAt||
                existing.KnownAt<existing.Evidence.KnownAt||existing.KnownAt<acceptedKnown||
                existing.PublicationOrder<1||existing.PublicationOrder<acceptedPublication||
                existing.PublicationOrder==acceptedPublication&&existing.KnownAt!=acceptedKnown)
                throw TimelineError("futures_margin_history","Accepted determinations require visible, causally ordered certified publication evidence.");
            RequireMarginFact(market,policy,events[index[existing.OccurrenceId]],existing.Evidence,context);
            if(existing.Price is null||existing.PreviousBasis!=acceptedBasis||existing.BasisDeterminationId!=acceptedPredecessor||
                existing.ExactVariation is null||existing.Variation is null||existing.Price.Unit!=acceptedBasis.Unit||
                existing.Variation.Currency!=Currency||existing.ExactVariation.Currency!=Currency)
                throw TimelineError("futures_margin_coverage","Accepted determinations must retain their exact predecessor, basis, quotation and currency.");
            var(oldExact,oldTotal,_)=FuturesMargin.Difference(acceptedBasis,existing.Price,holding.Quantity,policy.Rounding,policy.RoundingScope);
            if(existing.ExactVariation.CompareTo(oldExact)!=0||existing.Variation!=oldTotal)
                throw TimelineError("futures_margin_coverage","Accepted totals must agree with their pinned quantity, basis and rounding policy.");
            acceptedBasis=existing.Price;acceptedPredecessor=existing.DeterminationId;
            acceptedKnown=existing.KnownAt;acceptedPublication=existing.PublicationOrder;
        }
        var prefix=events.Take(last+1).ToImmutableArray();
        if(!prefix.Select(e=>e.Id).ToHashSet().SetEquals(facts.Keys)||correctionDue.Any(p=>!index.ContainsKey(p.Key)||!TradingInstant.IsSupported(p.Value)))
            throw TimelineError("futures_margin_coverage","Every required occurrence must have its exact visible source; no scheduled margin is skipped.");
        var prior=recorded.ToDictionary(r=>r.OccurrenceId);
        var previous=ExactUnitPrice.FromPrice(execution.ExecutionPrice);Guid? predecessor=null;var predecessorChanged=false;
        var steps=ImmutableArray.CreateBuilder<FuturesMarginStep>();var usedCorrections=new HashSet<Guid>();
        foreach(var occurrence in prefix)
        {
            var fact=facts[occurrence.Id];RequireMarginFact(market,policy,occurrence,fact,context);
            var(exact,total,effect)=FuturesMargin.Difference(previous,fact.Price,holding.Quantity,policy.Rounding,policy.RoundingScope);
            var existing=prior.GetValueOrDefault(occurrence.Id);
            var unchanged=existing is not null&&!predecessorChanged&&existing.Evidence==fact&&existing.PreviousBasis==previous&&
                existing.ExactVariation.CompareTo(exact)==0&&existing.Variation==total;
            var adjustment=ExactMoney.FromMoney(total).Subtract(existing is null?ExactMoney.Parse("0",Currency):ExactMoney.FromMoney(existing.Variation));
            DateTimeOffset? due=null;
            if(adjustment.Numerator!="0")
            {
                if(existing is null)due=occurrence.DueAt;
                else if(correctionDue.TryGetValue(occurrence.Id,out var selected)&&selected>=occurrence.At)
                {due=selected;usedCorrections.Add(occurrence.Id);}
                else throw TimelineError("futures_margin_correction_due","Select an explicit due time for every nonzero correction adjustment.");
            }
            steps.Add(new(occurrence,existing?.DeterminationId,predecessor,previous,fact,exact,total,adjustment,due,effect,unchanged));
            previous=fact.Price;predecessor=occurrence.Id;predecessorChanged=!unchanged;
        }
        if(!usedCorrections.SetEquals(correctionDue.Keys))
            throw TimelineError("futures_margin_correction_due","Supply due terms only for the nonzero correction adjustments in this exact closure.");
        var final=prefix[^1];
        return new(new(trade!.Id,bindingId),trade.BookId,policy,new(previous,final.At,
            final.Phase==FuturesMarginPhase.Final?FuturesMarginStatus.FinalMarginDetermined:FuturesMarginStatus.Open),steps.ToImmutable());
    }

    private static int MarginLast(ImmutableArray<FuturesMarginOccurrence> events,Dictionary<Guid,int> index,Guid through,
        ImmutableArray<RecordedFuturesMargin> recorded,MarketSnapshot context)
    {
        if(!index.TryGetValue(through,out var last)||events[last].At>context.ValuationAt||
            recorded.Any(r=>r is null||r.Evidence is null||r.DeterminationId==Guid.Empty||!index.ContainsKey(r.OccurrenceId)||
                events[index[r.OccurrenceId]].At>context.ValuationAt)||
            recorded.Select(r=>r.OccurrenceId).Distinct().Count()!=recorded.Length)
            throw TimelineError("futures_margin_coverage","Select a due occurrence and every accepted predecessor without backdating state.");
        return recorded.Length>0?Math.Max(last,recorded.Max(r=>index[r.OccurrenceId])):last;
    }

    private ImmutableArray<FuturesMarginOccurrence> MarginEvents(FuturesMarginPolicy policy,FuturesMarketBinding market,
        DateTimeOffset executedAt,MarketSnapshot context)
    {
        RequireMarginExecutionPolicy(policy,market,executedAt,context);
        if(context.ValuationAt<executedAt)
            throw TimelineError("futures_margin_policy","The selected policy must apply to this exact execution and business instant.");
        return [..policy.Occurrences.Where(e=>e.At>executedAt||e.At==executedAt&&policy.Opening==FuturesMarginOpening.IncludeOpeningInstant)
            .OrderBy(e=>e.At).ThenBy(e=>e.Phase)];
    }

    private void RequireMarginExecutionPolicy(FuturesMarginPolicy policy,FuturesMarketBinding market,DateTimeOffset executedAt,MarketSnapshot context)
    {
        RequireMarginPolicy(policy,market,context);
        if(executedAt<policy.From||executedAt>=policy.Until)
            throw TimelineError("futures_margin_policy","The selected policy must cover this exact execution instant.");
    }

    private void RequireMarginPolicy(FuturesMarginPolicy policy,FuturesMarketBinding market,MarketSnapshot context)
    {
        if(policy is null||policy.Implementation!=FuturesMarginPolicy.Version||policy.Id==Guid.Empty||policy.Revision<1||
            policy.Contract!=market.Contract.Key||context.Definitions!=policy.Contract.Revision||
            policy.SettlementCurve.Revision!=policy.Contract.Revision||string.IsNullOrWhiteSpace(policy.SettlementCurve.Code)||
            !TradingInstant.IsSupported(policy.From)||!TradingInstant.IsSupported(policy.Until)||policy.From>=policy.Until||
            !Enum.IsDefined(policy.Opening)||policy.Occurrences.IsDefaultOrEmpty||policy.Rounding is null||
            policy.RoundingScope!=FuturesMargin.HeldBindingRounding||policy.Occurrences.Any(e=>e is null||e.Id==Guid.Empty||!Enum.IsDefined(e.Phase)||
                !TradingInstant.IsSupported(e.At)||!TradingInstant.IsSupported(e.DueAt)||e.At<policy.From||e.At>=policy.Until||e.DueAt<e.At||
                (e.Phase==FuturesMarginPhase.Daily?e.At>Series.LastTradingAt:e.At<Series.LastTradingAt))||
            policy.Occurrences.Select(e=>e.Id).Distinct().Count()!=policy.Occurrences.Length||
            policy.Occurrences.Select(e=>(e.At,e.Phase)).Distinct().Count()!=policy.Occurrences.Length||
            policy.Occurrences.Count(e=>e.Phase==FuturesMarginPhase.Final)!=1)
            throw TimelineError("futures_margin_policy","Select an exact complete dated margin policy and explicit opening/rounding conventions.");
        policy.Rounding.Require(Currency,CashRoundingStage.PaymentTotal);
    }

    private void RequireMarginFact(FuturesMarketBinding market,FuturesMarginPolicy policy,FuturesMarginOccurrence occurrence,FuturesSettlementFact fact,MarketSnapshot context)
    {
        if(fact is null||fact.Contract!=market.Contract.Key||fact.Series!=SeriesKey||fact.SettlementCurve!=policy.SettlementCurve||
            fact.Observation.CatalogId!=market.Contract.Key.Revision.CatalogId||
            fact.Observation.Id==Guid.Empty||fact.Price is null||fact.Price.Unit!=PriceUnit.FromQuote(market.Quote)||
            !TradingInstant.IsSupported(fact.KnownAt)||fact.KnownAt>context.Knowledge!.KnownAt||fact.EffectiveAt!=occurrence.At||
            fact.EffectiveAt>context.MarketCutoff||fact.Role!=(occurrence.Phase==FuturesMarginPhase.Final?ObservationRole.FinalContractSettlement:ObservationRole.DailySettlement))
            throw TimelineError("futures_settlement_evidence","Every source must match its exact occurrence, series, role and captured cutoff.");
        UnitPrice price;
        try{price=fact.Price.ToUnitPriceExact();}
        catch(MarketDataException e){throw new MarketDataException("futures_settlement_evidence","Select a qualified native settlement quote.",e);}
        if(!price.Value.IsMultipleOf(market.Quote.ClearingIncrement)||price.Value.CompareTo(market.Quote.Minimum)<0||price.Value.CompareTo(market.Quote.Maximum)>0)
            throw TimelineError("futures_settlement_evidence","The settlement must satisfy its native quotation domain and clearing grid.");
        if(occurrence.Phase==FuturesMarginPhase.Final&&Series.SettlementMethod!="cash")
            throw TimelineError("futures_delivery_transition","A physical final occurrence requires its qualified delivery adapter.");
    }
    private static MarketDataException TimelineError(string code,string message)=>new(code,message);

    private (TradeInstrument Holding,FuturesExecution Execution) MarginExecution(Trade trade,Guid bindingId,MarketSnapshot context,string code)
    {
        var holding=trade?.Instruments.SingleOrDefault(p=>p.Id==bindingId);
        if(holding is null||holding.Instrument.Id!=Id)
            throw TimelineError(code,"Supply this exact held futures binding and its retained execution.");
        var execution=FuturesExecution.Require(holding);
        RequireSameDefinition(execution.Instrument);
        RequireMarginContext(context,execution.MarketBinding.Contract.Key.Revision,code);
        return(holding,execution);
    }
    private static void RequireMarginContext(MarketSnapshot context,DefinitionRevision revision,string code)
    {
        if(context?.Knowledge is null||context.Id==Guid.Empty||context.Definitions!=revision||!context.HasCoherentSelections()||
            context.SelectionPolicy!=InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering||
            !TradingInstant.IsSupported(context.ValuationAt)||!TradingInstant.IsSupported(context.MarketCutoff)||context.MarketCutoff>context.ValuationAt)
            throw TimelineError(code,"Supply the exact coherent futures source context and supported business, market and certified knowledge coordinates.");
    }
}
