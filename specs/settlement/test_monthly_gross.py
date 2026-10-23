"""Executable acceptance specification for PROPOSED policies, not production tests."""
import unittest
from fractions import Fraction as F
from reference_model import (
    APPROVAL, ROUND, Actor, Binding, Command, Family, Finality,
    PaymentEvidence, Reading, Refusal, State, accept, approve, gross_cash,
    native_month, plan, preview, round_cash, value_partition,
)
from datetime import timedelta
from zoneinfo import ZoneInfo
from dataclasses import replace

ROUND = 'cash_2dp_half_even_per_occurrence_v1'


class NumericalContract(unittest.TestCase):
    def test_base_month_and_single_interval_correction(self):
        cells = native_month(2031, 11, 'America/New_York', 60)
        self.assertEqual(len(cells), 721)
        base = sum(gross_cash((F(50),), (F(100),), F(80), F(40), -1) for _ in cells)
        self.assertEqual(base, -1153600)
        changed_one = base - gross_cash((F(50),), (F(100),), F(80), F(40), -1) + gross_cash((F(51),), (F(100),), F(80), F(40), -1)
        self.assertEqual(changed_one, -1153632)
        self.assertEqual(changed_one - base, -32)
        all_cells = sum(gross_cash((F(51),), (F(100),), F(80), F(40), -1) for _ in cells)
        self.assertEqual(all_cells, -1176672)
        self.assertEqual(all_cells - base, -23072)

    def test_round_once_and_difference_of_rounded_totals(self):
        self.assertEqual(round_cash(F('-1.005'), ROUND), F('-1.00'))
        self.assertEqual(round_cash(F('-1.015'), ROUND), F('-1.02'))
        self.assertEqual(round_cash(F('-1.015'), ROUND)-round_cash(F('-1.005'), ROUND), F('-.02'))
        self.assertEqual(round_cash(F('.005')+F('.005'), ROUND), F('.01'))


class ContractCases(unittest.TestCase):
    def setUp(self):
        self.grid = native_month(2031, 11, 'America/New_York', 60)
        self.now = self.grid[-1][1] + timedelta(hours=1)
        self.due = self.now + timedelta(days=10)
        self.family = Family('catalog', 'trade', 'strike', 'original-membership', 'SYN',
                             'America/New_York', self.due, ('payer', 'receiver'), -1,
                             (('b1', 's1'), ('b2', 's2')))
        self.binding = Binding('b1', 's1', self.grid[0][0], self.grid[1][1], F(80), F(40),
                               self.due, 'SYN', self.family.parties, -1, (('asset/source/stage', F(100), 'v1'),))
        self.planned = self.make_plan((self.binding,))
        self.rows = self.planned.rows
        self.readings = tuple(Reading(('asset/source/stage', s, e), 'r1', F(50), e) for s, e in self.grid[:2])
        self.heads = tuple(Finality(r.target, 'h'+str(i), 1, None, 'r1', 'final', r.target[2], r.known)
                           for i, r in enumerate(self.readings))
        grants = frozenset(('read', 'propose', 'approve', 'accept', 'adjust'))
        self.actors = tuple(Actor(a, p, 'book', grants, False, grants, True)
                            for a, p in (('maker', 'person-a'), ('checker', 'person-b'), ('executor', 'person-c')))
        self.p = self.make_preview()

    def make_plan(self, bindings, **kw):
        args = dict(family=self.family, year=2031, month=11, minutes=60,
                    history={1: bindings}, selected_head=1, active_from=self.grid[0][0],
                    active_to=self.grid[1][1], as_of=self.now)
        args.update(kw)
        return plan(**args)

    def make_preview(self, **kw):
        args = dict(family=self.family, year=2031, month=11, book='book', trade_head=1,
                    planned=self.planned, readings=self.readings, publications=self.heads,
                    as_of=self.now, known_at=self.now, rounding=ROUND, approval_policy=APPROVAL)
        args.update(kw)
        return preview(**args)

    def command(self, p=None, expected=0, ident='command', **kw):
        p = p or self.p
        a = approve('approval-'+ident, p, expected, 'maker', 'checker', self.now,
                    self.now+timedelta(hours=1), self.actors)
        return replace(Command('executor', ident, p, expected, a), **kw)

    def commit(self, state=None, command=None, **kw):
        state = state or State(); command = command or self.command()
        args = dict(actors=self.actors, current_heads=command.proposal.heads,
                    current_trade_head=command.proposal.trade_head, now=self.now+timedelta(minutes=1),
                    certified_at=self.now+timedelta(minutes=2))
        args.update(kw)
        return accept(state, command, **args)

    def correction(self, energy=F(51), seq=2, prior='h0', **kw):
        r = Reading(self.readings[0].target, 'r'+str(seq), energy, self.now)
        h = Finality(r.target, 'h0-'+str(seq), seq, prior, r.revision, 'final', self.now, self.now)
        return self.make_preview(readings=self.readings+(r,), publications=self.heads+(h,), **kw)

    def provisional(self):
        return tuple((r.key, F(-1600), self.due) for r in self.rows)

    def test_complete_plan_cannot_omit_an_interval(self):
        omitted = replace(self.planned, rows=self.planned.rows[:-1])
        with self.assertRaisesRegex(Refusal, 'contribution_coverage'):
            self.make_preview(planned=omitted)

    def test_dst_and_native_quarter_hours(self):
        z = ZoneInfo('America/New_York')
        self.assertEqual(sum(s.astimezone(z).day == 2 for s, _ in self.grid), 25)
        spring = native_month(2031, 3, 'America/New_York', 60)
        self.assertEqual(sum(s.astimezone(z).day == 9 for s, _ in spring), 23)
        quarters = native_month(2031, 11, 'America/New_York', 15)
        self.assertEqual(len(quarters), 2884)
        self.assertEqual(sum(gross_cash((F('12.5'),), (F(100),), F(80), F(40), -1) for _ in quarters), -1153600)
        repeated = [s for s, _ in self.grid if s.astimezone(z).day == 2 and s.astimezone(z).hour == 1]
        self.assertEqual(len(set(repeated)), 2)

    def test_multi_asset_missing_and_explicit_zero(self):
        self.assertEqual(gross_cash((F(50), F(0)), (F(100), F(100)), F(80), F(40), -1), -800)
        with self.assertRaisesRegex(Refusal, 'meter_required'):
            gross_cash((F(50), None), (F(100), F(100)), F(80), F(40), -1)
        with self.assertRaisesRegex(Refusal, 'quantity'):
            gross_cash((F(50),), (F(0),), F(80), F(40), -1)

    def test_capacity_version_and_amended_terms(self):
        first = replace(self.binding, end=self.grid[0][1])
        second = replace(self.binding, id='b2', side='s2', start=self.grid[1][0],
                         assets=(('asset/source/stage', F(200), 'v2'),))
        p = self.make_preview(planned=self.make_plan((first, second)))
        self.assertEqual(p.exact, -2400)
        second = replace(second, mw=F(40), strike=F(50), assets=self.binding.assets)
        p = self.make_preview(planned=self.make_plan((first, second)))
        self.assertEqual(p.exact, -2600)
        self.assertEqual(p.occurrence, self.p.occurrence)
        self.assertEqual(tuple(r.key for r in p.rows), tuple(r.key for r in self.p.rows))

    def test_one_selected_trade_head_and_termination(self):
        old = replace(self.binding, end=self.grid[2][1])
        rows = self.make_plan((self.binding,), history={0: (old,), 1: (self.binding,)})
        self.assertEqual(len(rows.rows), 2)
        self.assertEqual(self.make_preview(planned=rows).exact, -3200)
        with self.assertRaisesRegex(Refusal, 'month_not_complete'):
            self.make_plan((self.binding,), as_of=self.grid[3][1])

    def test_gap_overlap_and_missing_lineage(self):
        for bindings in ((replace(self.binding, end=self.grid[0][1]),), (self.binding, self.binding)):
            with self.assertRaisesRegex(Refusal, 'coverage'):
                self.make_plan(bindings)
        with self.assertRaisesRegex(Refusal, 'lineage_mapping'):
            self.make_plan((replace(self.binding, side='unmapped'),))

    def test_incompatible_terms_refuse_whole_occurrence(self):
        for b in (replace(self.binding, due=self.due+timedelta(days=1)),
                  replace(self.binding, currency='OTHER'), replace(self.binding, sign=1),
                  replace(self.binding, parties=('another', 'receiver'))):
            with self.assertRaisesRegex(Refusal, 'incompatible_terms'):
                self.make_plan((b,))

    def test_missing_finality_and_explicit_policy(self):
        with self.assertRaisesRegex(Refusal, 'missing_finality'):
            self.make_preview(publications=())
        for name in ('rounding', 'approval_policy'):
            with self.assertRaises(Refusal):
                self.make_preview(**{name: None})

    def test_unverified_raw_correction_keeps_published_finality(self):
        r = Reading(self.readings[0].target, 'r2', F(99), self.now)
        self.assertEqual(self.make_preview(readings=self.readings+(r,)).exact, -3200)
        self.assertEqual(self.correction().exact, -3232)

    def test_later_known_finality_and_business_visibility(self):
        r = Reading(self.readings[0].target, 'r2', F(51), self.now)
        h = Finality(r.target, 'new', 2, 'h0', 'r2', 'final', self.now, self.now+timedelta(hours=1))
        args = dict(readings=self.readings+(r,), publications=self.heads+(h,))
        self.assertEqual(self.make_preview(**args).exact, -3200)
        self.assertEqual(self.make_preview(**args, known_at=h.known).exact, -3232)
        self.assertEqual(self.make_preview(**args, as_of=self.now-timedelta(minutes=1), known_at=h.known).exact, -3200)

    def test_withdrawal_and_suspension_do_not_fall_back(self):
        for status in ('withdrawn', 'suspended'):
            h = Finality(self.readings[0].target, 'new', 2, 'h0', None, status, self.now, self.now)
            with self.assertRaisesRegex(Refusal, 'finality_unavailable'):
                self.make_preview(publications=self.heads+(h,))

    def test_duplicate_revision_and_invalid_excluded_cell(self):
        with self.assertRaisesRegex(Refusal, 'duplicate_reading'):
            self.make_preview(readings=self.readings+(self.readings[0],))
        bad = Reading(('outside', *self.grid[5]), 'bad', F(-1), self.now+timedelta(days=1))
        with self.assertRaisesRegex(Refusal, 'invalid_reading'):
            self.make_preview(readings=self.readings+(bad,))
        with self.assertRaisesRegex(Refusal, 'head_chain'):
            self.make_preview(publications=self.heads+(replace(self.heads[0], id='bad', sequence=3),))

    def test_initial_acceptance_is_immutable(self):
        empty = State(); state, receipt = self.commit(empty)
        self.assertEqual(empty, State())
        self.assertEqual(receipt.amount, -3200)
        self.assertEqual(len(state.accepted), 1)
        self.assertEqual(len(state.claims), 2)
        self.assertGreater(receipt.effective, self.p.as_of)
        with self.assertRaisesRegex(Refusal, 'acceptance_time'):
            self.commit(certified_at=self.now)

    def test_preview_rounds_once_across_amendment_segments(self):
        first = replace(self.binding, end=self.grid[0][1], mw=F(1), strike=F('.01'))
        second = replace(first, id='b2', side='s2', start=self.grid[1][0], end=self.grid[1][1])
        p = self.make_preview(planned=self.make_plan((first, second)))
        self.assertEqual(p.exact, F('-.01'))
        self.assertEqual(p.rounded, F('-.01'))
        self.assertEqual(round_cash(F('-.005'), ROUND)*2, 0)

    def test_accepted_adjustment_is_difference_of_rounded_totals(self):
        # The two-cell occurrence has one tiny positive meter reading and explicit zero.
        readings = (replace(self.readings[0], energy=F('1.005')/32),
                    replace(self.readings[1], energy=F(0)))
        p = self.make_preview(readings=readings)
        state, original = self.commit(command=self.command(p))
        self.assertEqual((p.exact, original.amount), (F('-1.005'), F('-1.00')))
        corrected = Reading(readings[0].target, 'r2', F('1.015')/32, self.now)
        head = Finality(corrected.target, 'new', 2, 'h0', 'r2', 'final', self.now, self.now)
        p2 = self.make_preview(readings=readings+(corrected,), publications=self.heads+(head,))
        _, adjustment = self.commit(state, self.command(p2, 1, 'rounded-correction'))
        self.assertEqual((p2.exact, p2.rounded, adjustment.amount),
                         (F('-1.015'), F('-1.02'), F('-.02')))

    def test_corrections_use_cumulative_rounded_cash(self):
        state, original = self.commit()
        p = self.correction()
        state2, correction = self.commit(state, self.command(p, 1, 'correction'))
        self.assertEqual(correction.amount, -32)
        self.assertEqual(state2.accepted[0], original)
        # A separately published third head restores the original quantity.
        r2 = Reading(self.readings[0].target, 'r2', F(51), self.now)
        h2 = Finality(r2.target, 'h0-2', 2, 'h0', 'r2', 'final', self.now, self.now)
        r3 = replace(r2, revision='r3', energy=F(50))
        h3 = replace(h2, id='h0-3', sequence=3, previous='h0-2', reading='r3')
        restored = self.make_preview(readings=self.readings+(r2, r3), publications=self.heads+(h2, h3))
        state3, correction3 = self.commit(state2, self.command(restored, 2, 'restore'))
        self.assertEqual(correction3.amount, 32)
        self.assertEqual(sum(a.amount for a in state3.accepted), -3200)

    def test_previously_accepted_target_cannot_be_accepted_again(self):
        state, _ = self.commit()
        state, _ = self.commit(state, self.command(self.correction(), 1, 'correction'))
        with self.assertRaisesRegex(Refusal, 'already_accepted'):
            self.commit(state, self.command(self.p, 2, 'old-target'))

    def test_zero_correction_advances_evidence(self):
        state, _ = self.commit()
        p = self.correction(F(50))
        state, receipt = self.commit(state, self.command(p, 1, 'zero-correction'))
        self.assertEqual(receipt.amount, 0)
        self.assertEqual(len(state.claims), 2)
        with self.assertRaisesRegex(Refusal, 'already_accepted'):
            self.commit(state, self.command(p, 2, 'again'))

    def test_zero_original_claims_and_alias_cannot_reaccept(self):
        zero = tuple(replace(r, energy=F(0)) for r in self.readings)
        p = self.make_preview(readings=zero)
        state, receipt = self.commit(command=self.command(p))
        self.assertEqual(receipt.amount, 0)
        self.assertEqual(len(state.claims), 2)
        alias_family = replace(self.family, name='alias')
        alias = self.make_preview(family=alias_family, readings=zero,
                                  planned=self.make_plan((self.binding,), family=alias_family))
        with self.assertRaisesRegex(Refusal, 'contribution_owned'):
            self.commit(state, self.command(alias, 0, 'alias'))
        b = value_partition(self.provisional(), state.accepted, (), self.due, self.due, F(1))
        self.assertEqual((b.primary, b.overdue, b.complete, b.unaccepted_count), (0, 0, 0, 0))

    def test_exact_replay_survives_new_heads_and_expiry(self):
        command = self.command(); state, receipt = self.commit(command=command)
        replay_state, replay = self.commit(state, command, current_heads=(), current_trade_head=99,
            now=self.now+timedelta(days=2), certified_at=self.now+timedelta(days=2))
        self.assertEqual((replay_state, replay), (state, receipt))
        with self.assertRaisesRegex(Refusal, 'command_conflict'):
            self.commit(state, replace(command, proposal=self.correction()))
        with self.assertRaisesRegex(Refusal, 'authority'):
            self.commit(state, command, actors=())

    def test_new_command_does_not_duplicate_financial_action(self):
        state, _ = self.commit()
        with self.assertRaisesRegex(Refusal, 'already_accepted'):
            self.commit(state, self.command(ident='another-worker'))
        self.assertEqual(len(state.accepted), 1)

    def test_same_human_alias_cannot_check(self):
        actors = tuple(replace(a, principal='person-a') if a.account == 'checker' else a for a in self.actors)
        with self.assertRaisesRegex(Refusal, 'separation'):
            approve('alias', self.p, 0, 'maker', 'checker', self.now, self.now+timedelta(hours=1), actors)

    def test_approval_expiry_equality_and_maximum(self):
        c = self.command()
        with self.assertRaisesRegex(Refusal, 'approval_expired'):
            self.commit(command=c, now=c.approval.expires, certified_at=c.approval.expires)
        with self.assertRaisesRegex(Refusal, 'approval_expiry'):
            approve('long', self.p, 0, 'maker', 'checker', self.now, self.now+timedelta(hours=25), self.actors)

    def test_current_authority_for_all_participants(self):
        for account in ('maker', 'checker', 'executor'):
            for change in ({'grants': frozenset()}, {'principal_grants': frozenset()},
                           {'book': 'other'}, {'delegated': True, 'delegation_active': False}):
                actors = tuple(replace(a, **change) if a.account == account else a for a in self.actors)
                with self.subTest(account=account, change=change), self.assertRaisesRegex(Refusal, 'authority'):
                    self.commit(actors=actors)

    def test_approval_content_and_failed_transition_atomicity(self):
        state = State(); command = replace(self.command(), proposal=self.correction())
        with self.assertRaisesRegex(Refusal, 'approval_content'):
            self.commit(state, command)
        self.assertEqual(state, State())
        state, _ = self.commit(state)
        self.assertEqual(len(state.used_approvals), 1)

    def test_publication_acceptance_serial_orders(self):
        changed = self.correction()
        with self.assertRaisesRegex(Refusal, 'stale_proposal'):
            self.commit(current_heads=changed.heads)
        state, original = self.commit()
        self.assertEqual(original.amount, -3200)
        state, adjustment = self.commit(state, self.command(changed, 1, 'correction'))
        self.assertEqual(adjustment.amount, -32)

    def test_expected_revision_race(self):
        state, _ = self.commit()
        p1 = self.correction(F(51)); p2 = self.correction(F(52))
        state, _ = self.commit(state, self.command(p1, 1, 'first'))
        # Different certified target for the second candidate, but the old expected version.
        p2 = replace(p2, heads=tuple((target, head+'-different') for target, head in p2.heads))
        with self.assertRaisesRegex(Refusal, 'version'):
            self.commit(state, self.command(p2, 1, 'second'))
        self.assertEqual(len(state.accepted), 2)

    def test_correction_cannot_reprice_original_terms(self):
        state, _ = self.commit(); p = self.correction()
        changed_row = replace(p.rows[0], binding=replace(p.rows[0].binding, strike=F(99)))
        p = replace(p, rows=(changed_row, p.rows[1]))
        with self.assertRaisesRegex(Refusal, 'original_terms'):
            self.commit(state, self.command(p, 1, 'changed-terms'))

    def test_value_partition_no_double_count_and_explicit_discount(self):
        before = value_partition(self.provisional(), (), (), self.now, self.now, F('0.9'))
        self.assertEqual((before.primary, before.unaccepted_count), (-2880, 2))
        state, receipt = self.commit()
        changed_rows = tuple((key, F(-9999), due) for key, _, due in self.provisional())
        after = value_partition(changed_rows, state.accepted, (), receipt.known, receipt.known, F('0.9'))
        self.assertEqual((after.primary, after.unaccepted_count), (-2880, 0))
        with self.assertRaisesRegex(Refusal, 'discount_required'):
            value_partition(self.provisional(), (), (), self.now, self.now, None)

    def test_historical_partition_uses_both_clocks(self):
        state, receipt = self.commit()
        for effective, known in ((self.now, receipt.known), (receipt.known, self.now)):
            b = value_partition(self.provisional(), state.accepted, (), effective, known, F(1))
            self.assertEqual(b.unaccepted_count, 2)

    def test_due_equality_and_net_zero_overdue(self):
        state, receipt = self.commit()
        b = value_partition(self.provisional(), state.accepted, (), self.due, self.due, F(1))
        self.assertEqual((b.primary, b.overdue, b.complete), (0, -3200, None))
        offset = replace(receipt, revision=2, amount=F(3200))
        b = value_partition((), (receipt, offset), (), self.due, self.due, F(1))
        self.assertEqual((b.overdue, b.complete), (0, None))

    def test_full_payment_and_later_adjustment_remain_separate(self):
        state, original = self.commit()
        state, correction = self.commit(state, self.command(self.correction(), 1, 'correction'))
        payment = PaymentEvidence((original.occurrence, 1), original.amount, self.due, self.due)
        b = value_partition(self.provisional(), state.accepted, (payment,), self.due, self.due, F(1))
        self.assertEqual((b.primary, b.overdue, b.complete), (0, -32, None))
        self.assertEqual(correction.amount, -32)

    def test_payment_temporal_visibility(self):
        state, original = self.commit()
        payment = PaymentEvidence((original.occurrence, 1), original.amount, self.due, self.due+timedelta(hours=1))
        for effective, known in ((self.due-timedelta(seconds=1), payment.known), (payment.known, self.due)):
            b = value_partition((), state.accepted, (payment,), effective, known, F(1))
            self.assertEqual(b.primary+b.overdue, -3200)
        b = value_partition((), state.accepted, (payment,), payment.known, payment.known, F(1))
        self.assertEqual((b.primary, b.overdue, b.complete), (0, 0, 0))

    def test_invalid_payment_evidence(self):
        state, original = self.commit()
        p = PaymentEvidence((original.occurrence, 1), original.amount, self.due, self.due)
        for bad in (replace(p, amount=F(-1)), replace(p, amount=F(-6400)), replace(p, amount=F(3200)),
                    replace(p, obligation=(original.occurrence, 99))):
            with self.assertRaisesRegex(Refusal, 'full_named_payment'):
                value_partition((), state.accepted, (bad,), self.due, self.due, F(1))
        with self.assertRaisesRegex(Refusal, 'duplicate_payment'):
            value_partition((), state.accepted, (p, p), self.due, self.due, F(1))


if __name__ == '__main__':
    unittest.main()
