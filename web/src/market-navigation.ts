import {publicText,marketTypeName,type MarketType} from './market-view.js';
import type {Context} from './date-editor.js';
import {validFrameCatalog,type FrameCatalog} from './frame-catalog.js';

export type FrameSummary={id:string;name:string;at:string;originCloseId?:string|null};
export type NavigationCurve={code:string;name:string;marketType:MarketType;category:{code:string;name:string}};
export type MarketNavigation={kind:'market_navigation';profile:string;context:Context|null;
 definitionRevision:{catalogId:string;revisionId:string};referenceRevision:{catalogId:string;revisionId:string};
 futuresConvention:string;example:{at:string;curve:string}|null;initialFrameIds:string[];
 frames:FrameSummary[];curveNames:{code:string;name:string}[];curves:NavigationCurve[];frameCatalog?:FrameCatalog};

export function validateMarketNavigation(value:MarketNavigation){
 const text=(value:unknown)=>typeof value==='string'&&value.length>0;
 const revision=(value:MarketNavigation['definitionRevision'])=>value&&text(value.catalogId)&&text(value.revisionId);
 if(!value||value.kind!=='market_navigation'||!text(value.profile)||!text(value.futuresConvention)||
  !revision(value.definitionRevision)||!revision(value.referenceRevision)||
  value.context!==null&&(!value.context||!text(value.context.contextId)||!text(value.context.effectiveOn)||!text(value.context.knownAt))||
  value.example!==null&&(!value.example||!text(value.example.at)||!text(value.example.curve))||
  !Array.isArray(value.curves)||value.curves.some(c=>!c||!text(c.code)||!text(c.name)||!['futures','forwards','mixed','rates'].includes(c.marketType)||!c.category||!text(c.category.code)||!text(c.category.name))||
  new Set(value.curves.map(c=>c.code)).size!==value.curves.length||
  !Array.isArray(value.curveNames)||value.curveNames.some(c=>!c||!text(c.code)||!text(c.name))||
  !Array.isArray(value.frames)||value.frames.some(f=>!f||!text(f.id)||!text(f.name)||!text(f.at)||f.originCloseId!=null&&!text(f.originCloseId)||Object.keys(f).some(k=>!['at','id','name','originCloseId'].includes(k)))||
  new Set(value.frames.map(f=>f.id)).size!==value.frames.length||
  (value.frameCatalog!==undefined&&!validFrameCatalog(value.frameCatalog))||value.frames.some(f=>!!f.originCloseId)&&!validFrameCatalog(value.frameCatalog!)||
  !Array.isArray(value.initialFrameIds)||value.initialFrameIds.some(id=>!value.frames.some(f=>f.id===id)))
  throw Error('The market navigation is missing or inconsistent. Refresh and try again.');
 return value;
}

export function navigationCategoryName(curve:NavigationCurve){
 const name=publicText(curve.category.name.replace(/^Synthetic /,''));return name.charAt(0).toUpperCase()+name.slice(1);
}
export function navigationGroups(navigation:MarketNavigation,by:'commodity'|'marketType',commodity:string,type:string){
 const groups=new Map<string,NavigationCurve[]>();
 for(const curve of navigation.curves){
  if(commodity!=='all'&&curve.category.code!==commodity||type!=='all'&&curve.marketType!==type)continue;
  const name=by==='commodity'?navigationCategoryName(curve):marketTypeName(curve.marketType);
  const rows=groups.get(name)??[];rows.push(curve);groups.set(name,rows);
 }
 return [...groups.entries()].sort(([a],[b])=>a.localeCompare(b)).map(([name,curves])=>({name,curves}));
}
