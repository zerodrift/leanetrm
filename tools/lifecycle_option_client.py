"""Explicit synthetic option enrolment for shared Lifecycle HTTP checks."""
from copy import deepcopy
from datetime import datetime,timedelta,timezone
import json
from uuid import uuid4
from futures_delivery_fixture import configure


class LifecycleOptionClient:
    def __init__(self,client,query,manifest,*,issuer,context,as_of,business_date,from_at,until_at,mark_curve,carrying):
        self.client,self.query,self.manifest=client,query,manifest
        selected_at=datetime.fromisoformat(as_of.replace('Z','+00:00'))
        if selected_at.tzinfo is None:raise AssertionError('Select an explicitly zoned fixture instant.')
        self.context,self.as_of,self.business_date=context,selected_at.astimezone(timezone.utc).isoformat(timespec='microseconds').replace('+00:00','Z'),business_date
        self.window=(from_at,until_at);self.mark_curve,self.carrying=mark_curve,carrying
        self.issuer=issuer;self.policies={};self.margin={};self.targets={};self.receipts={};self.retired=set()

    def __getattr__(self,name):return getattr(self.client,name)
    @staticmethod
    def lit(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
    def native(self,sql,profile='trader'):
        m=self.manifest
        token=self.query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
            f"'{self.issuer}','{profile}','human_interactive','{m['tenantId']}','{m['workspaceId']}',NULL,'preview_runtime',60);")
        return self.query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}COMMIT;")
    def capture(self):return self.native(f"SELECT security.capture_date_context('{self.manifest['catalogId']}','{self.business_date}',NULL);")
    def enroll(self,trade):
        m=self.manifest;catalog=m['catalogId']
        source=json.loads(self.query("SELECT jsonb_build_object('book',r.book_id,'entity',r.legal_entity_id,'entityRevision',r.legal_entity_revision,"
            "'instrument',r.option_instrument_id,'contract',f.id,'from',f.delivery_from,'to',f.delivery_to,'final',to_char(f.expiry_at AT TIME ZONE 'UTC','YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"')) "
            "FROM trading.futures_option_trade_revisions r JOIN market_data.contracts o ON(o.catalog_id,o.revision_id,o.id)=(r.catalog_id,r.definition_revision_id,r.contract_id) "
            "JOIN ref_data.futures_option_series s ON(s.catalog_id,s.revision_id,s.code)=(o.reference_catalog_id,o.reference_revision_id,o.futures_option_series_code) "
            "JOIN market_data.contracts f ON(f.catalog_id,f.revision_id,f.futures_series_code)=(r.catalog_id,r.definition_revision_id,s.underlying_series_code) "
            f"WHERE(r.catalog_id,r.trade_id,r.revision)=('{catalog}','{trade}',1);"))
        key=(source['contract'],source['book'],source['entity'],source['entityRevision'])
        if key not in self.margin:
            mark=self.query(f"SELECT c.id FROM market_data.contracts c JOIN market_data.curve_members cm ON(cm.catalog_id,cm.revision_id,cm.contract_id)=(c.catalog_id,c.revision_id,c.id) WHERE(c.catalog_id,c.revision_id,cm.curve_code)=('{catalog}','{m['revisionId']}','{self.mark_curve}') ORDER BY c.expiry_at DESC LIMIT 1;")
            due=(datetime.strptime(source['to'],'%Y-%m-%d').replace(tzinfo=timezone.utc)+timedelta(days=1,hours=12)).isoformat().replace('+00:00','Z')
            template=configure(self.query,m,self.native,self.capture,curve=self.mark_curve,contract=source['contract'],book=source['book'],entity=source['entity'],entity_revision=source['entityRevision'],
                mark_contract=mark,account_code='synthetic_option_'+uuid4().hex,account_effective_at=self.window[0],as_of=self.as_of,
                delivery_from=source['from']+'T00:00:00Z',delivery_to=source['to']+'T00:00:00Z',cash_due=due,carrying=self.carrying,policy_from=self.window[0],policy_until=self.window[1])
            margin=dict(id=str(uuid4()),definitionRevisionId=m['revisionId'],contractId=source['contract'],settlementCurve=self.mark_curve,referenceCatalogId=m['referenceCatalogId'],referenceRevisionId=m['referenceRevisionId'],
                currency='usd',fromAt=self.window[0],untilAt=self.window[1],opening='include_opening_instant',increment='0.01',tie='to_even',stage='payment_total',roundingScope='held_binding_difference_payment_total_v1',
                implementation='dated_futures_margin_policy_v2',delivery=dict(id=template['profile']['id'],revision=1),occurrences=[dict(id=str(uuid4()),at=source['final'],dueAt=source['final'],phase='final')])
            self.native(f"SELECT settlement_data.save_futures_margin_policy('{catalog}',{self.lit(dict(operationId=str(uuid4()),expectedRevision=0,contextId=self.capture(),asOf=self.as_of,policy=margin))});",'editor')
            self.margin[key]=margin['id']
        self.targets[trade]=dict(handler='listed_option',scopeId=trade,bindingId=source['instrument'])
        for action in ('option.exercise','option.assignment','option.expiry','option.exercise_instruction','option.abandon_instruction'):
            if action not in self.policies:
                policy=str(uuid4());self.policies[action]=policy
                self.client.call('lifecycle/policies/save',dict(operationId=str(uuid4()),policyId=policy,expectedRevision=0,action=action,mode='review',fromAt=self.window[0],untilAt=self.window[1],implementation='explicit_lifecycle_action_policy_v1'))
            self.client.call('lifecycle/options/configure',dict(operationId=str(uuid4()),tradeId=trade,action=action,expectedRevision=0,policyId=self.policies[action],policyRevision=1,
                marginPolicyId=self.margin[key],marginPolicyRevision=1,futureMarkCurve=self.mark_curve,expiryCurve=self.mark_curve if action=='option.expiry' else None))
    def request(self,path,body):
        action={'exercise':'option.exercise','assignment':'option.assignment','expiry':'option.expiry'}.get(path)
        if path=='instruction':action='option.'+str(body.get('instruction'))+'_instruction'
        trade=body['tradeId'];target=self.targets.get(trade,dict(handler='listed_option',scopeId=trade,bindingId=str(uuid4())))
        data=self.client.call('lifecycle/view',dict(snapshotId=body['snapshotId'],knowledgeMode=body['knowledgeMode'],convention='trading_workspace_v1'))
        version=int(self.query(f"SELECT count(*) FROM trading.futures_option_events WHERE(catalog_id,trade_id)=('{self.manifest['catalogId']}','{trade}');"))
        return dict(operationId=body['operationId'],context=data['context'],target=target,action=action,policyId=self.policies.get(action,self.policies['option.exercise']),policyRevision=1,
            automatic=False,previewHash='0'*64,selection=dict(evidenceId=trade if path=='expiry' else body['operationId'],lots=body.get('lots'),expectedEventVersion=version,profileRevision=1))
    def acknowledge(self,original,operation):
        retained=deepcopy(self.receipts[original][1]);retained['operationId']=operation
        return self.client.call('lifecycle/execute',retained)
    def call(self,path,body=None,expected=200,**options):
        if path=='options/book':
            result=self.client.call(path,body,expected=expected,**options)
            if expected==200:self.enroll(result['tradeId'])
            return result
        action=path.removeprefix('options/')
        if action not in ('exercise','assignment','expiry','instruction'):return self.client.call(path,body,expected=expected,**options)
        operation=body['operationId']
        if operation in self.receipts:
            original,request=self.receipts[operation];request=deepcopy(request)
            if original!=body:
                request['selection']['lots']=body.get('lots');request['previewHash']='f'*64
            return self.client.call('lifecycle/execute',request,expected=expected,**options)
        if expected==200 and action not in self.retired:
            assert self.client.call(path,body,expected=422)['code']=='lifecycle_configuration_required';self.retired.add(action)
        try:
            request=self.request(action,body)
            review=self.client.call('lifecycle/preview',request)
            request['previewHash']=review['hash']
            result=self.client.call('lifecycle/execute',request,expected=expected,**options)
        except AssertionError as error:
            marker=f'expected 200, got {expected}: '
            if expected!=200 and marker in str(error):return json.loads(str(error).split(marker,1)[1])
            raise
        if expected==200:self.receipts[operation]=(deepcopy(body),deepcopy(request))
        return result
