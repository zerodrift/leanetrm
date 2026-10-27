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
/** The operations that may feed an input instead of its current source: those whose output its catalog type accepts, never the operation
 * itself, its current source, or (for a dynamic input) one already feeding another of its inputs. The backend still validates, cycles included. */
export function connectableSources(definition:CurveDefinition,catalog:{operators:OperatorPorts[]},target:string,port:string):string[]{
 const node=definition.nodes.find(n=>n.id===target),spec=catalog.operators.find(op=>op.kind===node?.kind);if(!node||!spec)return [];
 const fixed=spec.inputPorts.find(p=>p.name===port),type=fixed?.type??(port in node.inputs?spec.dynamicInputs?.type:undefined);if(type===undefined)return [];
 const fed=new Set(fixed?[node.inputs[port]]:Object.values(node.inputs));
 return definition.nodes.filter(c=>{const output=catalog.operators.find(op=>op.kind===c.kind)?.outputType;return c.id!==target&&!fed.has(c.id)&&output!==undefined&&(type===output||type==='any');}).map(c=>c.id);
}
/** Removes one arrow. Contract rows naming the port stay, for the person to reconnect or the backend to reject. */
export function removeCurveConnection(definition:CurveDefinition,target:string,port:string):CurveDefinition{
 return changeCurveConnection(definition,target,port,'');
}
export function changeContractWeights(definition:CurveDefinition,target:string,rows:ContractWeight[]):CurveDefinition{
 return {...definition,nodes:definition.nodes.map(node=>node.id===target?{...node,contractWeights:rows.map(row=>({...row}))}:node)};
}
/** A mapping's explicit contract connections as graph objects, each with the node feeding its source port (null when none does). */
export type ContractObject=ContractWeight&{index:number;source:string|null};
export function contractObjects(definition:CurveDefinition,node:CurveNode):ContractObject[]{
 return (node.contractWeights??[]).map((row,index)=>({index,source:node.inputs[row.sourcePort]||null,sourcePort:row.sourcePort,contractId:row.contractId,weight:row.weight,outputPosition:row.outputPosition}));
}
/** The catalog's port metadata for an operation: only this decides what may be offered as a connection; the backend still validates. */
export type OperatorPorts={kind:string;inputPorts:{name:string;type:string}[];outputType:string;fields:{type:string}[];dynamicInputs?:{type:string;prefix:string}};
/** The next unused name for a new dynamic input of an operation: its catalog prefix and the first free number. */
export function nextDynamicPort(node:CurveNode,prefix:string):string{let n=1;while(node.inputs[prefix+n]!==undefined)n++;return prefix+n;}
export type ConnectableTarget={target:string;port:string;added:boolean};
/** The input ports an arrow from `source` may go to: ports whose catalog type accepts the source's output type, never on the source itself,
 * never a port it already feeds, and on an operation with dynamic inputs one free port (an empty one, or the next new name) unless it already feeds one. */
export function connectableTargets(definition:CurveDefinition,catalog:{operators:OperatorPorts[]},source:string):ConnectableTarget[]{
 const operator=(kind:string|undefined)=>catalog.operators.find(op=>op.kind===kind);
 const output=operator(definition.nodes.find(node=>node.id===source)?.kind)?.outputType;if(output===undefined)return [];
 const accepts=(type:string)=>type===output||type==='any';
 return definition.nodes.flatMap(node=>{
  const spec=operator(node.kind);if(node.id===source||!spec)return [];
  const fixed=spec.inputPorts.filter(port=>accepts(port.type)&&node.inputs[port.name]!==source).map(port=>({target:node.id,port:port.name,added:false}));
  const dynamic=spec.dynamicInputs;if(!dynamic||!accepts(dynamic.type))return fixed;
  const names=Object.keys(node.inputs).filter(name=>!spec.inputPorts.some(port=>port.name===name));
  if(names.some(name=>node.inputs[name]===source))return fixed;
  const empty=names.find(name=>!node.inputs[name]);if(empty!==undefined)return [...fixed,{target:node.id,port:empty,added:false}];
  return [...fixed,{target:node.id,port:nextDynamicPort(node,dynamic.prefix),added:true}];
 });
}
const holdsContracts=(catalog:{operators:OperatorPorts[]},node:CurveNode|undefined)=>!!catalog.operators.find(op=>op.kind===node?.kind)?.fields.some(field=>field.type==='contract_weights');
/** The mapping port a contract listed by `source` would use: the port already connecting it, else a free one; none if the mapping cannot take it. */
export function contractPort(definition:CurveDefinition,catalog:{operators:OperatorPorts[]},target:string,source:string):string|undefined{
 const node=definition.nodes.find(n=>n.id===target);if(!holdsContracts(catalog,node))return undefined;
 return Object.entries(node!.inputs).find(([,input])=>input===source)?.[0]??connectableTargets(definition,catalog,source).find(t=>t.target===target)?.port;
}
/** The mappings that can take a contract listed by `source`: those that already connect it, or can connect it through one of their ports. */
export function contractTargets(definition:CurveDefinition,catalog:{operators:OperatorPorts[]},source:string):string[]{
 return definition.nodes.filter(node=>contractPort(definition,catalog,node.id,source)!==undefined).map(node=>node.id);
}
/** Adds one explicit contract connection, connecting the source through a port first when needed. No weight or output position is chosen. */
export function pickContract(definition:CurveDefinition,catalog:{operators:OperatorPorts[]},target:string,source:string,contractId:string):{definition:CurveDefinition;row:number}{
 const port=contractPort(definition,catalog,target,source);if(port===undefined)throw Error('This operation cannot take a contract connection from that source.');
 const connected=changeCurveConnection(definition,target,port,source),rows=connected.nodes.find(n=>n.id===target)!.contractWeights??[];
 return {definition:changeContractWeights(connected,target,[...rows,{outputPosition:null,sourcePort:port,contractId,weight:''}]),row:rows.length};
}
/** What the contract connections still lack, in alphabetical order: the person chooses every contract, output position and weight, so preview and save wait for them. */
export function missingContractInputs(definition:CurveDefinition):string[]{
 const rows=definition.nodes.flatMap(node=>node.contractWeights??[]),missing:string[]=[];
 if(rows.some(row=>row.contractId===''))missing.push('contract');
 if(rows.some(row=>row.outputPosition===null))missing.push('output position');
 if(rows.some(row=>row.weight.trim()===''))missing.push('weight');
 return missing;
}
/** Places cards in columns by graph depth. Each column stacks its cards by their measured heights (presentation only), keeping saved
 * positions, so placed cards never overlap one another or a saved card placed before them. */
export function curveLayout(definition:CurveDefinition,saved:CurveLayout={},heights:Record<string,number>={}):CurveLayout{
 const positions:CurveLayout={},depths=new Map<string,number>(),visited=new Set<string>();
 function depth(id:string,path:Set<string>):number{if(depths.has(id))return depths.get(id)!;if(path.has(id))return 0;const node=definition.nodes.find(n=>n.id===id);if(!node)return 0;
  const next=new Set(path).add(id),inputs=Object.values(node.inputs),level=inputs.length?1+Math.max(...inputs.map(input=>depth(input,next))):0;depths.set(id,level);return level;}
 const gap=28,below=new Map<number,number>();
 for(const node of definition.nodes){const column=depth(node.id,visited),height=heights[node.id]??136,kept=saved[node.id];
  const position=kept?{...kept}:{x:24+column*288,y:below.get(column)??24};positions[node.id]=position;
  below.set(column,Math.max(below.get(column)??24,position.y+height+gap));}
 return positions;
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
export function previewTiles(summary:PreviewSummary|undefined):TileSpec[]{
 if(!summary||!Number.isSafeInteger(summary.points)||!Number.isSafeInteger(summary.unavailablePoints)||!statedPeriod(summary.firstPeriod)||!statedPeriod(summary.lastPeriod)||typeof summary.unit!=='string')
  throw Error('The preview does not state its summary. Preview again.');
 const words=(label:string,figure:string)=>({label,rows:[{figure}],words:true});
 const period=(value:DeliveryPeriod|null)=>value===null?'None':deliveryLabel(value.from,value.to);
 return [
  {label:'Points',rows:[{figure:String(summary.points)}],...(summary.unavailablePoints?{note:`${summary.unavailablePoints} without a price`}:{}),words:false},
  words('First period',period(summary.firstPeriod)),words('Last period',period(summary.lastPeriod)),words('Unit',summary.unit||'No points'),
 ];
}
