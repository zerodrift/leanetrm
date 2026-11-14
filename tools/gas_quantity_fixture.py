"""Independent literal principal totals for the owned 6.03 gas fixture."""
import json
from decimal import Decimal
from datetime import datetime


# Independent cumulative eighth-unit values at the fixture's locked 6.03.
# The half-even 9.045 case is retained explicitly rather than recomputed by
# the production rounding helper or a second implementation of its loop.
exact=['0','0.75375','1.5075','2.26125','3.015','3.76875','4.5225','5.27625','6.03','6.78375','7.5375','8.29125','9.045','9.79875','10.5525','11.30625','12.06']
rounded=['0','0.75','1.51','2.26','3.02','3.77','4.52','5.28','6.03','6.78','7.54','8.29','9.04','9.80','10.55','11.31','12.06']

def principal_plan(query,catalog,holding,updates=None,added=None):
    updates={} if updates is None else updates
    rows=json.loads(query(f"SELECT coalesce(jsonb_agg(to_jsonb(p)),'[]') FROM trading.gas_futures_performances_current p WHERE(catalog_id,position_id)=('{catalog}','{holding}');"))
    if added is not None:rows.append(added)
    rows.sort(key=lambda row:(datetime.fromisoformat(row['performed_at'].replace('Z','+00:00')),row['business_reference'].encode('utf-8')))
    owner=json.loads(query(f"SELECT to_jsonb(p) FROM trading.gas_owned_positions p WHERE(catalog_id,id)=('{catalog}','{holding}');"))
    native_link=json.loads(query(f"SELECT to_jsonb(l) FROM settlement_data.futures_delivery_obligations l WHERE(catalog_id,position_id)=('{catalog}','{holding}');"))
    current=json.loads(query(f"SELECT to_jsonb(r) FROM trading.gas_futures_delivery_principal_runs r WHERE(catalog_id,position_id)=('{catalog}','{holding}') ORDER BY revision DESC LIMIT 1;"))
    version=current['revision'] if current else 0
    previous=json.loads(query(f"SELECT coalesce(jsonb_object_agg(performance_id,to_jsonb(d)),'{{}}') FROM trading.gas_futures_delivery_principal_determinations d WHERE(catalog_id,position_id,run_revision)=('{catalog}','{holding}',{version});"))
    source=query(f"SELECT source_observation_id FROM settlement_data.futures_margin_determinations d WHERE(d.catalog_id,d.trade_id,d.binding_id,d.occurrence_id)=('{catalog}','{native_link['trade_id']}','{native_link['binding_id']}','{native_link['final_occurrence_id']}') ORDER BY run_revision DESC LIMIT 1;")
    sign=Decimal(-1) if Decimal(str(owner['quantity']))>0 else Decimal(1)
    units=0;before=Decimal(0);steps=[]
    for row in rows:
        quantity=Decimal(str(updates.get(row['id'],row['quantity'])));parts=quantity/Decimal('.125')
        assert parts==int(parts) and quantity>=0
        units+=int(parts);total=sign*Decimal(rounded[units]);contribution=total-before;old=previous.get(row['id'])
        steps.append(dict(performanceId=row['id'],previousDeterminationId=old['id'] if old else None,cumulativeQuantity=str(-sign*Decimal('.125')*units),
            exactTotal=str(sign*Decimal(exact[units])),roundedTotal=str(total),contribution=str(contribution),adjustment=str(contribution-(Decimal(str(old['contribution'])) if old else 0))))
        before=total
    return dict(implementation='gas_futures_delivery_continuation_v1',positionId=holding,expectedVersion=version,finalOccurrenceId=native_link['final_occurrence_id'],sourceObservationId=source,steps=steps)
