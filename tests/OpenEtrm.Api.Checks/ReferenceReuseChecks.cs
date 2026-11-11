using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class ReferenceReuseChecks
{
    internal static async Task Run(string path)
    {
        var root=Environment.GetEnvironmentVariable("OPENETRM_PREVIEW_ROOT")??throw new Exception("Use the owned preview runner.");
        await using var database=new PreviewDatabase(root);
        using var document=JsonDocument.Parse(File.ReadAllText(path));var fixture=document.RootElement;
        var source=new CatalogueSource(fixture.Id("definitionRevisionId"),fixture.Id("referenceCatalogId"),fixture.Id("referenceRevisionId"));
        await database.WithIdentity("trader","ReadMarketData",async(connection,ct)=>
        {
            var candidate=await database.Read(connection,ct,selectedRevision:database.Manifest.RevisionId,observationIds:[]);
            var selected=await database.Read(connection,ct,ppaBundle:source,sharedReferences:candidate,observationIds:[]);
            if(selected.ReferenceRevision!=new ReferenceRevision(source.ReferenceCatalogId,source.ReferenceRevisionId)||
               !selected.References["interest_rate_definitions"].Any(r=>r.Text("code")=="demo_simple_rate")||
               candidate.References["interest_rate_definitions"].Any(r=>r.Text("code")=="demo_simple_rate"))
                throw new Exception("A different-reference source reused the candidate's rows.");
            try
            {
                await database.Read(connection,ct,ppaBundle:source with{ReferenceRevisionId=candidate.ReferenceRevision.RevisionId},sharedReferences:candidate,observationIds:[]);
                throw new Exception("A reference cache admitted a mismatched exact bundle.");
            }
            catch(MarketDataException error)when(error.Code=="reference_pin_mismatch") { }
            var before=await new KnowledgeStore(database.Manifest.CatalogId).Read(connection,fixture.Id("beforeSourceContextId"),ct);
            await MarketContextStore.Bind(connection,database.Manifest.CatalogId,before.Context,ct);
            var eligible=await database.Read(connection,ct,selectedRevision:database.Manifest.RevisionId,sharedReferences:candidate,observationIds:[]);
            if(eligible.ReferenceRevision!=candidate.ReferenceRevision)throw new Exception("The earlier context lost its still-authorized primary reference.");
            try
            {
                await database.Read(connection,ct,ppaBundle:source,sharedReferences:candidate,observationIds:[]);
                throw new Exception("A reference cache revived a source absent at the knowledge cutoff.");
            }
            catch(PreviewDeniedException) { }
            catch(MarketDataException error)when(error.Code=="ambiguous_reference") { }
            return true;
        },CancellationToken.None);
        Console.WriteLine("PASS reference reuse: own exact rows, mismatched bundle refusal and historical target denial with an authorized candidate");
    }
}
