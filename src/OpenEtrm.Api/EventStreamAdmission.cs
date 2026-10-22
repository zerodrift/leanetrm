namespace OpenEtrm.Api;

internal static class EventStreamAdmission
{
    internal static bool FromOwnPage(IHeaderDictionary headers)=>headers["Sec-Fetch-Site"] is [string site]&&site=="same-origin"
        &&headers["Sec-Fetch-Mode"] is [string mode]&&mode=="cors";
}
