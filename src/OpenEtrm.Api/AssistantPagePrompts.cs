using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SavePagePromptRequest([property:JsonRequired]Guid OperationId,[property:JsonRequired]string Page,
    [property:JsonRequired]int ExpectedVersion,[property:JsonRequired]string Instructions);

static class AssistantPagePrompts
{
    public static async Task<object> Directory(NpgsqlConnection connection,CancellationToken ct)=>new
    {
        pages=PageReports.All.Select(page=>new{id=page,name=PageReports.Name(page)}),
        configuration=await ModelProviders.Read(connection,"SELECT assistant.page_prompt_directory()::text",[],ct)
    };
    public static Task<JsonElement> Save(NpgsqlConnection connection,SavePagePromptRequest body,CancellationToken ct)=>
        body.OperationId!=Guid.Empty&&body.Instructions is not null&&PageReports.All.Contains(body.Page)
            ?ModelProviders.Read(connection,"SELECT assistant.save_page_prompt($1,$2,$3,$4)::text",[body.OperationId,body.Page,body.ExpectedVersion,body.Instructions],ct)
            :throw new MarketDataException("page_prompt_input","Choose a page and supply the complete prompt revision.");
}
