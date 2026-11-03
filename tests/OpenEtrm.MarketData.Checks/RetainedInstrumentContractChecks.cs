using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using OpenEtrm.MarketData.Crude;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;

// Published identity encodings are a contract independent of CLR inheritance.
internal static class RetainedInstrumentContractChecks
{
    internal static void Gas(RetainedGasInstrument instrument)
    {
        var members = new JsonArray();
        foreach (var m in instrument.Members) members.Add(new JsonObject
        {
            ["contract"] = "retained_gas_member_v1", ["Direction"] = m.Direction.ToString(), ["ProgramDigest"] = m.ProgramDigest,
            ["Ratio"] = new JsonArray("exact_ratio", m.Ratio.Numerator.ToString(), m.Ratio.Denominator.ToString())
        });
        var assignments = new JsonArray();
        foreach (var a in instrument.Assignments) assignments.Add(JsonNode.Parse(DefinitionIdentity.For(a).Canonical));
        Require(instrument, new JsonObject { ["contract"] = "retained_gas_instrument_v1", ["Assignments"] = assignments, ["Members"] = members },
            string.Join('/', instrument.Members.Select(m => m.ProgramDigest)));
    }
    internal static void Crude(RetainedCrudeInstrument instrument)
    {
        var members = new JsonArray();
        foreach (var m in instrument.Members) members.Add(new JsonObject
        {
            ["contract"] = "retained_crude_member_v1", ["Coefficient"] = m.Coefficient.ToString(), ["ProgramDigest"] = m.ProgramDigest
        });
        Require(instrument, new JsonObject { ["contract"] = "retained_crude_instrument_v1",
            ["Assignment"] = JsonNode.Parse(DefinitionIdentity.For(instrument.Assignment).Canonical), ["Members"] = members },
            string.Join('/', instrument.Members.Select(m => m.ProgramDigest)));
    }
    private static void Require(Instrument instrument, JsonObject expected, string programs)
    {
        var canonical = expected.ToJsonString();
        if (instrument.EconomicIdentity.Canonical != canonical || instrument.EconomicIdentity.Digest != Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))) ||
            instrument.RetainedScheduleIdentity != "retained_side_programs_v1/" + programs)
            throw new Exception("Retained instrument identity bytes, digest and ordered program identity must remain compatible.");
    }
}
