using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;

namespace OpenEtrm.Api;

internal static class CurveMarkEditorEndpoints
{
    // Honor nullable annotations and strict object identities before mapping to a DTO.
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        NumberHandling = JsonNumberHandling.Strict
    };

    internal static void Map(RouteGroupBuilder api, PreviewDatabase database,
        Func<HttpContext, Func<PreviewSession, Task<IResult>>, Task<IResult>> locked, Func<PreviewSession, string> profile)
    {
        var routes = api.MapGroup("/curve-marks/editor");
        var store = new CurveMarkEditorStore(database);
        void Post<TRequest,TResponse>(string route, Func<NpgsqlConnection, PreviewSession, TRequest, CancellationToken, Task<TResponse>> run)
            where TRequest : class
        {
            Task<IResult> Handle(HttpContext http) => locked(http, async session =>
            {
                var request = await http.Request.ReadFromJsonAsync<TRequest>(Json, http.RequestAborted)
                    ?? throw new JsonException("Supply a curve mark editor request.");
                if (route == "/record") http.Items["curve_mark_record_attempted"] = true;
                var result = await database.WithIdentity(profile(session), null, (connection, ct) =>
                    run(connection, session, request, ct), http.RequestAborted);
                return Results.Ok(result);
            });
            routes.MapPost(route, (Func<HttpContext, Task<IResult>>)Handle);
        }
        Post<CurveMarkEditorSetupRequest, CurveMarkEditorSetupResponse>("/setup", store.Setup);
        Post<CurveMarkEditorValidateRequest, CurveMarkEditorValidateResponse>("/validate", store.Validate);
        Post<CurveMarkEditorHistoryRequest, CurveMarkEditorHistoryResponse>("/history", store.History);
        Post<CurveMarkEditorBatch, CurveMarkEditorReviewResponse>("/review", store.Review);
        Post<CurveMarkEditorBatch, CurveMarkEditorReceipt>("/record", store.Record);
        Post<CurveMarkEditorStatusRequest, CurveMarkEditorStatusResponse>("/status", (c, _, request, ct) => store.Status(c, request, ct));
    }
}
