import {deliveryLabel} from './market-view.js';
import type {TileSpec} from './tiles.js';

/** Presentation contracts for the managed custom-curve operations. No market
 * selection, dates, aggregation or financial arithmetic runs in this module. */
export type ContractWeight={outputPosition:number|null;sourcePort:string;contractId:string;weight:string};
export type PanelWeight={sourcePort:string;weight:string};
export type PublishedSourceReference={catalogId:string;curveId:string;version:number;outputNode:string};
export type CurveNode={id:string;kind:string;parameters:Record<string,unknown>;inputs:Record<string,string>;contractWeights?:ContractWeight[];publishedSource?:PublishedSourceReference;panelWeights?:PanelWeight[]};
export type CurveDefinition={code:string;name:string;nodes:CurveNode[];output:string};
export type NodePosition={x:number;y:number};
export type CurveLayout=Record<string,NodePosition>;
export type CurveEdge={source:string;target:string;port:string};
export function curveEdges(definition:CurveDefinition):CurveEdge[]{
 return definition.nodes.flatMap(node=>Object.entries(node.inputs).map(([port,source])=>({source,target:node.id,port})));
}
export function changeCurveConnection(definition:CurveDefinition,target:string,port:string,source:string):CurveDefinition{
 return {...definition,nodes:definition.nodes.map(node=>{if(node.id!==target)return node;const inputs={...node.inputs};if(source)inputs[port]=source;else delete inputs[port];return {...node,inputs,...(node.panelWeights?{panelWeights:node.panelWeights.filter(weight=>weight.sourcePort in inputs)}:{})};})};
}
export function changeContractWeights(definition:CurveDefinition,target:string,rows:ContractWeight[]):CurveDefinition{
 return {...definition,nodes:definition.nodes.map(node=>node.id===target?{...node,contractWeights:rows.map(row=>({...row}))}:node)};
}
export function curveLayout(definition:CurveDefinition,saved:CurveLayout={}):CurveLayout{
 const positions:CurveLayout={},depths=new Map<string,number>(),visited=new Set<string>();
 function depth(id:string,path:Set<string>):number{if(depths.has(id))return depths.get(id)!;if(path.has(id))return 0;const node=definition.nodes.find(n=>n.id===id);if(!node)return 0;
  const next=new Set(path).add(id),inputs=Object.values(node.inputs),level=inputs.length?1+Math.max(...inputs.map(input=>depth(input,next))):0;depths.set(id,level);return level;}
 const rows=new Map<number,number>();for(const node of definition.nodes){const column=depth(node.id,visited),row=rows.get(column)??0;rows.set(column,row+1);positions[node.id]=saved[node.id]?{...saved[node.id]}:{x:24+column*264,y:24+row*152};}return positions;
}
export function previewTable(preview:{columns:string[];rows:(string|null)[][]}){
 return {headers:preview.columns,rows:preview.rows.map(row=>row.map(cell=>cell===null?'Unavailable':cell))};
}

export function knownCurveRejection(code:string|undefined):boolean{
 return !!code&&['curve_activation_conflict','curve_activation_input','curve_activation_overlap','custom_curve_conflict','custom_curve_definition','recipe_definition','recipe_transform','custom_curve_limit','access_denied','historical_read_only','request_format','request_too_large','request_token','definition_rejected'].includes(code);
}
export type ConditionalCurveField={key:string;when?:{key:string;values:string[]}};
export function curveFieldVisible(field:ConditionalCurveField,parameters:Record<string,unknown>):boolean{
 return !field.when||field.when.values.includes(String(parameters[field.when.key]));
}
export function editCurveParameters(parameters:Record<string,unknown>,key:string,value:unknown,fields:ConditionalCurveField[]):Record<string,unknown>{
 const next={...parameters};if(value===null)delete next[key];else next[key]=value;
 for(const field of fields)if(!curveFieldVisible(field,next))delete next[field.key];return next;
}

/** What a saved curve's state is called. A state this page does not know is refused rather than shown as another. */
export function recipeStateLabel(state:unknown):'Draft'|'Published'{
 if(state==='draft')return 'Draft';
 if(state==='published')return 'Published';
 throw Error('A saved curve has a state this page cannot show. Reload the library.');
}

type DeliveryPeriod={from:string;to:string};
export type PreviewSummary={points:number;unavailablePoints:number;firstPeriod:DeliveryPeriod|null;lastPeriod:DeliveryPeriod|null;unit:string};
const statedPeriod=(value:unknown)=>value===null||typeof value==='object'&&typeof (value as DeliveryPeriod).from==='string'&&typeof (value as DeliveryPeriod).to==='string';
/**
 * The preview's tiles, from the summary the backend states with it: nothing is counted and no period is chosen here. A tile of words
 * (a period or a unit) wraps in its tile; a preview without a complete summary is refused.
 */
export function previewTiles(summary:PreviewSummary|undefined):(TileSpec&{words:boolean})[]{
 if(!summary||!Number.isSafeInteger(summary.points)||!Number.isSafeInteger(summary.unavailablePoints)||!statedPeriod(summary.firstPeriod)||!statedPeriod(summary.lastPeriod)||typeof summary.unit!=='string')
  throw Error('The preview does not state its summary. Preview again.');
 const words=(label:string,figure:string)=>({label,rows:[{figure}],words:true});
 const period=(value:DeliveryPeriod|null)=>value===null?'None':deliveryLabel(value.from,value.to);
 return [
  {label:'Points',rows:[{figure:String(summary.points)}],...(summary.unavailablePoints?{note:`${summary.unavailablePoints} without a price`}:{}),words:false},
  words('First period',period(summary.firstPeriod)),words('Last period',period(summary.lastPeriod)),words('Unit',summary.unit||'None'),
 ];
}
