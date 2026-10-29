import {type CurveDefinition,type CurveNode,type CurveLayout,type ContractWeight,type PublishedSourceReference,type PreviewSummary,type ConnectableTarget,changeContractWeights,curveEdges,curveLayout,changeCurveConnection,previewTable,previewTiles,recipeStateLabel,knownCurveRejection,curveFieldVisible,editCurveParameters,nextDynamicPort,removeCurveConnection,contractObjects,connectableTargets,contractTargets,pickContract,missingContractInputs,contractPort,connectableSources} from './custom-curve-model.js';
import {renderRegularTable} from './regular-table.js';
import {clearChart,renderChart,type ChartSpec} from './chart-view.js';
import {displayInstant,publicText} from './market-view.js';
import {PendingCommand,type RequestCommand} from './pending-command.js';
import {setupCurveBlend} from './curve-blend.js';
import {askInPanel} from './side-panel.js';
import {renderTile,space} from './tiles.js';
import {kindBadge,kindGroups,kindLabel,kindsVersion,type CurveKind} from './curve-kinds.js';
import {setupDock} from './curve-dock.js';
import {menuKey,placeOf,setupMenu,type MenuItem} from './context-menu.js';

type Choice={value:unknown;label:string};
type Field={key:string;label:string;type:string;required?:boolean;options?:Choice[];help?:string;when?:{key:string;values:string[]}};
type Operator={kind:string;label:string;description?:string;inputPorts:{name:string;label?:string;type:string;required?:boolean}[];outputType:string;fields:Field[];dynamicInputs?:{type:string;label:string;prefix:string}};
type Template={code:string;name:string;description?:string;available?:boolean;unavailableReason?:string;definition:CurveDefinition;layout?:CurveLayout};
type Recipe={id:string;version:number;generation:number;state:string;kind:CurveKind;definition:CurveDefinition;layout?:CurveLayout;publishedAt?:string};
type ActivationSegment={effectiveFrom:string;effectiveTo:string;version:number;outputNode:string};
type ActivationPlan={curveId:string;revision:number;policy:string;reason:string;segments:ActivationSegment[];knownFrom?:string|null};
type Source={code:string;name:string;catalogId?:string;revisionId?:string;contracts?:{id:string;label:string;deliveryFrom:string;deliveryTo:string}[]};
type Schedule={id?:string;version:number;symbol?:string;name:string};
/** Saved curves, sources and published sources carry the backend's kind (D259, `kindsVersion`). */
type Catalog={kindsVersion:typeof kindsVersion;context?:{effectiveOn?:string;knownAt?:string};canEdit:boolean;canPublish:boolean;operators:Operator[];templates:Template[];sources:(Source&{kind:CurveKind})[];publishedSources?:{label:string;kind:CurveKind;reference:PublishedSourceReference}[];calendars:(Source&{reference?:unknown})[];schedules:Schedule[];recipes:Recipe[];formulaSupported?:boolean};
type Preview={columns:string[];rows:(string|null)[][];at?:string;snapshotId?:string;summary?:PreviewSummary;chart?:ChartSpec;context?:{effectiveOn?:string;knownAt?:string};dependencies?:unknown[];inputs?:unknown[];trace?:unknown;warnings?:string[]};
type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type Options={api:Api;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;zone:()=>string;identity:()=>string;source:()=>{snapshotId:string;context?:unknown};historical:()=>boolean;marketScopeChanged?:()=>void;unsavedChanged?:()=>void;
 /** Whether the page is too narrow to show tab groups side by side. */
 narrow:()=>boolean;
 /** The page assistant: whether this person may use it, and opening it with a question to start from. */
 assistant:{available:()=>boolean;draft:(text:string)=>void}};
const clone=<T>(value:T):T=>structuredClone(value);
const empty=():CurveDefinition=>({code:'',name:'',nodes:[],output:''});
const pretty=(value:unknown)=>typeof value==='string'?value:JSON.stringify(value,null,2);
const assistantStart='Help me set up a new curve in the Curve builder.';

/** Editable graph presentation, laid out as a desk (markets-desk.md, Curve builder): a header with the curve's state and actions, the
 * saved-curve library, and the curve's views as tabs in a dock. All date selection, type validation, lineage, aggregation, values and
 * summaries are supplied by the authorized managed operations. */
export function setupCustomCurves(host:HTMLElement,o:Options){
 const el=<K extends keyof HTMLElementTagNameMap>(tag:K,value?:string)=>{const node=document.createElement(tag);if(value!==undefined)node.textContent=publicText(value);return node;};
 const button=(label:string,id:string,run:()=>void)=>{const node=el('button',label);node.type='button';if(id)node.id=id;node.onclick=run;return node;};
 const label=(name:string,control:HTMLElement)=>{const node=el('label',name);node.append(control);return node;};
 const option=(select:HTMLSelectElement,value:string,name:string)=>{select.append(new Option(publicText(name),value));};
 const field=(id:string,type='text')=>{const input=el('input');input.id=id;input.type=type;return input;};
 const view=(id:string)=>{const node=el('section');node.id=id;node.className='custom-curve-view';return node;};
 const chip=(text:string,state:string)=>{const node=el('span',text);node.className='curve-state-chip';node.dataset.state=state;return node;};
 let catalog:Catalog|undefined,definition=empty(),layout:CurveLayout={},selected:Recipe|undefined,selectedNode='',generation=0,dirty=false,mode='visual',formulaDirty=false,zoom=1,fit=false;
 // Whether a curve is open: a saved one, a template, a proposal or a new one. With none, the page offers ways to start one.
 let opened=false,initialized=false;
 let activationPlan:ActivationPlan|null=null,activationLoaded=false,activationDirty=false,effectiveCurve:string|undefined;
 let lastPreview:{values:Preview;source:{snapshotId:string;context?:unknown}}|undefined;
 let drag:{id:string;pointer:number;startX:number;startY:number;x:number;y:number}|undefined;
 // Graph presentation state, never saved: an arrow or contract being connected, a wire being dragged, the listed contract whose connections
 // are shown, and the source cards whose contracts are listed (with each list's scroll).
 let connecting:{source:string;contract?:string;replace?:{target:string;port:string};refocus:()=>void}|undefined;
 let wire:{pointer:number;x:number;y:number;startX:number;startY:number;moved:boolean}|undefined;
 let shown:{source:string;contract:string}|undefined;
 const expanded=new Set<string>(),listScroll=new Map<string,number>();
 // Cards placed before they could be measured (the board was hidden); they are placed again by their heights once it shows.
 const estimated=new Set<string>();
 const pending=new PendingCommand(knownCurveRejection);
 const storageKey=()=>`openetrm.custom-curves.request.${o.identity()}`;
 function storePending(){try{if(pending.command)sessionStorage.setItem(storageKey(),JSON.stringify(pending.command));else sessionStorage.removeItem(storageKey());}catch{/* The exact request remains available in memory. */}}

 // Header: the curve's name, its state and version, and the actions on one line.
 const head=el('header');head.className='custom-curve-head';const titles=el('div');titles.className='custom-curve-titles';
 const kicker=el('p','Custom curve');kicker.className='eyebrow';const heading=el('h2');heading.id='custom-curves-heading';
 const headKind=el('span');headKind.id='custom-curves-kind';const titleRow=el('div');titleRow.className='custom-curve-title-row';titleRow.append(headKind,heading);
 const status=el('p');status.id='custom-curves-state';status.className='custom-curve-status';status.setAttribute('role','status');titles.append(kicker,titleRow,status);
 const resetLayoutButton=button('Reset layout','custom-curves-reset-layout',()=>dock.reset());resetLayoutButton.className='text-button';
 const previewButton=button('Preview','custom-curves-preview',()=>void o.act(preview));previewButton.className='secondary';
 const saveButton=button('Save draft','custom-curves-save',()=>void o.act(save));
 const publishButton=button('Publish','custom-curves-publish',()=>void o.act(publish));publishButton.className='secondary';
 const actions=el('div');actions.className='custom-curve-actions';actions.append(resetLayoutButton,previewButton,saveButton,publishButton);head.append(titles,actions);
 const templateHelp=el('p');templateHelp.id='custom-curves-template-help';templateHelp.className='custom-curve-about';
 const accessNote=el('p');accessNote.id='custom-curves-access-note';accessNote.className='mode-banner';accessNote.hidden=true;
 const pendingPanel=el('div');pendingPanel.id='custom-curves-pending';pendingPanel.className='mode-banner custom-curve-pending';pendingPanel.hidden=true;pendingPanel.append(el('p','The previous save or publish request needs a confirmed result. Reload the library to inspect changes, then retry the original request or discard it after checking.'));
 const retryButton=button('Retry original request','custom-curves-retry',()=>void o.act(async()=>{if(pending.command)await sendMutation(pending.command);}));
 const discardButton=button('Discard checked request','custom-curves-discard',()=>{if(o.busy())return;pending.clear();storePending();pendingPanel.hidden=true;sync();});discardButton.className='secondary';pendingPanel.append(retryButton,discardButton);
 // The question asked before unsaved edits are given up (askInPanel), above the curve's views.
 const prompt=el('div');prompt.id=host.id+'-prompt';prompt.className='side-panel-prompt custom-curve-prompt';prompt.setAttribute('role','alertdialog');prompt.setAttribute('aria-label','Unsaved changes');prompt.hidden=true;

 // Left rail: asking the assistant, and the saved curves with their kind, version and state. The rail collapses to its toggle, giving its
 // width to the views.
 const desk=el('div');desk.className='custom-curve-desk';
 const rail=el('section');rail.id='custom-curves-rail';rail.className='custom-curve-rail';rail.setAttribute('aria-label','Saved curves');
 const railBody=el('div');railBody.id='custom-curves-rail-body';railBody.className='custom-curve-rail-body';
 const railToggle=button('','custom-curves-toggle-rail',()=>{railBody.hidden=!railBody.hidden;desk.classList.toggle('rail-collapsed',railBody.hidden);railToggle.setAttribute('aria-expanded',String(!railBody.hidden));railToggle.setAttribute('aria-label',(railBody.hidden?'Expand':'Collapse')+' saved curves');});
 const railTitle=el('span','Saved curves');railTitle.className='custom-curve-rail-title';const railChevron=el('span','‹');railChevron.className='custom-curve-rail-chevron';railChevron.setAttribute('aria-hidden','true');
 railToggle.className='custom-curve-rail-toggle';railToggle.append(railTitle,railChevron);railToggle.setAttribute('aria-controls',railBody.id);railToggle.setAttribute('aria-expanded','true');railToggle.setAttribute('aria-label','Collapse saved curves');
 const askAssistant=()=>o.assistant.draft(assistantStart);
 const askButton=button('Ask the assistant to set up a curve','custom-curves-ask',askAssistant);askButton.className='custom-curve-ask';
 const startNew=()=>replacing(()=>{openDefinition(empty(),{},undefined,'configuration');if(!code.disabled)code.focus({preventScroll:true});});
 const newButton=button('New curve','custom-curves-new',startNew);newButton.className='secondary';
 const reloadButton=button('Reload','custom-curves-reload',()=>replacing(()=>void o.act(reloadSelected),undefined,()=>!!selected&&editing()));reloadButton.className='text-button';reloadButton.setAttribute('aria-label','Reload library');
 const libraryActions=el('div');libraryActions.className='custom-curve-library-actions';libraryActions.append(newButton,reloadButton);
 const library=el('ul');library.id='custom-curves-library';library.className='custom-curve-list';library.setAttribute('aria-label','Saved custom curves');
 railBody.append(askButton,libraryActions,library);rail.append(railToggle,railBody);

 // With no curve open, the main area offers the ways to start one.
 const start=el('section');start.id='custom-curves-start';start.className='card custom-curve-start';start.setAttribute('aria-labelledby','custom-curves-start-title');
 const startTitle=el('h3','Start a curve');startTitle.id='custom-curves-start-title';
 const startAsk=button('Ask the assistant to set up a curve','custom-curves-start-ask',askAssistant),startNewButton=button('New curve','custom-curves-start-new',startNew);startNewButton.className='secondary';
 const startActions=el('div');startActions.className='custom-curve-start-actions';startActions.append(startAsk,startNewButton);
 const startTemplates=el('ul');startTemplates.id='custom-curves-start-templates';startTemplates.className='custom-curve-start-templates';startTemplates.setAttribute('aria-labelledby','custom-curves-start-templates-title');
 const startTemplatesTitle=el('h4','Or start from a template');startTemplatesTitle.id='custom-curves-start-templates-title';
 start.append(startTitle,el('p','Describe the curve you need and the assistant sets it up, or build it yourself from a blank graph or a template.'),startActions,startTemplatesTitle,startTemplates);

 // Configuration: the code, name, kind and template, the ingredients the graph reads, and combining published curves.
 const configuration=view('custom-curves-tab-configuration');configuration.classList.add('custom-curve-config');
 const details=el('div');details.id='custom-curves-details';
 const templates=el('select');templates.id='custom-curves-template';templates.setAttribute('aria-label','Custom curve template');
 const code=field('custom-curves-code'),name=field('custom-curves-name');code.required=true;name.required=true;
 const detailHelp=el('p','The code is the curve’s permanent identity; it cannot change once the curve is saved.');detailHelp.className='field-help';
 const kindNote=el('p');kindNote.id='custom-curves-config-kind';kindNote.className='custom-curve-config-kind';
 details.append(label('Code',code),label('Name',name),detailHelp,el('h4','Kind'),kindNote,label('Start from a template',templates));
 const ingredientsPart=el('div');const ingredients=el('ul');ingredients.id='custom-curves-ingredients';ingredients.className='custom-curve-ingredients';ingredients.setAttribute('aria-labelledby','custom-curves-ingredients-title');
 const ingredientsTitle=el('h4','Ingredients');ingredientsTitle.id='custom-curves-ingredients-title';ingredientsPart.append(ingredientsTitle,ingredients);
 const blend=setupCurveBlend(request=>void o.act(async()=>{
  if(!code.reportValidity()||!name.reportValidity())throw Error('Enter a code and name for this curve.');
  const started=guard(),source=o.source();
  const prepared=await o.api<{definition:CurveDefinition;description:string}>('custom-curves/compose',{snapshotId:source.snapshotId,composition:{...request,code:code.value,name:name.value}});
  if(!current(started))return;
  openDefinition(prepared.definition,{},selected,'graph');dirty=true;blend.clean();templateHelp.textContent=prepared.description;renderStatus();
 }),()=>{generation++;templateHelp.textContent='';clearPreview();renderStatus();});
 ingredientsPart.append(blend.element);configuration.append(details,ingredientsPart);

 // Versions: the curve's effective versions.
 const activation=view('curve-activation');
 const activationNote=el('p','Load the selected curve’s timeline. Start dates are included and end dates are excluded. Publishing replaces the complete timeline for later knowledge; omitted dates become inactive. Booked exact versions stay pinned.');activationNote.className='field-help';
 const activationState=el('p');activationState.id='curve-activation-state';activationState.setAttribute('role','status');
 const activationRows=el('div');activationRows.id='curve-activation-rows';const activationForm=el('form');
 const activationReason=field('curve-activation-reason');activationReason.required=true;activationReason.maxLength=500;
 const loadActivationButton=button('Load timeline','curve-activation-load',()=>void o.act(loadActivation));loadActivationButton.className='secondary';
 const effectiveButton=button('Preview effective version','custom-curves-effective',()=>void o.act(previewEffective));effectiveButton.className='secondary';
 const addActivationButton=button('Add effective interval','curve-activation-add',()=>{if(!o.busy()){activationRow();activationEdited();sync();}});addActivationButton.className='secondary';
 const publishActivationButton=button('Publish complete timeline','curve-activation-publish',()=>void o.act(publishActivation));
 const activationActions=el('div');activationActions.className='custom-curve-view-actions';activationActions.append(loadActivationButton,effectiveButton);
 activationForm.append(activationRows,label('Reason for replacement',activationReason),addActivationButton,publishActivationButton);
 activationForm.onsubmit=event=>event.preventDefault();activationForm.oninput=activationEdited;activationForm.onchange=activationEdited;
 activation.append(activationNote,activationActions,activationState,activationForm);

 // Graph: the visual or formula board, with the operations to add and the selected operation's inspector beside it.
 const graphTab=el('section');graphTab.id='custom-curves-tab-graph';graphTab.className='custom-curve-graph-tab';
 const centre=el('section');centre.className='custom-curve-centre';centre.setAttribute('aria-label','Calculation graph');
 const toolbar=el('div');toolbar.className='custom-curve-toolbar';const modes=el('div');modes.className='custom-curve-mode';modes.setAttribute('role','group');modes.setAttribute('aria-label','Curve authoring mode');
 const visualButton=button('Visual','custom-curves-mode-visual',()=>setMode('visual')),formulaButton=button('Formula','custom-curves-mode-formula',()=>{if(o.busy())return;setMode('formula');void o.act(formatFormula);});modes.append(visualButton,formulaButton);
 const board=el('div');board.id='custom-curves-graph';board.className='custom-curve-board';board.setAttribute('aria-label','Editable custom curve graph');
 const scene=el('div');scene.className='custom-curve-scene';const canvas=el('div');canvas.className='custom-curve-canvas';scene.append(canvas);board.append(scene);
 const ns='http://www.w3.org/2000/svg',svg=document.createElementNS(ns,'svg'),wires=document.createElementNS(ns,'g'),draft=document.createElementNS(ns,'path');
 svg.classList.add('custom-curve-wires');svg.setAttribute('role','group');svg.setAttribute('aria-label','Connections');draft.classList.add('custom-curve-draft');draft.style.display='none';svg.append(wires,draft);
 const menu=setupMenu('custom-curve-menu');
 const connectNote=el('div');connectNote.className='custom-curve-connect';connectNote.hidden=true;const connectText=el('span');connectText.setAttribute('role','status');
 const cancelConnectButton=button('Cancel','custom-curves-connect-cancel',()=>cancelConnect());cancelConnectButton.className='secondary';connectNote.append(connectText,cancelConnectButton);const graphTools=el('div');graphTools.className='custom-curve-graph-tools';const zoomLabel=el('span');zoomLabel.className='custom-curve-zoom';const fitButton=button('Fit graph','custom-curves-fit',()=>{if(o.busy())return;fit=true;positionCanvas();});const actualButton=button('100%','custom-curves-actual-size',()=>{if(o.busy())return;fit=false;zoom=1;positionCanvas();});const arrangeButton=button('Arrange','custom-curves-arrange',()=>{if(o.busy()||!editAllowed())return;layout={};renderGraph();});for(const b of [fitButton,actualButton,arrangeButton])b.className='secondary';graphTools.append(zoomLabel,fitButton,actualButton,arrangeButton);
 // The operations and the inspector beside the board fold away to give the board their width; selecting a card shows them again.
 const sideToggle=button('Hide operations','custom-curves-toggle-side',()=>showSide(!!side.hidden));sideToggle.className='secondary';sideToggle.setAttribute('aria-controls','custom-curves-side');sideToggle.setAttribute('aria-expanded','true');
 toolbar.append(modes,graphTools,sideToggle);
 const expression=el('div');expression.id='custom-curves-expression';expression.hidden=true;const formula=el('textarea');formula.id='custom-curves-formula';formula.rows=7;formula.spellcheck=false;formula.setAttribute('aria-label','Curve formula');
 const compile=button('Apply formula','custom-curves-compile',()=>void o.act(async()=>{clearPreview();const request=guard();const result=await o.api<{definition:CurveDefinition;layout?:CurveLayout}>('custom-curves/compile',{formula:formula.value,snapshotId:o.source().snapshotId,code:code.value,name:name.value});if(!current(request))return;definition=clone(result.definition);layout={...(result.layout??layout)};dirty=true;formulaDirty=false;render();notifyMarketScope();}));
 const syntax=el('p');syntax.className='field-help';syntax.textContent='Use one assignment per line: node = operation(parameter="value", input=@other_node). Parameters are quoted strings; contract mappings use typed JSON rows and connected inputs use @node. The backend compiles this into the same typed graph.';expression.append(label('Formula',formula),compile,syntax);
 const hint=el('p','Drag cards to arrange the graph and drag from a card’s output dot to an input to connect. Right-click an arrow, card or listed contract (or press Shift+F10) for its actions; select a card to edit it beside the graph.');hint.className='custom-curve-hint';
 centre.append(toolbar,connectNote,board,expression,hint);
 const side=el('div');side.id='custom-curves-side';side.className='custom-curve-side';
 const palette=el('section');palette.id='custom-curves-palette';palette.className='custom-curve-palette';palette.setAttribute('aria-labelledby','custom-curves-palette-title');const paletteTitle=el('h3','Add an operation');paletteTitle.id='custom-curves-palette-title';
 const operations=el('div');operations.id='custom-curves-operators';palette.append(paletteTitle,operations);
 const inspector=el('section');inspector.id='custom-curves-inspector';inspector.className='custom-curve-view custom-curve-inspector';inspector.setAttribute('aria-labelledby','custom-curves-inspector-title');
 const inspectorTitle=el('h3','Selected operation');inspectorTitle.id='custom-curves-inspector-title';const properties=el('div');properties.id='custom-curves-properties';inspector.append(inspectorTitle,properties);
 side.append(palette,inspector);graphTab.append(centre,side);
 function showSide(on:boolean){side.hidden=!on;sideToggle.textContent=on?'Hide operations':'Show operations';sideToggle.setAttribute('aria-expanded',String(on));}
 // The graph is measured again when its tab comes into view and whenever its room changes, as when its group is resized. The board's
 // own size is not watched: its scrollbars come and go with the graph it draws.
 function remeasure(){if(board.hidden||!measurable())return;if(estimated.size)renderGraph();else positionCanvas();}
 new ResizeObserver(remeasure).observe(centre);

 // Outputs: the preview's tiles, chart and table, or the way to run one. Lineage: its dependencies, observations and trace.
 const outputs=el('section');outputs.id='custom-curves-tab-outputs';outputs.className='custom-curve-outputs';
 const outputsEmpty=el('div');outputsEmpty.id='custom-curves-outputs-empty';outputsEmpty.className='custom-curve-outputs-empty';
 const outputsPreview=button('Preview','custom-curves-outputs-preview',()=>void o.act(preview));
 outputsEmpty.append(el('p','No preview yet. Preview calculates this curve’s prices from the selected market context.'),outputsPreview);
 const result=el('div');result.id='custom-curves-result';result.className='custom-curve-preview';const receipt=el('p');receipt.id='custom-curves-receipt';receipt.className='field-help';
 const summaryTiles=el('div');summaryTiles.id='custom-curves-summary';summaryTiles.className='desk-tiles custom-curve-tiles';
 const chart=el('div');chart.id='custom-curves-chart';chart.className='chart';const table=el('div');table.id='custom-curves-values';table.className='table-wrap';result.append(receipt,summaryTiles,chart,table);result.hidden=true;
 outputs.append(outputsEmpty,result);
 const lineage=view('custom-curves-tab-lineage');const lineageEmpty=el('p','Preview the curve to see the dependencies, source observations and evaluation trace it was calculated from.');lineageEmpty.className='field-help';
 const lineageBody=el('div');lineageBody.id='custom-curves-lineage';lineage.append(lineageEmpty,lineageBody);

 const dockHost=el('div');dockHost.id='custom-curves-dock';
 const dock=setupDock(dockHost,{narrow:o.narrow,storageKey:'openetrm.custom-curves.layout.v1',shown:id=>{if(id==='graph')remeasure();},tabs:[{id:'outputs',label:'Outputs',body:outputs},{id:'graph',label:'Graph',body:graphTab},
  {id:'configuration',label:'Configuration',body:configuration},{id:'versions',label:'Versions',body:activation},{id:'lineage',label:'Lineage',body:lineage}]});
 const workspace=el('div');workspace.className='custom-curve-main';workspace.append(start,dockHost);desk.append(rail,workspace);
 host.replaceChildren(head,templateHelp,accessNote,pendingPanel,prompt,desk,menu.element);
 function guard(){return {generation,identity:o.identity()};}function current(request:{generation:number;identity:string}){return generation===request.generation&&o.identity()===request.identity;}
 function notifyMarketScope(){if(initialized)o.marketScopeChanged?.();}
 function dateDefinition():CurveDefinition|undefined{return catalog&&!formulaDirty&&definition.nodes.length?clone(definition):undefined;}
 function invalidate(){opened=true;generation++;dirty=true;effectiveCurve=undefined;templateHelp.textContent='';clearPreview();renderStatus();notifyMarketScope();}
 function clearPreview(){lastPreview=undefined;result.hidden=true;outputsEmpty.hidden=false;lineageEmpty.hidden=false;receipt.textContent='';summaryTiles.replaceChildren();clearChart(chart);table.replaceChildren();lineageBody.replaceChildren();}
 function editAllowed(){return !!catalog?.canEdit&&!o.historical();}
 function renderStatus(){accessNote.hidden=!catalog||editAllowed();const previewAccess=document.getElementById('preview-access');accessNote.textContent=o.historical()?'This historical view is read-only. Return to current inputs to edit curves.':catalog?.canPublish?'This account can publish saved drafts, but editing is read-only. '+(previewAccess&&!previewAccess.hidden?'Choose Market Data Editor in Profile → Preview access to edit.':'Ask your workspace administrator for market-data authoring access.'):'Curve editing is read-only for this account. '+(previewAccess&&!previewAccess.hidden?'Choose Market Data Editor in Profile → Preview access to edit.':'Ask your workspace administrator for market-data authoring access.');
  start.hidden=opened;dockHost.hidden=!opened;resetLayoutButton.hidden=!opened;for(const ask of [askButton,startAsk])ask.hidden=!o.assistant.available();
  heading.textContent=publicText(!opened?'No curve open':definition.name||(selected?selected.definition.name:'New curve'));
  // The backend names a saved version's kind; an edit is not classified until it is saved.
  const kind=selected&&!dirty?selected.kind:undefined;
  headKind.replaceChildren(...kind?[kindBadge(kind,'md')]:[]);
  kindNote.replaceChildren(...kind?[kindBadge(kind),el('span',kindLabel(kind))]:[el('span','The kind is named when the curve is saved.')]);
  renderIngredients();
  const parts:HTMLElement[]=[];
  if(selected)parts.push(chip(recipeStateLabel(selected.state),selected.state),el('span','Version '+selected.version));
  if(dirty)parts.push(chip('Unsaved changes','unsaved'),...(selected?[]:[el('span','Not saved yet')]));
  if(selected?.publishedAt)parts.push(el('span','Published '+displayInstant(selected.publishedAt,o.zone())));
  if(!opened)parts.push(el('span','Choose a saved curve, ask the assistant, or start a new curve.'));
  else if(!selected&&!dirty)parts.push(el('span','Not saved yet.'));
  if(o.historical())parts.push(el('span','Historical view is read-only.'));
  const missing=missingContractInputs(definition);
  if(missing.length){const enter=missing.filter(m=>m!=='contract').join(' and '),steps=[...missing.includes('contract')?['choose its contract']:[],...enter?[`enter its ${enter}`]:[]];const note=el('span',`For each contract connection, ${steps.join(' and ')} to preview or save.`);note.id='custom-curves-weight-needed';parts.push(note);}
  if(blend.dirty()){const go=button('Build or discard the ingredient changes in Combine curves','custom-curves-open-combine',()=>showCombine());go.className='text-button';parts.push(go);}
  status.replaceChildren(...parts.flatMap((part,i)=>i?[space(),part]:[part]));
  markLibrary();sync();}
 /** The curves and published curves the graph reads, each with its kind as the catalog names it. */
 function renderIngredients(){
  const read=definition.nodes.filter(node=>node.kind==='source'||node.kind==='published_source');
  ingredients.replaceChildren(...read.map(node=>{
   const kind=ingredientKind(node),item=el('li'),named=node.kind==='source'?catalog?.sources.find(s=>s.code===node.parameters.curve)?.name:catalog?.publishedSources?.find(s=>JSON.stringify(s.reference)===JSON.stringify(node.publishedSource))?.label;
   item.append(...kind?[kindBadge(kind)]:[],el('span',named??'Curve not chosen'),el('small',node.id));return item;}));
  if(!read.length)ingredients.append(el('li','This graph reads no curves yet.'));
 }
 /** Shows Combine curves in Configuration, for the ingredient changes waiting to be built or discarded. */
 function showCombine(){dock.show('configuration');blend.element.scrollIntoView({block:'nearest'});blend.element.querySelector<HTMLElement>('select:enabled,input:enabled')?.focus({preventScroll:true});}
 /** Marks the library's entry for the exact version open, if it is one. */
 function markLibrary(){for(const entry of library.querySelectorAll<HTMLElement>('[data-recipe]')){if(selected&&entry.dataset.recipe===selected.id+'|'+selected.version)entry.setAttribute('aria-current','true');else entry.removeAttribute('aria-current');}}
 function sync(){const busy=o.busy(),editable=editAllowed(),retryAllowed=['custom-curves/publish','custom-curves/activation'].includes(pending.command?.path??'')?!!catalog?.canPublish&&!o.historical():pending.command?.path==='custom-curves/save'?editable:false;for(const input of [code,name,templates])input.disabled=busy||!editable;code.disabled ||=!!selected;retryButton.disabled=busy||!retryAllowed;discardButton.disabled=busy;pendingPanel.hidden=!pending.command;newButton.disabled=startNewButton.disabled=busy||!editable;for(const b of startTemplates.querySelectorAll('button'))b.disabled=busy||!editable;reloadButton.disabled=busy;for(const entry of library.querySelectorAll('button'))entry.disabled=busy;templateHelp.hidden=!templateHelp.textContent;const incomplete=missingContractInputs(definition).length>0;previewButton.disabled=busy||!catalog||!definition.nodes.length||formulaDirty||incomplete;effectiveButton.disabled=busy||!selected||dirty||formulaDirty;saveButton.disabled=busy||!editable||!definition.nodes.length||formulaDirty||incomplete;publishButton.disabled=busy||!catalog?.canPublish||o.historical()||!selected||selected.state==='published'||dirty;compile.disabled=busy||!editable||!catalog?.formulaSupported;formula.disabled=busy||!editable||!catalog?.formulaSupported;fitButton.disabled=busy;actualButton.disabled=busy;arrangeButton.disabled=busy||!editable;for(const b of operations.querySelectorAll('button'))b.disabled=busy||!editable;for(const input of properties.querySelectorAll<HTMLInputElement|HTMLSelectElement|HTMLButtonElement>('input,select,button,textarea'))input.disabled=busy||!editable;
  for(const control of [previewButton,effectiveButton,saveButton,publishButton])control.disabled ||=blend.dirty();
  effectiveButton.disabled ||=activationDirty;
  outputsPreview.disabled=previewButton.disabled;
  o.unsavedChanged?.();
  blend.sync(busy,editable&&!pending.command,definition.nodes.length>0);
  // Combining needs the catalog's weighted-panel operation; without it Combine curves is not offered.
  blend.element.hidden=!catalog?.operators.some(op=>op.kind==='weighted_panels');
  loadActivationButton.disabled=busy||!selected;const canActivate=!!catalog?.canPublish&&!o.historical()&&activationLoaded&&!!selected&&!pending.command;
  for(const input of activationForm.querySelectorAll<HTMLInputElement|HTMLSelectElement|HTMLButtonElement>('input,select,button'))input.disabled=busy||!canActivate;
 }
 // The formula shows only the graph it was formatted from: an earlier one is cleared first, so a graph the backend cannot format shows none.
 async function formatFormula(){if(!catalog?.formulaSupported||formulaDirty)return;formula.value='';const request=guard();const result=await o.api<{formula:string}>('custom-curves/format',{definition});if(!current(request))return;formula.value=result.formula;formulaDirty=false;sync();}
 function setMode(value:string){if(o.busy())return;mode=value;board.hidden=value!=='visual';expression.hidden=value!=='formula';graphTools.hidden=value!=='visual';if(value==='visual'){if(estimated.size)renderGraph();else{sizeCanvas();positionCanvas();}}for(const b of [visualButton,formulaButton]){const active=b.id.endsWith(value);b.classList.toggle('active',active);b.setAttribute('aria-pressed',String(active));}sync();}
 function openDefinition(next:CurveDefinition,positions:CurveLayout={},recipe?:Recipe,show?:'graph'|'configuration'){opened=true;generation++;clearGraphModes();effectiveCurve=undefined;clearActivation();mode='visual';definition=clone(next);layout={...positions};estimated.clear();selected=recipe;selectedNode=definition.nodes.find(node=>(catalog?.operators.find(operator=>operator.kind===node.kind)?.fields.length??0)>0)?.id||definition.output||definition.nodes[0]?.id||'';dirty=false;formulaDirty=false;formula.value='';templateHelp.textContent='';code.value=definition.code;name.value=definition.name;clearPreview();render();notifyMarketScope();if(show)dock.show(show);}
 function openRecipe(recipe:Recipe){if(catalog){catalog.recipes=[...catalog.recipes.filter(r=>r.id!==recipe.id||r.version!==recipe.version),recipe];renderLibrary();}openDefinition(recipe.definition,recipe.layout,recipe);setTemplate('');}
 function renderLibrary(){const recipes=catalog?.recipes??[];
  const entry=(recipe:Recipe)=>{const item=el('li'),entry=button('','',()=>replacing(()=>openRecipe(recipe))),title=el('span',recipe.definition.name);entry.className='custom-curve-entry';entry.dataset.recipe=recipe.id+'|'+recipe.version;title.className='custom-curve-entry-name';entry.setAttribute('aria-label',`${recipe.definition.name}, ${kindLabel(recipe.kind)}, ${recipeStateLabel(recipe.state)}, ${recipe.definition.code} version ${recipe.version}`);entry.append(kindBadge(recipe.kind),title,chip(recipeStateLabel(recipe.state),recipe.state),el('small',`${recipe.definition.code} · v${recipe.version}`));item.append(entry);return item;};
  // Saved curves are grouped by kind, in the kinds' order; each group keeps the library's order. Every entry is drawn first, so that an
  // unknown kind fails rather than drops its curve.
  const entries=recipes.map(recipe=>({kind:recipe.kind,item:entry(recipe)}));
  library.replaceChildren(...kindGroups.flatMap(g=>g.kinds).flatMap(kind=>{const listed=entries.filter(e=>e.kind===kind);if(!listed.length)return [];
   const group=el('li'),name=el('span',kindLabel(kind)),list=el('ul');group.className='custom-curve-kind-group';name.className='custom-curve-kind-name';name.id='custom-curves-kind-'+kind;
   list.className='custom-curve-list';list.setAttribute('aria-labelledby',name.id);list.append(...listed.map(e=>e.item));group.append(name,list);return [group];}));
  if(catalog&&!recipes.length){const none=el('li','No saved curves yet. Start a new curve to save one.');none.className='custom-curve-empty';library.append(none);}
  markLibrary();
  // Ingredients chosen for a combination the catalog no longer offers cannot be built, so they are not kept to block the page.
  if(!catalog?.operators.some(op=>op.kind==='weighted_panels'))blend.reset();
  templates.replaceChildren();option(templates,'','Choose a template…');for(const template of catalog?.templates??[])option(templates,template.code,template.name);
  startTemplates.replaceChildren(...(catalog?.templates??[]).map(template=>{const item=el('li'),choose=button(template.name,'',()=>replacing(()=>openTemplate(template)));choose.className='secondary';choose.dataset.template=template.code;if(template.description)choose.title=publicText(template.description);item.append(choose);return item;}));const combination=catalog?.operators.find(op=>op.kind==='weighted_panels');blend.catalog(catalog?.publishedSources??[],combination?.fields.find(f=>f.key==='aggregation')?.options??[],combination?.fields.find(f=>f.key==='alignment')?.options??[]);}
 /** Follows the page's width; a page out of view is arranged when it is next read, so a resize elsewhere does not redraw it. */
 function place(){if(!host.hidden)dock.relayout();}
 async function load(){dock.relayout();clearActivation();clearPreview();const request=guard();catalog=undefined;library.replaceChildren();templates.replaceChildren();operations.replaceChildren();properties.replaceChildren();canvas.replaceChildren();code.value='';name.value='';sync();notifyMarketScope();const loaded=await o.api<Catalog>('custom-curves');if(!current(request))return;if(loaded.kindsVersion!==kindsVersion)throw Error('The curve library is inconsistent. Refresh and try again.');catalog=loaded;discarded=false;if(selected&&!catalog.recipes.some(recipe=>recipe.id===selected?.id&&recipe.version===selected.version)){opened=false;definition=empty();layout={};selected=undefined;selectedNode='';dirty=false;effectiveCurve=undefined;}try{const stored=sessionStorage.getItem(storageKey());pending.command=stored?{...JSON.parse(stored),uncertain:true}:undefined;}catch{pending.clear();}renderLibrary();renderPalette();formulaButton.hidden=!catalog.formulaSupported;render();notifyMarketScope();}
 async function reloadSelected(){const request=guard(),id=selected?.id,version=selected?.version;await load();if(!current(request)||!id)return;const fresh=catalog?.recipes.find(recipe=>recipe.id===id&&recipe.version===version);if(fresh)openRecipe(fresh);}
 let shownTemplate='';
 function setTemplate(code:string){templates.value=code;shownTemplate=code;}
 function openTemplate(template:Template){openDefinition(template.definition);setTemplate(template.code);templateHelp.textContent=template.description??template.unavailableReason??'';dirty=true;renderStatus();}
 templates.onchange=()=>{
  if(o.busy()||!editAllowed())return;
  const template=catalog?.templates.find(t=>t.code===templates.value);if(!template)return;
  // The select keeps showing the template the graph is until the person agrees to replace it.
  templates.value=shownTemplate;
  replacing(()=>openTemplate(template));
 };
 code.oninput=()=>{definition={...definition,code:code.value};invalidate();};name.oninput=()=>{definition={...definition,name:name.value};invalidate();};formula.oninput=()=>{formulaDirty=true;invalidate();};
 function renderPalette(){operations.replaceChildren();for(const operator of catalog?.operators??[]){const b=button(operator.label,'',()=>{if(o.busy()||!editAllowed())return;addNode(operator);});b.title=operator.description??operator.label;operations.append(b);}}
 function addNode(operator:Operator,parameters:Record<string,unknown>={}){let i=1;while(definition.nodes.some(n=>n.id===operator.kind+'_'+i))i++;const id=operator.kind+'_'+i;definition={...definition,nodes:[...definition.nodes,{id,kind:operator.kind,parameters,inputs:{}}]};selectedNode=id;invalidate();render();dock.show('graph');}
 /** Selecting a card shows its operation in the inspector beside the graph; focus on the card stays on it. */
 function selectNode(id:string){if(o.busy()||drag)return;const fromCard=canvas.contains(document.activeElement);selectedNode=id;dock.show('graph');showSide(true);renderGraph();renderInspector();
  if(fromCard)canvas.querySelector<HTMLButtonElement>(`[data-node-id="${CSS.escape(id)}"] button`)?.focus({preventScroll:true});}
 function nodeTitle(node:CurveNode){return catalog?.operators.find(op=>op.kind===node.kind)?.label??node.kind;}
 function fieldValue(node:CurveNode,field:Field){return field.type==='published_source'?node.publishedSource:node.parameters[field.key];}
 function describe(node:CurveNode){if(node.kind==='weighted_contracts')return `${node.contractWeights?.length??0} explicit contract connections · ${Object.keys(node.inputs).length} sources`;const fields=catalog?.operators.find(operator=>operator.kind===node.kind)?.fields??[];return fields.filter(field=>curveFieldVisible(field,node.parameters)&&fieldValue(node,field)!==undefined).map(field=>{const value=fieldValue(node,field),choice=choicesFor(field).find(choice=>JSON.stringify(choice.value)===JSON.stringify(value));return field.label+': '+(choice?.label??(field.type==='schedule'?'Retained schedule':field.type==='published_source'?'Retained published output':String(value)));}).join(' · ');}
 function render(){code.value=definition.code;name.value=definition.name;renderGraph();renderInspector();renderStatus();setMode(mode);}
 function positionCanvas(){const width=parseFloat(canvas.style.width)||720,height=parseFloat(canvas.style.height)||340;if(fit&&board.clientWidth)zoom=Math.max(.2,Math.min(1,(board.clientWidth-24)/width,(board.clientHeight-24)/height));canvas.style.transform=`scale(${zoom})`;scene.style.width=width*zoom+'px';scene.style.height=height*zoom+'px';zoomLabel.textContent=Math.round(zoom*100)+'%';drawWires();}
 const operatorOf=(node:CurveNode|undefined)=>catalog?.operators.find(op=>op.kind===node?.kind);
 const cardOf=(id:string)=>canvas.querySelector<HTMLElement>(`.custom-curve-node[data-node-id="${CSS.escape(id)}"]`);
 /** An operation's input ports: the catalog's ports, then the dynamic inputs the definition names. */
 function nodePorts(node:CurveNode){const operator=operatorOf(node);return [...(operator?.inputPorts??[]),...(operator?.dynamicInputs?Object.keys(node.inputs).filter(name=>!operator.inputPorts.some(p=>p.name===name)).map(name=>({name,label:name.replaceAll('_',' '),type:operator.dynamicInputs!.type})):[])];}
 /** The contracts the catalog lists for the curve a source operation reads, as the catalog names them; none for any other operation. */
 function listedContracts(node:CurveNode|undefined){const field=operatorOf(node)?.fields.find(f=>f.type==='curve');return field?catalog?.sources.find(s=>s.code===node!.parameters[field.key])?.contracts??[]:[];}
 /** A mapping row's contract, named as the catalog lists it for the source feeding the row. */
 function contractName(source:string|null,contractId:string){if(!contractId)return 'Contract not chosen';return listedContracts(definition.nodes.find(n=>n.id===source)).find(c=>c.id===contractId)?.label??'Retained contract';}
 /** Whether any mapping connects this listed contract of this source. */
 function connected(source:string,contractId:string){return definition.nodes.some(node=>contractObjects(definition,node).some(row=>row.source===source&&row.contractId===contractId));}
 function clearGraphModes(){connecting=undefined;shown=undefined;expanded.clear();listScroll.clear();menu.close();connectNote.hidden=true;}

 // Connect mode: the valid targets of a new or moved arrow (catalog port types) or of a picked contract (the mappings that take it) are offered as
 // buttons; anything else is dimmed. A click or Enter on one connects; Escape or Cancel leaves the graph as it was.
 const CONTRACT='';
 function connectTargets():ConnectableTarget[]{
  if(!connecting||!catalog)return [];const {source,contract,replace}=connecting;
  if(contract!==undefined)return contractTargets(definition,catalog,source).map(target=>({target,port:CONTRACT,added:false}));
  return connectableTargets(definition,catalog,source).filter(t=>!(replace&&t.target===replace.target&&t.port===replace.port));
 }
 function startConnect(source:string,options:{contract?:string;replace?:{target:string;port:string}},refocus:()=>void,focusTarget=true){
  if(o.busy()||!editAllowed())return;menu.close();connecting={source,...options,refocus};renderGraph();
  const node=definition.nodes.find(n=>n.id===source),what=options.contract!==undefined?'Fixed contract · '+contractName(source,options.contract):nodeTitle(node!)+' '+source;
  connectText.textContent=connectTargets().length?(options.contract!==undefined?`Choose a contract mapping for ${what}.`:`Choose an input for the arrow from ${what}.`)+' Escape cancels.':`No input in this graph accepts ${what}.`;connectNote.hidden=false;
  if(focusTarget)canvas.querySelector<HTMLButtonElement>('[data-connect-target]')?.focus({preventScroll:true});
 }
 function cancelConnect(){if(!connecting)return;const back=connecting.refocus;connecting=undefined;connectNote.hidden=true;renderGraph();back();}
 function connectTo(target:string,port:string){
  if(!connecting||o.busy()||!editAllowed())return;const {source,contract,replace}=connecting;connecting=undefined;connectNote.hidden=true;
  if(contract!==undefined){const picked=pickContract(definition,catalog!,target,source,contract);definition=picked.definition;invalidate();editRow(target,picked.row,'Weight');return;}
  definition=changeCurveConnection(replace?removeCurveConnection(definition,replace.target,replace.port):definition,target,port,source);invalidate();renderGraph();renderInspector();focusWire(`${target}|${port}`);
 }
 /** Shows a mapping in the inspector and puts focus in one field of one of its rows: the existing editor is where every row value is entered. */
 function editRow(target:string,row:number,fieldName:'Weight'|'Source'|'Contract'){selectedNode=target;dock.show('graph');showSide(true);renderGraph();renderInspector();
  properties.querySelector<HTMLElement>(`[aria-label="${fieldName} for connection ${row+1}"]`)?.focus();}
 function addContractRow(target:string){const rows=definition.nodes.find(n=>n.id===target)?.contractWeights??[];definition=changeContractWeights(definition,target,[...rows,{outputPosition:null,sourcePort:'',contractId:'',weight:''}]);invalidate();return rows.length;}
 function removeNode(id:string){for(const target of definition.nodes)for(const [port,source] of Object.entries(target.inputs))if(source===id)definition=removeCurveConnection(definition,target.id,port);definition={...definition,output:definition.output===id?'':definition.output,nodes:definition.nodes.filter(n=>n.id!==id)};delete layout[id];expanded.delete(id);selectedNode=definition.nodes[0]?.id??'';invalidate();render();}
 function focusCard(id:string){cardOf(id)?.querySelector<HTMLButtonElement>('.custom-curve-node-handle')?.focus({preventScroll:true});}
 function focusWire(key:string){svg.querySelector<SVGGElement>(`[data-wire="${CSS.escape(key)}"]`)?.focus({preventScroll:true});}

 // Menus (context-menu.ts) on the graph's arrows, cards and contracts: Escape returns focus to the object a menu opened from.
 let menuTrigger:Element|undefined;
 const openMenu=(name:string,items:MenuItem[],at:{x:number;y:number},back:()=>void)=>menu.open(name,items,at,back,menuTrigger);
 document.addEventListener('keydown',event=>{if(event.key==='Escape'&&connecting&&menu.element.hidden&&host.contains(document.activeElement)){event.preventDefault();cancelConnect();}});
 const wireMenus=new WeakMap<SVGGElement,(at:{x:number;y:number})=>void>();
 /** Wires a graph object's context menu to right-click and to the context-menu key or Shift+F10. */
 function withMenu(element:HTMLElement|SVGElement,open:(at:{x:number;y:number})=>void,trigger:Element=element){
  element.addEventListener('contextmenu',event=>{event.preventDefault();event.stopPropagation();menuTrigger=trigger;open({x:(event as MouseEvent).clientX,y:(event as MouseEvent).clientY});});
  element.addEventListener('keydown',event=>{if(!menuKey(event as KeyboardEvent))return;event.preventDefault();event.stopPropagation();menuTrigger=trigger;open(placeOf(element));});
 }
 function cardMenu(node:CurveNode,at:{x:number;y:number}){
  const back=()=>focusCard(node.id);
  if(!editAllowed()){openMenu('Operation',[{label:'View',run:()=>selectNode(node.id)}],at,back);return;}
  const mapping=operatorOf(node)?.fields.some(f=>f.type==='contract_weights');
  openMenu('Operation',[{label:'Add arrow from here…',run:()=>startConnect(node.id,{},back)},
   ...(mapping?[{label:'Add contract connection…',run:()=>{if(!o.busy()&&editAllowed())editRow(node.id,addContractRow(node.id),'Source');}}]:[]),
   {label:'Remove operation',run:()=>{if(!o.busy()&&editAllowed())removeNode(node.id);}}],at,back);
 }
 function portWireMenu(edge:{source:string;target:string;port:string},at:{x:number;y:number}){
  const back=()=>focusWire(`${edge.target}|${edge.port}`);
  if(!editAllowed()){openMenu('Arrow',[{label:'View',run:()=>selectNode(edge.target)}],at,back);return;}
  const sources=connectableSources(definition,catalog!,edge.target,edge.port).map(id=>definition.nodes.find(n=>n.id===id)!);
  openMenu('Arrow',[{label:'Change source…',run:()=>openMenu('Choose a new source',sources.length?sources.map(c=>({label:`${nodeTitle(c)} · ${c.id}`,run:()=>{if(o.busy()||!editAllowed())return;definition=changeCurveConnection(definition,edge.target,edge.port,c.id);invalidate();renderGraph();renderInspector();back();}})):[{label:'No other operation’s output fits this input'}],at,back)},
   {label:'Change target port…',run:()=>startConnect(edge.source,{replace:{target:edge.target,port:edge.port}},back)},
   {label:'Remove arrow',run:()=>{if(o.busy()||!editAllowed())return;definition=removeCurveConnection(definition,edge.target,edge.port);invalidate();renderGraph();renderInspector();focusCard(edge.target);}}],at,back);
 }
 /** A contract connection's menu, from its arrow or its row. A row whose source input was removed has no arrow; its menu is how it is mended or removed.
  * Changing the source clears the contract, which belongs to the old source's catalog, so the person picks one again. */
 function contractMenu(target:string,row:number,at:{x:number;y:number}){
  const rowButton=()=>cardOf(target)?.querySelector<HTMLElement>(`[data-contract-row="${row}"] button`),node=definition.nodes.find(n=>n.id===target)!;
  const orphan=!contractObjects(definition,node)[row]?.source,back=()=>{if(!orphan&&svg.querySelector(`[data-wire="${CSS.escape(target+'#'+row)}"]`))focusWire(`${target}#${row}`);else rowButton()?.focus({preventScroll:true});};
  if(!editAllowed()){openMenu('Contract connection',[{label:'View',run:()=>editRow(target,row,'Weight')}],at,back);return;}
  const rows=()=>definition.nodes.find(n=>n.id===target)?.contractWeights??[];
  const ports=Object.entries(node.inputs).filter(([port,input])=>input&&port!==rows()[row]?.sourcePort);
  openMenu('Contract connection',[{label:'Change source…',run:()=>openMenu('Choose a new source',ports.length?ports.map(([port,input])=>{const fed=definition.nodes.find(n=>n.id===input);return {label:`${port.replaceAll('_',' ')} · ${fed?nodeTitle(fed)+' '+fed.id:input}`,run:()=>{if(o.busy()||!editAllowed())return;definition=changeContractWeights(definition,target,rows().map((r,n)=>n===row?{...r,sourcePort:port,contractId:''}:r));invalidate();editRow(target,row,'Contract');}};}):[{label:'No other source input on this mapping'}],at,back)},
   {label:'Edit weight',run:()=>editRow(target,row,'Weight')},
   {label:orphan?'Remove row':'Remove arrow',run:()=>{if(o.busy()||!editAllowed())return;definition=changeContractWeights(definition,target,rows().filter((_,n)=>n!==row));invalidate();renderGraph();renderInspector();focusCard(target);}}],at,back);
 }
 function listedMenu(source:string,contractId:string,at:{x:number;y:number}){
  const back=()=>cardOf(source)?.querySelector<HTMLElement>(`[data-contract-id="${CSS.escape(contractId)}"] button`)?.focus({preventScroll:true});
  const showing=shown?.source===source&&shown.contract===contractId,linked=connected(source,contractId);
  openMenu('Contract',[...(editAllowed()?[{label:'Connect to…',run:()=>startConnect(source,{contract:contractId},back)}]:[]),
   ...(linked?[{label:showing?'Hide connections':'Show connections',run:()=>{shown=showing?undefined:{source,contract:contractId};renderGraph();back();}}]:[])],at,back);
 }

 // Dragging from an output dot (a card's, or a listed contract's) to a highlighted input connects; a click without moving enters connect mode.
 function beginWire(event:PointerEvent,source:string,contract:string|undefined,back:()=>void){
  if(o.busy()||!editAllowed()||event.button!==0)return;event.preventDefault();event.stopPropagation();
  startConnect(source,contract===undefined?{}:{contract},back,false);
  const start=canvasPoint(event);wire={pointer:event.pointerId,x:start.x,y:start.y,startX:event.clientX,startY:event.clientY,moved:false};board.setPointerCapture(event.pointerId);
 }
 function canvasPoint(event:{clientX:number;clientY:number}){const base=canvas.getBoundingClientRect();return {x:(event.clientX-base.left)/zoom,y:(event.clientY-base.top)/zoom};}
 board.addEventListener('pointermove',event=>{if(!wire||event.pointerId!==wire.pointer)return;if(Math.hypot(event.clientX-wire.startX,event.clientY-wire.startY)>4)wire.moved=true;if(!wire.moved)return;
  const to=canvasPoint(event),dx=Math.max(30,Math.abs(to.x-wire.x)/2);draft.setAttribute('d',`M ${wire.x} ${wire.y} C ${wire.x+dx} ${wire.y}, ${to.x-dx} ${to.y}, ${to.x} ${to.y}`);draft.style.display='';});
 const endWire=(event:PointerEvent)=>{if(!wire||event.pointerId!==wire.pointer)return;const moved=wire.moved;wire=undefined;draft.style.display='none';draft.removeAttribute('d');
  if(!moved){canvas.querySelector<HTMLButtonElement>('[data-connect-target]')?.focus({preventScroll:true});return;}
  const target=document.elementFromPoint(event.clientX,event.clientY)?.closest<HTMLButtonElement>('[data-connect-target]');if(target&&canvas.contains(target))target.click();else cancelConnect();};
 board.addEventListener('pointerup',endWire);board.addEventListener('pointercancel',endWire);

 /** The backend's kind of the curve a source card reads, when the catalog lists it. */
 function ingredientKind(node:CurveNode){
  if(node.kind==='source')return catalog?.sources.find(s=>s.code===node.parameters.curve)?.kind;
  if(node.kind==='published_source')return catalog?.publishedSources?.find(s=>JSON.stringify(s.reference)===JSON.stringify(node.publishedSource))?.kind;
 }
 function targetButton(label:string,run:()=>void){const node=button('','',run);node.className='custom-curve-dot connectable';node.dataset.connectTarget='';node.setAttribute('aria-label',label);return node;}
 function renderCard(node:CurveNode,targets:ConnectableTarget[]){
  const position=layout[node.id],card=el('section'),title=nodeTitle(node),editable=editAllowed(),operator=operatorOf(node);
  const mine=targets.filter(t=>t.target===node.id),isSource=connecting?.source===node.id;
  card.className='custom-curve-node'+(selectedNode===node.id?' selected':'')+(definition.output===node.id?' output':'')+(isSource?' connect-source':'')+(connecting&&!isSource&&!mine.length?' dimmed':'');
  card.dataset.nodeId=node.id;card.dataset.kind=node.kind;card.style.left=position.x+'px';card.style.top=position.y+'px';card.setAttribute('aria-label',title+' '+node.id);
  card.onclick=event=>{if((event.target as Element).closest('button,li[data-contract-id]'))return;selectNode(node.id);};
  const handle=button(title,'',()=>selectNode(node.id)),kind=ingredientKind(node);handle.className='custom-curve-node-handle';handle.setAttribute('aria-label','Select or move '+title+(kind?', '+kindLabel(kind):''));if(kind)handle.prepend(kindBadge(kind));handle.setAttribute('aria-pressed',String(selectedNode===node.id));handle.setAttribute('aria-haspopup','menu');handle.setAttribute('aria-expanded','false');
  withMenu(card,at=>cardMenu(node,at),handle);
  handle.onkeydown=event=>{if(o.busy()||!editAllowed()||!['ArrowLeft','ArrowRight','ArrowUp','ArrowDown'].includes(event.key))return;event.preventDefault();layout[node.id]={x:Math.max(0,layout[node.id].x+(event.key==='ArrowRight'?12:event.key==='ArrowLeft'?-12:0)),y:Math.max(0,layout[node.id].y+(event.key==='ArrowDown'?12:event.key==='ArrowUp'?-12:0))};estimated.delete(node.id);renderGraph();focusCard(node.id);};
  handle.onpointerdown=event=>{if(o.busy()||!editAllowed()||event.button!==0)return;selectedNode=node.id;drag={id:node.id,pointer:event.pointerId,startX:event.clientX,startY:event.clientY,x:position.x,y:position.y};handle.setPointerCapture(event.pointerId);};
  handle.onpointermove=event=>{if(!drag||drag.id!==node.id||drag.pointer!==event.pointerId)return;layout[node.id]={x:Math.max(0,drag.x+(event.clientX-drag.startX)/zoom),y:Math.max(0,drag.y+(event.clientY-drag.startY)/zoom)};card.style.left=layout[node.id].x+'px';card.style.top=layout[node.id].y+'px';drawWires();};
  const end=(event:PointerEvent)=>{if(!drag||drag.id!==node.id||drag.pointer!==event.pointerId)return;drag=undefined;estimated.delete(node.id);renderGraph();renderInspector();};handle.onpointerup=end;handle.onpointercancel=end;
  const output=editable?button('','',()=>startConnect(node.id,{},()=>focusCard(node.id))):el('span');output.className='custom-curve-output';
  if(editable){output.setAttribute('aria-label','Add arrow from '+title+' '+node.id);output.onpointerdown=event=>beginWire(event,node.id,undefined,()=>focusCard(node.id));}
  card.append(handle,output,el('span',node.id),el('p',describe(node)||'Select to configure'));
  const ports=nodePorts(node);if(ports.length||mine.some(t=>t.added)){const list=el('ul');list.className='custom-curve-ports';
   for(const port of ports){const item=el('li'),t=mine.find(t=>t.port===port.name),fed=node.inputs[port.name],replaces=t&&fed?` · replaces ${fed}`:'';item.dataset.port=port.name;
    item.append(t?targetButton(`Connect to ${title} ${node.id} · ${port.label??port.name}${replaces}`,()=>connectTo(node.id,port.name)):Object.assign(el('span'),{className:'custom-curve-dot'+(fed?' filled':'')}),el('span',(port.label??port.name)+replaces));list.append(item);}
   for(const t of mine.filter(t=>t.added)){const item=el('li'),name=`New ${operator?.dynamicInputs?.label.toLowerCase()??'input'}`;item.dataset.port=t.port;item.className='added';item.append(targetButton(`Connect to ${title} ${node.id} · ${name}`,()=>connectTo(node.id,t.port)),el('span',name));list.append(item);}
   card.append(list);}
  if(operator?.fields.some(f=>f.type==='contract_weights')){const list=el('ul');list.className='custom-curve-contracts';list.setAttribute('aria-label','Contract connections of '+node.id);
   for(const row of contractObjects(definition,node)){
    const item=el('li'),orphan=row.source===null,text=orphan?'Fixed contract · Source disconnected':'Fixed contract · '+contractName(row.source,row.contractId);
    const open=button('','',()=>{menuTrigger=open;contractMenu(node.id,row.index,placeOf(open));}),weight=el('b',row.weight===''?'Weight needed':row.weight),meta=el('small',`${row.sourcePort.replaceAll('_',' ')||'no source'} · output ${row.outputPosition??'—'}`);
    open.className='custom-curve-row-open';open.setAttribute('aria-haspopup','menu');open.setAttribute('aria-expanded','false');open.setAttribute('aria-label',`${text} · weight ${row.weight||'not entered'}`);
    const words=el('span');words.append(el('span',text),meta);open.append(words,weight);withMenu(open,at=>contractMenu(node.id,row.index,at));
    item.dataset.contractRow=String(row.index);if(shown&&shown.source===row.source&&shown.contract===row.contractId)item.classList.add('shown');
    if(orphan||!row.contractId)item.classList.add('incomplete');if(row.weight==='')weight.className='missing';
    const dot=el('span');dot.className='custom-curve-dot'+(orphan?'':' filled');item.append(dot,open);list.append(item);}
   if(mine.length&&connecting?.contract!==undefined){const source=connecting.source,port=contractPort(definition,catalog!,node.id,source)!,connects=node.inputs[port]===source?'':` · connects ${source} as ${port.replaceAll('_',' ')}`,item=el('li');
    item.className='added';item.dataset.contractRow='new';item.append(targetButton(`Add the fixed contract to ${title} ${node.id}${connects}`,()=>connectTo(node.id,CONTRACT)),el('span','Add as a fixed contract'+connects));list.append(item);}
   if(list.childElementCount)card.append(list);}
  const contracts=listedContracts(node);if(contracts.length){
   const open=expanded.has(node.id),toggle=button(`${open?'Hide':'Show'} contracts · ${contracts.length}`,'',()=>{if(expanded.has(node.id))expanded.delete(node.id);else expanded.add(node.id);renderGraph();cardOf(node.id)?.querySelector<HTMLButtonElement>('.custom-curve-expand')?.focus({preventScroll:true});});
   toggle.className='custom-curve-expand';toggle.setAttribute('aria-expanded',String(open));toggle.setAttribute('aria-controls','custom-curve-listed-'+node.id);card.append(toggle);
   if(open){const list=el('ul');list.id='custom-curve-listed-'+node.id;list.className='custom-curve-listed';list.setAttribute('aria-label','Contracts of '+node.id);
    for(const contract of contracts){const item=el('li'),back=()=>cardOf(node.id)?.querySelector<HTMLElement>(`[data-contract-id="${CSS.escape(contract.id)}"] button`)?.focus({preventScroll:true});
     const pick=button(contract.label,'',()=>{if(editAllowed())startConnect(node.id,{contract:contract.id},back);else{menuTrigger=pick;listedMenu(node.id,contract.id,placeOf(pick));}});
     item.dataset.contractId=contract.id;pick.className='custom-curve-pick';pick.setAttribute('aria-haspopup','menu');pick.setAttribute('aria-expanded','false');pick.setAttribute('aria-label',`Fixed contract ${contract.label}`+(editAllowed()?', connect to a contract mapping':''));
     if(shown?.source===node.id&&shown.contract===contract.id)item.classList.add('shown');if(connected(node.id,contract.id))item.classList.add('connected');
     if(editAllowed())pick.onpointerdown=event=>beginWire(event,node.id,contract.id,back);withMenu(pick,at=>listedMenu(node.id,contract.id,at));item.append(pick);list.append(item);}
    card.append(list);list.scrollTop=listScroll.get(node.id)??0;list.addEventListener('scroll',()=>{listScroll.set(node.id,list.scrollTop);drawWires();},{passive:true});}
  }
  card.append(el('small',(operator?.outputType??'Value')+(definition.output===node.id?' · Curve output':'')));
  return card;
 }
 /** Draws the cards; a card without a kept position is placed by the measured heights of the rendered cards, so placed cards never overlap. */
 function renderGraph(){
  if(connecting&&!definition.nodes.some(n=>n.id===connecting!.source)){connecting=undefined;connectNote.hidden=true;}
  canvas.replaceChildren();canvas.classList.toggle('connecting',!!connecting);const targets=connectTargets();
  const fresh=new Set(definition.nodes.filter(n=>!layout[n.id]||estimated.has(n.id)).map(n=>n.id)),kept=Object.fromEntries(Object.entries(layout).filter(([id])=>!fresh.has(id)));
  layout=curveLayout(definition,kept);const cards=definition.nodes.map(node=>renderCard(node,targets));canvas.append(...cards,svg);
  if(fresh.size&&measurable()){layout=curveLayout(definition,kept,Object.fromEntries(cards.map(card=>[card.dataset.nodeId!,card.offsetHeight])));
   for(const card of cards){const position=layout[card.dataset.nodeId!];card.style.left=position.x+'px';card.style.top=position.y+'px';}estimated.clear();}
  else for(const id of fresh)estimated.add(id);
  if(!definition.nodes.length)canvas.append(el('p','Choose a saved curve, a template in Details, or add an operation to start your curve.'));
  sizeCanvas();positionCanvas();
 }
 const measurable=()=>canvas.getClientRects().length>0;
 /** Sizes the canvas to the rendered cards (their output dots included), so Fit and scrolling reach every card however tall it grows. */
 function sizeCanvas(){
  const cards=[...canvas.querySelectorAll<HTMLElement>('.custom-curve-node')],measured=measurable()&&cards.length>0,places=definition.nodes.map(n=>layout[n.id]).filter(Boolean);
  const width=Math.max(720,...(measured?cards.map(c=>c.offsetLeft+c.offsetWidth+32):places.map(p=>p.x+256))),height=Math.max(340,...(measured?cards.map(c=>c.offsetTop+c.offsetHeight+24):places.map(p=>p.y+184)));
  canvas.style.width=width+'px';canvas.style.height=height+'px';svg.setAttribute('width',String(width));svg.setAttribute('height',String(height));
 }
 /** Draws the arrows from where the cards' dots now are: each is a focusable object with a wide hit area and a context menu. */
 function drawWires(){
  if(board.hidden||!svg.isConnected)return;const base=canvas.getBoundingClientRect();if(!base.width)return;
  const at=(element:Element|null|undefined,side:'left'|'right',within?:Element|null)=>{if(!element)return undefined;const r=element.getBoundingClientRect();let y=r.top+r.height/2;if(within){const w=within.getBoundingClientRect();y=Math.max(w.top+6,Math.min(w.bottom-6,y));}return {x:((side==='left'?r.left:r.right)-base.left)/zoom,y:(y-base.top)/zoom};};
  // Arrows are kept and updated by key rather than rebuilt, so a focused arrow keeps its focus while cards move or lists scroll.
  const existing=new Map([...wires.children].map(g=>[(g as SVGGElement).dataset.wire!,g as SVGGElement])),drawn=new Set<string>();
  const draw=(from:{x:number;y:number}|undefined,to:{x:number;y:number}|undefined,key:string,label:string,classes:string[],open:(at:{x:number;y:number})=>void)=>{
   if(!from||!to)return;drawn.add(key);const dx=Math.max(36,Math.abs(to.x-from.x)/2),d=`M ${from.x} ${from.y} C ${from.x+dx} ${from.y}, ${to.x-7-dx} ${to.y}, ${to.x-7} ${to.y}`;
   let g=existing.get(key);
   if(!g){g=document.createElementNS(ns,'g');const wire=g;wire.dataset.wire=key;wire.setAttribute('tabindex','0');wire.setAttribute('role','button');wire.setAttribute('aria-haspopup','menu');wire.setAttribute('aria-expanded','false');
    const part=(name:string)=>{const p=document.createElementNS(ns,'path');p.classList.add(name);return p;};wire.append(document.createElementNS(ns,'title'),part('wire-hit'),part('wire-line'),part('wire-head'));
    withMenu(wire,place=>wireMenus.get(wire)?.(place));wire.addEventListener('keydown',event=>{if(event.key==='Enter'||event.key===' '){event.preventDefault();menuTrigger=wire;wireMenus.get(wire)?.(placeOf(wire));}});wires.append(wire);}
   wireMenus.set(g,open);g.setAttribute('class',['custom-curve-wire',...classes].join(' '));g.setAttribute('aria-label',label);g.querySelector('title')!.textContent=label;
   g.querySelector('.wire-hit')!.setAttribute('d',d);g.querySelector('.wire-line')!.setAttribute('d',d);g.querySelector('.wire-head')!.setAttribute('d',`M ${to.x-8} ${to.y-4.5} L ${to.x} ${to.y} L ${to.x-8} ${to.y+4.5} Z`);};
  const output=(id:string)=>at(cardOf(id)?.querySelector('.custom-curve-output'),'right');
  for(const edge of curveEdges(definition)){const source=definition.nodes.find(n=>n.id===edge.source),target=definition.nodes.find(n=>n.id===edge.target);if(!source||!target)continue;
   const port=cardOf(edge.target)?.querySelector(`[data-port="${CSS.escape(edge.port)}"] .custom-curve-dot`)??cardOf(edge.target)?.querySelector('.custom-curve-node-handle');
   draw(output(edge.source),at(port,'left'),`${edge.target}|${edge.port}`,`Arrow from ${nodeTitle(source)} ${source.id} to ${nodeTitle(target)} ${target.id} · ${edge.port.replaceAll('_',' ')}`,edge.target===selectedNode||edge.source===selectedNode?['selected']:[],place=>portWireMenu(edge,place));}
  for(const node of definition.nodes)for(const row of contractObjects(definition,node)){if(!row.source)continue;const list=expanded.has(row.source)?cardOf(row.source)?.querySelector('.custom-curve-listed'):null;
   const from=list?at(list.querySelector(`[data-contract-id="${CSS.escape(row.contractId)}"]`),'right',list):undefined;
   const classes=['contract',...(node.id===selectedNode?['selected']:[]),...(shown?.source===row.source&&shown.contract===row.contractId?['shown']:[]),...(row.weight===''?['missing']:[])];
   draw(from??output(row.source),at(cardOf(node.id)?.querySelector(`[data-contract-row="${row.index}"] .custom-curve-dot`),'left'),`${node.id}#${row.index}`,`Fixed contract · ${contractName(row.source,row.contractId)} · weight ${row.weight||'not entered'} · to ${nodeTitle(node)} ${node.id}`,classes,place=>contractMenu(node.id,row.index,place));}
  for(const [key,g] of existing)if(!drawn.has(key))g.remove();
 }
 function choicesFor(field:Field):Choice[]{if(field.options?.length)return field.options;
  if(field.type==='curve')return (catalog?.sources??[]).map(source=>({value:source.code,label:source.name}));
  if(field.type==='published_source')return (catalog?.publishedSources??[]).map(source=>({value:source.reference,label:source.label}));
  if(field.type==='calendar')return (catalog?.calendars??[]).map(calendar=>({value:calendar.code,label:calendar.name??calendar.code}));
  if(field.type==='schedule')return (catalog?.schedules??[]).map(schedule=>({value:schedule.id+':'+schedule.version,label:`${schedule.name}${schedule.symbol?' · '+schedule.symbol:''} · v${schedule.version}`}));
  return [];
 }
 function changeParameter(nodeId:string,key:string,value:unknown){
  const node=definition.nodes.find(node=>node.id===nodeId);if(!node)return;
  if(key==='publishedSource'){definition={...definition,nodes:definition.nodes.map(current=>current.id===nodeId?{...current,publishedSource:value===null?undefined:value as PublishedSourceReference}:current)};invalidate();renderGraph();return;}
  const fields=catalog?.operators.find(operator=>operator.kind===node.kind)?.fields??[];
  const parameters=editCurveParameters(node.parameters,key,value,fields);
  const visible=(values:Record<string,unknown>)=>fields.filter(field=>curveFieldVisible(field,values)).map(field=>field.key).join('|');
  const changedFields=visible(node.parameters)!==visible(parameters);
  definition={...definition,nodes:definition.nodes.map(current=>current.id===nodeId?{...current,parameters}:current)};
  invalidate();renderGraph();if(changedFields)renderInspector();
 }
 function renderWeights(node:CurveNode,spec:Field){
  const section=el('section');section.className='curve-contract-mapping';section.dataset.mappingNode=node.id;
  section.append(el('h4',spec.label),el('p',spec.help??'Choose each exact contract and enter its weight. The server validates units and calculates the selected aggregation.'));
  const rows=node.contractWeights??[];const currentRows=()=>definition.nodes.find(n=>n.id===node.id)?.contractWeights??rows;
  function saveRows(next:ContractWeight[],redraw=false){definition=changeContractWeights(definition,node.id,next);invalidate();renderGraph();if(redraw)renderInspector();}
  rows.forEach((row,index)=>{
   const card=el('div');card.className='curve-contract-row';card.dataset.mappingRow=String(index);
   const title=el('strong',`Connection ${index+1}`),remove=button('Remove','',()=>saveRows(currentRows().filter((_,n)=>n!==index),true));remove.className='text-button';remove.setAttribute('aria-label',`Remove connection ${index+1}`);
   const top=el('div');top.className='curve-contract-row-heading';top.append(title,remove);card.append(top);
   const position=field('','number');position.step='1';position.placeholder='Output position';position.value=row.outputPosition===null?'':String(row.outputPosition);position.required=true;position.setAttribute('aria-label',`Output position for connection ${index+1}`);
   const source=el('select');source.setAttribute('aria-label',`Source for connection ${index+1}`);option(source,'','Choose a source…');
   for(const [port,input] of Object.entries(node.inputs)){const selected=definition.nodes.find(n=>n.id===input);option(source,port,`${port.replaceAll('_',' ')} · ${selected?describe(selected):'Connect a source'}`);}
   if(row.sourcePort&&!Array.from(source.options).some(o=>o.value===row.sourcePort))option(source,row.sourcePort,'Retained source · reconnect to continue');source.value=row.sourcePort;
   const sourceNode=definition.nodes.find(n=>n.id===node.inputs[row.sourcePort]);const contracts=listedContracts(sourceNode);
   const contract=el('select');contract.setAttribute('aria-label',`Contract for connection ${index+1}`);option(contract,'','Choose an exact contract…');for(const item of contracts)option(contract,item.id,item.label);
   if(row.contractId&&!contracts.some(c=>c.id===row.contractId))option(contract,row.contractId,'Retained contract · unavailable in this source');contract.value=row.contractId;contract.required=true;
   const weight=field('');weight.inputMode='decimal';weight.placeholder='Explicit weight';weight.value=row.weight;weight.required=true;weight.setAttribute('aria-label',`Weight for connection ${index+1}`);
   const changed=(key:keyof ContractWeight,value:string|number|null,redraw=false)=>{if(o.busy()||!editAllowed())return;const current=definition.nodes.find(n=>n.id===node.id)?.contractWeights??rows;saveRows(current.map((r,n)=>n===index?{...r,[key]:value}:r),redraw);};
   position.oninput=()=>changed('outputPosition',position.value===''?null:Number(position.value));source.onchange=()=>changed('sourcePort',source.value,true);contract.onchange=()=>changed('contractId',contract.value);weight.oninput=()=>changed('weight',weight.value);
   card.append(label('Output position',position),label('Source connection',source),label('Contract',contract),label('Weight',weight));section.append(card);
  });
  if(!rows.length)section.append(el('p','No contracts mapped yet. Add a connection, then choose its source, contract, output position and weight.'));
  const add=button('Add contract connection','custom-curve-add-weight',()=>{addContractRow(node.id);renderGraph();renderInspector();});add.className='secondary';section.append(add);properties.append(section);
 }
 function renderPanelWeights(node:CurveNode,spec:Field){
  const section=el('section');section.append(el('h4',spec.label),el('p','Each weight applies to every matching delivery period. Exact alignment and arithmetic are validated by the server.'));
  for(const [port,source] of Object.entries(node.inputs)){
   const upstream=definition.nodes.find(n=>n.id===source),input=field('');input.inputMode='decimal';input.required=true;
   input.setAttribute('aria-label','Weight for '+port);input.value=node.panelWeights?.find(w=>w.sourcePort===port)?.weight??'';
   input.oninput=()=>{if(o.busy()||!editAllowed())return;definition={...definition,nodes:definition.nodes.map(current=>current.id===node.id?{...current,
    panelWeights:Object.keys(current.inputs).map(sourcePort=>({sourcePort,weight:sourcePort===port?input.value:current.panelWeights?.find(w=>w.sourcePort===sourcePort)?.weight??''}))}:current)};invalidate();renderGraph();};
   section.append(label(`${port} · ${upstream?describe(upstream):'Connect a curve'}`,input));
  }
  if(!Object.keys(node.inputs).length)section.append(el('p','Add curve connections below, then enter their weights.'));
  properties.append(section);
 }
 function renderInspector(){properties.replaceChildren();const node=definition.nodes.find(n=>n.id===selectedNode);if(!node){properties.append(el('p','Select a graph card to edit its properties.'));return;}const operator=catalog?.operators.find(op=>op.kind===node.kind);properties.append(el('h4',nodeTitle(node)),el('p',operator?.description??''));
  for(const spec of operator?.fields??[]){if(!curveFieldVisible(spec,node.parameters))continue;if(spec.type==='contract_weights'){renderWeights(node,spec);continue;}if(spec.type==='panel_weights'){renderPanelWeights(node,spec);continue;}const choices=choicesFor(spec),currentValue=fieldValue(node,spec),id='custom-curve-property-'+spec.key;let control:HTMLInputElement|HTMLSelectElement;
   if(choices.length||['select','curve','calendar','schedule','published_source'].includes(spec.type)){const select=el('select');select.id=id;option(select,'','Choose…');choices.forEach((choice,index)=>option(select,String(index),choice.label));const index=choices.findIndex(choice=>JSON.stringify(choice.value)===JSON.stringify(currentValue));select.value=index<0?'':String(index);if(currentValue!==undefined&&index<0){option(select,'retained','Retained value · '+pretty(currentValue));select.value='retained';}select.onchange=()=>{if(o.busy()||!editAllowed())return;const value=select.value;if(value==='retained')return;changeParameter(node.id,spec.key,value===''?null:choices[Number(value)].value);};control=select;
   }else{const input=field(id,spec.type==='boolean'?'checkbox':spec.type==='integer'?'number':'text');if(spec.type==='boolean')input.checked=currentValue==='true';else input.value=currentValue===undefined||currentValue===null?'':String(currentValue);if(spec.type==='integer')input.step='1';input.onchange=()=>{if(o.busy()||!editAllowed())return;changeParameter(node.id,spec.key,spec.type==='boolean'?String(input.checked):input.value);};control=input;}
   control.required=spec.required??false;const item=label(spec.label,control);if(spec.help)item.append(el('small',spec.help));properties.append(item);
  }
  if(Object.keys(node.parameters).some(key=>!operator?.fields.some(field=>field.key===key))){const retained=el('details');retained.append(el('summary','Retained parameters'),el('pre',JSON.stringify(node.parameters,null,2)));properties.append(retained);}
  const inputHeading=el('h4','Input connections');properties.append(inputHeading);
  const ports=nodePorts(node);
  for(const port of ports){const select=el('select');select.dataset.inputPort=port.name;select.setAttribute('aria-label',(port.label??port.name)+' input');option(select,'','Connect '+(port.label??port.name)+'…');
   for(const candidate of definition.nodes){if(candidate.id===node.id)continue;const output=catalog?.operators.find(op=>op.kind===candidate.kind)?.outputType;if(output!==port.type&&port.type!=='any')continue;option(select,candidate.id,nodeTitle(candidate)+' · '+candidate.id);}
   if(node.inputs[port.name]&&!Array.from(select.options).some(op=>op.value===node.inputs[port.name]))option(select,node.inputs[port.name],node.inputs[port.name]+' · verify type');select.value=node.inputs[port.name]??'';
   select.onchange=()=>{if(o.busy()||!editAllowed())return;definition=changeCurveConnection(definition,node.id,port.name,select.value);invalidate();renderGraph();renderInspector();};const connection=label(`${port.label??port.name} · ${port.type}`,select);if(operator?.dynamicInputs){const remove=button('Remove source','',()=>{definition=changeCurveConnection(definition,node.id,port.name,'');invalidate();render();});remove.className='text-button';connection.append(remove);}properties.append(connection);
  }
  if(operator?.dynamicInputs){const spec=operator.dynamicInputs;const add=button('Add source connection','custom-curve-add-source',()=>{const port=nextDynamicPort(node,spec.prefix);definition={...definition,nodes:definition.nodes.map(current=>current.id===node.id?{...current,inputs:{...current.inputs,[port]:''}}:current)};invalidate();render();});add.className='secondary';properties.append(add);}
  else if(!ports.length)properties.append(el('p','This operation has no graph inputs.'));
  const output=el('input');output.type='checkbox';output.id='custom-curve-output';output.checked=definition.output===node.id;output.onchange=()=>{if(o.busy()||!editAllowed())return;definition={...definition,output:output.checked?node.id:''};invalidate();renderGraph();};properties.append(label('Use as the curve output',output));
  const remove=button('Remove operation','custom-curve-remove-node',()=>{if(o.busy()||!editAllowed())return;removeNode(node.id);});remove.className='text-button';properties.append(remove);sync();
 }
 function displayEvidence(value:unknown):unknown{if(typeof value==='string'&&/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}/.test(value))return displayInstant(value,o.zone());if(Array.isArray(value))return value.map(displayEvidence);if(value&&typeof value==='object')return Object.fromEntries(Object.entries(value).map(([key,item])=>[key,displayEvidence(item)]));return value;}
 function validate(){if(blend.dirty())throw Error('Build or discard the ingredient changes first.');if(!code.reportValidity()||!name.reportValidity())throw Error('Enter a code and name for this curve.');if(formulaDirty)throw Error('Apply the formula before previewing or saving.');}
 async function preview(retain=false){validate();if(!retain){effectiveCurve=undefined;clearPreview();dock.show('outputs');}const request=guard(),source=o.source();const values=await o.api<Preview>('custom-curves/preview',{definition,snapshotId:source.snapshotId,...(selected&&!dirty?{id:selected.id,version:selected.version}:{})});if(!current(request))return;lastPreview={values,source};showPreview(values,source);}
 function clearActivation(){activationLoaded=false;activationDirty=false;activationPlan=null;activationRows.replaceChildren();activationReason.value='';activationState.textContent='';}
 function activationEdited(){activationDirty=true;effectiveCurve=undefined;clearPreview();activationState.textContent='Unpublished replacement timeline · publish it or reload the retained timeline before an effective preview.';sync();}
 function activationRow(segment?:ActivationSegment){
  const row=el('div');row.className='schedule-fields';const from=field('','date'),to=field('','date'),version=el('select');
  from.className='activation-from';to.className='activation-to';version.className='activation-version';from.required=to.required=version.required=true;
  option(version,'','Choose an exact published version…');for(const recipe of catalog?.recipes.filter(r=>r.id===selected?.id&&r.state==='published')??[])option(version,String(recipe.version),`Version ${recipe.version} · ${recipe.definition.name} · ${recipe.definition.output}`);
  if(segment){from.value=segment.effectiveFrom;to.value=segment.effectiveTo;version.value=String(segment.version);}
  const remove=button('Remove interval','',()=>{if(!o.busy()){row.remove();activationEdited();}});remove.className='text-button';row.append(label('Effective from',from),label('Effective to (exclusive)',to),label('Published version/output',version),remove);activationRows.append(row);
 }
 function showActivation(plan:ActivationPlan|null){if(plan&&plan.curveId!==selected?.id)throw Error('The returned timeline belongs to another curve.');activationPlan=plan;activationLoaded=true;activationDirty=false;activationRows.replaceChildren();activationReason.value='';for(const segment of plan?.segments??[])activationRow(segment);activationState.textContent=plan?`Timeline revision ${plan.revision}${plan.knownFrom?' · known '+displayInstant(plan.knownFrom,o.zone()):''}. ${plan.segments.length?'':'No dates are active.'}`:'No activation timeline has been published.';sync();}
 async function loadActivation(){if(!selected)throw Error('Choose a saved curve first.');const request=guard();const result=await o.api<{plan:ActivationPlan|null}>('custom-curves/activation?curveId='+encodeURIComponent(selected.id));if(!current(request))return;const loaded=await o.api<Catalog>('custom-curves');if(!current(request))return;catalog=loaded;renderLibrary();showActivation(result.plan);}
 async function publishActivation(){
  if(!selected||!activationLoaded||!catalog?.canPublish||o.historical())throw Error('Load a timeline in the current workspace with publishing permission.');
  if(!activationForm.reportValidity())return;
  const segments=[...activationRows.children].map(row=>{const version=Number(row.querySelector<HTMLSelectElement>('.activation-version')!.value),recipe=catalog!.recipes.find(r=>r.id===selected!.id&&r.version===version&&r.state==='published');if(!recipe)throw Error('Choose an offered published version.');return{effectiveFrom:row.querySelector<HTMLInputElement>('.activation-from')!.value,effectiveTo:row.querySelector<HTMLInputElement>('.activation-to')!.value,version,outputNode:recipe.definition.output};});
  const command=pending.begin('custom-curves/activation',{curveId:selected.id,expectedRevision:activationPlan?.revision??0,policy:'complete_effective_timeline_v1',reason:activationReason.value,segments});storePending();await sendMutation(command);
 }
 async function previewEffective(retain=false){
  if(activationDirty)throw Error('Publish or reload the replacement timeline before an effective preview.');
  const id=effectiveCurve??selected?.id;if(!id)throw Error('Choose a saved logical curve first.');const request=guard(),source=o.source();if(!retain){clearPreview();dock.show('outputs');}
  const result=await o.api<{activation:ActivationPlan;recipe:Recipe;preview:Preview}>('custom-curves/effective-preview',{curveId:id,snapshotId:source.snapshotId,policy:'complete_effective_timeline_v1'});
  if(!current(request))return;if(result.recipe?.id!==id||result.activation?.curveId!==id||result.preview?.snapshotId!==source.snapshotId)throw Error('The effective preview returned inconsistent selection identities.');
  previewTiles(result.preview.summary);openRecipe(result.recipe);effectiveCurve=id;showActivation(result.activation);lastPreview={values:result.preview,source};showPreview(result.preview,source);
 }
 function showPreview(values:Preview,source:{snapshotId:string;context?:unknown}){const shown=previewTable(values),tiles=previewTiles(values.summary);result.hidden=false;outputsEmpty.hidden=true;
  summaryTiles.replaceChildren(...tiles.map(renderTile));const context=values.context??catalog?.context;receipt.textContent=[values.at?'As of '+displayInstant(values.at,o.zone()):'',context?.effectiveOn?`Context date ${context.effectiveOn}`:'',context?.knownAt?`Known ${displayInstant(context.knownAt,o.zone())}`:'',...(values.warnings??[])].filter(Boolean).join(' · ');
  renderRegularTable(table,'custom-curves-preview',JSON.stringify([o.identity(),source,definition]),shown.headers,shown.rows,[],o.busy);if(values.chart)renderChart(chart,values.chart);else clearChart(chart);lineageBody.replaceChildren();for(const [title,items] of [['Dependencies',values.dependencies],['Source observations',values.inputs],['Evaluation trace',values.trace]] as const){if(items&&(Array.isArray(items)?items.length:Object.keys(items).length)){lineageBody.append(el('h4',title),el('pre',JSON.stringify(displayEvidence(items),null,2)));}}lineageEmpty.hidden=lineageBody.childElementCount>0;
 }
 async function sendMutation(command:RequestCommand){
  const request=guard(),previouslyUncertain=pending.sending();storePending();let saved:Recipe|ActivationPlan;
  try{saved=await o.api<Recipe|ActivationPlan>(command.path,{...command.body,requestId:command.operationId});if(!current(request)||pending.command!==command)return;
   if(command.path==='custom-curves/activation'){const plan=saved as ActivationPlan;if(!plan||plan.curveId!==command.body.curveId||!Number.isSafeInteger(plan.revision)||plan.revision!==Number(command.body.expectedRevision)+1||plan.policy!=='complete_effective_timeline_v1'||!Array.isArray(plan.segments))throw Error('The activation confirmation is incomplete. Retry the original request to reconcile it.');}
  }
  catch(error){if(!current(request)||pending.command!==command)return;pending.failed((error as {code?:string}).code,previouslyUncertain);storePending();pendingPanel.hidden=!pending.command;throw error;}
  pending.clear();storePending();if(command.path==='custom-curves/activation'){clearPreview();const plan=saved as ActivationPlan;if(plan.curveId===selected?.id)showActivation(plan);else{clearActivation();activationState.textContent='The original curve’s timeline was published. Load the selected curve’s timeline to inspect it.';}return;}await load();if(current(request))openRecipe(saved as Recipe);
 }
 async function save(){validate();clearPreview();if(!editAllowed())throw Error('This context is read-only.');const command=pending.begin('custom-curves/save',{id:selected?.id??null,version:selected?.version??null,generation:selected?.generation??null,expectedState:selected?.state??null,definition,layout,snapshotId:o.source().snapshotId});storePending();pendingPanel.hidden=false;await sendMutation(command);}
 async function publish(){if(!selected||dirty)throw Error('Save the exact draft before publishing.');clearPreview();const command=pending.begin('custom-curves/publish',{id:selected.id,version:selected.version,generation:selected.generation});storePending();pendingPanel.hidden=false;await sendMutation(command);}
 function reset(){discarded=false;question?.abort();question=undefined;opened=false;clearGraphModes();blend.reset();generation++;effectiveCurve=undefined;clearActivation();mode='visual';pending.clear();catalog=undefined;definition=empty();layout={};selected=undefined;selectedNode='';dirty=false;formulaDirty=false;drag=undefined;library.replaceChildren();templates.replaceChildren();operations.replaceChildren();clearPreview();render();notifyMarketScope();}
 function snapshotChanged(){generation++;effectiveCurve=undefined;clearActivation();drag=undefined;clearPreview();catalog=undefined;renderStatus();notifyMarketScope();}
 function contextChanged(){opened=false;formulaDirty=false;formula.value='';blend.reset();clearGraphModes();generation++;effectiveCurve=undefined;clearActivation();mode='visual';clearPreview();catalog=undefined;selected=undefined;definition=empty();layout={};selectedNode='';dirty=false;render();notifyMarketScope();}
 /** Edits not yet saved, which leaving discards; a request whose outcome is unknown is not among them, for it is retained across a move. */
 const editing=()=>dirty||formulaDirty||activationDirty||blend.dirty();
 /** What a move would give up: all the edits, or, for a change of close, which keeps the graph, only an effective-version timeline not yet published. */
 const holds=(scope:'edits'|'timeline')=>scope==='timeline'?activationDirty:editing();
 /** Set when the edits were discarded for a move, which clears the page until it is read again. */
 let discarded=false;
 const questions={edits:'This curve has unsaved edits. Leaving discards them.',timeline:'The effective-version timeline has unpublished edits. Changing the close discards them.'};
 /** The open leave question, which a reset of the page abandons; only one is asked at a time. */
 let question:AbortController|undefined;
 /** Asks above the curve's views whether to give up what `scope` holds: false means the person kept it, and focus returns to the control that asked. */
 async function leave(scope:'edits'|'timeline'='edits'){
  if(!holds(scope))return true;
  if(question)return false;
  const trigger=document.activeElement as HTMLElement|null,asking=question=new AbortController();
  let going=false;
  try{going=await askInPanel(host,{text:questions[scope],keep:'Keep editing',go:'Discard edits'})(asking.signal);}finally{if(question===asking)question=undefined;}
  if(!going&&trigger&&trigger!==document.body&&trigger.isConnected)trigger.focus({preventScroll:true});
  return going&&!asking.signal.aborted;
 }
 /** Drops the curve's edits for a move that leaves the page or changes the context; the page is read again when it is next shown. */
 function discard(){contextChanged();discarded=true;}
 /** Reads the page again when a move that discarded its edits did not complete, so that it is not left empty. */
 async function recover(){if(discarded)await load();}
 /** Runs `replace`, which opens something in place of the edits, once the person has agreed to give them up. */
 function replacing(replace:()=>void,kept?:()=>void,asks=editing){
  if(o.busy())return;
  if(!asks()){replace();return;}
  void leave().then(going=>{if(going)replace();else kept?.();});
 }
 function refresh(){renderStatus();if(lastPreview)showPreview(lastPreview.values,lastPreview.source);}
 async function liveRefresh(){generation++;drag=undefined;if(!activationDirty)clearActivation();else activationLoaded=false;renderStatus();if(lastPreview){if(effectiveCurve)await previewEffective(true);else await preview(true);}}
 function openPrepared(prepared:{definition:CurveDefinition;origin:{effectiveOn:string;knownAt:string}},request:ReturnType<typeof guard>){
  if(!current(request)||!catalog)throw Error('The editor or account changed. Review the proposed curve again.');
  if(pending.command)throw Error('Reconcile the unconfirmed curve request before opening a proposed draft.');
  openDefinition(prepared.definition);dirty=true;blend.reset();
  templateHelp.textContent=`Proposed for ${prepared.origin.effectiveOn}, as known at ${displayInstant(prepared.origin.knownAt,o.zone(),true)}. This is an unpriced draft. Preview calculates under the selected header context.`;
  renderStatus();
 }
 reset();initialized=true;return {load,reset,place,refresh,snapshotChanged,contextChanged,liveRefresh,sync,dateDefinition,openPrepared,guard,holds,leave,discard,recover,editing,unconfirmed:()=>!!pending.command||editing()};
}
