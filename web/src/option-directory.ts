import {friendly,publicText} from './market-view.js';

export function element<K extends keyof HTMLElementTagNameMap>(tag:K,className?:string,text?:string){
 const node=document.createElement(tag);
 if(className)node.className=className;
 if(text!==undefined)node.textContent=publicText(text);
 return node;
}
export function action(className:string,label:string,id?:string){
 const button=element('button',className,label);button.type='button';
 if(id)button.id=id;
 return button;
}

type Row=Record<string,string|number|null>;
type ProductPage={definitionRevisionId:string;rows:Row[];references:Record<string,Row[]>;offset:number;pageSize:number;hasMore:boolean};
type Product={code:string;name:string;commodity:string;market:string;exchange:string;underlying:string;underlyingCode:string};
type Api=<T>(path:string,body?:unknown,signal?:AbortSignal)=>Promise<T>;

// Resolve labels from the relationships on the same reference page, including subsequent pages of a large directory.
export async function readOptionProducts(api:Api,snapshotId:string,signal:AbortSignal,pinned?:string){
 const products:Product[]=[];
 const params=new URLSearchParams({table:'futures_option_products',snapshotId,offset:'0',search:''});
 let definition=pinned,offset=0;
 for(;;){
  if(definition)params.set('definitionRevisionId',definition);
  const page=await api<ProductPage>('reference/catalog?'+params,undefined,signal);
  signal.throwIfAborted();
  if(!page.definitionRevisionId||definition&&definition!==page.definitionRevisionId)
   throw Error('The option product directory changed definition. Refresh the page.');
  definition=page.definitionRevisionId;
  const index=(table:string)=>new Map((page.references?.[table]??[]).map(row=>[String(row.code),row]));
  const underlyings=index('futures_products'),exchanges=index('exchanges'),commodities=index('commodities');
  for(const row of page.rows){
   const underlyingCode=String(row.underlying_product_code??''),underlying=underlyings.get(underlyingCode);
   const commodity=String(underlying?.commodity_code??''),exchangeCode=String(row.exchange_code??'');
   products.push({code:String(row.code),name:String(row.name),underlyingCode,commodity,
    underlying:String(underlying?.name??underlyingCode),exchange:String(exchanges.get(exchangeCode)?.name??exchangeCode),
    market:String(commodities.get(commodity)?.name??(commodity?friendly(commodity):'Other markets'))});
  }
  if(!page.hasMore)return {definition,products};
  if(page.offset!==offset||!(page.pageSize>0)||page.rows.length!==page.pageSize)throw Error('The option directory returned an incomplete page.');
  offset+=page.pageSize;params.set('offset',String(offset));
 }
}

export function optionProductPicker(choose:(code:string)=>void,browseCurrent:()=>void){
 const root=element('section','options-directory');root.id='options-products';root.setAttribute('aria-label','Option products');
 const head=element('div','options-directory-head'),title=element('h3',undefined,'Option products');
 const search=element('input');search.id='options-product-search';search.type='search';search.placeholder='Search products, underlyings or exchanges';search.setAttribute('aria-label','Search option products');
 const market=element('select');market.setAttribute('aria-label','Filter by commodity');market.id='options-market-filter';
 const tools=element('div','options-directory-tools');tools.append(market,search);
 const groups=element('div','options-market-groups'),count=element('span','options-directory-count');count.setAttribute('role','status');
 const current=action('options-contract-link','Browse current products','options-browse-current');current.hidden=true;current.onclick=browseCurrent;
 const heading=element('div','options-directory-title');heading.append(title,count,current);head.append(heading,tools);root.append(head,groups);
 let products:Product[]=[],selected='';
 function draw(){
  const query=search.value.trim().toLowerCase(),matches=products.filter(p=>(!market.value||p.commodity===market.value)&&
   [p.code,p.name,p.market,p.underlying,p.underlyingCode,p.exchange].some(value=>value.toLowerCase().includes(query)));
  const byMarket=new Map<string,Product[]>();
  for(const p of matches){const group=byMarket.get(p.commodity)??[];group.push(p);byMarket.set(p.commodity,group);}
  count.textContent=matches.length+' '+(matches.length===1?'product':'products');
  groups.replaceChildren();
  for(const entries of byMarket.values()){
   const section=element('section','options-market'),grid=element('div','options-product-grid');
   section.append(element('h4',undefined,entries[0].market),grid);groups.append(section);
   for(const p of entries){
    const chip=action('options-product','');chip.dataset.product=p.code;chip.setAttribute('aria-pressed',String(p.code===selected));
    chip.append(element('span','options-product-name',p.name),element('span','options-product-meta',[p.exchange,p.code].filter(Boolean).join(' · ')));
    chip.title=[p.name,p.underlying,p.exchange,p.code].filter(Boolean).join(' · ');
    chip.onclick=()=>choose(p.code);grid.append(chip);
   }
  }
  if(!matches.length)groups.append(element('p','vol-empty-shelf',products.length?'No matching products. Change the market or search.':'No option products at this date.'));
 }
 search.oninput=market.onchange=draw;
 return {root,loading(){root.inert=true;root.setAttribute('aria-busy','true');current.hidden=true;groups.replaceChildren();count.textContent='Loading…';},
 set(next:Product[],code:string,canReturn:boolean){
  root.inert=false;root.removeAttribute('aria-busy');
  products=next;selected=code;current.hidden=!canReturn;
  const markets=new Map(next.filter(p=>p.commodity).map(p=>[p.commodity,p.market])),chosen=market.value;
  market.replaceChildren(new Option('All markets',''),...[...markets].sort((a,b)=>a[1].localeCompare(b[1])).map(([key,label])=>new Option(label,key)));
  market.value=markets.has(chosen)?chosen:'';draw();
 },select(code:string){selected=code;for(const chip of groups.querySelectorAll<HTMLButtonElement>('.options-product'))chip.setAttribute('aria-pressed',String(chip.dataset.product===code));},
 clearFilters(){market.value='';search.value='';},
 clear(canReturn=false){products=[];selected='';root.inert=false;root.removeAttribute('aria-busy');current.hidden=!canReturn;market.replaceChildren();search.value='';groups.replaceChildren();count.textContent='';}};
}

export type ListedExpiry={expiryAt:string;underlyingCode:string};
export type ListedSeries={code:string;right:'call'|'put';strike:string;openForBooking:boolean;lastTradingAt?:string|null};
export type UnderlyingContract=ListedExpiry&{name:string;deliveryFrom:string;deliveryTo:string;contractId:string|null;curves:string[];settlementMethod:string|null;openForBooking:boolean};
export type OptionListings={snapshotId:string;definitionRevisionId:string;product:string;expiries:ListedExpiry[];underlying:UnderlyingContract|null;series:ListedSeries[];canBook:boolean};

export function listedChain(series:ListedSeries[],selected:string,choose:(code:string)=>void){
 const table=element('table','options-listed-chain');table.id='options-listed-chain';
 const head=table.createTHead().insertRow();
 for(const label of ['Calls','Strike','Puts']){const th=element('th',undefined,label);th.scope='col';head.append(th);}
 const strikes=new Map<string,Partial<Record<ListedSeries['right'],ListedSeries>>>();
 for(const option of series){const row=strikes.get(option.strike)??{};row[option.right]=option;strikes.set(option.strike,row);}
 const body=table.createTBody();
 for(const [strike,sides] of strikes){
  const row=body.insertRow();
  for(const side of ['call','strike','put'] as const){
   if(side==='strike'){const th=element('th','options-strike',strike);th.scope='row';row.append(th);continue;}
   const td=row.insertCell(),option=sides[side];
   if(!option){td.textContent='—';td.setAttribute('aria-label','Unlisted');continue;}
   const button=action('options-series-link','');button.dataset.series=option.code;button.title=option.code;
   button.append(element('span','options-series-code',option.code),element('span','options-series-short',strike+' '+side));
   button.setAttribute('aria-label',strike+' '+side+' · '+option.code);button.setAttribute('aria-pressed',String(selected===option.code));
   button.onclick=()=>choose(option.code);td.append(button);
  }
 }
 return table;
}
