using OpenEtrm.Api;
using OpenEtrm.MarketData;
using System.Text.Json;

internal static class SchedulePreviewChecks
{
    public static void Run()
    {
        var passed = 0;
        void Check(string name, Action check) { check(); passed++; Console.WriteLine($"PASS {name}"); }
        void Reject(string code, Action action)
        {
            try { action(); } catch (MarketDataException error) when (error.Code == code) { return; }
            throw new Exception($"Expected {code}.");
        }
        var revision = new DefinitionRevision(Guid.NewGuid(), Guid.NewGuid());
        var reference = new ReferenceRevision(Guid.NewGuid(), Guid.NewGuid());
        var at = new DateTimeOffset(2026,1,1,0,0,0,TimeSpan.Zero);
        var contract = new MarketContract(new(revision,Guid.NewGuid()),ContractKind.Future,new(2026,2,1),new(2026,3,1),at.AddDays(20),new(reference,"gas_feb"));
        var sequence = new ContractSequence(new(revision,"gas"),reference,at,at.AddDays(20),[new(1,contract,at,new(2026,1,21))]);
        var shortcut = new ScheduleShortcut("oe.ng1","Gas prompt",sequence,1,ScheduleDateRole.DeliveryStart);
        var library = new PreviewShortcutLibrary();
        Check("UTC parsing rejects local or unspecified time interpretations", () => {
            if(SchedulePreview.ParseAt("2026-01-01T00:00:00Z") != at) throw new Exception("UTC changed");
            Reject("sequence_context",()=>SchedulePreview.ParseAt("2026-01-01"));
            Reject("sequence_context",()=>SchedulePreview.ParseAt("2026-01-01T00:00:00"));
            Reject("sequence_context",()=>SchedulePreview.ParseAt("2026-01-01T00:00:00+01:00"));
        });
        Check("date-only milestone cannot silently become a midnight trading cutoff", () => {
            using var dateOnly = JsonDocument.Parse("{\"value_state\":\"known\",\"business_date\":\"2026-01-21\",\"event_at\":null}");
            Reject("sequence_definition",()=>SchedulePreview.RequiredMilestoneInstant(dateOnly.RootElement));
        });
        Check("keeping a shortcut is idempotent and cannot overwrite its terms", () => {
            if(!ReferenceEquals(library.Keep("viewer",shortcut),library.Keep("viewer",shortcut))) throw new Exception("Not idempotent");
            Reject("shortcut_conflict",()=>library.Keep("viewer",new("oe.ng1","Changed",sequence,1,ScheduleDateRole.DeliveryEnd)));
            if(library.Find("viewer","oe.ng1").DateRole!=ScheduleDateRole.DeliveryStart)throw new Exception("Saved terms changed");
        });
        Check("private names do not leak to another persona", () => {
            Reject("shortcut_missing",()=>library.Find("editor","oe.ng1"));
            if(library.For("editor").Any())throw new Exception("Wrong persona");
        });
        Check("separate symbols share their exact compiled sequence", () => {
            var other = library.Keep("viewer",new("oe.gas","Other name",sequence,1,ScheduleDateRole.DeliveryEnd));
            if(!ReferenceEquals(other.Sequence,shortcut.Sequence))throw new Exception("Sequence copied");
        });
        Check("observation contract preview resolves exact pins without implicit selection terms",()=>{
            var observation=new ObservationContractInput(0,new(2026,3,1),new(2026,3,2),at.AddDays(2));
            var request=new ObservationContractPreviewRequest("gas",new("prompt_offset",null,0,ContractRollRule.ExpiryImplementation,null),[observation]);
            if(ObservationContractPreview.Resolve(sequence,request).Single().Contract!=contract.Key)throw new Exception("Wrong contract");
            Reject("contract_selection",()=>ObservationContractPreview.Resolve(sequence,request with{Selection=request.Selection with{Roll=null}}));
            Reject("contract_selection",()=>ObservationContractPreview.Resolve(sequence,request with{Selection=request.Selection with{Contract=contract.Key}}));
            Reject("contract_selection",()=>ObservationContractPreview.Resolve(sequence,request with{Curve="other"}));
            var exact=request with{Selection=new("exact",contract.Key,null,null,null)};
            if(ObservationContractPreview.Resolve(sequence,exact).Single().Observation.From!=observation.From)throw new Exception("Observation dates changed");
        });
        Console.WriteLine($"{passed} schedule adapter checks passed.");
    }
}
