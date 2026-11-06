// The Vol surfaces library as the page arranges it. A stored surface belongs to the exact close it was fitted at (D345): the
// backend lists each with that close, and these rules only sort and classify what it listed, for display.

/** One stored surface as the library read lists it: the latest revision of one product fitted at one close or working snapshot. */
export type LibrarySurface={revision:string;product:string;productName:string;closeId:string|null;businessDate:string;valuationAt:string;knownFrom:string|null;
 close:{session:string;businessDate:string;inForce:boolean}|null;expiries:number;fittedExpiries:number;series:number};
/** Where the page reads: a recorded close of a session, or a working snapshot (no close), at its valuation instant. */
export type Here={closeId:string|null;at:string;close:{session:string;businessDate:string}|null};
/** Frame ids are session-scoped, so a stored surface's location is its close, or none, and its valuation instant. */
export function sameLocation(surface:Pick<LibrarySurface,'closeId'|'valuationAt'>,here:Pick<Here,'closeId'|'at'>):boolean{
 return surface.closeId===here.closeId&&Date.parse(surface.valuationAt)===Date.parse(here.at);
}

/**
 * A product as the library shows it: its surface at this close, its newest surface at another one, and so its state. A product
 * without a surface listed has none only when every surface was read (`complete`); otherwise it is `unknown`.
 */
export type ProductEntry={code:string;name:string;state:'here'|'elsewhere'|'none'|'unknown';here:LibrarySurface|null;latest:LibrarySurface|null};
export function libraryEntries(products:readonly {code:string;name:string}[],surfaces:readonly LibrarySurface[],here:Here|undefined,complete:boolean):ProductEntry[]{
 const named=new Map(products.map(p=>[p.code,p.name]));
 for(const s of surfaces)if(!named.has(s.product))named.set(s.product,s.productName);
 const entries=[...named].map(([code,name]):ProductEntry=>{
  const own=surfaces.filter(s=>s.product===code),at=(s:LibrarySurface)=>!!here&&sameLocation(s,here);
  const current=own.find(at)??null,latest=own.find(s=>!at(s))??null;
  return {code,name,here:current,latest,state:current?'here':latest?'elsewhere':complete?'none':'unknown'};
 });
 const rank={here:0,elsewhere:1,none:2,unknown:2};
 // Surfaces are listed newest first, so a stable sort keeps the newest elsewhere first and the catalog's order after.
 return entries.sort((a,b)=>rank[a.state]-rank[b.state]||(a.state==='elsewhere'&&b.state==='elsewhere'?surfaces.indexOf(a.latest!)-surfaces.indexOf(b.latest!):0));
}

/**
 * How the page can reach a stored surface: it is this close's, a close or working snapshot this view offers, a close of another
 * session (chosen in the header, never switched to for the person), a close since replaced (history), or a day this view does
 * not offer, as a historical view offers only its own.
 */
export type Reach='current'|'open'|'otherSession'|'replaced'|'otherDay';
export function reach(surface:LibrarySurface,here:Here,offered:(surface:LibrarySurface)=>string|null):Reach{
 if(sameLocation(surface,here))return 'current';
 if(surface.close&&!surface.close.inForce)return 'replaced';
 if(offered(surface))return 'open';
 if(surface.close&&surface.close.session!==here.close?.session)return 'otherSession';
 return 'otherDay';
}
