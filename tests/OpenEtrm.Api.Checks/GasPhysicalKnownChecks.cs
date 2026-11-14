using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class GasPhysicalKnownChecks
{
    internal static async Task Run(string path)
    {
        using var document=JsonDocument.Parse(File.ReadAllText(path));var fixture=document.RootElement;
        var manifest=fixture.GetProperty("manifest").Deserialize<PreviewManifest>(LifecycleNative.Json)!;
        var host=Path.GetFullPath(fixture.Text("host"));
        if(!host.StartsWith("/private/tmp/oetrm-ref-",StringComparison.Ordinal)&&!host.StartsWith("/tmp/oetrm-ref-",StringComparison.Ordinal))
            throw new Exception("Physical source checks require owned disposable PostgreSQL.");
        var login=new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="preview_runtime",Timezone="UTC",Pooling=false};
        await using var database=new PreviewDatabase(manifest,new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="preview_authenticator"},login);
        await database.WithIdentity("trader",null,async(connection,ct)=>
        {
            var context=fixture.Id("contextId");var at=fixture.Instant("at");var source=(await new KnowledgeStore(manifest.CatalogId).Read(connection,context,ct)).Context;
            await PinnedContext.BindExact(connection,manifest.CatalogId,source,ct);await TradingContext.Bind(connection,manifest.CatalogId,source,at,ct);
            var frame=new InputFrame(Guid.NewGuid(),"Synthetic known physical source",at,DateWorkspace.Date(source.EffectiveOn),
                [..fixture.GetProperty("pins").EnumerateArray().Select(p=>p.GetGuid())],
                ImmutableDictionary<string,ImmutableArray<Guid>>.Empty.Add("nymex_ng",[fixture.Id("markContract")]),context);
            var data=await MarketInputSelection.Root(database,connection,frame,ct);var snapshot=data.Snapshot(frame).WithKnowledge(context,SchedulePreview.ParseAt(source.KnownAt));
            var books=ImmutableArray.Create(fixture.Id("bookId"));
            var read=await GasPhysicalKnownAttachmentStore.Read(connection,database,frame,snapshot,books,ct);
            bool currentQuantitySource;
            await using(var publicationContract=ScheduleStore.Command(connection,"SELECT to_regprocedure('security.reconcile_gas_futures_delivery_batch(uuid,uuid,uuid,jsonb,timestamptz,uuid,uuid,integer,boolean,jsonb)') IS NOT NULL"))
                currentQuantitySource=(bool)(await publicationContract.ExecuteScalarAsync(ct))!;
            if(currentQuantitySource&&read.Attachments.SelectMany(a=>a.Performances).Any(p=>p.OriginalPublicationOrder is null or <1))
                    throw new Exception("The current native source lost original physical publication identity when materializing a financial revision.");
            if(read.Attachments.Length!=2||read.Attachments.Any(a=>!a.SourceSnapshot.HasSameInputs(snapshot)||a.FinalOccurrence.At>=at||
                !a.Performances.Any(p=>p.PerformedAt>at)||a.CurrentFinal.Price.ToExactString()!="6.03")||
                read.Attachments.Select(a=>a.DeliveryOwner).Distinct().Count()!=2)
                throw new Exception("The typed physical reader lost exact native activation keys, final history or source-known future descendants.");
            var longSide=read.Attachments.Single(a=>a.OriginalQuantity.Amount.CompareTo(default)>0);
            if(currentQuantitySource)
            {
                var whole=await GasOwnedKnownHistoryStore.Read(connection,database,frame,snapshot,books,ct);
                var actual=await GasOwnedHoldings.Read(connection,database,frame,snapshot,books,ct);
                var markets=ImmutableDictionary.CreateBuilder<DefinitionRevision,GasOwnedProjectionMarket>();
                foreach(var market in actual.Markets.Values)
                {
                    var curves=ImmutableDictionary.CreateBuilder<ReferenceKey,ISideDiscounting>();
                    foreach(var selected in read.Attachments.Where(a=>a.Terms.Mark.Curve.Revision==market.Snapshot.Definitions&&a.Terms.Carrying==GasHoldingCarrying.CurrencyCurve).Select(a=>a.Terms.Currency).Distinct())
                    {
                        curves.Add(selected,new MappingOnlyDiscounting());
                    }
                    markets.Add(market.Snapshot.Definitions,new(market.Snapshot,curves.ToImmutable()));
                }
                var mapped=GasPhysicalQuantityMapping.Build(read.Attachments,whole,actual,ImmutableDictionary<AccountingPositionKey,KnownFuturesPhysicalFinal>.Empty,markets.ToImmutable());
                var future=read.Attachments.SelectMany(a=>a.Performances.Where(p=>!p.Retracted&&p.PerformedAt>at).Select(p=>(Parent:a,Performance:p))).ToArray();
                if(!mapped.OpeningHoldings.Select(p=>p.Position).ToHashSet().SetEquals(actual.Positions.Where(p=>p.Kind=="delivery_obligation").Select(p=>p.Position))||
                    !mapped.Transitions.Select(p=>p.Id).ToHashSet().SetEquals(future.Select(p=>LifecycleProjectionEvent.ScopedId(p.Parent.DeliveryOwner,p.Performance.Id)))||
                    future.Any(p=>mapped.OriginalPublications[LifecycleProjectionEvent.ScopedId(p.Parent.DeliveryOwner,p.Performance.Id)]!=p.Performance.OriginalPublicationOrder))
                    throw new Exception("The native mapping lost an active obligation, future physical performance, or its original event identity/publication.");
                _=mapped.RequireSource(whole,actual,markets.ToImmutable());
                var futureReceipt=read.Attachments.SelectMany(a=>a.Performances).First(p=>!p.Retracted&&p.PerformedAt>at&&p.ReceiptStockOwner is not null).ReceiptStockOwner!.Value;
                var altered=read.Attachments.Select(a=>a with{Stocks=[..a.Stocks.Select(s=>s.Owner==futureReceipt?s with{Lot=s.Lot with{AvailableAt=s.Lot.AvailableAt.AddHours(1)}}:s)]}).ToImmutableArray();
                try
                {
                    _=GasPhysicalQuantityMapping.Build(altered,whole,actual,ImmutableDictionary<AccountingPositionKey,KnownFuturesPhysicalFinal>.Empty,markets.ToImmutable());
                    throw new Exception("Physical quantity mapping changed native receipt availability relative to the complete whole-book journal.");
                }
                catch(MarketDataException error)when(error.Code=="gas_projection_source"){}
                try
                {
                    _=GasPhysicalQuantityMapping.Build(read.Attachments,whole with{Scope=GasOwnedKnownScope.PhysicalDependencies},actual,
                        ImmutableDictionary<AccountingPositionKey,KnownFuturesPhysicalFinal>.Empty,markets.ToImmutable());
                    throw new Exception("Physical quantity mapping treated a dependency closure as a whole-book quantity source.");
                }
                catch(MarketDataException error)when(error.Code=="gas_projection_source"){}
                Console.WriteLine("PASS native physical quantity mapping: exact active obligations, complete future title/supply schedule and original publication identity");
            }
            var currency=longSide.Terms.Currency;
            if(longSide.Performances.Length!=4||longSide.PrincipalRuns.Length!=6||longSide.Finals.Length!=4||longSide.Stocks.Length!=4||
                longSide.Cash.Select(c=>ExactMoney.FromMoney(new(c.Quantity.Amount,currency))).Aggregate((a,b)=>a.Add(b))!=ExactMoney.Parse("-6.03",currency)||
                longSide.Cash.Sum(c=>c.State.Allocations.Length)!=2)
                throw new Exception("The physical closure omitted complete principal runs, native stock or corrected payment allocations.");
            var narrowed=await GasPhysicalKnownAttachmentStore.Read(connection,database,frame,snapshot,books,ct,[new(longSide.FutureOwner.Owner,longSide.FutureOwner.BindingId)]);
            if(narrowed.Attachments.Length!=1||narrowed.Attachments.Single().DeliveryOwner!=longSide.DeliveryOwner||
                narrowed.Journal.Scope!=GasOwnedKnownScope.PhysicalDependencies||narrowed.Journal.Positions.ContainsKey(read.Attachments.Single(a=>a!=longSide).DeliveryOwner.Owner.Id))
                throw new Exception("An exact parent source widened to an unrelated physical obligation in the same account.");
            try
            {
                await GasOwnedProjection.Prepare(connection,database,narrowed.Journal,null,snapshot,at.AddDays(3),[],
                    ImmutableDictionary<ReferenceKey,ISideDiscounting>.Empty,ct);
                throw new Exception("A dependency-only physical source was treated as a complete whole-book gas portfolio.");
            }
            catch(MarketDataException error)when(error.Code=="gas_projection_scope"){}
            try
            {
                await GasPhysicalKnownAttachmentStore.Read(connection,database,frame,snapshot with{Id=Guid.NewGuid()},books,ct);
                throw new Exception("The physical source accepted a forged coherent snapshot identity.");
            }
            catch(MarketDataException error)when(error.Code=="gas_physical_known_source"){}
            var json=JsonSerializer.SerializeToElement(read.Attachments,LifecycleNative.Json).GetRawText();
            if(json.Contains("authorityUntil",StringComparison.Ordinal))throw new Exception("Physical source authority entered immutable projected facts.");
            return true;
        },default);
        Console.WriteLine("PASS typed physical source: exact owner keys, complete future descendants, exact-parent closure, payment revisions and forged-source refusal");
    }
    // This test qualifies mapping of native facts, without pricing the fixture's
    // historical rate curve at a later date. Financial native tests supply valid
    // captured curves separately; a mapper must never resolve a discount input.
    private sealed class MappingOnlyDiscounting:ISideDiscounting
    {
        public ImmutableArray<SidePaymentFactor> Resolve(ImmutableArray<SidePaymentKey> payments,MarketSnapshot source)=>
            throw new Exception("Physical quantity mapping attempted financial valuation.");
    }
}
