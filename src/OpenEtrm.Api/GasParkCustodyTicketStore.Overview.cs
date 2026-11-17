using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenEtrm.Api;

internal sealed partial class GasParkCustodyTicketStore
{
    sealed record OverviewValue(
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("value")] string Value,
        [property: JsonPropertyName("kind"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Kind = null);

    internal static object[] Overview(JsonElement agreement)
    {
        var terms = agreement.GetProperty("terms");
        var balances = agreement.GetProperty("balances");
        var cash = agreement.GetProperty("cash");
        var sections = new List<object>();
        OverviewValue Text(string label, string value) => new(label, value);
        OverviewValue Instant(string label, JsonElement row, string key) => new(label, row.Text(key), "instant");
        string Quantity(string amount) => amount + " " + terms.Text("quantity");
        void Section(string label, params OverviewValue[] values) => sections.Add(new { label, values });

        Section("Custody",
            Text("Committed quantity", Quantity(agreement.Text("quantity"))),
            Text("In custody", Quantity(balances.Text("inCustody"))),
            Text("Reserved customer stock", Quantity(balances.Text("reserved"))),
            Text("Overdue return", Quantity(balances.Text("overdue"))),
            Text("Observation reconciliation", agreement.GetProperty("reconciliationRequired").GetBoolean()
                ? "Required; accepted custody and cash have not been rewritten" : "Not required"));
        Section("Independent fee",
            Text("Absolute fee", terms.Text("fixedFeeAmount") + " " + terms.Text("currency")),
            Text("Direction", terms.Text("feeDirection")),
            Text("Cash owner", cash.ValueKind == JsonValueKind.Null ? "Not created" : cash.Text("bindingId")));

        var pinned = new Dictionary<Guid, string>();
        var acceptance = agreement.GetProperty("acceptance");
        if (acceptance.ValueKind != JsonValueKind.Null)
        {
            pinned.Add(acceptance.Id("agreementEvidenceId"), "Pinned by accepted custody");
            pinned.Add(acceptance.Id("receiptEvidenceId"), "Pinned by accepted custody");
            Section("Accepted custody",
                Text("Accepted quantity", Quantity(acceptance.Text("quantity"))),
                Instant("Accepted at", acceptance, "effectiveAt"),
                Instant("Known at", acceptance, "knownAt"),
                Text("Agreement observation", acceptance.Text("agreementSourceKey") + " revision " + acceptance.GetProperty("agreementRevision").GetInt32()),
                Text("Receipt observation", acceptance.Text("receiptSourceKey") + " revision " + acceptance.GetProperty("receiptRevision").GetInt32()));
        }
        foreach (var returned in agreement.GetProperty("returns").EnumerateArray())
        {
            pinned.Add(returned.Id("evidenceId"), "Pinned by accepted return");
            Section("Accepted return " + returned.Text("id"),
                Text("Returned quantity", Quantity(returned.Text("quantity"))),
                Instant("Accepted at", returned, "effectiveAt"),
                Instant("Known at", returned, "knownAt"),
                Text("Return observation", returned.Text("evidenceSourceKey") + " revision " + returned.GetProperty("evidenceRevision").GetInt32()));
        }

        var heads = agreement.GetProperty("evidenceHeads").EnumerateArray().Select(e => e.Id("id")).ToHashSet();
        foreach (var observation in agreement.GetProperty("evidence").EnumerateArray()
                     .OrderBy(e => e.Text("sourceKey"), StringComparer.Ordinal).ThenBy(e => e.GetProperty("revision").GetInt32()))
        {
            var id = observation.Id("id");
            var status = heads.Contains(id) ? "Current observation" : "Superseded observation";
            if (pinned.TryGetValue(id, out var applied)) status += "; " + applied;
            else status += "; not applied";
            Section("Retained observation " + observation.Text("sourceKey") + " revision " + observation.GetProperty("revision").GetInt32(),
                Text("Kind", observation.Text("kind")),
                Text("Status", status),
                Text("Observed quantity", Quantity(observation.Text("rawQuantity"))),
                Instant("Observed at", observation, "observedAt"),
                Instant("Known at", observation, "knownAt"),
                Text("Evidence", observation.Text("evidence")));
        }
        if (cash.ValueKind != JsonValueKind.Null)
            foreach (var payment in cash.GetProperty("payments").EnumerateArray()
                         .OrderBy(p => p.Id("allocation_id")).ThenBy(p => p.GetProperty("revision").GetInt32()))
                Section("Retained bank payment " + payment.Text("allocation_id") + " revision " + payment.GetProperty("revision").GetInt32(),
                    Text("Recorded amount", payment.Text("amount") + " " + cash.Text("currency")),
                    Instant("Paid at", payment, "paid_at"),
                    Instant("Known at", payment, "known_at"));
        return sections.ToArray();
    }
}
