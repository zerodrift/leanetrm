# REC inventory and owned gas reach people through installed roles: design

Status: approved by the owner on 26 November 2026. Nothing is built. It is
the access piece named in the Scheduling by job design, which is published
separately.

## Intent

On an installation no person can use REC inventory or owned gas.

- `ReadRecInventory`, `RecordRecInventory` and `ConfigureRecInventory`
  (migration 0344) are granted to no role.
- `ReadGasOwnedHoldings` and `RecordGasOwnedHoldings` (migration 0265) are
  held only by the agent-only role Gas lifecycle operator (migration 0305).
  `ConfigureGasOwnedHoldings` is held by no role.
- An agent acts only within what its delegating person also holds (D401). So
  owned gas, park and loan and the customer side of park custody cannot be
  used by anyone, the agent included.

The local preview and the checks hide this by granting themselves the
permissions. Operating facilities had the same gap until D444.

The owner decided on 26 November 2026 that roles people hold get these
permissions, and that recording and setup go to named operating roles and not
to Trader.

## Goals

- Every human role that reads trades reads REC inventory and owned gas.
- One named role records and one sets up, for each of the two areas.
- A grant an installation already made by hand is kept.
- A person without a permission is refused exactly as today.

## Non-goals

- A new role, or a change to the limit of six roles for one account.
- Any change to the agent-only roles or to how delegation works.
- Any change to what the permissions gate.
- Giving any person a role.
- The pages themselves. The Scheduling by job design covers those.

## Anti-goals

- The local preview, or a check of who may do what, granting itself what the
  installed roles do not. That is how this gap stayed hidden. About thirty
  standalone checks of owned gas mechanics grant their own fixture identity.
  They test the mechanics, not who holds what, and converting them is a
  separate cleanup.
- A refusal check that passes because its assertion was weakened.

## Approaches

1. **Named operating roles record and set up. Chosen by the owner.** It
   keeps the split already used for gas delivery and facility quantities.
   The cost: the hosted owner holds six roles, the most one account may hold,
   and none of the four named roles. The owner reads both areas and can take
   one of the four by giving up a role.
2. **Trader records and sets up as well.** The hosted owner and a one-person
   desk could do everything with roles already held. A trader could then
   book a sale and record its transfer. Declined by the owner.
3. **Read for the operating roles only.** A park and loan agreement in a book
   would then refuse that book's Blotter and position reads to every other
   trade reader. D444 rejected the same narrowing for operating rights.
4. **New roles for REC and gas inventory.** No person would hold them at
   installation, and the six-role limit makes them hard to add. D444 rejected
   a new setup role for the same reason.

## Design

### The grants

One migration, modelled on 0389.

| Permission | Granted to |
| --- | --- |
| `ReadRecInventory` | Trader, Risk analyst, Facility operations, Facility operating limits, Gas delivery operations, Gas service setup, Contracts administration, Counterparty credit, Trade confirmations, Trade supervisor |
| `ReadGasOwnedHoldings` | The same ten roles |
| `RecordRecInventory` | Facility operations |
| `ConfigureRecInventory` | Contracts administration |
| `RecordGasOwnedHoldings` | Gas delivery operations, Gas service setup |
| `ConfigureGasOwnedHoldings` | Gas service setup |

- The ten readers are the human roles that read trades, the set D444 used.
- Contracts administration sets up REC accounts and transfer destinations
  because that form lists legal entities and counterparties only to a holder
  of `ReadTradeParties`. The facility roles do not hold it.
- Gas service setup also holds the record permission because every owned gas
  setup operation demands it (`security.require_gas_owned_write`).
- The migration stops before granting anything if an installed role is
  missing. It keeps a grant made by hand. Applying it again changes nothing.
  It assigns no role to any person.

### What each role can then do

- **Every trade-reading human role.** Read REC accounts, holdings and
  history. See gas rows on Lifecycle and read owned gas and park and loan
  agreements. A role that also reads gas operations reads the customer side
  of a custody agreement.
- **Trader.** Book a park and loan agreement and the customer side of a
  custody agreement. Both need `BookTrade` and the owned gas read. Trader
  records nothing and sets up nothing here.
- **Risk analyst.** Select the pricing policy that includes independent gas
  holdings, so a book that holds owned gas can be valued and risked whole. A
  service account that holds Risk analyst gains the read too, as D444 noted
  for operating rights.
- **Facility operations.** Record REC receipts, reservations, transfers and
  retirements.
- **Contracts administration.** Set up REC accounts and transfer
  destinations.
- **Gas delivery operations.** Record the owned gas actions: title,
  reservation, delivery, loss, park and loan advances and returns, operator
  balances and the customer side of custody.
- **Gas service setup.** Set up owned gas accounts, their action rules,
  inventory marks, projection settings and physical futures delivery
  settings, and save gas action policies.

### What does not change

- Viewer, Market data editor, Market data approver, Access administrator and
  Assistant prompt editor read no trades and are granted nothing.
- The agent-only roles are unchanged. An agent still needs a delegating
  person who holds the permissions.
- A whole-book P&L run over a book that holds owned gas is still refused for
  every reader (D452).
- Binding an automatic action policy still needs `RunJobs`, which neither gas
  role holds.
- No single role both books and reads a custody agreement. Trader does not
  read gas operations.
- A role an installation added by hand is not changed. If it reads trades,
  its holders meet the refusals above until the installation grants it the
  reads.

### Checks and the local preview

- A new check runs each installed role as one person against the REC and
  owned gas routes, as the operating access check does for D444. Park
  custody and action execution get no route assertion of their own: holder
  sets pin who can use them, and recording is asserted on a route that passes
  the same native gate.
- A check compares the human roles that read trades with the holders of the
  two read permissions, so a later trade-reading role is not forgotten.
- A check exercises the park and loan refusal that approach 3 relies on: a
  trade reader without the owned gas read, on a book that holds a park and
  loan agreement. That refusal was read from the code and has not been run.
- The checks that assert a role lacks these permissions keep their
  assertions and use an identity that still lacks them. These include the
  REC inventory check, the lifecycle valuation browser check, the lifecycle
  economic worker check, the cash option value check and the pinned list for
  Trade supervisor. Every check worker is provisioned as a Risk analyst, so
  the checks of a worker without the owned gas read change too. The plan
  lists them.
- The local preview stops granting its Trader the REC permissions. It gains
  one demo operator who holds Facility operations, Facility operating
  limits, Gas delivery operations, Gas service setup and Contracts
  administration. The preview's demo users are listed in the API as well as
  in the fixture, so the operator is added in both.

### Documents

`docs/security.md`, `docs/gas-owned-holdings.md`,
`docs/structured-power-trading.md`, `docs/ai-assistant.md`, `db/README.md`
and `docs/verification.md` state who holds these permissions and are updated
in the same change. `docs/security.md` says there are two agent-only roles.
There are three.

## Verification

- The checks above are written first and fail before the migration exists.
- It is an authorization change. It gets an independent adversarial review
  before publication.
- It ships as one migration and follows the release steps for a schema
  change.

## Decisions to record

- A new decision, numbered when it merges, in the form of D444: who reads,
  who records, who sets up, what is unchanged, and what was rejected.

## Design calls

The owner's.

- Roles people hold get REC inventory and owned gas.
- Recording and setup go to named operating roles, not to Trader.

The maintainer's, approved by the owner with this document.

- Read goes to all ten trade-reading human roles for both areas. REC read
  could be narrower, since no other read depends on it.
- The four named roles: Facility operations and Contracts administration for
  RECs, Gas delivery operations and Gas service setup for owned gas.
- Gas service setup holds the record permission as well as setup.
- The local preview's Trader loses its REC permissions, and the preview
  gains a demo operator.

## Left to the builder

- The migration's number and the decision's number.
- How each changed check obtains an identity without the permissions.
- How the demo operator is added to the preview's fixture and its checks.
