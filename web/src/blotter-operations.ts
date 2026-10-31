// The Blotter's actions as named operations with typed input (blotter-workbench.md, Shared operations). The toolbar, keyboard, panes and
// menus dispatch them, and the assistant will; this module only reads and routes them, so a handler is the one place an action happens.
import {parseArrangement,type Arrangement} from './dock-model.js';
import type {GridLayout} from './data-grid.js';
import {blotterColumns,blotterDockRules,blotterPanes,rowGroups,validColumnFilters,validIds,validSort,validPositionKey,validViewName,type RowGroup} from './blotter-layout.js';

export type BlotterPane=typeof blotterPanes[number];
export type BlotterOperation=
  |{kind:'selectView';name:string}|{kind:'chooseScope';bookIds:string[];portfolioIds:string[]}|{kind:'groupRows';rowGroup:RowGroup}
  |{kind:'sort';sort:GridLayout['sort']}|{kind:'filter';query:string;columns:Record<string,string>}|{kind:'chooseColumns';columns:string[]}
  |{kind:'selectPosition';key:string|null}|{kind:'details';open:boolean}|{kind:'arrange';panes:Arrangement}
  |{kind:'collapsePane';pane:BlotterPane;collapsed:boolean}|{kind:'resetLayout'}|{kind:'popOut'}|{kind:'bringBack'}
  |{kind:'draftCloseOut';key:string}|{kind:'amend';key:string};
export type BlotterOperationHandlers={[K in BlotterOperation['kind']]:(operation:Extract<BlotterOperation,{kind:K}>)=>Promise<void>};

const refuse=(why:string)=>Error(`The Blotter operation was refused: ${why}.`);
// An operation is exactly its kind and fields: a missing, extra or mistyped one is refused, never defaulted or dropped.
const exactly=(value:Record<string,unknown>,...fields:string[])=>{
  const keys=Object.keys(value).sort(),wanted=['kind',...fields].sort();
  if(keys.length!==wanted.length||keys.some((k,i)=>k!==wanted[i]))throw refuse(`${value.kind} takes exactly ${fields.length?fields.join(', '):'no fields'}`);
  return value;
};
const flag=(value:unknown,what:string)=>{if(typeof value!=='boolean')throw refuse(`${what} must be true or false`);return value;};
const pane=(value:unknown):BlotterPane=>{if(!blotterPanes.includes(value as BlotterPane))throw refuse('that pane is not one of the Blotter’s');return value as BlotterPane;};

export function readBlotterOperation(value:unknown):BlotterOperation{
  if(!value||typeof value!=='object'||Array.isArray(value))throw refuse('an operation is an object');
  const v=value as Record<string,unknown>;
  switch(v.kind){
    case 'selectView':exactly(v,'name');
      if(!validViewName(v.name))throw refuse('a view name is 1 to 60 characters');
      return {kind:v.kind,name:v.name.trim()};
    case 'chooseScope':exactly(v,'bookIds','portfolioIds');
      if(!validIds(v.bookIds)||!validIds(v.portfolioIds))throw refuse('books and portfolios are unique identifiers');
      return {kind:v.kind,bookIds:[...v.bookIds],portfolioIds:[...v.portfolioIds]};
    case 'groupRows':exactly(v,'rowGroup');
      if(v.rowGroup!==null&&!rowGroups.includes(v.rowGroup as never))throw refuse('that row grouping is not offered');
      return {kind:v.kind,rowGroup:v.rowGroup as RowGroup};
    case 'sort':exactly(v,'sort');
      if(!validSort(v.sort)||v.sort.some(s=>Object.keys(s).length!==2))throw refuse('a sort is up to 16 distinct columns, each ascending or descending');
      return {kind:v.kind,sort:v.sort.map(s=>({key:s.key,direction:s.direction}))};
    case 'filter':exactly(v,'query','columns');
      if(typeof v.query!=='string'||v.query.length>200||!validColumnFilters(v.columns))throw refuse('a filter is search text and column texts of at most 200 characters');
      return {kind:v.kind,query:v.query,columns:{...v.columns}};
    case 'chooseColumns':exactly(v,'columns');
      if(!Array.isArray(v.columns)||!v.columns.length||new Set(v.columns).size!==v.columns.length||v.columns.some(c=>!blotterColumns.some(b=>b[0]===c)))throw refuse('columns are distinct and offered by the Blotter');
      return {kind:v.kind,columns:[...v.columns]};
    case 'selectPosition':exactly(v,'key');
      if(v.key!==null&&!validPositionKey(v.key))throw refuse('a position key is tradeId:bindingId');
      return {kind:v.kind,key:v.key as string|null};
    case 'details':exactly(v,'open');return {kind:v.kind,open:flag(v.open,'open')};
    case 'arrange':exactly(v,'panes');
      try{return {kind:v.kind,panes:parseArrangement(v.panes,blotterPanes,blotterDockRules)};}catch{throw refuse('that pane arrangement is not one the Blotter allows');}
    // The positions grid is the Blotter's one fixed pane: it is never collapsed.
    case 'collapsePane':exactly(v,'pane','collapsed');
      if(blotterDockRules.fixed.includes(v.pane as string))throw refuse('the positions pane cannot be collapsed');
      return {kind:v.kind,pane:pane(v.pane),collapsed:flag(v.collapsed,'collapsed')};
    case 'resetLayout':case 'popOut':case 'bringBack':exactly(v);return {kind:v.kind};
    case 'draftCloseOut':case 'amend':exactly(v,'key');
      if(!validPositionKey(v.key))throw refuse('a position key is tradeId:bindingId');
      return {kind:v.kind,key:v.key};
    default:throw refuse('that operation is not one the Blotter has');
  }
}

/** One entry point for every action: the operation is read here, whoever built it, then goes to the handler of its own kind and to no other. */
export const createBlotterOperations=(handlers:BlotterOperationHandlers)=>async(operation:BlotterOperation):Promise<void>=>{
  const read=readBlotterOperation(operation);
  if(!Object.hasOwn(handlers,read.kind))throw refuse(`${read.kind} has no handler`);
  await (handlers[read.kind] as (operation:BlotterOperation)=>Promise<void>)(read);
};
