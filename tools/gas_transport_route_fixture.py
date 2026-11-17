"""Synthetic same-pipeline capacity sources for retained route qualification."""
import json
from datetime import datetime, timedelta
from fractions import Fraction
from uuid import uuid4

from gas_transport_capacity_fixture import TransportCapacityFixture, fixture as capacity_fixture, identity, literal


def fixture(root):
    reference,market,access,manifest=capacity_fixture(root)
    pins=f"'{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}'"
    rows=[]
    for key in ['a','h','b','c','m','d']:
        point='synthetic_route_point_'+key
        rows.extend([f"INSERT INTO ref_data.locations(catalog_id,revision_id,code,name,type_code,clock_code) VALUES({pins},'{point}','Synthetic route point {key.upper()}','test_delivery_point','test_utc');",
            f"INSERT INTO ref_data.commodity_locations VALUES({pins},'test_gas','{point}');",
            f"INSERT INTO ref_data.pipeline_delivery_points VALUES({pins},'test_pipeline_a','synthetic_gas_zone_a','{point}');",
            f"INSERT INTO ref_data.gas_delivery_conventions VALUES({pins},'test_pipeline_a','synthetic_gas_zone_a','{point}','test_gas','test_utc',0);"])
    marker='UPDATE ref_data.revisions SET'
    assert market.count(marker)==1
    return reference,market.replace(marker,'\n'.join(rows)+'\n'+marker,1),access,manifest


class RouteFixture(TransportCapacityFixture):
    @staticmethod
    def instant(value):
        if isinstance(value,str):value=datetime.fromisoformat(value.replace('Z','+00:00'))
        return value.isoformat(timespec='microseconds').replace('+00:00','Z')

    @staticmethod
    def ratio(value):
        value=Fraction(value)
        return dict(n=str(value.numerator),d=str(value.denominator))

    def capacity_witness(self,facts,claims):
        """Independent exact interval oracle over explicit synthetic sources."""
        rows=[]
        paths={p['id']:p for p in facts['paths']}
        scopes=[('agreement',None,None,facts['agreement'])]
        scopes += [('route',None,p['id'],p['limits']) for p in facts['paths']]
        scopes += [('receipt',p['point_code'],None,p['limits']) for p in facts['receipts']]
        scopes += [('delivery',p['point_code'],None,p['limits']) for p in facts['deliveries']]
        for axis,point,path,limits in scopes:
            selected=[c for c in claims if axis=='agreement' or axis=='route' and c['pathId']==path or
                axis=='receipt' and paths[c['pathId']]['receipt_point_code']==point or axis=='delivery' and paths[c['pathId']]['delivery_point_code']==point]
            side='delivery' if axis=='delivery' else 'receipt'
            boundaries=sorted({self.instant(l[k]) for l in limits for k in ['from','through']} |
                {self.instant(c[side+k]) for c in selected for k in ['From','To']})
            for start,end in zip(boundaries,boundaries[1:]):
                active=[c for c in selected if self.instant(c[side+'From'])<=start and self.instant(c[side+'To'])>=end]
                used=sum((Fraction(c[side+'Quantity'])*3600000000/self.micros(c[side+'From'],c[side+'To']) for c in active),Fraction())
                limit=next(Fraction(l['maximum_per_hour']) for l in limits if self.instant(l['from'])<=start and self.instant(l['through'])>=end)
                rows.append(dict(axis=axis,point=point,pathId=path,**{'from':start},through=end,claims=sorted(c['claimId'] for c in active),used=self.ratio(used),maximum=self.ratio(limit),withinLimit=used<=limit))
        return dict(implementation='gas_transport_capacity_witness_v1',versionId=facts['version']['id'],admissionPolicy='complete_feasible_replacement_v1',intervals=rows)

    @staticmethod
    def micros(start,end):
        delta=datetime.fromisoformat(end.replace('Z','+00:00'))-datetime.fromisoformat(start.replace('Z','+00:00'))
        return (delta.days*86400+delta.seconds)*1000000+delta.microseconds

    def source_plans(self):
        if hasattr(self,'_source_plans'):return self._source_plans
        # Literal independent physical route:100 receipts,2 fuel,98 through the
        #60/38 split and merge; the direct H→M path is explicitly idle.
        stages={'ah':(0,1,'100',1,2,'98'),'hb':(1,2,'60',1,2,'60'),'hc':(1,2,'38',1,2,'38'),
            'hm':(1,2,'0',2,3,'0'),'bm':(1,2,'60',2,3,'60'),'cm':(1,2,'38',2,3,'38'),'md':(2,3,'98',2,3,'98')}
        result=[]
        for facts in self.capacities():
            operation=str(uuid4());claims=[]
            for path in facts['paths']:
                a,b,q,c,d,r=stages[path['path_key']]
                claims.append(dict(pathId=path['id'],receiptFrom=f'2020-12-20T0{a}:00:00.000000Z',receiptTo=f'2020-12-20T0{b}:00:00.000000Z',receiptQuantity=q,
                    deliveryFrom=f'2020-12-20T0{c}:00:00.000000Z',deliveryTo=f'2020-12-20T0{d}:00:00.000000Z',deliveryQuantity=r))
            claims.sort(key=lambda c:json.dumps(c,separators=(',',':')))
            for ordinal,claim in enumerate(claims):claim['claimId']=identity('gas_transport_claim_v1',operation,ordinal)
            claims.sort(key=lambda c:(c['pathId'],c['claimId']))
            body=dict(kind='plan',entitlementId=facts['entitlement']['id'],expectedStateVersion=facts['stateVersion'],expectedPreviousId=None,
                businessKey='synthetic_complete_capacity_source',versionId=facts['version']['id'],admissionPolicy='complete_feasible_replacement_v1',evidence='Synthetic explicit complete capacity source',claims=claims)
            self.record(body,self.capacity_witness(facts,claims),operation=operation)
            result.append(self.read(facts['entitlement']['id']))
        self._source_plans=result
        return result

    def replace_capacity(self,facts,*,evidence='Synthetic revised capacity-source evidence',effective=None,context=None):
        operation=str(uuid4());head=facts['plans'][0]
        claims=[dict(pathId=c['path_id'],receiptFrom=self.instant(c['receipt_from']),receiptTo=self.instant(c['receipt_to']),receiptQuantity=c['receipt_quantity'],
            deliveryFrom=self.instant(c['delivery_from']),deliveryTo=self.instant(c['delivery_to']),deliveryQuantity=c['delivery_quantity']) for c in facts['claims'] if c['plan_id']==head['id']]
        claims.sort(key=lambda c:json.dumps(c,separators=(',',':')))
        for ordinal,claim in enumerate(claims):claim['claimId']=identity('gas_transport_claim_v1',operation,ordinal)
        claims.sort(key=lambda c:(c['pathId'],c['claimId']))
        body=dict(kind='plan',entitlementId=facts['entitlement']['id'],expectedStateVersion=facts['stateVersion'],expectedPreviousId=head['id'],businessKey=head['business_key'],versionId=facts['version']['id'],
            admissionPolicy='complete_feasible_replacement_v1',evidence=evidence,claims=claims)
        outcome=self.record(body,self.capacity_witness(facts,claims),operation=operation,effective=effective,context=context)
        return body,outcome

    def add_capacity_stream(self,facts,key):
        operation=str(uuid4());head=facts['plans'][0]
        existing=[dict(claimId=c['id'],pathId=c['path_id'],receiptFrom=self.instant(c['receipt_from']),receiptTo=self.instant(c['receipt_to']),receiptQuantity=c['receipt_quantity'],
            deliveryFrom=self.instant(c['delivery_from']),deliveryTo=self.instant(c['delivery_to']),deliveryQuantity=c['delivery_quantity']) for c in facts['claims']]
        claims=[{k:v for k,v in c.items() if k!='claimId'} for c in existing if next(n for n in facts['claims'] if n['id']==c['claimId'])['plan_id']==head['id']]
        claims.sort(key=lambda c:json.dumps(c,separators=(',',':')))
        for ordinal,claim in enumerate(claims):claim['claimId']=identity('gas_transport_claim_v1',operation,ordinal)
        claims.sort(key=lambda c:(c['pathId'],c['claimId']))
        body=dict(kind='plan',entitlementId=facts['entitlement']['id'],expectedStateVersion=facts['stateVersion'],expectedPreviousId=None,businessKey=key,versionId=facts['version']['id'],
            admissionPolicy='complete_feasible_replacement_v1',evidence='Synthetic second independent capacity stream',claims=claims)
        return self.record(body,self.capacity_witness(facts,existing+claims),operation=operation)

    def route_plan(self,facts,key='synthetic_route_chosen',*,source_key=None):
        stream=identity('gas_transport_route_plan_v1:'+key,facts['route']['id'])
        sources=[];source_plans=[];flows=[]
        for capacity in facts['sourceCapacities']:
            sources.append(dict(entitlementId=capacity['entitlement']['id'],capacityVersionId=capacity['version']['id'],capacityGeneration=capacity['stateVersion']))
            selected_plan_ids={p['id'] for p in capacity['plans'] if source_key is None or p['business_key']==source_key}
            for descriptor in capacity['sourcePlans']:
                if descriptor['planRevisionId'] not in selected_plan_ids:continue
                source_plans.append(dict(entitlementId=capacity['entitlement']['id'],firstPlanRevisionId=descriptor['firstPlanRevisionId'],planRevisionId=descriptor['planRevisionId'],expectedMembershipGeneration=descriptor['membershipGeneration']))
            for claim in capacity['claims']:
                if claim['plan_id'] not in selected_plan_ids:continue
                segment=next(s for s in facts['segments'] if s['path_id']==claim['path_id'] and s['entitlement_id']==claim['entitlement_id'])
                flows.append(dict(segmentId=segment['id'],claimId=claim['id'],flowId=identity('gas_transport_route_flow_v1:'+segment['id'],stream)))
        previous=next((p['id'] for p in facts['plans'] if p['business_key']==key),None)
        return dict(kind='plan',routeId=facts['route']['id'],definitionId=facts['definition']['id'],expectedStateVersion=facts['stateVersion'],expectedPreviousId=previous,businessKey=key,
            admissionPolicy='complete_current_sources_and_conserving_route_v1',sources=sorted(sources,key=lambda s:s['entitlementId']),
            sourcePlans=sorted(source_plans,key=lambda s:(s['entitlementId'],s['firstPlanRevisionId'])),flows=sorted(flows,key=lambda f:f['segmentId']),evidence='Synthetic conserving whole-stream route adoption')

    def route_witness(self,facts,command,operation):
        capacities=[];segments=[];nodes=[];stages={}
        claims={c['id']:c for s in facts['sourceCapacities'] for c in s['claims']}
        for source in sorted(facts['sourceCapacities'],key=lambda s:s['entitlement']['id']):
            native=[dict(claimId=c['id'],pathId=c['path_id'],receiptFrom=c['receipt_from'],receiptTo=c['receipt_to'],receiptQuantity=c['receipt_quantity'],
                deliveryFrom=c['delivery_from'],deliveryTo=c['delivery_to'],deliveryQuantity=c['delivery_quantity']) for c in source['claims']]
            capacities.append(dict(entitlementId=source['entitlement']['id'],capacityVersionId=source['version']['id'],capacityGeneration=source['stateVersion'],assessment=self.capacity_witness(source,native)))
        def dt(value):return datetime.fromisoformat(value.replace('Z','+00:00'))
        def rate(quantity,start,end):return quantity*3600000000/self.micros(start,end)
        for flow in sorted(command['flows'],key=lambda f:f['segmentId']):
            segment=next(s for s in facts['segments'] if s['id']==flow['segmentId']);claim=claims[flow['claimId']]
            receipt=Fraction(claim['receipt_quantity']);delivery=Fraction(claim['delivery_quantity']);fuel=receipt*Fraction(segment['fuel_n'])/Fraction(segment['fuel_d']);loss=receipt*Fraction(segment['loss_n'])/Fraction(segment['loss_d']);remaining=receipt-fuel-loss
            start=self.instant(dt(claim['receipt_from'])+timedelta(microseconds=int(segment['transit_microseconds'])));end=self.instant(dt(claim['receipt_to'])+timedelta(microseconds=int(segment['transit_microseconds'])))
            actual_start=self.instant(claim['delivery_from']);actual_end=self.instant(claim['delivery_to']);intervals=[]
            bounds=sorted({start,end,actual_start,actual_end})
            for a,b in zip(bounds,bounds[1:]):
                expected=rate(remaining,start,end) if start<=a and end>=b else Fraction();actual=rate(delivery,actual_start,actual_end) if actual_start<=a and actual_end>=b else Fraction()
                intervals.append(dict(**{'from':a},through=b,expectedPerHour=self.ratio(expected),deliveredPerHour=self.ratio(actual),residualPerHour=self.ratio(actual-expected),conserved=actual==expected))
            timing=(start,end)==(actual_start,actual_end)
            segments.append(dict(**flow,receiptQuantity=self.ratio(receipt),fuelQuantity=self.ratio(fuel),lossQuantity=self.ratio(loss),expectedDeliveryQuantity=self.ratio(remaining),deliveryQuantity=self.ratio(delivery),timingAligned=timing,conserved=timing and all(i['conserved'] for i in intervals),intervals=intervals))
            stages[segment['id']]=(flow,claim,segment)
        for node in sorted((n for n in facts['nodes'] if n['role']=='no_storage_transit_v1'),key=lambda n:n['id']):
            relevant=[s for s in stages.values() if node['id'] in (s[2]['from_node_id'],s[2]['to_node_id'])];bounds=set()
            for _,claim,segment in relevant:
                side='delivery' if segment['to_node_id']==node['id'] else 'receipt';bounds.update(self.instant(claim[side+'_'+k]) for k in ['from','to'])
            intervals=[];bounds=sorted(bounds)
            for a,b in zip(bounds,bounds[1:]):
                incoming=[];outgoing=[];total_in=Fraction();total_out=Fraction()
                for flow,claim,segment in sorted(relevant,key=lambda s:s[2]['id']):
                    is_in=segment['to_node_id']==node['id'];side='delivery' if is_in else 'receipt';start=self.instant(claim[side+'_from']);end=self.instant(claim[side+'_to'])
                    if start<=a and end>=b:
                        (incoming if is_in else outgoing).append(flow)
                        value=rate(Fraction(claim[side+'_quantity']),start,end)
                        if is_in:total_in+=value
                        else:total_out+=value
                intervals.append(dict(**{'from':a},through=b,incoming=incoming,outgoing=outgoing,incomingPerHour=self.ratio(total_in),outgoingPerHour=self.ratio(total_out),residualPerHour=self.ratio(total_in-total_out),conserved=total_in==total_out))
            nodes.append(dict(nodeId=node['id'],conserved=all(i['conserved'] for i in intervals),intervals=intervals))
        return dict(implementation='gas_transport_route_witness_v1',definitionId=facts['definition']['id'],routePlanId=identity('gas_transport_route_plan_v1:'+command['businessKey'],facts['route']['id']),revisionId=operation,
            feasible=all(s['conserved'] for s in segments) and all(n['conserved'] for n in nodes),capacities=capacities,segments=segments,nodes=nodes)
    def capacities(self):
        if hasattr(self,'_capacities'):return self._capacities
        groups=[('synthetic_route_entry',[('ah','a','h')]),('synthetic_route_split',[('hb','h','b'),('hc','h','c'),('hm','h','m')]),
            ('synthetic_route_merge',[('bm','b','m'),('cm','c','m')]),('synthetic_route_terminal',[('md','m','d')])]
        result=[]
        for index,(key,paths) in enumerate(groups):
            definition=self.definition(key)
            window=dict(**{'from':'2020-12-20T00:00:00Z'},through='2020-12-20T03:00:00Z',maximumPerHour='200')
            entitlement=definition['entitlementId']
            definition.update(through=window['through'],agreement=[dict(window)],paths=[],receipts=[],deliveries=[])
            if index%2:definition['counterpartyId']='d5000000-0000-0000-0000-000000000002'
            for path_key,start,end in paths:
                definition['paths'].append(dict(pathKey=path_key,pathId=identity('gas_transport_path_v1:'+path_key,entitlement),pipeline='test_pipeline_a',
                    receiptZone='synthetic_gas_zone_a',receiptPoint='synthetic_route_point_'+start,deliveryZone='synthetic_gas_zone_a',
                    deliveryPoint='synthetic_route_point_'+end,product='test_gas',limits=[dict(window)]))
            for field,position in [('receipts',1),('deliveries',2)]:
                definition[field]=[dict(point='synthetic_route_point_'+point,limits=[dict(window)]) for point in sorted({p[position] for p in paths})]
            definition['paths'].sort(key=lambda p:p['pathId'])
            operation=str(uuid4());self.record(definition,operation=operation)
            result.append(self.read(entitlement))
        self._capacities=result
        return result

    def route_definition(self,key='synthetic_retained_route'):
        capacities=self.capacities()
        route=identity('gas_transport_route_v1:'+key,self.book)
        nodes=[]
        for node in ['a','h','b','c','m','d']:
            nodes.append(dict(nodeKey=node,nodeId=identity('gas_transport_route_node_v1:'+node,route),
                role='external_receipt_v1' if node=='a' else 'external_delivery_v1' if node=='d' else 'no_storage_transit_v1',
                pipeline='test_pipeline_a',zone='synthetic_gas_zone_a',point='synthetic_route_point_'+node))
        segments=[]
        for capacity in capacities:
            for path in capacity['paths']:
                segment=path['path_key'];start=path['receipt_point_code'].rsplit('_',1)[1];end=path['delivery_point_code'].rsplit('_',1)[1]
                segments.append(dict(segmentKey=segment,segmentId=identity('gas_transport_route_segment_v1:'+segment,route),
                    entitlementId=capacity['entitlement']['id'],pathId=path['id'],fromNodeKey=start,toNodeKey=end,
                    fromNodeId=identity('gas_transport_route_node_v1:'+start,route),toNodeId=identity('gas_transport_route_node_v1:'+end,route),
                    fuelFraction=dict(n='1' if segment=='ah' else '0',d='50' if segment=='ah' else '1'),lossFraction=dict(n='0',d='1'),
                    transitMicroseconds='3600000000' if segment in ['ah','bm','cm','hm'] else '0'))
        return dict(kind='define',routeId=route,expectedStateVersion=0,expectedPreviousId=None,businessKey=key,bookId=self.book,
            legalEntityId='d4000000-0000-0000-0000-000000000001',legalEntityRevision=1,
            referenceCatalogId=self.manifest['referenceCatalogId'],referenceRevisionId=self.manifest['referenceRevisionId'],product='test_gas',quantity='test_gas_quantity',
            implementation='gas_transport_chosen_uniform_route_v1',fractionBasis='fuel_and_loss_each_fraction_of_gross_receipt_v1',
            flowShape='one_explicit_uniform_receipt_and_delivery_per_segment_v1',transitLaw='fixed_nonnegative_elapsed_microseconds_v1',
            nodeBalanceLaw='simultaneous_inflow_equals_outflow_no_storage_v1',junctionPolicy='same_exact_pipeline_point_handoff_v1',
            membershipPolicy='whole_capacity_plan_stream_exclusive_v1',nodes=sorted(nodes,key=lambda n:n['nodeId']),segments=sorted(segments,key=lambda s:s['segmentId']),
            evidence='Synthetic declared route over exact native commercial paths')

    def route_record(self,body,witness=None,*,operation=None,context=None,effective=None):
        return json.loads(self.call(f"SELECT security.record_gas_transport_route('{self.catalog}','{operation or uuid4()}',{literal(body)},"
            f"'{effective or self.effective}',{'NULL' if witness is None else literal(witness)},'{context or self.capture()}')::text;"))

    def route_read(self,route,*,context=None):
        return json.loads(self.call(f"SELECT security.use_market_context('{self.catalog}','{context or self.capture()}');"
            f"SELECT security.use_trading_context('{self.catalog}','{self.at}');SELECT security.read_gas_transport_route('{self.catalog}','{route}')::text;"))
