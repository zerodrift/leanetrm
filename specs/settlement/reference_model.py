"""PROPOSED contract oracle. Pure immutable state; no application imports or I/O.

Authority/head arguments represent trusted snapshots, not caller claims. Serial
transitions specify future atomic outcomes, not SQL/RLS/durability implementation.
"""
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from fractions import Fraction as F
from zoneinfo import ZoneInfo

ROUND = 'cash_2dp_half_even_per_occurrence_v1'
APPROVAL = 'two_principals_exact_action_24h_v1'


class Refusal(ValueError):
    pass


def require(condition, code):
    if not condition:
        raise Refusal(code)


def native_month(year, month, zone, minutes):
    require(minutes in (15, 60), 'cadence')
    z = ZoneInfo(zone)
    start = datetime(year, month, 1, tzinfo=z).astimezone(timezone.utc)
    end = datetime(year + (month == 12), month % 12 + 1, 1, tzinfo=z).astimezone(timezone.utc)
    step = timedelta(minutes=minutes)
    cells = []
    while start < end:
        cells.append((start, start + step))
        start += step
    return tuple(cells)


def gross_cash(whole_asset_mwh, capacity_mw, contract_mw, strike, sign):
    require(len(whole_asset_mwh) == len(capacity_mw) > 0, 'asset_coverage')
    require(all(x is not None and x >= 0 for x in whole_asset_mwh), 'meter_required')
    require(all(c > 0 for c in capacity_mw) and contract_mw > 0 and sign in (-1, 1), 'quantity')
    return sign * F(contract_mw) * sum(whole_asset_mwh, F(0)) * F(strike) / sum(capacity_mw, F(0))


def round_cash(amount, policy):
    require(policy == ROUND, 'rounding_policy')
    scaled = abs(F(amount)) * 100
    quotient, remainder = divmod(scaled.numerator, scaled.denominator)
    if 2 * remainder > scaled.denominator or (2 * remainder == scaled.denominator and quotient % 2):
        quotient += 1
    return F(quotient if amount >= 0 else -quotient, 100)


@dataclass(frozen=True)
class Family:
    catalog: str
    trade: str
    name: str
    origin: str
    currency: str
    zone: str
    due: datetime
    parties: tuple
    sign: int
    assignments: tuple  # trusted explicit (binding, side) lineage mappings

    def occurrence(self, year, month):
        return self.catalog, self.trade, self.name, year, month


@dataclass(frozen=True)
class Binding:
    id: str
    side: str
    start: datetime
    end: datetime
    mw: F
    strike: F
    due: datetime
    currency: str
    parties: tuple
    sign: int
    assets: tuple  # exact (asset/source/stage key, capacity MW, version) triples


@dataclass(frozen=True)
class Contribution:
    key: tuple  # excludes caller occurrence/lineage aliases
    binding: Binding
    start: datetime
    end: datetime


@dataclass(frozen=True)
class PlannedOccurrence:
    rows: tuple
    required_keys: frozenset
    occurrence: tuple
    trade_head: int


def plan(family, year, month, minutes, history, selected_head, active_from, active_to, as_of):
    grid = native_month(year, month, family.zone, minutes)
    require(as_of >= grid[-1][1], 'month_not_complete')
    require(active_from in {s for s, _ in grid} and active_to in {e for _, e in grid}
            and active_from < active_to, 'active_coverage')
    require(selected_head in history, 'trade_head')
    bindings = history[selected_head]  # deliberately never sum historical revisions
    for b in bindings:
        require((b.id, b.side) in family.assignments, 'lineage_mapping')
        require((b.due, b.currency, b.parties, b.sign) ==
                (family.due, family.currency, family.parties, family.sign), 'incompatible_terms')
        require(b.start < b.end and b.mw > 0 and len({a for a, _, _ in b.assets}) == len(b.assets), 'binding')
    rows = []
    for start, end in grid:
        selected = [b for b in bindings if b.start < end and start < b.end]
        active = active_from <= start and end <= active_to
        require(len(selected) == (1 if active else 0), 'coverage')
        if active:
            b = selected[0]
            require(b.start <= start and end <= b.end, 'native_boundary')
            key = family.catalog, family.trade, family.origin, start, end
            rows.append(Contribution(key, b, start, end))
    return PlannedOccurrence(tuple(rows), frozenset(r.key for r in rows),
                             family.occurrence(year, month), selected_head)


@dataclass(frozen=True)
class Reading:
    target: tuple  # asset/source/stage, UTC start, UTC end; excludes revision
    revision: str
    energy: F
    known: datetime


@dataclass(frozen=True)
class Finality:
    target: tuple
    id: str
    sequence: int
    previous: str | None
    reading: str | None
    state: str
    effective: datetime
    known: datetime


def select_final(target, readings, publications, as_of, known_at):
    # Validate supplied facts before selection, including excluded future facts.
    require(len({(r.target, r.revision) for r in readings}) == len(readings), 'duplicate_reading')
    require(all(r.energy >= 0 for r in readings), 'invalid_reading')
    require(len({h.id for h in publications}) == len(publications), 'duplicate_head')
    chains = {}
    for h in publications:
        chains.setdefault(h.target, []).append(h)
    for t, members in chains.items():
        chain = sorted(members, key=lambda h: h.sequence)
        previous = None
        for seq, h in enumerate(chain, 1):
            require(h.sequence == seq and h.previous == (previous.id if previous else None), 'head_chain')
            require(h.state in ('final', 'withdrawn', 'suspended') and t[2] <= h.effective <= h.known, 'finality_time')
            require(previous is None or (previous.known <= h.known and previous.effective <= h.effective), 'head_time')
            previous = h
    visible = [h for h in publications if h.target == target and h.effective <= as_of and h.known <= known_at]
    require(bool(visible), 'missing_finality')
    head = max(visible, key=lambda h: h.sequence)
    require(head.state == 'final', 'finality_unavailable')
    rows = [r for r in readings if r.target == target and r.revision == head.reading and r.known <= known_at]
    require(len(rows) == 1 and rows[0].known <= head.known, 'reading_unavailable')
    return rows[0], head


@dataclass(frozen=True)
class Proposal:
    occurrence: tuple
    book: str
    trade_head: int
    rows: tuple
    heads: tuple
    exact: F
    rounded: F
    due: datetime
    currency: str
    as_of: datetime
    known_at: datetime
    rounding: str
    approval_policy: str


def preview(family, year, month, book, trade_head, planned, readings, publications, as_of, known_at, rounding, approval_policy):
    rows = planned.rows
    require(planned.occurrence == family.occurrence(year, month) and planned.trade_head == trade_head, 'plan_context')
    require(frozenset(r.key for r in rows) == planned.required_keys, 'contribution_coverage')
    require(approval_policy == APPROVAL, 'approval_policy')
    require(bool(rows) and len({r.key for r in rows}) == len(rows), 'contribution_coverage')
    total, pins = F(0), set()
    for row in rows:
        energy, capacity = [], []
        for asset, mw, _ in row.binding.assets:
            reading, head = select_final((asset, row.start, row.end), readings, publications, as_of, known_at)
            energy.append(reading.energy); capacity.append(mw)
            pins.add((head.target, head.id))
        total += gross_cash(tuple(energy), tuple(capacity), row.binding.mw, row.binding.strike, row.binding.sign)
    return Proposal(family.occurrence(year, month), book, trade_head, rows, tuple(sorted(pins)), total,
                    round_cash(total, rounding), family.due, family.currency, as_of, known_at, rounding, approval_policy)


@dataclass(frozen=True)
class Actor:
    account: str
    principal: str
    book: str
    grants: frozenset
    delegated: bool
    principal_grants: frozenset
    delegation_active: bool


def authority(actors, account, permission, book):
    actor = next((a for a in actors if a.account == account), None)
    require(actor is not None and bool(actor.principal) and actor.book == book and
            {'read', permission} <= actor.grants and {'read', permission} <= actor.principal_grants and
            (not actor.delegated or actor.delegation_active), 'authority')
    return actor.principal


@dataclass(frozen=True)
class Approval:
    id: str
    proposal: Proposal
    expected: int
    maker: str
    checker: str
    maker_principal: str
    checker_principal: str
    issued: datetime
    expires: datetime


def approve(id, proposal, expected, maker, checker, now, expires, actors):
    m = authority(actors, maker, 'propose', proposal.book)
    c = authority(actors, checker, 'approve', proposal.book)
    require(m != c, 'separation')
    require(now < expires <= now + timedelta(hours=24), 'approval_expiry')
    return Approval(id, proposal, expected, maker, checker, m, c, now, expires)


@dataclass(frozen=True)
class Command:
    actor: str
    id: str
    proposal: Proposal
    expected: int
    approval: Approval


@dataclass(frozen=True)
class Accepted:
    occurrence: tuple
    revision: int
    proposal: Proposal
    amount: F  # original or correction delta, already rounded
    effective: datetime
    known: datetime


@dataclass(frozen=True)
class State:
    accepted: tuple = ()
    claims: frozenset = frozenset()
    used_approvals: frozenset = frozenset()
    receipts: tuple = ()


def accept(state, command, actors, current_heads, current_trade_head, now, certified_at):
    p, approval = command.proposal, command.approval
    permission = 'accept' if command.expected == 0 else 'adjust'
    authority(actors, command.actor, permission, p.book)
    # Original command reconciliation precedes expiry/current-head checks.
    for original, receipt in state.receipts:
        if (original.proposal.occurrence[0], original.actor, original.id) == (p.occurrence[0], command.actor, command.id):
            require(original == command, 'command_conflict')
            return state, receipt
    history = [a for a in state.accepted if a.occurrence == p.occurrence]
    latest = history[-1] if history else None
    require(not any(p.heads == a.proposal.heads for a in history), 'already_accepted')
    require(command.expected == len(history), 'version')
    require(approval.proposal == p and approval.expected == command.expected, 'approval_content')
    require(approval.id not in state.used_approvals, 'approval_used')
    require(approval.issued <= now < approval.expires and approval.expires-approval.issued <= timedelta(hours=24), 'approval_expired')
    m = authority(actors, approval.maker, 'propose', p.book)
    c = authority(actors, approval.checker, 'approve', p.book)
    require((m, c) == (approval.maker_principal, approval.checker_principal) and m != c, 'separation')
    require(tuple(sorted(current_heads)) == p.heads and current_trade_head == p.trade_head, 'stale_proposal')
    require(p.rounding == ROUND and p.approval_policy == APPROVAL and round_cash(p.exact, p.rounding) == p.rounded, 'policy')
    require(certified_at >= now >= p.as_of and certified_at >= p.known_at, 'acceptance_time')
    keys = frozenset(r.key for r in p.rows)
    require(len(keys) == len(p.rows) > 0, 'contribution_coverage')
    if latest:
        original = history[0].proposal
        require((p.rows, p.currency, p.due, p.rounding) == (original.rows, original.currency, original.due, original.rounding), 'original_terms')
    else:
        require(not keys & state.claims, 'contribution_owned')
    delta = p.rounded - sum((a.amount for a in history), F(0))
    receipt = Accepted(p.occurrence, len(history)+1, p, delta, now, certified_at)
    return State(state.accepted+(receipt,), state.claims | keys, state.used_approvals | {approval.id},
                 state.receipts+((command, receipt),)), receipt


@dataclass(frozen=True)
class Balance:
    primary: F
    overdue: F
    complete: F | None
    unaccepted_count: int


@dataclass(frozen=True)
class PaymentEvidence:
    obligation: tuple  # (occurrence, accepted revision)
    amount: F
    effective: datetime
    known: datetime


def value_partition(provisional, accepted, paid, as_of, known_at, discount):
    """paid is immutable external S4 evidence, not a payment command.

    provisional: (contribution key, signed exact cash, due) known rows only.
    Discount is explicit, even for no-discounting (factor1).
    """
    visible = [a for a in accepted if a.effective <= as_of and a.known <= known_at]
    require(len({p.obligation for p in paid}) == len(paid), 'duplicate_payment')
    obligations = {(a.occurrence, a.revision): a for a in accepted}
    for p in paid:
        a = obligations.get(p.obligation)
        require(a is not None and a.amount != 0 and p.amount == a.amount, 'full_named_payment')
        require(a.effective <= p.effective <= p.known and a.known <= p.known, 'payment_time')
    payments = {p.obligation: p.amount for p in paid if p.effective <= as_of and p.known <= known_at}
    covered = {r.key for a in visible for r in a.proposal.rows}
    require(len({key for key, _, _ in provisional}) == len(provisional), 'duplicate_valuation')
    rows = [(cash, due) for key, cash, due in provisional if key not in covered]
    count = len(rows)
    for a in visible:
        cash = a.amount - payments.get((a.occurrence, a.revision), F(0))
        if cash:  # accepted zero has no outstanding cash/payment right
            rows.append((cash, a.proposal.due))
    primary, overdue, has_overdue = F(0), F(0), False
    for cash, due in rows:
        if as_of >= due:
            overdue += cash
            has_overdue = True
        else:
            require(discount is not None and discount > 0, 'discount_required')
            primary += cash * discount
    return Balance(primary, overdue, None if has_overdue else primary, count)
