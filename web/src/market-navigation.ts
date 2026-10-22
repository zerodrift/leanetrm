import {publicText,marketTypeName,type MarketType} from './market-view.js';
import type {Context} from './date-editor.js';
import {validFrameCatalog,type FrameCatalog} from './frame-catalog.js';
import type {LiveSelection} from './live-context.js';

export type FrameSummary={id:string;name:string;at:string;originCloseId?:string|null};
export type NavigationCategory={code:string;name:string};
// A commodity curve names its commodity. An interest-rate curve has none; its market type identifies it.
export type NavigationCurve={code:string;name:string;marketType:MarketType;category:NavigationCategory|null};
export type MarketNavigation={kind:'market_navigation';profile:string;context:Context|null;live?:LiveSelection|null;
 definitionRevision:{catalogId:string;revisionId:string};referenceRevision:{catalogId:string;revisionId:string};
 futuresConvention:string;example:{at:string;curve:string}|null;initialFrameIds:string[];
 frames:FrameSummary[];curveNames:{code:string;name:string}[];curves:NavigationCurve[];frameCatalog?:FrameCatalog};

export function validateMarketNavigation(value:MarketNavigation){
 const text=(value:unknown)=>typeof value==='string'&&value.length>0;
 const revision=(value:MarketNavigation['definitionRevision'])=>value&&text(value.catalogId)&&text(value.revisionId);
 if(!value||value.kind!=='market_navigation'||!text(value.profile)||!text(value.futuresConvention)||
  !revision(value.definitionRevision)||!revision(value.referenceRevision)||
  value.context!==null&&(!value.context||!text(value.context.contextId)||!text(value.context.effectiveOn)||!text(value.context.knownAt))||
  value.live!=null&&(!value.context||!text(value.live.id)||!Number.isSafeInteger(value.live.generation)||value.live.generation<1||value.live.policy!=='selected_facts_latest_committed_corrections_v1')||
  value.example!==null&&(!value.example||!text(value.example.at)||!text(value.example.curve))||
  !Array.isArray(value.curves)||value.curves.some(c=>!c||!text(c.code)||!text(c.name)||!['futures','forwards','mixed','rates'].includes(c.marketType)||
   (c.marketType==='rates'?c.category!==null:!c.category||!text(c.category.code)||!text(c.category.name)))||
  new Set(value.curves.map(c=>c.code)).size!==value.curves.length||conflictingLabels(value.curves)||
  !Array.isArray(value.curveNames)||value.curveNames.some(c=>!c||!text(c.code)||!text(c.name))||
  !Array.isArray(value.frames)||value.frames.some(f=>!f||!text(f.id)||!text(f.name)||!text(f.at)||f.originCloseId!=null&&!text(f.originCloseId)||Object.keys(f).some(k=>!['at','id','name','originCloseId'].includes(k)))||
  new Set(value.frames.map(f=>f.id)).size!==value.frames.length||
  (value.frameCatalog!==undefined&&!validFrameCatalog(value.frameCatalog))||value.frames.some(f=>!!f.originCloseId)&&!validFrameCatalog(value.frameCatalog!)||
  !Array.isArray(value.initialFrameIds)||value.initialFrameIds.some(id=>!value.frames.some(f=>f.id===id)))
  throw Error('The market navigation is missing or inconsistent. Refresh and try again.');
 return value;
}
// Every curve of one commodity must carry the same label.
function conflictingLabels(curves:NavigationCurve[]){
 const labels=new Map<string,string>();
 for(const {category} of curves){
  if(!category)continue;
  if((labels.get(category.code)??category.name)!==category.name)return true;
  labels.set(category.code,category.name);
 }
 return false;
}

function categoryName(category:NavigationCategory){
 const name=publicText(category.name.replace(/^Synthetic /,''));return name.charAt(0).toUpperCase()+name.slice(1);
}
export function navigationCurveKind(curve:NavigationCurve){
 const type=marketTypeName(curve.marketType);return curve.category?`${categoryName(curve.category)} · ${type}`:type;
}
export function navigationCategories(navigation:MarketNavigation){
 return [...new Map(navigation.curves.flatMap(c=>c.category?[[c.category.code,categoryName(c.category)] as const]:[]))].map(([code,name])=>({code,name})).sort((a,b)=>a.name.localeCompare(b.name));
}
// A null commodity lists all markets. Groups are keyed by commodity, or by market type when browsing by type
// or for curves without a commodity, so equal names never merge them.
export function navigationGroups(navigation:MarketNavigation,by:'commodity'|'marketType',commodity:string|null,type:string){
 const groups=new Map<string,{key:string;name:string;curves:NavigationCurve[]}>();
 for(const curve of navigation.curves){
  if(commodity!==null&&curve.category?.code!==commodity||type!=='all'&&curve.marketType!==type)continue;
  const category=by==='commodity'?curve.category:null,key=category?'commodity:'+category.code:'type:'+curve.marketType;
  const group=groups.get(key)??{key,name:category?categoryName(category):marketTypeName(curve.marketType),curves:[]};
  group.curves.push(curve);groups.set(key,group);
 }
 return [...groups.values()].sort((a,b)=>a.name.localeCompare(b.name)).map(({key,name,curves})=>({key,name,curves:curves.sort((a,b)=>a.name.localeCompare(b.name))}));
}
