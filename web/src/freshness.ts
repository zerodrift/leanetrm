import {displayInstant} from './market-view.js';

// What a widget's footer states, all of it from backend fields. A widget never judges freshness itself.
export type Freshness={state:'latest'|'newer_available'|'historical'|'not_yet_available'|'failed';observedAt:string|null;source:string;reason?:string};

const states:Record<Freshness['state'],string>={latest:'Latest',newer_available:'Newer available',historical:'Historical',not_yet_available:'Not yet available',failed:'Failed'};

export function freshnessFooter(f:Freshness,zone:string):string{
 if(f.state==='failed'&&!f.reason)throw Error('A failed reading must state its reason.');
 const observed=f.observedAt===null?'No observation yet':displayInstant(f.observedAt,zone);
 return [f.source,observed,f.state==='failed'?`${states.failed}: ${f.reason}`:states[f.state]].join(' · ');
}
