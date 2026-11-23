# Delivery Quantity as a Valuation Input Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Status:** Implemented and verified locally under D445 on 23 November 2026. All eleven tasks are complete in one integrated local commit. Managed and trading groups, all gas preview workloads, frontend and repository checks pass. Independent review findings are resolved. Publication and deployment remain separate. The reviewed requirements govern the original task sketches below; implemented contracts are recorded in the linked design and verification documents.

**Goal:** A physical gas membership can follow its booked delivery schedule, so that value, accrued cash and risk use each gas day's scheduled and then allocated quantity.

**Architecture:** The booked schedule and its database admission stay as they are. The held valuation state (`PhysicalCashState`) gains each day's contract and selected quantity, and the shared in-term lifecycle evaluator (`PhysicalLifecycle`) scales each row by selected over contract. Both valuation routes (the composition store, and the retained instrument used by portfolio risk) already pass that state to that evaluator. A new saved term on the physical membership opts a trade in; a new selection policy picks each day's quantity; one new database read supplies the records.

**Tech Stack:** C# on .NET 10 (`src/OpenEtrm.MarketData`, `src/OpenEtrm.Api`), PostgreSQL migrations (`db/migrations`), TypeScript presentation (`web/src`), Python check tools (`tools/`).

**Spec:** [docs/superpowers/specs/2026-11-23-delivery-quantity-input-design.md](../specs/2026-11-23-delivery-quantity-input-design.md)

## Global Constraints

- Selection order for a membership that follows its schedule: allocated final, allocated provisional, scheduled, contract. Nominated and confirmed records never move value.
- The saved policy value is exactly `delivery_schedule_v1`. Its absence means contract quantity, for old and new trades alike. There is no second spelling of contract quantity in a stored request or row.
- A composition with any quantity link refuses the policy.
- Trades on contract quantity must produce byte-identical results to today. Every existing check stays green.
- No change to schedule compilation, schedule admission SQL, or any `[EconomicDefinition]` record.
- All arithmetic is exact (`ExactQuantity`, `ExactMoney`, `SignedRatio`). No rounding is introduced.
- Business rules stay in the managed backend. TypeScript presents backend results only.
- Synthetic data only. No customer, vendor or competing product names in code, fixtures, docs or commit messages.
- Write the failing check first in every task. Commit with `git commit -s`. Do not push.
- Database, HTTP and browser checks need a disposable PostgreSQL environment; run them through `tools/run_preview.py` and `tools/check_all.py` as the existing gas checks do.
- The two-location entry screen keeps contract quantity.

## Reviewed implementation requirements

These requirements amend the sketches and signatures in Tasks 1–8:

- Held state carries an explicit immutable followed-slot/source mapping from saved
  component terms, separate from selected rows. Validate every expected physical
  day and payment source; a missing whole slot is not contract-policy behavior.
  Carry evidence source, revision and reference, including equal-valued winners.
- Validate contract days against retained literal schedules at the held scale,
  payment sources against the composition, and source copies for exact agreement.
  Only the declared quantity-linear profile admits this scaling.
- Read manual and facility heads at the financial business instant and the same
  resolved knowledge cutoff as payments. Never rebind to the facility desk clock.
  Follow exact adopted allocation relationships and retain withdrawal heads.
- The native read first admits exact catalogue, visible trade/binding and current
  source authority. Worker admission uses ordinary retained risk with the active
  lease and exact job trade/book scope. Missing or inaccessible bindings refuse.
- A conflicting quantity refuses the complete risk request. No new exclusion
  policy is introduced. Distinct held states remain separate in unit-result reuse.
- Schedule presentation uses the same selected state and scaling as valuation,
  including the common `SideValuation.InspectSchedule` path. Preserve final versus
  provisional rank and complete evidence, including on ended days.
- Add behavioral checks for known-but-future-effective records, both source
  withdrawals, equal-winner evidence, omitted whole slots and cash mappings,
  ordinary queued-risk authority and revoked/expired scope, shared Side definitions
  with different trade quantities, and replay after a later correction.

## Review Focus

1. **A withdrawn allocation source.** It suppresses only that source stream. Another current allocation remains eligible; only after both allocation streams are absent or withdrawn can selection fall back to scheduled. Earlier revisions of a withdrawn stream never return. (Task 1)
2. **A provisional allocation recorded while the gas day is still in progress.** It outranks the scheduled quantity and the day stays unrealized. (Tasks 1, 3)
3. **A period-total membership whose daily contract quantity is a repeating fraction.** Scaling stays exact; nothing is rounded. (Task 3)
4. **A composition where one physical side follows its schedule and another stays on contract.** The contract side is valued exactly as today, including its equal-daily-quantity rule. (Task 3)
5. **A correction that lowers accrued cash below what was already paid.** The row reads overpaid. When accrued falls to zero under the new quantity policy, retain paid cash and show the full overpaid balance. Preserve legacy settlement admission and introduce no refund. (Task 3)

---

### Task 1: Valuation selection policy

**Files:**
- Modify: `src/OpenEtrm.MarketData/Gas/GasDailyQuantitySelection.cs`
- Modify: `src/OpenEtrm.MarketData/Trading/PhysicalLifecycle.cs:83` (label constants only)
- Test: `tests/OpenEtrm.MarketData.Checks/GasDailyQuantityChecks.cs`

**Interfaces:**
- Produces, in `PhysicalLifecycle`:
  `public const string SelectedQuantityPolicy="selected_delivery_quantity_v1";`
  `public const string AllocatedFinalQuantity="allocated_final_v1",AllocatedProvisionalQuantity="allocated_provisional_v1",ScheduledQuantity="scheduled_v1";`
- Produces, in `GasDailyQuantitySelection`:
  `public const string ValuationPolicy="delivery_schedule_valuation_v1";`
  `public static string ValuationSource(GasSelectedQuantity selected)` returning one of the three labels above or `PhysicalLifecycle.ContractQuantity`.
- Changes `GasSelectedQuantity` to `(ExactQuantity Quantity,ExactQuantity NormalizedQuantity,string Kind,Guid? RevisionId,string? Evidence,string? Status=null)`.
- `Select(...)` keeps its signature and accepts `ValuationPolicy`.

- [x] **Step 1: Write the failing checks** in `GasDailyQuantityChecks.Run()`, reusing its local `contract` (1,000) and `Observation(kind,status,amount)` helper. Add a second helper `Facility(status,amount)` that builds an `allocated` observation with source `facility_allocation_adoption_v1`.

```csharp
const string policy=GasDailyQuantitySelection.ValuationPolicy;var scale=new ExactRatio(1000,1);
GasSelectedQuantity Pick(params GasQuantityObservation[] heads)=>GasDailyQuantitySelection.Select(contract,[..heads],policy,end,scale);
// Rank: final allocation, provisional allocation, scheduled, contract.
var final=Pick(Observation("scheduled","provisional","985"),Observation("allocated","final","980"));
Equal(final.Quantity,"980");Same(GasDailyQuantitySelection.ValuationSource(final),PhysicalLifecycle.AllocatedFinalQuantity);
var provisional=Pick(Observation("scheduled","provisional","985"),Observation("allocated","provisional","970"));
Equal(provisional.Quantity,"970");Same(GasDailyQuantitySelection.ValuationSource(provisional),PhysicalLifecycle.AllocatedProvisionalQuantity);
var scheduled=Pick(Observation("nominated","provisional","990"),Observation("confirmed","provisional","988"),Observation("scheduled","provisional","985"));
Equal(scheduled.Quantity,"985");Same(GasDailyQuantitySelection.ValuationSource(scheduled),PhysicalLifecycle.ScheduledQuantity);
// A nomination or confirmation alone does not move value.
var none=Pick(Observation("nominated","provisional","990"),Observation("confirmed","provisional","988"));
Equal(none.Quantity,"1000");Same(GasDailyQuantitySelection.ValuationSource(none),PhysicalLifecycle.ContractQuantity);
if(none.RevisionId is not null)throw new Exception("A contract fallback names no record.");
// Review Focus 1: a withdrawn allocation falls back to scheduled.
Equal(Pick(Observation("scheduled","provisional","985"),Observation("allocated","withdrawn","0")).Quantity,"985");
// An explicit zero is a quantity.
Equal(Pick(Observation("allocated","final","0")).Quantity,"0");
// Both sources count as allocated; agreement selects, disagreement refuses.
Equal(Pick(Observation("allocated","final","980"),Facility("final","980")).Quantity,"980");
Reject(()=>Pick(Observation("allocated","final","980"),Facility("final","975")));
// The normalized quantity divides once by the scale.
Equal(final.NormalizedQuantity,"0.98");
```

`Same(string,string)` is a local string-equality helper; add it beside the file's `Equal`.

- [x] **Step 2: Run to verify failure**

Run: `dotnet run --project tests/OpenEtrm.MarketData.Checks -c Release -- --gas-daily-quantities`
Expected: build failure naming `ValuationPolicy`.

- [x] **Step 3: Implement.** Add the constants. In `Select`, admit `ValuationPolicy`; under it accept heads from either source, drop withdrawn heads, rank `allocated`/`final` 3, `allocated`/`provisional` 2, `scheduled` 1, ignore other kinds. Take the highest rank present; if its heads differ in quantity throw `gas_quantity_selection`; if they agree retain both evidence references in deterministic source order. Set `Status` and complete provenance on the result; preserve the existing single-record fields for existing consumers. The other three policies keep their behavior and their single-source rule.

- [x] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/OpenEtrm.MarketData.Checks -c Release -- --gas-daily-quantities`
Expected: `PASS daily gas quantities: ...`

- [x] **Step 5: Commit (consolidated in the integrated local commit)**

```bash
git add src/OpenEtrm.MarketData/Gas/GasDailyQuantitySelection.cs src/OpenEtrm.MarketData/Trading/PhysicalLifecycle.cs tests/OpenEtrm.MarketData.Checks/GasDailyQuantityChecks.cs
git commit -s -m "Select a gas day's valuation quantity by allocation, then schedule, then contract"
```

---

### Task 2: Day quantities on the held state

**Files:**
- Modify: `src/OpenEtrm.MarketData/Trading/PhysicalLifecycle.cs:9-44`
- Create: `tests/OpenEtrm.MarketData.Checks/DeliveryQuantityChecks.cs`
- Modify: `tests/OpenEtrm.MarketData.Checks/Program.cs` (flag near `:81`, default suite near `:630`)

**Interfaces:**
- Consumes: the label constants of Task 1.
- Produces:
  `public sealed record HeldDayQuantity(int Slot,DateOnly Day,ExactQuantity Contract,ExactQuantity Selected,string Source,Guid? RevisionId);`
  `PhysicalCashState(Guid tradeId,Guid bindingId,Quantity quantity,Guid? bookId,ImmutableArray<CashOccurrencePayment> payments,string policy,ImmutableArray<HeldDayQuantity> quantities=default,string? quantityPolicy=null)`
  `public ImmutableArray<HeldDayQuantity> Quantities { get; }` (empty, never default)
  `public string? QuantityPolicy { get; }`
  `internal bool Follows(int slot)`
  `internal HeldDayQuantity Day(int slot,DateOnly day)` (throws `physical_lifecycle_quantity` when the day is absent)
- A cash member's entries are its source physical member's days, stored under the cash member's slot.

- [x] **Step 1: Write the failing checks.** Create `internal static class DeliveryQuantityChecks` with `internal static void Run()` calling `HeldState()`, and register `--delivery-quantities` in `Program.cs` plus `DeliveryQuantityChecks.Run();passed++;` in the default suite. Use a unit `ReferenceKey` from `new CommodityLookupFixture(31).Quote.Quantity`.

```csharp
static void HeldState()
{
    var unit=new CommodityLookupFixture(31).Quote.Quantity;var day=new DateOnly(2026,11,1);
    ExactQuantity Q(string amount)=>ExactQuantity.FromQuantity(new(ExactDecimal.Parse(amount),unit));
    HeldDayQuantity Held(int slot,int n,string selected,string source,Guid? revision)=>new(slot,day.AddDays(n),Q("1000"),Q(selected),source,revision);
    PhysicalCashState State(ImmutableArray<HeldDayQuantity> days,string? policy)=>
        new(Guid.NewGuid(),Guid.NewGuid(),new(ExactDecimal.Parse("1000"),unit),null,[],PhysicalCashState.Policy,days,policy);
    var followed=State([Held(0,0,"980",PhysicalLifecycle.AllocatedFinalQuantity,Guid.NewGuid()),Held(0,1,"1000",PhysicalLifecycle.ContractQuantity,null)],PhysicalLifecycle.SelectedQuantityPolicy);
    Require(followed.Follows(0)&&!followed.Follows(1)&&followed.Day(0,day).Selected==Q("980"),"A followed slot reads its own days.");
    Reject("physical_lifecycle_quantity",()=>followed.Day(0,day.AddDays(2)));
    var plain=State(default,null);Require(plain.Quantities.IsEmpty&&plain.QuantityPolicy is null&&!plain.Follows(0),"A state without the policy follows nothing.");
    // Days without the policy, the policy without days, an unknown policy or label, a duplicate day,
    // a nonpositive contract, a negative selection, a record id on a contract fallback and a missing id on a record all refuse.
    Reject("physical_lifecycle_quantity",()=>State([Held(0,0,"980",PhysicalLifecycle.ScheduledQuantity,Guid.NewGuid())],null));
    Reject("physical_lifecycle_quantity",()=>State([],PhysicalLifecycle.SelectedQuantityPolicy));
    Reject("physical_lifecycle_quantity",()=>State([Held(0,0,"980","unknown_v1",Guid.NewGuid())],PhysicalLifecycle.SelectedQuantityPolicy));
    Reject("physical_lifecycle_quantity",()=>State([Held(0,0,"980",PhysicalLifecycle.ScheduledQuantity,Guid.NewGuid()),Held(0,0,"970",PhysicalLifecycle.ScheduledQuantity,Guid.NewGuid())],PhysicalLifecycle.SelectedQuantityPolicy));
    Reject("physical_lifecycle_quantity",()=>State([new(0,day,Q("0"),Q("0"),PhysicalLifecycle.ContractQuantity,null)],PhysicalLifecycle.SelectedQuantityPolicy));
    Reject("physical_lifecycle_quantity",()=>State([Held(0,0,"-1",PhysicalLifecycle.ScheduledQuantity,Guid.NewGuid())],PhysicalLifecycle.SelectedQuantityPolicy));
    Reject("physical_lifecycle_quantity",()=>State([Held(0,0,"1000",PhysicalLifecycle.ContractQuantity,Guid.NewGuid())],PhysicalLifecycle.SelectedQuantityPolicy));
    Reject("physical_lifecycle_quantity",()=>State([Held(0,0,"980",PhysicalLifecycle.ScheduledQuantity,null)],PhysicalLifecycle.SelectedQuantityPolicy));
    Console.WriteLine("PASS delivery quantities: held state carries complete labelled day quantities");
}
```

`Require` and `Reject(string code,Action run)` follow the private helpers in `PhysicalLifecycleChecks.cs:574-575`.

- [x] **Step 2: Run to verify failure**

Run: `dotnet run --project tests/OpenEtrm.MarketData.Checks -c Release -- --delivery-quantities`
Expected: build failure naming `HeldDayQuantity`.

- [x] **Step 3: Implement** the record, the two optional constructor parameters with the validation the checks pin (quantity specification of every entry equals the state's `Quantity.Specification`), and the two accessors backed by a dictionary keyed by `(Slot,Day)`.

- [x] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/OpenEtrm.MarketData.Checks -c Release -- --delivery-quantities` and `-- --physical-lifecycle`
Expected: both print their `PASS` lines.

- [x] **Step 5: Commit (consolidated in the integrated local commit)**

```bash
git add src/OpenEtrm.MarketData/Trading/PhysicalLifecycle.cs tests/OpenEtrm.MarketData.Checks/DeliveryQuantityChecks.cs tests/OpenEtrm.MarketData.Checks/Program.cs
git commit -s -m "Carry each followed gas day's contract and selected quantity in the held state"
```

---

### Task 3: The lifecycle evaluator applies the selected quantity

**Files:**
- Modify: `src/OpenEtrm.MarketData/Trading/PhysicalLifecycle.cs:171-248` (`EvaluateConnected`)
- Test: `tests/OpenEtrm.MarketData.Checks/DeliveryQuantityChecks.cs`

**Interfaces:**
- Consumes: `PhysicalCashState.Follows`, `PhysicalCashState.Day`, the labels.
- Produces: no new signature. `PhysicalLifecycle.Evaluate` and `EvaluateSide` return rows scaled for followed slots, with `QuantityBasis` set to the day's label on physical rows and to `SelectedQuantityPolicy` on cash rows.

- [x] **Step 1: Write the failing checks.** Add `WorkedExample()` to `DeliveryQuantityChecks` and call it from `Run()`. Build the fixture the way `PaymentDatePolicyChecks.cs:84-112` does: a `PhysicalGasSide` over four gas days (`from` to `from.AddDays(4)`, `GasQuantityBasis.PerOperatingDay`, the first four daily assignments), a fixed cash side built as `PhysicalGasSideChecks.Cash` does (`PhysicalGasSideChecks.cs:71`) at price 3 with an allocation of 4, both compiled with `ScheduleLifecycleSelection.StartedPhysicalDelivery`, executed through `ScheduleProgramExecutionChecks.Sources` with a supplied market price and a discount of 1. Members are `new LifecycleMember(program,result,n==0,value=>value.Scale(ExactDecimal.Parse(n==0?"1000":"-1000")))`. The valuation instant is one hour after the start of day 3. `Evaluate(state,price)` returns `PhysicalLifecycle.Evaluate(members,state,snapshot,currency)`.

```csharp
// Spec worked example: 1,000 per day for four days at 3.00, mark 3.20, valued in day 3.
var contract=Evaluate(State(default,null),"3.2");
Equal(contract.Unrealized,"400");Equal(contract.RealizedUnsettled,"-6000");Equal(contract.Total,"-5600");
var days=Followed(("980",PhysicalLifecycle.AllocatedFinalQuantity),("1050",PhysicalLifecycle.ScheduledQuantity),("900",PhysicalLifecycle.ScheduledQuantity),("1000",PhysicalLifecycle.ContractQuantity));
var followed=Evaluate(State(days,PhysicalLifecycle.SelectedQuantityPolicy),"3.2");
Equal(followed.Unrealized,"380");Equal(followed.RealizedUnsettled,"-6090");Equal(followed.Total,"-5710");
var physical=followed.Rows.Where(r=>r.Slot==0).OrderBy(r=>r.DeliveryFrom).ToArray();
Equal(physical[0].Unrealized,"0");Equal(physical[1].Unrealized,"0");Equal(physical[2].Unrealized,"2880");Equal(physical[3].Unrealized,"3200");
Require(physical.Select(r=>r.QuantityBasis).SequenceEqual([PhysicalLifecycle.AllocatedFinalQuantity,PhysicalLifecycle.ScheduledQuantity,PhysicalLifecycle.ScheduledQuantity,PhysicalLifecycle.ContractQuantity]),"Each day names the quantity it used.");
var cash=followed.Rows.Single(r=>r.Slot==1);
Equal(cash.Accrued,"-6090");Equal(cash.Unrealized,"-5700");Equal(cash.Expected!,"-11790");Require(cash.QuantityBasis==PhysicalLifecycle.SelectedQuantityPolicy,"A payment row is labelled as following the selected quantity.");
// A mark shock moves remaining delivery only.
var shocked=Evaluate(State(days,PhysicalLifecycle.SelectedQuantityPolicy),"3.3");
Equal(shocked.Unrealized,"570");Equal(shocked.RealizedUnsettled,"-6090");
// A later final allocation of 1,020 for day 2.
var corrected=Evaluate(State(Followed(("980",PhysicalLifecycle.AllocatedFinalQuantity),("1020",PhysicalLifecycle.AllocatedFinalQuantity),("900",PhysicalLifecycle.ScheduledQuantity),("1000",PhysicalLifecycle.ContractQuantity)),PhysicalLifecycle.SelectedQuantityPolicy),"3.2");
Equal(corrected.RealizedUnsettled,"-6000");
// Review Focus 2: a provisional allocation of 950 on in-progress day 3 stays unrealized.
var inProgress=Evaluate(State(Followed(("980",PhysicalLifecycle.AllocatedFinalQuantity),("1050",PhysicalLifecycle.ScheduledQuantity),("950",PhysicalLifecycle.AllocatedProvisionalQuantity),("1000",PhysicalLifecycle.ContractQuantity)),PhysicalLifecycle.SelectedQuantityPolicy),"3.2");
Equal(inProgress.Unrealized,"390");Equal(inProgress.RealizedUnsettled,"-6090");
// A followed slot must cover every day its rows deliver.
Reject("physical_lifecycle_quantity",()=>Evaluate(State([..days.Where(d=>d.Day!=from.AddDays(3))],PhysicalLifecycle.SelectedQuantityPolicy),"3.2"));
```

`Followed(...)` builds four `HeldDayQuantity` for slot 0 and the same four for slot 1 (the cash member carries its source's days), each with contract 1,000 and a new revision id unless the label is `ContractQuantity`.

Add three further cases in the same method:

```csharp
// Review Focus 3: a period total of 1,000 over three days (contract 1000/3 per day), day 1 selected 300, valued before delivery.
// Physical value 3.2*(300+1000/3+1000/3)=9280/3; cash -3*(2900/3)=-2900. Nothing is rounded.
Equal(periodTotal.Unrealized,"580/3");
// Review Focus 4: a second physical side on contract quantity beside the followed one keeps today's rows exactly.
Require(mixed.Rows.Where(r=>r.Slot is 2 or 3).SequenceEqual(contractOnly.Rows.Where(r=>r.Slot is 2 or 3)),"A side on contract quantity is unchanged.");
// Review Focus 5: day 1 paid in full at 980 (-2,940), then corrected to 900: accrued for days 1 and 2 is -5,850 against -2,940 paid; correcting both days to 0 retains a 2,940 overpaid balance.
Require(lowered.Rows.Single(r=>r.Slot==1).Settlement==SettlementState.PartPaid,"Accrued above paid stays part paid.");
Require(overpaid.Rows.Single(r=>r.Slot==1).Settlement==SettlementState.Overpaid,"Accrued below paid reads overpaid.");
var zeroBalance=EvaluatePaid(zeroed).Rows.Single(r=>r.Slot==1);
Require(zeroBalance.Settlement==SettlementState.Overpaid,"Zero accrued cash preserves the overpayment.");
Equal(zeroBalance.Accrued,"0");Equal(zeroBalance.Paid,"-2940");Equal(zeroBalance.Outstanding,"2940");
```

For the period-total case build a three-day side with `GasQuantityBasis.PeriodTotalEqualOperatingDays`, held scale 1,000, an instant before day 1, and `Equal` on the exact fraction text the file's helper prints. For the paid cases record one `CashOccurrencePayment` of −2,940 against the cash row's occurrence, paid and known before the valuation instant; `overpaid` selects 900 on day 1 and 0 on day 2 (accrued −2,700).

- [x] **Step 2: Run to verify failure**

Run: `dotnet run --project tests/OpenEtrm.MarketData.Checks -c Release -- --delivery-quantities`
Expected: FAIL at `Equal(followed.Unrealized,"380")`, reporting 400.

- [x] **Step 3: Implement in `EvaluateConnected`.**
  - Physical row of a followed slot: multiply the held present value by `Selected/Contract` of that operating day; label it with the day's `Source`, including `ContractQuantity` for an explicit contract fallback after delivery.
  - Cash row of a followed slot: read every covered day (a missing day throws through `Day`). With `conAll`, `selAll` and `selEnded` the sums of contract, selected and ended-day selected quantities: retain the original contract amount A0 and PV V0; total amount is `A0*selAll/conAll`, accrued `A0*selEnded/conAll`, unrealized `V0*(selAll-selEnded)/conAll`, and expected `A0*selAll/conAll - paid`. Never scale the amount twice or scale recorded payments. Skip the equal-daily-quantity check for this row. Label it `SelectedQuantityPolicy`. Apply the same `selAll/conAll` scaling in the projected-price branch.
  - Rows of slots that do not follow are computed by the existing code, untouched.
  - Build each ratio from the quantities' numerators and denominators as a `SignedRatio`.

- [x] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/OpenEtrm.MarketData.Checks -c Release -- --delivery-quantities`, then `-- --physical-lifecycle` and `-- --payment-date-policy`
Expected: all three print `PASS`.

- [x] **Step 5: Commit (consolidated in the integrated local commit)**

```bash
git add src/OpenEtrm.MarketData/Trading/PhysicalLifecycle.cs tests/OpenEtrm.MarketData.Checks/DeliveryQuantityChecks.cs
git commit -s -m "Value and accrue a followed gas day on its selected quantity"
```

---

### Task 4: The retained instrument admits and names the policy

**Files:**
- Modify: `src/OpenEtrm.MarketData/Gas/RetainedGasInstrument.cs:61-66,104-110`
- Test: `tests/OpenEtrm.MarketData.Checks/DeliveryQuantityChecks.cs`

**Interfaces:**
- Consumes: `PhysicalCashState.Quantities`, `QuantityPolicy`.
- Produces: `RetainedGasInstrument.Declare` returns a model version that contains `PhysicalLifecycle.SelectedQuantityPolicy + "/"` directly after the `PhysicalCashState.Policy + "/"` prefix when the state names the policy. `ValidateValuationState` additionally refuses, with `physical_lifecycle_quantity`, a state whose entries name a slot outside the members, a physical slot's day the retained program does not deliver, or a physical contract quantity different from the program's literal day quantity times the held quantity times the member ratio.

- [x] **Step 1: Write the failing checks.** Add `RetainedRoute()` to `DeliveryQuantityChecks`. Build a `RetainedGasInstrument` from the Task 3 programs as `PhysicalLifecycleChecks.Demo` does (`PhysicalLifecycleChecks.cs:269-273`), with one physical member at direction 1 and one cash member at direction −1, held quantity 1,000, and value it through `instrument.EvaluateLifecycle(...)` as `Demo.Lifecycle` does (`:378`).

```csharp
var value=Lifecycle(State(days,PhysicalLifecycle.SelectedQuantityPolicy));
Equal(value.Unrealized,"380");Equal(value.RealizedUnsettled,"-6090");Equal(value.Total,"-5710");
Require(ModelVersion(State(days,PhysicalLifecycle.SelectedQuantityPolicy)).StartsWith(PhysicalCashState.Policy+"/"+PhysicalLifecycle.SelectedQuantityPolicy+"/"),"A retained valuation names its quantity policy.");
Require(!ModelVersion(State(default,null)).Contains(PhysicalLifecycle.SelectedQuantityPolicy),"A contract-quantity valuation keeps its model version.");
Reject("physical_lifecycle_quantity",()=>instrument.ValidateValuationState(State([..days.Select(d=>d with{Slot=d.Slot+5})],PhysicalLifecycle.SelectedQuantityPolicy)));
Reject("physical_lifecycle_quantity",()=>instrument.ValidateValuationState(State([..days.Select(d=>d with{Contract=Q("999")})],PhysicalLifecycle.SelectedQuantityPolicy)));
Reject("physical_lifecycle_quantity",()=>instrument.ValidateValuationState(State([..days.Select(d=>d with{Day=d.Day.AddDays(40)})],PhysicalLifecycle.SelectedQuantityPolicy)));
```

`ModelVersion(state)` reads `instrument.Declare(context).ModelVersion` the way `PhysicalLifecycleChecks.Demo.ModelVersion` does (`:423`).

- [x] **Step 2: Run to verify failure**

Run: `dotnet run --project tests/OpenEtrm.MarketData.Checks -c Release -- --delivery-quantities`
Expected: FAIL at the model-version requirement.

- [x] **Step 3: Implement** the model-version segment and the three validations. Read literal day quantities with `PhysicalDeliverySchedule.ReadLiteralQuantities(program,ExactRatio.One)`.

- [x] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/OpenEtrm.MarketData.Checks -c Release -- --delivery-quantities` and `-- --physical-lifecycle`
Expected: both print `PASS`.

- [x] **Step 5: Commit (consolidated in the integrated local commit)**

```bash
git add src/OpenEtrm.MarketData/Gas/RetainedGasInstrument.cs tests/OpenEtrm.MarketData.Checks/DeliveryQuantityChecks.cs
git commit -s -m "Admit and name the selected quantity policy on retained gas instruments"
```

---

### Task 5: Build the held quantities from contract days and records

**Files:**
- Create: `src/OpenEtrm.MarketData/Gas/GasHeldQuantities.cs`
- Test: `tests/OpenEtrm.MarketData.Checks/DeliveryQuantityChecks.cs`

**Interfaces:**
- Consumes: `GasDailyQuantitySelection.Select` with `ValuationPolicy`, `ValuationSource`, `GasDailyContract`, `GasQuantityObservation`, `HeldDayQuantity`.
- Produces:
  `public sealed record GasFollowedMember(int Slot,ImmutableArray<GasDailyContract> Days,ExactRatio Scale,ImmutableArray<int> PaymentSlots);`
  `public sealed record GasQuantityHead(int Slot,DateOnly Day,GasQuantityObservation Observation);`
  `public static ImmutableArray<HeldDayQuantity> GasHeldQuantities.Build(ImmutableArray<GasFollowedMember> members,ImmutableArray<GasQuantityHead> heads,DateTimeOffset at)`
- `Days` are the member's absolute contract days from `GasDailyQuantitySelection.ReadContract(program,scale)`. `PaymentSlots` are the cash and fee members whose source is this member.

- [x] **Step 1: Write the failing checks.** Add `Build()` to `DeliveryQuantityChecks`.

```csharp
var built=GasHeldQuantities.Build([new(0,contractDays,scale,[1,2])],
    [new(0,d1,Observation("allocated","final","980")),new(0,d2,Observation("scheduled","provisional","1050")),new(0,d3,Observation("scheduled","provisional","900")),new(0,d3,Observation("nominated","provisional","880"))],at);
Require(built.Length==12&&built.Count(q=>q.Slot==0)==4&&built.Count(q=>q.Slot==1)==4&&built.Count(q=>q.Slot==2)==4,"The physical member and both payment members each carry all four days.");
Require(built.Where(q=>q.Slot==2).Select(q=>q.Selected.ToExactString()).SequenceEqual(["980","1050","900","1000"]),"A fee member follows its source's selected days.");
Require(built.Single(q=>q.Slot==0&&q.Day==d4) is {RevisionId:null} last&&last.Source==PhysicalLifecycle.ContractQuantity,"A day with no record is present as contract quantity.");
Require(GasHeldQuantities.Build([],[],at).IsEmpty,"No followed member yields no entries.");
// A record for a day the member does not deliver, or for a slot that does not follow, refuses.
Reject("gas_quantity_selection",()=>GasHeldQuantities.Build([new(0,contractDays,scale,[1])],[new(0,d4.AddDays(1),Observation("scheduled","provisional","900"))],at));
Reject("gas_quantity_selection",()=>GasHeldQuantities.Build([new(0,contractDays,scale,[1])],[new(3,d1,Observation("scheduled","provisional","900"))],at));
```

- [x] **Step 2: Run to verify failure**

Run: `dotnet run --project tests/OpenEtrm.MarketData.Checks -c Release -- --delivery-quantities`
Expected: build failure naming `GasHeldQuantities`.

- [x] **Step 3: Implement `Build`.** For each member and day, select under `ValuationPolicy` from that slot and day's heads and emit one entry for the member and one per payment slot.

- [x] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/OpenEtrm.MarketData.Checks -c Release -- --delivery-quantities`
Expected: `PASS`.

- [x] **Step 5: Commit (consolidated in the integrated local commit)**

```bash
git add src/OpenEtrm.MarketData/Gas/GasHeldQuantities.cs tests/OpenEtrm.MarketData.Checks/DeliveryQuantityChecks.cs
git commit -s -m "Build held day quantities from contract days and recorded heads"
```

---

### Task 6: The schedule view's amounts follow the selected quantity

**Files:**
- Modify: `src/OpenEtrm.MarketData/Trading/ScheduleView.cs:21-26,90-150`
- Test: `tests/OpenEtrm.MarketData.Checks/DeliveryQuantityChecks.cs`

**Interfaces:**
- Produces:
  `public sealed record ScheduleFollowedDay(SignedRatio Contract,SignedRatio Selected,string? Source);`
  `SideViewInput` gains a trailing `ImmutableDictionary<DateOnly,ScheduleFollowedDay>? FollowedDays=null`.
- `Contract` and `Selected` are signed and scaled like the side's other shown quantities. `Source` is `"allocated"`, `"scheduled"` or null for contract; it must be in `ScheduleViews.RecordedTags` when present.
- A side supplies `FollowedDays` or `Recorded`, never both.

- [x] **Step 1: Write the failing checks.** Add `View()` to `DeliveryQuantityChecks`, valuing the Task 3 programs one hour before day 1 and building views as `PhysicalLifecycleChecks.Demo.Views` does (`:444`). Day 3 is scheduled at 900; the other days have no record.

```csharp
var view=ScheduleViews.Build([physicalInput with{FollowedDays=physicalDays},cashInput with{FollowedDays=cashDays}],levels,boundary);
Equal(view.Totals["delivery.quantity"].Value,"3900");
Equal(view.Totals["delivery.amount"].Value,"12480");   // 3.2 * 3,900
Equal(view.Totals["payment.amount"].Value,"-11700");   // 3 * 3,900
Require(DayCell(view,"delivery.quantity",day3).Sources.SequenceEqual(["scheduled"])&&DayCell(view,"delivery.quantity",day4).Sources.SequenceEqual(["contract"]),"Each day shows its source.");
Equal(DayCell(view,"delivery.amount",day3).Value,"2880");
Reject("schedule_view",()=>ScheduleViews.Build([physicalInput with{FollowedDays=physicalDays,Recorded=recorded}],levels,boundary));
Reject("schedule_view",()=>ScheduleViews.Build([physicalInput with{FollowedDays=physicalDays.Remove(day4)}],levels,boundary));
```

- [x] **Step 2: Run to verify failure**

Run: `dotnet run --project tests/OpenEtrm.MarketData.Checks -c Release -- --delivery-quantities`
Expected: build failure naming `FollowedDays`.

- [x] **Step 3: Implement in `Pieces`.** For a side with `FollowedDays`, every shown cell reads the days it covers (all must be present): shown quantity is the sum of `Selected`, amount is the cell's amount times the sum of `Selected` over the sum of `Contract`, and a one-day cell takes its tag from `Source` (the contract tag when null). The ended-day branch shows the day's `Selected` with its tag and the delivered tag. Sides without `FollowedDays` are untouched.

- [x] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/OpenEtrm.MarketData.Checks -c Release -- --delivery-quantities` and `-- --physical-lifecycle`
Expected: both print `PASS`.

- [x] **Step 5: Commit (consolidated in the integrated local commit)**

```bash
git add src/OpenEtrm.MarketData/Trading/ScheduleView.cs tests/OpenEtrm.MarketData.Checks/DeliveryQuantityChecks.cs
git commit -s -m "Show a followed side's schedule quantities and amounts on the selected quantity"
```

---

### Task 7: Database: the saved policy and the valuation read

**Files:**
- Create: `db/migrations/0390_gas_delivery_quantity_valuation.sql`
- Modify: `db/README.md` (the "Recent schema groups" list)
- Create: `tools/check_gas_delivery_quantity.py`
- Modify: `tools/run_preview.py` (flag beside `--gas-daily-quantity-check` at `:47`, implication at `:195-199`, dispatch beside `:1132`)
- Modify: `tools/check_all.py:286` (add the flag to the gas suite)

**Interfaces:**
- Produces: column `trading.gas_component_terms.quantity_policy text`, null or `'delivery_schedule_v1'`, allowed only where `role='physical'` and `source_slot IS NULL`.
- Produces: `security.write_gas_composition(uuid,jsonb,timestamptz,boolean)` accepts the optional physical key `quantityPolicy`, stores it, and raises `gas_quantity_policy` for any other value or when any physical side of the body carries a `link`.
- Produces: `security.read_gas_valuation_quantities(catalog uuid,trade uuid,instrument uuid) RETURNS jsonb`, granted to `openetrm_runtime` and `openetrm_risk_worker`. It returns a JSON array of `{id,slot,day,kind,status,source,quantity,evidence}`: for each physical slot of that instrument, the latest visible revision per series and day of kinds `scheduled` and `allocated` from the manual series (status included, so a withdrawn head is returned as withdrawn), and the latest adopted facility target per obligation and day as `read_trade_facility_final_quantities` returns it. `quantity` is text.
- Produces: `tools/check_gas_delivery_quantity.py` with `run_checks(url, query, manifest, run_worker)`.

- [x] **Step 1: Write the failing checks** in `tools/check_gas_delivery_quantity.py`, following the structure of `tools/check_gas_daily_quantities.py` and booking through the same composition body that `tools/check_gas_composition.py` uses. Assert:
  - a one-side purchase booked with `"quantityPolicy":"delivery_schedule_v1"` stores that value in `gas_component_terms.quantity_policy` for the physical slot and null for the cash slot; replaying the command returns its first outcome;
  - the same body without the key stores null;
  - a body with a linked second physical side and the key is refused with `gas_quantity_policy`; so is the value `"other_v1"`;
  - `read_gas_valuation_quantities` returns `[]` before any record; after recording a scheduled 900 and a final allocation of 980 on two ended days it returns exactly those two heads with their slot and day; a withdrawal is returned with status `withdrawn`; a nominated record is not returned;
  - a context captured before the allocation does not return it;
  - a runtime identity without `ReadTrades` is refused with SQLSTATE `42501`;
  - the risk worker, inside its lease (`run_worker`), reads the same heads, and outside it is refused.

- [x] **Step 2: Run to verify failure**

Run: `python3 tools/run_preview.py --pg-bin "$PG_BIN" --dotnet "$DOTNET" --check --gas-delivery-quantity-check`
Expected: FAIL, unknown key `quantityPolicy`.

- [x] **Step 3: Write the migration.**
  - Follow the house upgrade pattern: one transaction, `SET LOCAL search_path=pg_catalog`, the advisory lock, and a predecessor contract pin (signature, definition hash, owner, ACL, config) for every function patched, as `db/migrations/0326_gas_calendar_payment_terms.sql:8-39` does.
  - Add the column with its check.
  - Patch `security.write_gas_composition` by text replacement, asserting each replaced text occurs exactly once: extend the physical key list, add the value and link checks, and write the column.
  - Create `security.read_gas_valuation_quantities` as `SECURITY DEFINER`, owned by `openetrm_security_owner`, visibility through `security.trading_visible`, runtime authority through `security.trading_access(catalog,'ReadTrades')` and `security.market_access(catalog,'ReadMarketData')`, worker authority through `risk.worker_read()` with exact active job/context/trade/book scope. Recheck current source authority and the active lease before disclosure; a valid ordinary composed-gas risk job must not require unrelated owned-gas permissions. Model the dual authority on `security.read_gas_owned_holdings` (`db/migrations/0278_economic_lifecycle_risk_jobs.sql:882`).
  - Add the schema group line to `db/README.md`.

- [x] **Step 4: Run to verify it passes**

Run: `python3 tools/run_preview.py --pg-bin "$PG_BIN" --dotnet "$DOTNET" --check --gas-delivery-quantity-check`, then `--gas-composition-check`, `--gas-in-term-check` and `--gas-daily-quantity-check`
Expected: each run ends with its checks passed.

- [x] **Step 5: Commit (consolidated in the integrated local commit)**

```bash
git add db/migrations/0390_gas_delivery_quantity_valuation.sql db/README.md tools/check_gas_delivery_quantity.py tools/run_preview.py tools/check_all.py
git commit -s -m "Save a gas membership's quantity policy and read its valuation quantities"
```

---

### Task 8: The store carries the policy and builds the held state

**Files:**
- Modify: `src/OpenEtrm.Api/GasCompositionStore.cs` (`GasPhysicalRequest` `:18`, `GasCompositionRow` `:35`, `GasCompositionExecution` `:59`, `Resolve` `:117`, core `Value` `:159-241`, `Prepare` `:256`, `Load` `:339`)
- Create: `src/OpenEtrm.Api/GasDeliveryQuantityStore.cs`
- Modify: `src/OpenEtrm.Api/GasCashPaymentStore.cs:17`
- Modify: `src/OpenEtrm.Api/GasPortfolio.cs:48`
- Modify: `src/OpenEtrm.Api/GasDailyQuantities.cs:83`
- Modify: `src/OpenEtrm.MarketData/Trading/PhysicalLifecycle.cs:86` (`TradeRefusal`)
- Test: `tests/OpenEtrm.Api.Checks/GasTicketChecks.cs`, `tools/check_gas_delivery_quantity.py`

**Interfaces:**
- Consumes: `security.read_gas_valuation_quantities` (Task 7), `GasHeldQuantities.Build` (Task 5), `ScheduleFollowedDay` (Task 6), the state constructor (Task 2).
- Produces: `GasPhysicalRequest` gains a trailing `[property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]string? QuantityPolicy=null`. `GasCompositionRow` gains a trailing `string? QuantityPolicy=null`.
- Produces: `GasCompositionStore.DeliverySchedulePolicy="delivery_schedule_v1"`.
- Produces: `internal bool GasCompositionExecution.FollowsSchedule`.
- Produces, in `GasDeliveryQuantityStore`:
  `internal static async Task<ImmutableArray<HeldDayQuantity>> Read(NpgsqlConnection connection,Guid catalog,GasCompositionExecution execution,DateTimeOffset at,CancellationToken ct)`
  `internal static ImmutableArray<HeldDayQuantity> Unrecorded(ImmutableArray<GasCompositionRow> members,ImmutableArray<RetainedSideProgram> programs,ExactDecimal quantity,DateTimeOffset at)`
- Changes `GasCashPaymentStore.Read` by adding a trailing `ImmutableArray<HeldDayQuantity> quantities=default`; the saved membership policy and authoritative source mapping, independently of returned rows, select `PhysicalLifecycle.SelectedQuantityPolicy`. Empty or incomplete returned coverage under that policy refuses.

- [x] **Step 1: Write the failing checks.**
  - In `GasTicketChecks.Run()` add a check that a `GasPhysicalRequest` with `QuantityPolicy` null serializes with no `quantityPolicy` property and deserializes from JSON lacking it, and that one with `"delivery_schedule_v1"` round-trips the property.
  - In `tools/check_gas_delivery_quantity.py` add, for a four-day purchase of 1,000 per day at a fixed 3.00 booked on the follows-schedule policy through the administrator's in-term load with the valuation instant in day 3 and records of final 980, scheduled 1,050 and scheduled 900: the single-trade read (`tickets/trade`) reports `measures.realizedUnsettled` of exactly `-6090` and `measures.unrealized` equal to 900 times day 3's mark less 3.00, plus 1,000 times day 4's mark less 3.00, computed in the check from the fixture's marks; a context captured before a later final allocation of 1,020 for day 2 still reports `-6090` after it is recorded, and a current read reports `-6000`; the Blotter row and a queued risk job's base value equal the single-trade total; the physical side's `deliveryRows` carry quantities 980, 1050, 900, 1000 with `quantitySource` `allocated_final_v1`, `scheduled_v1`, `scheduled_v1`, `contractual_quantity_v1`; `gas/quantities/read` reports `valuationEffect` `selected_delivery_quantity_v1`; the same trade booked on contract quantity reports `-6000` and `contractual_pv_unchanged`.
  - Add: a trade whose day has a manual final allocation of 980 and an adopted facility allocation of 975 shows its own `gas_quantity_selection` reason on its Blotter row while the other trade still values, and the complete risk request refuses. Keep the existing missing-market exclusion whitelist unchanged.

- [x] **Step 2: Run to verify failure**

Run: `dotnet run --project tests/OpenEtrm.Api.Checks -- --gas-ticket`
Expected: build failure naming `QuantityPolicy`.

- [x] **Step 3: Implement.**
  - `Resolve`: admit only null or `DeliverySchedulePolicy`; throw `MarketDataException("gas_quantity_policy",...)` when any physical side has the policy and any has a link; set the policy on the physical row only.
  - `Load`: read `quantity_policy` into the rebuilt `GasPhysicalRequest` and the physical row.
  - `GasDeliveryQuantityStore.Read`: return empty unless `execution.FollowsSchedule`; otherwise read the heads with `SELECT security.read_gas_valuation_quantities($1,$2,$3)::text`, keep only the heads of slots that follow, read each followed physical member's days with `GasDailyQuantitySelection.ReadContract(program,ratio times quantity)`, take its payment slots from the cash and fee rows whose `Source` names it, and call `GasHeldQuantities.Build`. `Unrecorded` does the same with no heads, for a draft that has no trade yet.
  - Core `Value` (`:216`) and `GasPortfolio.Prepare` (`:48`): read the quantities first and pass them to `GasCashPaymentStore.Read`. `Prepare` (`:257`) passes `Unrecorded(...)` into its empty state.
  - Core `Value`: for a followed member supply `FollowedDays` from the state (signed by the member's direction, tag `allocated` or `scheduled`, null for contract) and do not apply `recorded` to it; build its `deliveryRows` from the selected quantity and label.
  - `GasDailyQuantities.Read`: report `selected_delivery_quantity_v1` when the trade has a followed member.
  - Add `gas_quantity_selection` to `PhysicalLifecycle.TradeRefusal`.

- [x] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/OpenEtrm.Api.Checks -- --gas-ticket`, then `python3 tools/run_preview.py --pg-bin "$PG_BIN" --dotnet "$DOTNET" --check --gas-delivery-quantity-check --gas-in-term-check`
Expected: all checks pass.

- [x] **Step 5: Commit (consolidated in the integrated local commit)**

```bash
git add src/OpenEtrm.Api/GasCompositionStore.cs src/OpenEtrm.Api/GasDeliveryQuantityStore.cs src/OpenEtrm.Api/GasCashPaymentStore.cs src/OpenEtrm.Api/GasPortfolio.cs src/OpenEtrm.Api/GasDailyQuantities.cs src/OpenEtrm.MarketData/Trading/PhysicalLifecycle.cs tests/OpenEtrm.Api.Checks/GasTicketChecks.cs tools/check_gas_delivery_quantity.py
git commit -s -m "Value followed gas trades on their selected quantities in every read"
```

---

### Task 9: The ticket field

**Files:**
- Modify: `src/OpenEtrm.Api/GasTicket.cs` (field list `:48`, `PhysicalRequest` `:158`, `Draft` `:227`, `Fixed` `:264`, `ChoiceSet` `:278`, `Request` `:122`)
- Test: `tests/OpenEtrm.Api.Checks/GasTicketChecks.cs`

**Interfaces:**
- Consumes: `GasPhysicalRequest.QuantityPolicy`, `GasCompositionStore.DeliverySchedulePolicy`.
- Produces: a required physical-side field with id `quantity_policy`, label `Quantity follows`, choice list `quantity_policies` holding `contract_quantity` ("Contract quantity") and `delivery_schedule` ("Delivery schedule"). The draft value `contract_quantity` maps to a null request policy and `delivery_schedule` to `delivery_schedule_v1`, in both directions.

- [x] **Step 1: Write the failing checks** in `GasTicketChecks.Run()`:

```csharp
Check("the quantity policy maps to the booking request and back",()=>{
 Equal(GasTicket.Request(Draft(("quantity_policy","delivery_schedule")),snapshot,"current",operation,definition).PhysicalSides[0].QuantityPolicy,"delivery_schedule_v1");
 Equal(GasTicket.Request(Draft(("quantity_policy","contract_quantity")),snapshot,"current",operation,definition).PhysicalSides[0].QuantityPolicy,null);
 Equal(GasTicket.Draft(terms with{PhysicalSides=[terms.PhysicalSides[0] with{QuantityPolicy="delivery_schedule_v1"}]}).Sides[0].Values["quantity_policy"],"delivery_schedule");
 // A trade stored before the term existed reopens as contract quantity.
 Equal(GasTicket.Reopen(terms).Sides[0].Values["quantity_policy"],"contract_quantity");});
Check("a linked composition refuses to follow its schedule, on that field",()=>{
 var refusal=Refused<TicketFieldException>(()=>GasTicket.Request(Linked(("quantity_policy","delivery_schedule")),snapshot,"current",operation,definition));
 Equal(refusal.Field,"quantity_policy");});
Check("the ticket proposes the delivery schedule, and contract quantity once a side is linked",()=>{
 Equal(GasTicket.ChoiceSet(Draft(),"delivery","quantity_policy",policies).Suggestion!.Value,"delivery_schedule");
 Equal(GasTicket.ChoiceSet(Linked(),"delivery","quantity_policy",policies).Suggestion!.Value,"contract_quantity");});
```

Use the file's existing draft builders and its `Check` and `Equal` helpers; `Linked(...)` is a draft with a second physical side whose `link_source` names the first. Adjust the accessor names to the existing `TicketChoices` and `TicketFieldException` members.

- [x] **Step 2: Run to verify failure**

Run: `dotnet run --project tests/OpenEtrm.Api.Checks -- --gas-ticket`
Expected: FAIL, unknown field `quantity_policy`.

- [x] **Step 3: Implement.** Add the field after `quantity_basis` with the meaning "Whether value and accrued cash use the contract quantity, or what is scheduled and then allocated for each gas day." Add the choice list, the suggestion, both mappings, and the refusal through the file's `On(side,field,message)` helper. `SinglePoint` and `PriceProbe` pass no policy.

- [x] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/OpenEtrm.Api.Checks -- --gas-ticket` and `-- --ticket-description`
Expected: both pass.

- [x] **Step 5: Commit (consolidated in the integrated local commit)**

```bash
git add src/OpenEtrm.Api/GasTicket.cs tests/OpenEtrm.Api.Checks/GasTicketChecks.cs
git commit -s -m "Choose on the gas ticket whether a side's quantity follows its delivery schedule"
```

---

### Task 10: Web labels

**Files:**
- Modify: `web/src/lifecycle-text.ts:22`
- Test: `web/tests/lifecycle-text.test.mjs:22`

**Interfaces:**
- Consumes: the four label strings produced by the backend.

- [x] **Step 1: Write the failing test**

```js
assert.equal(quantityBasisText('allocated_final_v1'),'allocated, final');
assert.equal(quantityBasisText('allocated_provisional_v1'),'allocated, provisional');
assert.equal(quantityBasisText('scheduled_v1'),'scheduled');
assert.equal(quantityBasisText('selected_delivery_quantity_v1'),'follows the delivery schedule');
```

- [x] **Step 2: Run to verify failure**

Run: `npm test --prefix web`
Expected: FAIL on the first new assertion.

- [x] **Step 3: Add the four entries to `basisTexts`.** The ticket field needs no web change: the deal ticket renders backend-described choice fields, and the schedule source tags `scheduled`, `allocated` and `contract` already exist in `web/src/schedule-grid.ts`.

- [x] **Step 4: Run to verify it passes**

Run: `npm test --prefix web`
Expected: all tests pass.

- [x] **Step 5: Commit (consolidated in the integrated local commit)**

```bash
git add web/src/lifecycle-text.ts web/tests/lifecycle-text.test.mjs
git commit -s -m "Name the selected quantity sources in the Side inspector"
```

---

### Task 11: Browser check, records and review

**Files:**
- Modify: `tests/browser/gas-delivery-quantity.mjs`
- Modify: `docs/decisions.md`, `docs/physical-gas-side.md`, `docs/physical-gas-daily-quantities.md`, `docs/deal-ticket.md`, `docs/plan.md`, `docs/implementation.md`, `docs/verification.md`
- Modify: `docs/superpowers/specs/2026-11-23-delivery-quantity-input-design.md` (status line)

- [x] **Step 1: Write the failing browser checks** in `tests/browser/gas-delivery-quantity.mjs`, using the `gasTicket` driver in `tests/browser/gas-ticket.mjs`: a new gas ticket shows "Valuation quantity" with "Delivery schedule" proposed; adding a linked second side and keeping "Delivery schedule" shows the refusal on that field; a booked follows-schedule trade with one scheduled day shows that day's quantity tagged Scheduled in its schedule.

- [x] **Step 2: Run to verify failure, then pass**

Run: `python3 tools/run_preview.py --pg-bin "$PG_BIN" --dotnet "$DOTNET" --check --gas-delivery-quantity-check --browser-check --browser "$BROWSER"`
Expected: five selected-quantity browser checks pass; the ordinary physical gas checks run in the other gas suites.

- [x] **Step 3: Run the full groups**

Run: `python3 tools/check_all.py --pg-bin "$PG_BIN" --dotnet "$DOTNET" --browser "$BROWSER" --group gas`, then `--group trading` and `--group managed`
Expected: every group passes.

- [x] **Step 4: Update the records.**
  - `docs/decisions.md`: a new decision under the next free number. It states the saved policy, the selection order, the held-state mechanism, that it supersedes D360's contract-quantity accrual for trades on the policy, the nine calls confirmed in the spec, and the carried limits (linked sides, two-location entry, P&L explain residual; zero accrual after payment retains an overpaid balance under the new policy).
  - `docs/physical-gas-side.md` and `docs/physical-gas-daily-quantities.md`: the new term, the fourth selection policy, and that the read no longer states unchanged value for these trades.
  - `docs/deal-ticket.md`: the field and its refusal.
  - `docs/plan.md`, `docs/implementation.md`, `docs/verification.md`: status, and the actual check counts from Step 3.
  - The spec's status line: built, with the decision number.

- [x] **Step 5: Obtain independent adversarial review** of the temporal behavior (knowledge cutoff of quantity records in both routes), the numerical behavior (exact scaling, the paid-cash cases) and the authorization of the new read (runtime and worker). Resolve findings, rerun Step 3.

- [x] **Step 6: Commit**

```bash
git add tests/browser/gas-delivery-quantity.mjs docs
git commit -s -m "Record the delivery quantity policy and its verification"
```
