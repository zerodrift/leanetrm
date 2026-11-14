"""Explicit synthetic Lifecycle enrollment for the settlement HTTP regressions.

The checks retain their product-native preview/read assertions. Financial writes
use saved review policies and the shared action API; the retired routes remain
separately tested as unavailable to new commands.
"""
from copy import deepcopy
from uuid import NAMESPACE_URL, uuid4, uuid5
import json


SYNTHETIC_SWAP_REVIEW=dict(from_at='2020-01-01T00:00:00Z',until_at='2035-01-01T00:00:00Z',
    rounding_policy='currency_minor_units_to_even_v1',due_policy='swap_due_nominal_v1',
    recognition_policy='swap_completed_period_trading_pnl_v1',correction_policy='swap_explicit_correction_due_v1')


class LifecycleSwapClient:
    def __init__(self, client, *, from_at, until_at, rounding_policy, due_policy, recognition_policy, correction_policy):
        self.client=client
        self.window=(from_at,until_at)
        self.terms=dict(duePolicy=due_policy,recognitionPolicy=recognition_policy,correctionPolicy=correction_policy)
        self.receipts={}
        self.retired_checked=set()
        self.rounding_policy=rounding_policy

    def __getattr__(self, name):return getattr(self.client,name)

    def with_client(self, client):
        return LifecycleSwapClient(client,from_at=self.window[0],until_at=self.window[1],rounding_policy=self.rounding_policy,
            due_policy=self.terms['duePolicy'],recognition_policy=self.terms['recognitionPolicy'],correction_policy=self.terms['correctionPolicy'])

    @staticmethod
    def key(*parts):return str(uuid5(NAMESPACE_URL,'https://preview.invalid/lifecycle-fixture/'+'/'.join(map(str,parts))))

    def view(self, context):
        return self.client.call('lifecycle/view',{**context,'convention':'trading_workspace_v1'})

    def policy(self, action):
        policy_id=self.key(action,*self.window)
        self.client.call('lifecycle/policies/save',dict(operationId=str(uuid4()),policyId=policy_id,expectedRevision=0,action=action,mode='review',
            fromAt=self.window[0],untilAt=self.window[1],implementation='explicit_lifecycle_action_policy_v1'))
        return policy_id

    def configure(self, preview, action, determination=None):
        data=self.view(preview['context']);trade=preview['tradeId']
        rows=[p for p in data['swapOccurrences'] if p['trade_id']==trade]
        if determination:
            inspection=self.client.call('swaps/settlement/inspect',dict(context=preview['context'],tradeId=trade,revision=None,
                duePolicy=self.terms['duePolicy'],recognitionPolicy=self.terms['recognitionPolicy']))
            state=inspection['settlement']
            contribution=next(d for d in state['determinations'] if d['id']==determination)
            occurrence=next(o for o in state['occurrences'] if o['id']==contribution['occurrence_id'])
            rows=[p for p in rows if int(p['membership_ordinal'])==int(occurrence['membership_ordinal']) and
                p['delivery_from']==occurrence['delivery_from'] and p['delivery_to']==occurrence['delivery_to']]
        else:rows=[p for p in rows if int(p['membership_ordinal'])==preview['membershipOrdinal'] and int(p['payment_ordinal'])==preview['paymentOrdinal']]
        if len(rows)!=1:raise AssertionError('Select exactly one native synthetic payment occurrence.')
        row=rows[0]
        retained=[p for p in data['swapProfiles'] if p['trade_id']==trade and p['action']==action and p['occurrence_id']==row['occurrence_id'] and
            all(p[name]==row[name] for name in ('membership_ordinal','payment_ordinal','delivery_from','delivery_to'))]
        head=max(retained,key=lambda p:int(p['revision'])) if retained else None
        policy_id=self.policy(action)
        profile_id=head['id'] if head else self.key(trade,action,row['membership_ordinal'],row['payment_ordinal'],row['delivery_from'],row['delivery_to'])
        self.client.call('lifecycle/swaps/configure',dict(operationId=str(uuid4()),profileId=profile_id,tradeId=trade,
            expectedRevision=int(head['revision']) if head else 0,originRevision=preview['expectedRevision'],action=action,policyId=policy_id,policyRevision=1,
            membershipOrdinal=int(row['membership_ordinal']),paymentOrdinal=int(row['payment_ordinal']),deliveryFrom=row['delivery_from'],deliveryTo=row['delivery_to'],
            referenceCatalogId=preview['currency']['revision']['catalogId'],referenceRevisionId=preview['currency']['revision']['revisionId'],currency=preview['currency']['code'],
            decimalPlaces=preview.get('decimalPlaces',int(row['decimal_places'])),roundingPolicy=preview.get('roundingPolicy',self.rounding_policy),**self.terms))
        return profile_id

    def configure_net(self, preview):
        data=self.view(preview['context']);trade=preview['tradeId']
        retained=[p for p in data.get('swapNetProfiles',[]) if p['trade_id']==trade]
        head=max(retained,key=lambda p:int(p['revision'])) if retained else None
        currencies=[p for p in data['swapOccurrences'] if p['trade_id']==trade and
            (p['reference_catalog_id'],p['reference_revision_id'],p['currency_code'])==
            (preview['currency']['revision']['catalogId'],preview['currency']['revision']['revisionId'],preview['currency']['code'])]
        if not currencies:raise AssertionError('Select an exact native net settlement currency.')
        row=currencies[0];profile_id=head['id'] if head else self.key(trade,'swap.net')
        self.client.call('lifecycle/swaps/net/configure',dict(operationId=str(uuid4()),profileId=profile_id,tradeId=trade,
            expectedRevision=int(head['revision']) if head else 0,originRevision=preview['expectedRevision'],action='swap.net',policyId=self.policy('swap.net'),policyRevision=1,
            referenceCatalogId=row['reference_catalog_id'],referenceRevisionId=row['reference_revision_id'],currency=row['currency_code'],decimalPlaces=int(row['decimal_places']),
            roundingPolicy=self.rounding_policy,netPolicy=preview['policy'],**self.terms))
        return profile_id

    def selection(self, action, body, profile_id):
        preview=body['preview'] if action!='swap.payment' else body
        data=self.view(preview['context'])
        handler='financial_swap_net' if action=='swap.net' else 'financial_swap'
        offer=next(a for a in data['actions'] if a['target']['handler']==handler and a['target']['scopeId']==preview['tradeId'] and
            a['action']==action and a['selection'] and a['selection']['profileId']==profile_id)
        request=dict(operationId=body['operationId'],context=data['context'],target=offer['target'],action=action,
            policyId=offer['policyId'],policyRevision=offer['policyRevision'],automatic=False,previewHash='0'*64,selection=deepcopy(offer['selection']))
        return self.fill(request,action,body)

    @staticmethod
    def fill(request, action, body):
        value=deepcopy(request)
        if action=='swap.determine':value['selection']['determination']={k:deepcopy(v) for k,v in body.items() if k not in ('operationId','previewHash')}
        elif action=='swap.payment':value['selection']['payment']={k:deepcopy(v) for k,v in body.items() if k not in ('operationId','context','tradeId')}
        else:value['selection']['net']={k:deepcopy(v) for k,v in body.items() if k not in ('operationId','previewHash')}
        return value

    def call(self, path, body=None, expected=200, **options):
        try:return self.run(path,body,expected,**options)
        except AssertionError as error:
            # A policy/profile or review may refuse before the final producer.
            # Preserve the actual HTTP code and response from that earlier gate.
            marker=f'expected 200, got {expected}: '
            if expected!=200 and marker in str(error):return json.loads(str(error).split(marker,1)[1])
            raise

    def run(self, path, body=None, expected=200, **options):
        if path not in ('swaps/settlement/preview','swaps/settlement/net-preview','swaps/settlement/determine','swaps/settlement/payment','swaps/settlement/net'):
            return self.client.call(path,body,expected=expected,**options)
        action={'swaps/settlement/determine':'swap.determine','swaps/settlement/payment':'swap.payment','swaps/settlement/net':'swap.net'}.get(path)
        if not isinstance(body,dict):return self.client.call(path,body,expected=expected,**options)
        preview=body.get('preview',body)
        context=preview.get('context') if isinstance(preview,dict) else None
        # Malformed transport and historical writes still exercise their real
        # HTTP admission, before any fixture settings can be recorded.
        if not isinstance(context,dict) or context.get('knowledgeMode')!='current' or context.get('contextId') is not None:
            return self.client.call(path,body,expected=expected,**options)
        operation=body.get('operationId')
        if action and operation in self.receipts:
            original,retained=self.receipts[operation]
            changed=self.fill(retained,action,body)
            if original!=body:changed['previewHash']=body.get('previewHash','f'*64) if body.get('previewHash')!=original.get('previewHash') else 'f'*64
            return self.client.call('lifecycle/execute',changed,expected=expected,**options)
        if action is None:return self.client.call(path,body,expected=expected,**options)
        if expected==200 and path not in self.retired_checked:
            refusal=self.client.call(path,body,expected=422,**options)
            if refusal.get('code')!='lifecycle_configuration_required':raise AssertionError('The retired financial adapter did not require explicit Lifecycle settings.')
            self.retired_checked.add(path)
        if action=='swap.payment':profile=self.configure(preview,action,body['determinationId'])
        elif action=='swap.net':profile=self.configure_net(preview)
        else:profile=self.configure(preview,action)
        request=self.selection(action,body,profile)
        review=self.client.call('lifecycle/preview',request,**options)
        if review['offer'].get('profileId')!=profile:raise AssertionError('The review changed its exact saved swap profile.')
        request['previewHash']=review['hash']
        if action!='swap.payment':
            native_path='swaps/settlement/net-preview' if action=='swap.net' else 'swaps/settlement/preview'
            current=self.client.call(native_path,preview)
            if current['previewHash']!=body['previewHash']:request['previewHash']=body['previewHash']
        outcome=self.client.call('lifecycle/execute',request,expected=expected,**options)
        if expected==200:self.receipts[operation]=(deepcopy(body),deepcopy(request))
        return outcome
