import type {Holding} from './desk-model.js';

// Booked facts are a separate read from valuation. The Blotter reads their first page for the count of positions it is valuing, and never
// draws rows from it; the complete priced response must still describe the same booked bindings.
export type FactsContext={contextId:string;effectiveOn:string;knownAt:string};
export type PositionFact={tradeId:string;bindingId:string;revision:number;tradeReference:string;bookId:string;book:string;
  instrumentType:'future'|'financial_swap'|'heat_rate'|'ppa';marketLabel:string|null;deliveryFrom:string|null;deliveryTo:string|null;
  bookedQuantity:string|null;bookedUnit:string|null};
export type PositionFacts={context:FactsContext;asOf:string;positions:PositionFact[];total:number;nextOffset:number|null};
const invalid='The position facts returned an inconsistent page. Refresh the Blotter.';
const changed='The booked positions changed while values were loading. Refresh the Blotter to read them together.';
const key=(position:{tradeId:string;bindingId:string})=>JSON.stringify([position.tradeId,position.bindingId]);
const sameContext=(left:FactsContext,right:FactsContext)=>left.contextId===right.contextId&&left.effectiveOn===right.effectiveOn&&left.knownAt===right.knownAt;

/** The first page of the booked positions, of at most `limit`, with the total the whole selection holds. */
export function readPositionFacts(value:unknown,limit:number):PositionFacts{
  const page=value as PositionFacts;
  if(!page||typeof page.context?.contextId!=='string'||!page.context.contextId||typeof page.context.effectiveOn!=='string'||
    typeof page.context.knownAt!=='string'||typeof page.asOf!=='string'||!Array.isArray(page.positions)||
    !Number.isInteger(page.total)||page.positions.length>limit||page.positions.length>page.total||
    (page.total>0&&page.positions.length===0)||page.nextOffset!==(page.positions.length<page.total?page.positions.length:null))throw Error(invalid);
  for(const position of page.positions){
    if(!position||['tradeId','bindingId','tradeReference','bookId','book'].some(field=>typeof position[field as keyof PositionFact]!=='string')||
      !Number.isInteger(position.revision)||position.revision<1||!['future','financial_swap','heat_rate','ppa'].includes(position.instrumentType)||
      ['marketLabel','deliveryFrom','deliveryTo','bookedQuantity','bookedUnit'].some(field=>position[field as keyof PositionFact]!==null&&typeof position[field as keyof PositionFact]!=='string'))throw Error(invalid);
  }
  if(new Set(page.positions.map(key)).size!==page.positions.length)throw Error(invalid);
  return page;
}

export function requirePricedFacts(facts:PositionFacts,priced:{context:FactsContext;asOf:string;holdings:{tradeId:string;bindingId:string;revision:string|number}[]},historical:boolean){
  if(priced.asOf!==facts.asOf||historical&&!sameContext(facts.context,priced.context))throw Error(invalid);
  if(priced.holdings.length!==facts.total)throw Error(changed);
  const bindings=new Map(priced.holdings.map(position=>[key(position),String(position.revision)]));
  if(bindings.size!==priced.holdings.length||facts.positions.some(position=>bindings.get(key(position))!==String(position.revision)))throw Error(changed);
}


export type IndividualPosition={context:FactsContext;asOf:string;holding:Holding};
export function readSelectedPosition(value:unknown,context:FactsContext,asOf:string,selected:Pick<PositionFact,'tradeId'|'bindingId'|'revision'|'instrumentType'>):IndividualPosition{
 const response=value as IndividualPosition,holding=response?.holding;
 if(!response?.context||!sameContext(response.context,context)||response.asOf!==asOf||!holding||key(holding)!==key(selected)||
   String(holding.revision)!==String(selected.revision)||holding.instrumentType!==selected.instrumentType)
  throw Error('The individual position does not match its booked facts. Refresh the Blotter.');
 return response;
}
