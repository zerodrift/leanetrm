"""Independent resource and source-capacity cases for ordinary gas corrections."""
import json
from datetime import datetime, timezone
from decimal import Decimal
from uuid import uuid4


def checks(query, call, capture, catalog, account, selected, policy):
    at=selected['at']
    preserve='preserve_unallocated_source_quantity_v1'
    withdraw='withdraw_all_source_allocations_v1'
    def literal(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
    def instant(value):return datetime.fromisoformat(value).astimezone(timezone.utc).strftime('%Y-%m-%dT%H:%M:%S.%fZ')
    def record(action,body):
        saved=query(f"SELECT policy_id FROM trading.gas_owned_action_bindings WHERE(catalog_id,account_id,action)=('{catalog}','{account}','gas.owned.{action}') ORDER BY revision DESC LIMIT 1;") if action!='evidence' else None
        return json.loads(call(f"SELECT security.record_gas_owned('{catalog}','{uuid4()}','{action}','{account}',{literal(body)},'{at}','{capture()}',"
            +(f"'{saved}',1" if saved else 'NULL,NULL')+",false)::text;"))
    def evidence(key,kind,quantity,when,revision=0):
        return record('evidence',dict(sourceKey=key,kind=kind,quantity=quantity,occurredAt=when,expectedRevision=revision,evidence='Synthetic corrected observed quantity'))['id']
    def body(rows):return dict(positions=[],consumptions=rows,evidence='Synthetic explicit complete observed resource correction')
    def use(identity,version,lot,quantity,document,allocation=preserve):
        return dict(consumptionId=identity,expectedRevision=version,lotId=lot,quantity=quantity,retracted=Decimal(quantity)==0,
            evidenceId=document,reservationId=None,sourceAllocation=allocation)
    def command(selection,context=None,identity=None):return f"SELECT security.reconcile_gas_owned_quantities('{catalog}','{identity or uuid4()}','{account}',{literal(selection)},'{at}','{context or capture()}','{policy}',1,false)::text;"
    def write(selection,context=None):return json.loads(call(command(selection,context)))
    def refused(statement,codes):
        try:call(statement)
        except RuntimeError as error:assert any(code in str(error) for code in codes),str(error)
        else:raise AssertionError('Ordinary gas correction accepted '+','.join(codes))
    def quantity(lot):return Decimal(query(f"SELECT trim_scale(p.quantity-coalesce((SELECT sum(c.quantity) FROM trading.gas_owned_consumptions_current c WHERE(c.catalog_id,c.lot_id)=(p.catalog_id,p.id)),0)) FROM trading.gas_owned_positions_current p WHERE(p.catalog_id,p.id)=('{catalog}','{lot}');"))
    def publication():return query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")

    # A source revision changes the selected dispatch, never the original
    # event identity. A new command for its old fact only acknowledges history.
    original=json.loads(query(f"SELECT to_jsonb(c)||jsonb_build_object('source_key',e.source_key) FROM trading.gas_owned_consumptions c JOIN trading.gas_owned_evidence e ON(e.catalog_id,e.id)=(c.catalog_id,c.evidence_id) WHERE(c.catalog_id,c.id)=('{catalog}','{selected['dispatch']}');"))
    dispatch_at=instant(original['effective_at'])
    corrected=evidence(original['source_key'],'physical_delivery','0.125',dispatch_at,1)
    old=capture();selection=body([use(original['id'],1,selected['stock'],'0.125',corrected)])
    changed=write(selection)
    assert changed['consumptions'][0]['currentRevision']==2 and quantity(selected['stock'])==Decimal('0.625')
    current=publication()
    acknowledged=write(body([use(original['id'],2,selected['stock'],'0.25',original['evidence_id'])]))
    assert not acknowledged['changed'] and acknowledged['consumptions'][0]['acknowledgedRevision']==1
    assert acknowledged['consumptions'][0]['currentRevision']==2 and publication()==current and quantity(selected['stock'])==Decimal('0.625')
    restored=evidence(original['source_key'],'physical_delivery','0.25',dispatch_at,2)
    # Reusing the pre-correction source cannot amend a later financial head.
    refused(command(body([use(original['id'],2,selected['stock'],'0.25',restored)]),old),['gas_owned_context','gas_quantity_source','gas_quantity_revision'])
    write(body([use(original['id'],2,selected['stock'],'0.25',restored)]))
    assert quantity(selected['stock'])==Decimal('0.5')

    loss_at='2020-12-20T13:00:00Z';loss_key=str(uuid4())
    loss_document=evidence(loss_key,'physical_loss','0.125',loss_at)
    loss=record('loss',dict(lotId=selected['stock'],evidenceId=loss_document,quantity='0.125',reservationId=None,effectiveAt=loss_at))['id']
    smaller_loss=evidence(loss_key,'physical_loss','0.0625',loss_at,1)
    write(body([use(loss,1,selected['stock'],'0.0625',smaller_loss)]))
    assert quantity(selected['stock'])==Decimal('0.4375')

    # One bounded source funds two different exact native uses. Its unused
    # quarter remains unused; correcting only one use cannot close the source.
    title_at='2020-12-19T09:00:00Z';second_title=evidence(str(uuid4()),'title_receipt','0.5',title_at)
    second=record('receive_title',dict(evidenceId=second_title,quantity='0.5',titleAt=title_at,availableAt=title_at,
        originPolicy='opening_owned_stock_v1',carrying='nominal_mark_v1'))['id']
    shared_key=str(uuid4());shared_at='2020-12-20T14:00:00Z';shared=evidence(shared_key,'physical_delivery','0.5',shared_at)
    uses=[record('dispatch',dict(lotId=lot,evidenceId=shared,quantity='0.125',reservationId=None,effectiveAt=shared_at))['id'] for lot in (selected['stock'],second)]
    smaller=evidence(shared_key,'physical_delivery','0.375',shared_at,1)
    complete=body([use(identity,1,lot,'0.0625',smaller) for identity,lot in zip(uses,(selected['stock'],second))])
    current=publication()
    refused(command(body(complete['consumptions'][:1])),['gas_owned_pending_source_reconciliation','gas_quantity_source_scope'])
    assert publication()==current
    accepted=write(complete)
    assert accepted['sourceAllocations'][0]['oldCapacity']=='0.5' and accepted['sourceAllocations'][0]['oldAllocated']=='0.25'
    assert accepted['sourceAllocations'][0]['capacity']=='0.375' and accepted['sourceAllocations'][0]['allocated']=='0.125'
    assert quantity(selected['stock'])==Decimal('0.375') and quantity(second)==Decimal('0.4375')
    zero=evidence(shared_key,'physical_delivery','0',shared_at,2)
    retired=body([use(identity,2,lot,'0',zero,withdraw) for identity,lot in zip(uses,(selected['stock'],second))])
    invalid=body([{**row,'sourceAllocation':preserve} for row in retired['consumptions']])
    current=publication();refused(command(invalid),['gas_source_allocation']);assert publication()==current
    result=write(retired)
    assert all(row['currentRevision']==3 and row['currentQuantity']=='0' for row in result['consumptions'])
    assert quantity(selected['stock'])==Decimal('0.4375') and quantity(second)==Decimal('0.5')
    print('PASS ordinary correction resources: revised dispatch/loss, historical acknowledgement without rewind, captured-head refusal, complete shared source, preserved unused capacity and explicit full withdrawal',flush=True)
    # The original dispatch consumer explicitly accepts source magnitude.
    # Retain that qualified interpretation for an already accepted negative
    # source document instead of silently introducing a positive-sign rule.
    signed_key=str(uuid4());signed_at='2020-12-20T15:00:00Z'
    signed=evidence(signed_key,'physical_delivery','-0.125',signed_at)
    signed_use=record('dispatch',dict(lotId=second,evidenceId=signed,quantity='0.125',reservationId=None,effectiveAt=signed_at))['id']
    signed_correction=evidence(signed_key,'physical_delivery','-0.0625',signed_at,1)
    write(body([use(signed_use,1,second,'0.0625',signed_correction)]))
    assert quantity(second)==Decimal('0.4375')
    print('PASS ordinary correction retains the existing explicit source-magnitude interpretation for negative delivery evidence',flush=True)
