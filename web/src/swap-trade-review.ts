import {renderRegularTable} from './regular-table.js';
import type {Valuation} from './swap-editor.js';
type Side=Valuation['sides'][number];
type Quote={currency:string;unit:string;quoteMultiplier:string};
type QuoteLabel=Quote&{currencyCode:string;quantityCode:string};
export function swapPaymentQuote(side:Side,quotes:QuoteLabel[]):Quote{
 const price=side.payments[0].resets[0];
 return {currency:quotes.find(q=>q.currencyCode===price.currency.code)?.currency??price.currency.code,
  unit:quotes.find(q=>q.quantityCode===price.quantity.code)?.unit??price.quantity.code,quoteMultiplier:price.multiplier};
}
type Format=(value:string,kind:'quantity'|'money'|'factor'|'price',side:string)=>string;
export function renderSwapTradePayments(section:HTMLElement,prefix:string,side:Side,i:Quote|undefined,busy:()=>boolean,format:Format=(value)=>value){
 const display={text:(value:string,kind:'quantity'|'money'|'factor'|'price',selected=side.kind)=>format(value,kind,selected)};
   const direction=side.coefficient==='-1'?'Pay':'Receive',primary=document.createElement('div');primary.className='table-wrap swap-payment-table';section.append(primary);
   renderRegularTable(primary,`${prefix}-${side.slot}-trade-payments`,JSON.stringify(side),
    ['Delivery start','Delivery end · exclusive','Total quantity','Unit','Direction','Payment date',`Signed cash · ${i?.currency}`,'DF',`Trade PV · ${i?.currency}`],
    side.payments.map(p=>[p.deliveryFrom,p.deliveryTo,p.resolvedQuantity,i?.unit??'',direction,p.paymentDate,p.tradeCashAmount,p.discountFactor,p.tradePresentValue]),[2,6,7,8],busy,
    {displayRows:side.payments.map(p=>[p.deliveryFrom,p.deliveryTo,display.text(p.resolvedQuantity,'quantity'),i?.unit??'',direction,p.paymentDate,display.text(p.tradeCashAmount,'money',side.kind),display.text(p.discountFactor,'factor',side.kind),display.text(p.tradePresentValue,'money',side.kind)])});
   const compact=document.createElement('div');compact.className='swap-payment-summary';
   for(const p of side.payments){const row=document.createElement('p');row.textContent=`${p.deliveryFrom} to ${p.deliveryTo} (exclusive) · total ${display.text(p.resolvedQuantity,'quantity')} ${i?.unit}. ${direction} · signed cash ${display.text(p.tradeCashAmount,'money',side.kind)} ${i?.currency} on ${p.paymentDate}. Discount factor ${display.text(p.discountFactor,'factor',side.kind)} · PV ${display.text(p.tradePresentValue,'money',side.kind)} ${i?.currency}.`;compact.append(row);}section.append(compact);
   if(side.kind==='fixed'&&new Set(side.payments.flatMap(p=>p.resets.map(r=>r.price))).size===1){const price=document.createElement('p');price.className='field-help';price.textContent=`Contractual fixed price: ${display.text(side.payments[0].resets[0].price,'price',side.kind)} ${i?.currency}/${i?.unit}${i?.quoteMultiplier==='1'?'':' × '+i?.quoteMultiplier}`;section.append(price);}
}
