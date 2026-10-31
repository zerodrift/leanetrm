// The Blotter's actions as named operations with typed input (blotter-workbench.md, Shared operations). The toolbar, keyboard, panes and
// menus dispatch them, and the assistant will; this module only reads and routes them, so a handler is the one place an action happens.
import {parseArrangement,type Arrangement} from './dock-model.js';
import type {GridLayout} from './data-grid.js';
import {blotterColumns,blotterDockRules,blotterPanes,columnsFitMode,quoteUnitsShown,rowGroups,validColumnFilters,validIds,validSort,validPositionKey,validViewName,type BlotterView,type RowGroup} from './blotter-layout.js';

export type BlotterPane=typeof blotterPanes[number];
export type BlotterOperation=
  |{kind:'selectView';name:string}|{kind:'chooseScope';bookIds:string[];portfolioIds:string[]}|{kind:'groupRows';rowGroup:RowGroup}
  |{kind:'sort';sort:GridLayout['sort']}|{kind:'filter';query:string;columns:Record<string,string>}|{kind:'chooseColumns';columns:string[]}
  |{kind:'selectPosition';key:string|null}|{kind:'details';open:boolean}|{kind:'arrange';panes:Arrangement}
  |{kind:'collapsePane';pane:BlotterPane;collapsed:boolean}|{kind:'resetLayout'}|{kind:'popOut'}|{kind:'bringBack'}
  |{kind:'draftCloseOut';key:string}|{kind:'amend';key:string};
/** What an operation answers once done: a position selection whether the position is shown, false when the person kept edits the details
 *  hold; every other operation nothing. */
export type BlotterAnswer<K extends BlotterOperation['kind']>=K extends 'selectPosition'?boolean:void;
/** Each kind's handler, which acts on it and is told how the caller asks (`Via`: the page says whether the person's own move placed the row). */
export type BlotterOperationHandlers<Via=void>={[K in BlotterOperation['kind']]:(operation:Extract<BlotterOperation,{kind:K}>,via:Via)=>Promise<BlotterAnswer<K>>};

/** A refusal of an operation, with why: an operation that does not read, and one its handler cannot carry out where the Blotter is. */
export const refuse=(why:string)=>Error(`The Blotter operation was refused: ${why}.`);
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

/** One entry point for every action: the operation is read here, whoever built it, then goes to the handler of its own kind and to no other,
 *  whose answer it gives. */
export const createBlotterOperations=<Via=void>(handlers:BlotterOperationHandlers<Via>)=>
  async<O extends BlotterOperation>(operation:O,via:Via):Promise<BlotterAnswer<O['kind']>>=>{
    const read=readBlotterOperation(operation);
    if(!Object.hasOwn(handlers,read.kind))throw refuse(`${read.kind} has no handler`);
    return await (handlers[read.kind] as (operation:BlotterOperation,via:Via)=>Promise<BlotterAnswer<O['kind']>>)(read,via);
  };

/** Columns the display mode shown takes, by the saved-view rules (blotter-layout.ts); any other choice is refused. */
export function requireModeColumns(view:Pick<BlotterView,'mode'|'groupBy'|'measures'>,columns:readonly string[]){
  if(!columnsFitMode(view,columns))throw refuse(view.mode==='trades'?'trade rows do not show trade counts or price measures'
    :'a pivot shows only its own dimensions and measures, with their units and currencies');
  if(!quoteUnitsShown(view,columns))throw refuse('a pivot of prices shows their quote units');
}
