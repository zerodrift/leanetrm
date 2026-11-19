using System.Text;
using System.Text.Json;
using OpenEtrm.Api;

internal static class ObservationPinPayloadChecks
{
    internal static async Task Run(string? path=null,int? expectedRows=null)
    {
        if(path is not null)
        {
            await using var source=File.OpenRead(path);
            var result=await ObservationPinPayload.Read(source,CancellationToken.None);
            if(result.Rows.Length!=expectedRows||result.Rows[0].GetProperty("value").GetString()!="-0.00000000000000000100"||result.Denied.Count!=1)
                throw new Exception("The complete synthetic batch and exact numeric text must survive streaming.");
            var fixture=PreviewDataChecks.Fixture(1);var first=result.Rows[0];
            var catalog=first.GetProperty("catalog_id").GetGuid();var revision=first.GetProperty("definition_revision_id").GetGuid();
            fixture.Definitions["revisions"]=[JsonSerializer.SerializeToElement(new{catalog_id=catalog,id=revision,reference_catalog_id=fixture.Manifest.ReferenceCatalogId,reference_revision_id=fixture.Manifest.ReferenceRevisionId,state="sealed"})];
            fixture.Definitions["series"]=[JsonSerializer.SerializeToElement(new{id=first.GetProperty("series_id").GetGuid(),contract_id=fixture.Definitions["contracts"][0].GetProperty("id").GetGuid(),quote_code="price",value_specification_code=(string?)null})];
            var metadata=new PreviewData(fixture.Manifest with{CatalogId=catalog,RevisionId=revision},fixture.Definitions,fixture.References,[]);
            var indexed=metadata.WithObservations(result.Rows);
            if(indexed.Observations.Length!=expectedRows||!indexed.HasObservation(first.GetProperty("id").GetGuid())||!indexed.HasObservation(result.Rows[^1].GetProperty("id").GetGuid()))
                throw new Exception("The complete batch must also retain its native observation indexes.");
            Console.WriteLine($"PIN_PAYLOAD rows={result.Rows.Length} indexedRows={indexed.Observations.Length} bytes={source.Length} heapLimit={GC.GetGCMemoryInfo().TotalAvailableMemoryBytes} runtime={Environment.Version}");
            return;
        }
        using var body=new MemoryStream(Encoding.UTF8.GetBytes("""{"rows":[{"ordinal":7,"value":-0.00000000000000000100,"label":"synthetic λ","empty":"","missing":null}],"denied":["00000000-0000-0000-0000-000000000001"]}"""));
        var payload=await ObservationPinPayload.Read(body,CancellationToken.None);
        var row=payload.Rows.Single();
        if(row.GetProperty("ordinal").GetInt32()!=7||row.GetProperty("value").GetString()!="-0.00000000000000000100"||row.GetProperty("label").GetString()!="synthetic λ"||row.GetProperty("empty").GetString()!=""||row.GetProperty("missing").ValueKind!=JsonValueKind.Null||!payload.Denied.Contains(Guid.Parse("00000000-0000-0000-0000-000000000001")))
            throw new Exception("Pin batches must retain row order, numeric lexemes, empty/null text and exact denied identities.");
        foreach(var value in new[]{"{}","{\"rows\":[]}","{\"denied\":[]}","{\"rows\":[],\"denied\":["})
        {
            using var malformed=new MemoryStream(Encoding.UTF8.GetBytes(value));
            try{await ObservationPinPayload.Read(malformed,CancellationToken.None);throw new Exception("Malformed or incomplete pin batches must not become empty successful results.");}
            catch(Exception error)when(error is JsonException or KeyNotFoundException){}
        }
        Console.WriteLine("PASS observation pin payload: complete rows/denials, exact numeric text and missing/truncated refusal");
    }
}
