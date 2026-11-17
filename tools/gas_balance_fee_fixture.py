"""Explicit synthetic booking terms for the balance-time fee qualifiers."""
from uuid import uuid4
import json

from gas_park_loan_fixture import literal

PROFILE = 'physical_gas_park_loan_balance_time_v1'


def terms(rate='0.004', tie='to_even', increment='0.01'):
    return dict(rateAmount=rate, basis='accepted_outstanding_principal_v1',
        timeBasis='elapsed_utc_hour_v1', startPolicy='accepted_advance_v1',
        endPolicy='contractual_accrual_cutoff_v1', overduePolicy='accrue_outstanding_until_cutoff_v1',
        projectionPolicy='remaining_return_at_fee_cutoff_v1',
        unadvancedPolicy='unadvanced_plan_from_later_of_earliest_or_evaluation_v1',
        determinationPolicy='rounded_cumulative_difference_v1', accrualThroughAt='2020-12-20T13:00:00Z',
        roundingIncrement=increment, roundingTieRule=tie, roundingStage='payment_total')


def booking(f, owned, claim, book, *, kind='loan', perspective='customer', rate='0.004'):
    return dict(operationId=str(uuid4()), profile=PROFILE, convention='physical_gas_fixed_quantity_v4',
        knowledgeMode='current', contextId=f.capture(), snapshotId=str(uuid4()), bookId=book,
        definitionRevisionId=f.manifest['revisionId'], legalEntityId=f.entity, legalEntityRevision=1,
        counterpartyId=f.party, counterpartyRevision=1, ownedAccountId=owned, claimAccountId=claim,
        serviceKind=kind, perspective=perspective, quantity='720', advanceAt='2020-12-20T10:00:00Z',
        returnAt='2020-12-20T12:00:00Z', fixedFeeAmount='0', feeDirection='pay',
        feeDueAt='2020-12-21T12:00:00Z', titlePolicy='fungible_gas_title_transfer_return_v1',
        returnPolicy='same_quantity_partial_returns_v1', returnTiming='accepted_advance_then_returns_v1',
        carrying='nominal_mark_v1', balanceFee=terms(rate))


class BalanceFeeService:
    """One explicitly configured synthetic agreement and its accepted native facts."""

    def __init__(self, f, label, *, kind='loan', perspective='customer', fee_direction='pay', quantity='720',
                 rate='0.004', tie='to_even', increment='0.01'):
        self.f, self.label, self.quantity = f, label, quantity
        self.direction = 1 if (kind, perspective) in [('loan', 'customer'), ('park', 'provider')] else -1
        self.owned, self.book = f.account(label)
        self.claim = f.account(label + '_claim', kind='operator_balance', book=self.book)[0] if self.direction == -1 else None
        self.policies = {}
        for account, actions in [(self.owned, ['gas.owned.receive_title', 'gas.owned.dispatch', 'gas.loan.borrow', 'gas.loan.repay', 'gas.cash.record_payment']),
                                 (self.claim, ['gas.operator.record', 'gas.operator.settle', 'gas.cash.record_payment'])]:
            if account:
                for action in actions:
                    self.policies[account, action] = f.bind(account, action)
        self.fee_owner = self.owned if self.direction == 1 else self.claim
        f.bind(self.fee_owner, 'gas.park_loan.fee_determine')
        f.at, f.day = '2020-12-19T16:00:00Z', '2020-12-19'
        self.body = booking(f, self.owned, self.claim, self.book, kind=kind, perspective=perspective, rate=rate)
        self.body.update(quantity=quantity, feeDirection=fee_direction, balanceFee=terms(rate, tie, increment))
        self.booked = json.loads(f.call(f"SELECT security.book_gas_park_loan('{f.catalog}',{literal(self.body)},'{f.at}')::text;"))
        self.trade = self.booked['tradeId']
        self.accepted = None

    def read(self, context=None):
        f = self.f; selected = context or f.capture()
        return json.loads(f.call(f"SELECT security.use_market_context('{f.catalog}','{selected}');"
            f"SELECT security.use_trading_context('{f.catalog}','{f.at}');"
            f"SELECT security.read_gas_park_loan('{f.catalog}','{self.trade}','{selected}','{f.at}')::text;"))

    def command(self, action, body, *, operation=None, context=None, preview=False):
        f = self.f
        return json.loads(f.call(f"SELECT security.record_gas_park_loan('{f.catalog}','{operation or uuid4()}',"
            f"'{self.trade}','{action}',{literal(body)},'{f.at}','{context or f.capture()}',{str(preview).lower()})::text;"))

    def advance(self, checkpoint=None):
        f = self.f; f.at, f.day = '2020-12-20T16:00:00Z', '2020-12-20'
        at = self.body['advanceAt']
        agreement = f.evidence(self.owned, self.label + '_agreement', 'loan_agreement', self.quantity, at)
        title = f.evidence(self.owned, self.label + '_advance', 'title_receipt' if self.direction == 1 else 'physical_delivery', self.quantity, at)
        claim = f.evidence(self.claim, self.label + '_claim', 'operator_statement', self.quantity, at) if self.claim else None
        lot = None
        if self.direction == -1:
            evidence = f.evidence(self.owned, self.label + '_funding', 'title_receipt', self.quantity, '2020-12-19T08:00:00Z')
            lot = f.native(self.owned, 'receive_title', dict(evidenceId=evidence, quantity=self.quantity,
                titleAt='2020-12-19T08:00:00Z', availableAt='2020-12-19T08:00:00Z',
                originPolicy='opening_owned_stock_v1', carrying='nominal_mark_v1'),
                policy=self.policies[self.owned, 'gas.owned.receive_title'])['id']
        if checkpoint:
            f.at = at
            checkpoint('before')
        self.accepted = self.command('advance', dict(expectedVersion=self.read()['stateVersion'], agreementEvidenceId=agreement,
            evidenceId=title, claimEvidenceId=claim, lotId=lot, reservationId=None, effectiveAt=at))
        if checkpoint:
            checkpoint('after')
            f.at = '2020-12-20T16:00:00Z'
        return self.accepted

    def returned(self, quantity, at, key):
        evidence = self.f.evidence(self.owned, self.label + '_' + key, 'loan_return' if self.direction == 1 else 'title_receipt', quantity, at)
        return self.command('return', dict(expectedVersion=self.read()['stateVersion'], businessKey=key, evidenceId=evidence,
            lotId=self.accepted['stockId'] if self.direction == 1 else None, reservationId=None, quantity=quantity, effectiveAt=at))

    def selection(self, through, key, *, effective=None):
        state = self.read(); rows = state['fee']['determinations']
        return dict(expectedVersion=state['stateVersion'], previousDeterminationId=rows[-1]['id'] if rows else None,
            through=through, effectiveAt=effective or through, businessKey=key, evidence='Synthetic accepted cumulative fee')
