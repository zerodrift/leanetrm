import {type CurveDefinition,type CurveNode,type CurveLayout,type ContractWeight,type PublishedSourceReference,type PreviewSummary,changeContractWeights,curveEdges,curveLayout,changeCurveConnection,previewTable,previewTiles,recipeStateLabel,knownCurveRejection,curveFieldVisible,editCurveParameters} from './custom-curve-model.js';
import {renderRegularTable} from './regular-table.js';
import {clearChart,renderChart,type ChartSpec} from './chart-view.js';
import {displayInstant,publicText} from './market-view.js';
import {PendingCommand,type RequestCommand} from './pending-command.js';
import {setupCurveBlend} from './curve-blend.js';
import {setupSidePanel,setupTabs} from './side-panel.js';
import {renderTile,space} from './tiles.js';

type Choice={value:unknown;label:string};
type Field={key:string;label:string;type:string;required?:boolean;options?:Choice[];help?:string;when?:{key:string;values:string[]}};
type Operator={kind:string;label:string;description?:string;inputPorts:{name:string;label?:string;type:string;required?:boolean}[];outputType:string;fields:Field[];dynamicInputs?:{type:string;label:string;prefix:string}};
type Template={code:string;name:string;description?:string;available?:boolean;unavailableReason?:string;definition:CurveDefinition;layout?:CurveLayout};
type Recipe={id:string;version:number;generation:number;state:string;definition:CurveDefinition;layout?:CurveLayout;publishedAt?:string};
type ActivationSegment={effectiveFrom:string;effectiveTo:string;version:number;outputNode:string};
type ActivationPlan={curveId:string;revision:number;policy:string;reason:string;segments:ActivationSegment[];knownFrom?:string|null};
type Source={code:string;name:string;catalogId?:string;revisionId?:string;contracts?:{id:string;label:string;deliveryFrom:string;deliveryTo:string}[]};
type Schedule={id?:string;version:number;symbol?:string;name:string};
type Catalog={context?:{effectiveOn?:string;knownAt?:string};canEdit:boolean;canPublish:boolean;operators:Operator[];templates:Template[];sources:Source[];publishedSources?:{label:string;reference:PublishedSourceReference}[];calendars:(Source&{reference?:unknown})[];schedules:Schedule[];recipes:Recipe[];formulaSupported?:boolean};
type Preview={columns:string[];rows:(string|null)[][];at?:string;snapshotId?:string;summary?:PreviewSummary;chart?:ChartSpec;context?:{effectiveOn?:string;knownAt?:string};dependencies?:unknown[];inputs?:unknown[];trace?:unknown;warnings?:string[]};
type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type Options={api:Api;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;zone:()=>string;identity:()=>string;source:()=>{snapshotId:string;context?:unknown};historical:()=>boolean;marketScopeChanged?:()=>void;
 /** Whether the page has room to dock the panel beside the graph, and whether the open panel covers the whole page (a phone's sheet). */
 docked:()=>boolean;sheet:()=>boolean};
/** The panel's views (markets-desk.md, Custom curves): the selected graph operation, the curve's details, its effective versions and combining published curves. */
type View='operation'|'details'|'versions'|'combine';
const clone=<T>(value:T):T=>structuredClone(value);
const empty=():CurveDefinition=>({code:'',name:'',nodes:[],output:''});
const pretty=(value:unknown)=>typeof value==='string'?value:JSON.stringify(value,null,2);

/** Editable graph presentation, laid out as a desk: a header with the curve's state and actions, the saved-curve library, the graph with its
 * preview, and a side panel. All date selection, type validation, lineage, aggregation, values and summaries are supplied by the authorized
 * managed operations. */
export function setupCustomCurves(host:HTMLElement,o:Options){
 const el=<K extends keyof HTMLElementTagNameMap>(tag:K,value?:string)=>{const node=document.createElement(tag);if(value!==undefined)node.textContent=publicText(value);return node;};
 const button=(label:string,id:string,run:()=>void)=>{const node=el('button',label);node.type='button';if(id)node.id=id;node.onclick=run;return node;};
 const label=(name:string,control:HTMLElement)=>{const node=el('label',name);node.append(control);return node;};
 const option=(select:HTMLSelectElement,value:string,name:string)=>{select.append(new Option(publicText(name),value));};
 const field=(id:string,type='text')=>{const input=el('input');input.id=id;input.type=type;return input;};
 const view=(id:string,title:string)=>{const node=el('section');node.id=id;node.className='custom-curve-view';node.append(el('h3',title));return node;};
 const chip=(text:string,state:string)=>{const node=el('span',text);node.className='curve-state-chip';node.dataset.state=state;return node;};
 let catalog:Catalog|undefined,definition=empty(),layout:CurveLayout={},selected:Recipe|undefined,selectedNode='',generation=0,dirty=false,mode='visual',formulaDirty=false,zoom=1,fit=false;
 let initialized=false;
 let activationPlan:ActivationPlan|null=null,activationLoaded=false,activationDirty=false,effectiveCurve:string|undefined;
 let lastPreview:{values:Preview;source:{snapshotId:string;context?:unknown}}|undefined;
 let drag:{id:string;pointer:number;startX:number;startY:number;x:number;y:number}|undefined;
 // The panel's view. The panel opens only when the person asks: by selecting a card, starting a new curve or with its toggle.
 let activeView:View='details';
 const pending=new PendingCommand(knownCurveRejection);
 const storageKey=()=>`openetrm.custom-curves.request.${o.identity()}`;
 function storePending(){try{if(pending.command)sessionStorage.setItem(storageKey(),JSON.stringify(pending.command));else sessionStorage.removeItem(storageKey());}catch{/* The exact request remains available in memory. */}}

 // Header: the curve's name, its state and version, and the actions on one line.
 const head=el('header');head.className='custom-curve-head';const titles=el('div');titles.className='custom-curve-titles';
 const kicker=el('p','Custom curve');kicker.className='eyebrow';const heading=el('h2');heading.id='custom-curves-heading';
 const status=el('p');status.id='custom-curves-state';status.className='custom-curve-status';status.setAttribute('role','status');titles.append(kicker,heading,status);
 const previewButton=button('Preview','custom-curves-preview',()=>void o.act(preview));previewButton.className='secondary';
 const saveButton=button('Save draft','custom-curves-save',()=>void o.act(save));
 const publishButton=button('Publish','custom-curves-publish',()=>void o.act(publish));publishButton.className='secondary';
 const actions=el('div');actions.className='custom-curve-actions';actions.append(previewButton,saveButton,publishButton);head.append(titles,actions);
 const templateHelp=el('p');templateHelp.id='custom-curves-template-help';templateHelp.className='custom-curve-about';
 const accessNote=el('p');accessNote.id='custom-curves-access-note';accessNote.className='mode-banner';accessNote.hidden=true;
 const pendingPanel=el('div');pendingPanel.id='custom-curves-pending';pendingPanel.className='mode-banner custom-curve-pending';pendingPanel.hidden=true;pendingPanel.append(el('p','The previous save or publish request needs a confirmed result. Reload the library to inspect changes, then retry the original request or discard it after checking.'));
 const retryButton=button('Retry original request','custom-curves-retry',()=>void o.act(async()=>{if(pending.command)await sendMutation(pending.command);}));
 const discardButton=button('Discard checked request','custom-curves-discard',()=>{if(o.busy())return;pending.clear();storePending();pendingPanel.hidden=true;sync();});discardButton.className='secondary';pendingPanel.append(retryButton,discardButton);

 // Left rail: the saved curves, each with its version and state, and beneath them the operations to add to the graph. The rail
 // collapses to its toggle, giving its width to the graph.
 const desk=el('div');desk.className='custom-curve-desk';
 const rail=el('section');rail.id='custom-curves-rail';rail.className='custom-curve-rail';rail.setAttribute('aria-label','Saved curves and operations');
 const railBody=el('div');railBody.id='custom-curves-rail-body';railBody.className='custom-curve-rail-body';
 const railToggle=button('','custom-curves-toggle-rail',()=>{railBody.hidden=!railBody.hidden;desk.classList.toggle('rail-collapsed',railBody.hidden);railToggle.setAttribute('aria-expanded',String(!railBody.hidden));railToggle.setAttribute('aria-label',(railBody.hidden?'Expand':'Collapse')+' saved curves and operations');positionCanvas();});
 const railTitle=el('span','Saved curves');railTitle.className='custom-curve-rail-title';const railChevron=el('span','‹');railChevron.className='custom-curve-rail-chevron';railChevron.setAttribute('aria-hidden','true');
 railToggle.className='custom-curve-rail-toggle';railToggle.append(railTitle,railChevron);railToggle.setAttribute('aria-controls',railBody.id);railToggle.setAttribute('aria-expanded','true');railToggle.setAttribute('aria-label','Collapse saved curves and operations');
 const newButton=button('New curve','custom-curves-new',()=>{if(o.busy())return;openDefinition(empty());void openPanel('details');if(!code.disabled)code.focus({preventScroll:true});});newButton.className='secondary';
 const reloadButton=button('Reload','custom-curves-reload',()=>void o.act(reloadSelected));reloadButton.className='text-button';reloadButton.setAttribute('aria-label','Reload library');
 const libraryActions=el('div');libraryActions.className='custom-curve-library-actions';libraryActions.append(newButton,reloadButton);
 const library=el('ul');library.id='custom-curves-library';library.className='custom-curve-list';library.setAttribute('aria-label','Saved custom curves');
 const palette=el('section');palette.id='custom-curves-palette';palette.className='custom-curve-palette';palette.setAttribute('aria-labelledby','custom-curves-palette-title');const paletteTitle=el('h3','Operations');paletteTitle.id='custom-curves-palette-title';
 const operations=el('div');operations.id='custom-curves-operators';palette.append(paletteTitle,operations);
 railBody.append(libraryActions,library,palette);rail.append(railToggle,railBody);

 // The panel's views. Details holds the code, name and template.
 const details=view('custom-curves-details','Curve details');
 const templates=el('select');templates.id='custom-curves-template';templates.setAttribute('aria-label','Custom curve template');
 const code=field('custom-curves-code'),name=field('custom-curves-name');code.required=true;name.required=true;
 const detailHelp=el('p','The code is the curve’s permanent identity; it cannot change once the curve is saved.');detailHelp.className='field-help';
 details.append(label('Code',code),label('Name',name),detailHelp,label('Start from a template',templates));
 const blend=setupCurveBlend(request=>void o.act(async()=>{
  if(!code.reportValidity()||!name.reportValidity())throw Error('Enter a code and name for this curve.');
  const started=guard(),source=o.source();
  const prepared=await o.api<{definition:CurveDefinition;description:string}>('custom-curves/compose',{snapshotId:source.snapshotId,composition:{...request,code:code.value,name:name.value}});
  if(!current(started))return;
  openDefinition(prepared.definition,{},selected,'operation');dirty=true;blend.clean();templateHelp.textContent=prepared.description;renderStatus();
 }),()=>{generation++;templateHelp.textContent='';clearPreview();renderStatus();});
 const activation=view('curve-activation','Effective versions');
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
 const inspector=view('custom-curves-inspector','Selected operation');inspector.classList.add('custom-curve-inspector');const properties=el('div');properties.id='custom-curves-properties';inspector.append(properties);

 // The side panel, docked beside the graph when the page has the room, an overlay otherwise and a sheet on a phone.
 const panel=el('aside');panel.id='custom-curves-panel';panel.className='side-panel custom-curve-panel';panel.tabIndex=-1;panel.hidden=true;
 const panelPart=(part:string,className?:string)=>{const node=el('div');node.id='custom-curves-panel-'+part;if(className)node.className=className;return node;};
 const panelFrame=el('div');panelFrame.className='side-panel-frame';const panelClose=button('×','custom-curves-panel-close',()=>{});panelClose.className='icon-button side-panel-close';panelClose.setAttribute('aria-label','Close the panel');
 const panelBody=panelPart('body','side-panel-body'),panelView=panelPart('view'),panelForm=panelPart('form','side-panel-form');panelBody.tabIndex=0;panelForm.hidden=true;
 const views:Record<View,HTMLElement>={operation:inspector,details,versions:activation,combine:blend.element};
 panelView.append(...Object.values(views));panelBody.append(panelView,panelForm);panelFrame.append(panelClose,panelPart('head','side-panel-head'),panelPart('tabs','side-panel-tabs'),panelBody);panel.append(panelFrame);
 // The panel holds no edits of its own: the curve's edits are the page's, and closing the panel keeps them.
 const shell=setupSidePanel(panel,{unsaved:()=>false,confirmLeave:async()=>false,docked:o.docked,sheet:o.sheet,
  closed:()=>panelToggle.focus({preventScroll:true}),changed:()=>{
   // Without the room to dock, an open panel lies over the page as an overlay (side-panel.ts); it is never closed for want of room.
   const open=shell.current()!==null;panelToggle.textContent=open?'Hide panel':'Show panel';panelToggle.setAttribute('aria-expanded',String(open));positionCanvas();
  }});
 const tabs=setupTabs(shell.element('tabs'),[['operation','Operation'],['details','Details'],['versions','Versions'],['combine','Combine']],{label:'Custom curve views',panel:panelBody,selected:activeView,select:id=>showView(id as View)});
 const combineTab=panel.querySelector<HTMLElement>('#custom-curves-panel-tabs-combine')!;
 /** Shows one of the panel's views. Focus inside a view that is put away moves to the chosen tab, so that it is not lost. */
 function showView(next:View){
  const away=Object.values(views).find(section=>section.contains(document.activeElement));
  activeView=next;tabs.mark(next);for(const [id,section] of Object.entries(views))section.hidden=id!==next;
  if(away&&away!==views[next])panel.querySelector<HTMLElement>('#custom-curves-panel-tabs-'+next)?.focus({preventScroll:true});
 }
 /** Opens the panel on a view (or the one it shows) at the person's request. Focus stays where it was unless the panel covers the page. */
 async function openPanel(next?:View){
  if(next)showView(next);
  if(shell.current()!==null)return;
  const keep=document.activeElement as HTMLElement|null;
  await shell.show({kind:'custom-curve'});
  if(!o.sheet()&&keep&&keep!==document.body&&keep.isConnected)keep.focus({preventScroll:true});
 }

 // Graph: the main area, with the preview beneath it.
 const workspace=el('div');workspace.className='custom-curve-main';
 const centre=el('section');centre.className='card custom-curve-centre';centre.setAttribute('aria-label','Calculation graph');
 const toolbar=el('div');toolbar.className='custom-curve-toolbar';const modes=el('div');modes.className='custom-curve-mode';modes.setAttribute('role','group');modes.setAttribute('aria-label','Curve authoring mode');
 const visualButton=button('Visual','custom-curves-mode-visual',()=>setMode('visual')),formulaButton=button('Formula','custom-curves-mode-formula',()=>{if(o.busy())return;setMode('formula');void o.act(formatFormula);});modes.append(visualButton,formulaButton);
 const board=el('div');board.id='custom-curves-graph';board.className='custom-curve-board';board.setAttribute('aria-label','Editable custom curve graph');
 const scene=el('div');scene.className='custom-curve-scene';const canvas=el('div');canvas.className='custom-curve-canvas';scene.append(canvas);board.append(scene);const graphTools=el('div');graphTools.className='custom-curve-graph-tools';const zoomLabel=el('span');zoomLabel.className='custom-curve-zoom';const fitButton=button('Fit graph','custom-curves-fit',()=>{if(o.busy())return;fit=true;positionCanvas();});const actualButton=button('100%','custom-curves-actual-size',()=>{if(o.busy())return;fit=false;zoom=1;positionCanvas();});const arrangeButton=button('Arrange','custom-curves-arrange',()=>{if(o.busy()||!editAllowed())return;layout=curveLayout(definition);renderGraph();});for(const b of [fitButton,actualButton,arrangeButton])b.className='secondary';graphTools.append(zoomLabel,fitButton,actualButton,arrangeButton);
 const panelTools=el('div');panelTools.className='custom-curve-panel-tools';panelTools.setAttribute('role','group');panelTools.setAttribute('aria-label','Graph panels');
 const panelToggle=button('Show panel','custom-curves-toggle-panel',()=>{if(shell.current()!==null)void shell.show(null);else void openPanel();});
 panelToggle.setAttribute('aria-controls',panel.id);panelToggle.setAttribute('aria-expanded','false');
 panelToggle.className='secondary';panelTools.append(panelToggle);toolbar.append(modes,graphTools,panelTools);
 const expression=el('div');expression.id='custom-curves-expression';expression.hidden=true;const formula=el('textarea');formula.id='custom-curves-formula';formula.rows=7;formula.spellcheck=false;formula.setAttribute('aria-label','Curve formula');
 const compile=button('Apply formula','custom-curves-compile',()=>void o.act(async()=>{clearPreview();const request=guard();const result=await o.api<{definition:CurveDefinition;layout?:CurveLayout}>('custom-curves/compile',{formula:formula.value,snapshotId:o.source().snapshotId,code:code.value,name:name.value});if(!current(request))return;definition=clone(result.definition);layout=curveLayout(definition,result.layout??layout);dirty=true;formulaDirty=false;render();notifyMarketScope();}));
 const syntax=el('p');syntax.className='field-help';syntax.textContent='Use one assignment per line: node = operation(parameter="value", input=@other_node). Parameters are quoted strings; contract mappings use typed JSON rows and connected inputs use @node. The backend compiles this into the same typed graph.';expression.append(label('Formula',formula),compile,syntax);
 const hint=el('p','Drag cards to arrange the graph. Select a card to edit its fields and connections in the panel.');hint.className='custom-curve-hint';
 centre.append(toolbar,board,expression,hint);
 const result=el('section');result.id='custom-curves-result';result.className='card custom-curve-preview';const resultHead=el('div');resultHead.className='custom-curve-preview-head';const receipt=el('p');receipt.id='custom-curves-receipt';receipt.className='field-help';resultHead.append(el('h3','Preview'),receipt);
 const summaryTiles=el('div');summaryTiles.id='custom-curves-summary';summaryTiles.className='desk-tiles custom-curve-tiles';
 const chart=el('div');chart.id='custom-curves-chart';chart.className='chart';const table=el('div');table.id='custom-curves-values';table.className='table-wrap';const lineage=el('details');lineage.id='custom-curves-lineage';lineage.append(el('summary','Inputs and dependencies'));const lineageBody=el('div');lineage.append(lineageBody);result.append(resultHead,summaryTiles,chart,table,lineage);result.hidden=true;
 workspace.append(centre,result);desk.append(rail,workspace,panel);
 host.replaceChildren(head,templateHelp,accessNote,pendingPanel,desk);
 function guard(){return {generation,identity:o.identity()};}function current(request:{generation:number;identity:string}){return generation===request.generation&&o.identity()===request.identity;}
 function notifyMarketScope(){if(initialized)o.marketScopeChanged?.();}
 function dateDefinition():CurveDefinition|undefined{return catalog&&!formulaDirty&&definition.nodes.length?clone(definition):undefined;}
 function invalidate(){generation++;dirty=true;effectiveCurve=undefined;templateHelp.textContent='';clearPreview();renderStatus();notifyMarketScope();}
 function clearPreview(){lastPreview=undefined;result.hidden=true;receipt.textContent='';summaryTiles.replaceChildren();clearChart(chart);table.replaceChildren();lineageBody.replaceChildren();}
 function editAllowed(){return !!catalog?.canEdit&&!o.historical();}
 function renderStatus(){accessNote.hidden=!catalog||editAllowed();const previewAccess=document.getElementById('preview-access');accessNote.textContent=o.historical()?'This historical view is read-only. Return to current inputs to edit curves.':catalog?.canPublish?'This account can publish saved drafts, but editing is read-only. '+(previewAccess&&!previewAccess.hidden?'Choose Market Data Editor in Profile → Preview access to edit.':'Ask your workspace administrator for market-data authoring access.'):'Curve editing is read-only for this account. '+(previewAccess&&!previewAccess.hidden?'Choose Market Data Editor in Profile → Preview access to edit.':'Ask your workspace administrator for market-data authoring access.');
  heading.textContent=publicText(definition.name||(selected?selected.definition.name:'New curve'));
  const parts:HTMLElement[]=[];
  if(selected)parts.push(chip(recipeStateLabel(selected.state),selected.state),el('span','Version '+selected.version));
  if(dirty)parts.push(chip('Unsaved changes','unsaved'),...(selected?[]:[el('span','Not saved yet')]));
  if(selected?.publishedAt)parts.push(el('span','Published '+displayInstant(selected.publishedAt,o.zone())));
  if(!selected&&!dirty)parts.push(el('span','Choose a saved curve, or start from a template or an operation.'));
  if(o.historical())parts.push(el('span','Historical view is read-only.'));
  if(blend.dirty()){const go=button('Build or discard the ingredient changes in Combine curves','custom-curves-open-combine',()=>void openPanel('combine'));go.className='text-button';parts.push(go);}
  status.replaceChildren(...parts.flatMap((part,i)=>i?[space(),part]:[part]));
  markLibrary();sync();}
 /** Marks the library's entry for the exact version open, if it is one. */
 function markLibrary(){for(const entry of library.querySelectorAll<HTMLElement>('[data-recipe]')){if(selected&&entry.dataset.recipe===selected.id+'|'+selected.version)entry.setAttribute('aria-current','true');else entry.removeAttribute('aria-current');}}
 function sync(){const busy=o.busy(),editable=editAllowed(),retryAllowed=['custom-curves/publish','custom-curves/activation'].includes(pending.command?.path??'')?!!catalog?.canPublish&&!o.historical():pending.command?.path==='custom-curves/save'?editable:false;for(const input of [code,name,templates])input.disabled=busy||!editable;code.disabled ||=!!selected;retryButton.disabled=busy||!retryAllowed;discardButton.disabled=busy;pendingPanel.hidden=!pending.command;newButton.disabled=busy||!editable;reloadButton.disabled=busy;for(const entry of library.querySelectorAll('button'))entry.disabled=busy;templateHelp.hidden=!templateHelp.textContent;previewButton.disabled=busy||!catalog||!definition.nodes.length||formulaDirty;effectiveButton.disabled=busy||!selected||dirty||formulaDirty;saveButton.disabled=busy||!editable||!definition.nodes.length||formulaDirty;publishButton.disabled=busy||!catalog?.canPublish||o.historical()||!selected||selected.state==='published'||dirty;compile.disabled=busy||!editable||!catalog?.formulaSupported;formula.disabled=busy||!editable||!catalog?.formulaSupported;fitButton.disabled=busy;actualButton.disabled=busy;arrangeButton.disabled=busy||!editable;for(const b of operations.querySelectorAll('button'))b.disabled=busy||!editable;for(const input of properties.querySelectorAll<HTMLInputElement|HTMLSelectElement|HTMLButtonElement>('input,select,button,textarea'))input.disabled=busy||!editable;
  for(const control of [previewButton,effectiveButton,saveButton,publishButton])control.disabled ||=blend.dirty();
  effectiveButton.disabled ||=activationDirty;
  blend.sync(busy,editable&&!pending.command,definition.nodes.length>0);
  // Combining needs the catalog's weighted-panel operation; without it the view is not offered.
  combineTab.hidden=!catalog?.operators.some(op=>op.kind==='weighted_panels');if(combineTab.hidden&&activeView==='combine')showView('details');
  loadActivationButton.disabled=busy||!selected;const canActivate=!!catalog?.canPublish&&!o.historical()&&activationLoaded&&!!selected&&!pending.command;
  for(const input of activationForm.querySelectorAll<HTMLInputElement|HTMLSelectElement|HTMLButtonElement>('input,select,button'))input.disabled=busy||!canActivate;
 }
 async function formatFormula(){if(!catalog?.formulaSupported||formulaDirty)return;const request=guard();const result=await o.api<{formula:string}>('custom-curves/format',{definition});if(!current(request))return;formula.value=result.formula;formulaDirty=false;sync();}
 function setMode(value:string){if(o.busy())return;mode=value;board.hidden=value!=='visual';expression.hidden=value!=='formula';graphTools.hidden=value!=='visual';if(value==='visual')positionCanvas();for(const b of [visualButton,formulaButton]){const active=b.id.endsWith(value);b.classList.toggle('active',active);b.setAttribute('aria-pressed',String(active));}sync();}
 function openDefinition(next:CurveDefinition,positions:CurveLayout={},recipe?:Recipe,show?:View){generation++;effectiveCurve=undefined;clearActivation();mode='visual';definition=clone(next);layout=curveLayout(definition,positions);selected=recipe;selectedNode=definition.nodes.find(node=>(catalog?.operators.find(operator=>operator.kind===node.kind)?.fields.length??0)>0)?.id||definition.output||definition.nodes[0]?.id||'';dirty=false;formulaDirty=false;formula.value='';templateHelp.textContent='';code.value=definition.code;name.value=definition.name;clearPreview();render();notifyMarketScope();if(show)showView(show);}
 function openRecipe(recipe:Recipe,show?:View){if(catalog){catalog.recipes=[...catalog.recipes.filter(r=>r.id!==recipe.id||r.version!==recipe.version),recipe];renderLibrary();}openDefinition(recipe.definition,recipe.layout,recipe,show);templates.value='';}
 function renderLibrary(){const recipes=catalog?.recipes??[];
  library.replaceChildren(...recipes.map(recipe=>{const item=el('li'),entry=button('','',()=>{if(o.busy())return;openRecipe(recipe,activeView);}),title=el('span',recipe.definition.name);entry.className='custom-curve-entry';entry.dataset.recipe=recipe.id+'|'+recipe.version;title.className='custom-curve-entry-name';entry.setAttribute('aria-label',`${recipe.definition.name}, ${recipeStateLabel(recipe.state)}, ${recipe.definition.code} version ${recipe.version}`);entry.append(title,chip(recipeStateLabel(recipe.state),recipe.state),el('small',`${recipe.definition.code} · v${recipe.version}`));item.append(entry);return item;}));
  if(catalog&&!recipes.length){const none=el('li','No saved curves yet. Start a new curve to save one.');none.className='custom-curve-empty';library.append(none);}
  markLibrary();
  // Ingredients chosen for a combination the catalog no longer offers cannot be built, so they are not kept to block the page.
  if(!catalog?.operators.some(op=>op.kind==='weighted_panels'))blend.reset();
  templates.replaceChildren();option(templates,'','Choose a template…');for(const template of catalog?.templates??[])option(templates,template.code,template.name);const combination=catalog?.operators.find(op=>op.kind==='weighted_panels');blend.catalog(catalog?.publishedSources??[],combination?.fields.find(f=>f.key==='aggregation')?.options??[],combination?.fields.find(f=>f.key==='alignment')?.options??[]);}
 async function load(){clearActivation();clearPreview();const request=guard();catalog=undefined;library.replaceChildren();templates.replaceChildren();operations.replaceChildren();properties.replaceChildren();canvas.replaceChildren();code.value='';name.value='';sync();notifyMarketScope();const loaded=await o.api<Catalog>('custom-curves');if(!current(request))return;catalog=loaded;if(selected&&!catalog.recipes.some(recipe=>recipe.id===selected?.id&&recipe.version===selected.version)){definition=empty();layout={};selected=undefined;selectedNode='';dirty=false;effectiveCurve=undefined;}try{const stored=sessionStorage.getItem(storageKey());pending.command=stored?{...JSON.parse(stored),uncertain:true}:undefined;}catch{pending.clear();}renderLibrary();renderPalette();formulaButton.hidden=!catalog.formulaSupported;render();notifyMarketScope();}
 async function reloadSelected(){const request=guard(),id=selected?.id,version=selected?.version;await load();if(!current(request)||!id)return;const fresh=catalog?.recipes.find(recipe=>recipe.id===id&&recipe.version===version);if(fresh)openRecipe(fresh);}
 templates.onchange=()=>{if(o.busy()||!editAllowed())return;const template=catalog?.templates.find(t=>t.code===templates.value);if(!template)return;openDefinition(template.definition,{},undefined,'operation');templates.value=template.code;templateHelp.textContent=template.description??template.unavailableReason??'';dirty=true;renderStatus();};
 code.oninput=()=>{definition={...definition,code:code.value};invalidate();};name.oninput=()=>{definition={...definition,name:name.value};invalidate();};formula.oninput=()=>{formulaDirty=true;invalidate();};
 function renderPalette(){operations.replaceChildren();for(const operator of catalog?.operators??[]){const b=button(operator.label,'',()=>{if(o.busy()||!editAllowed())return;addNode(operator);});b.title=operator.description??operator.label;operations.append(b);}}
 function addNode(operator:Operator,parameters:Record<string,unknown>={}){let i=1;while(definition.nodes.some(n=>n.id===operator.kind+'_'+i))i++;const id=operator.kind+'_'+i;definition={...definition,nodes:[...definition.nodes,{id,kind:operator.kind,parameters,inputs:{}}]};layout=curveLayout(definition,layout);selectedNode=id;invalidate();render();showView('operation');}
 /** Selecting a card shows its operation in the panel, opening it; focus on the card stays on it, unless the panel covers the page. */
 function selectNode(id:string){if(o.busy()||drag)return;const fromCard=canvas.contains(document.activeElement);selectedNode=id;renderGraph();renderInspector();
  void openPanel('operation').then(()=>{if(fromCard&&!o.sheet())canvas.querySelector<HTMLButtonElement>(`[data-node-id="${CSS.escape(id)}"] button`)?.focus({preventScroll:true});});}
 function nodeTitle(node:CurveNode){return catalog?.operators.find(op=>op.kind===node.kind)?.label??node.kind;}
 function fieldValue(node:CurveNode,field:Field){return field.type==='published_source'?node.publishedSource:node.parameters[field.key];}
 function describe(node:CurveNode){if(node.kind==='weighted_contracts')return `${node.contractWeights?.length??0} explicit contract connections · ${Object.keys(node.inputs).length} sources`;const fields=catalog?.operators.find(operator=>operator.kind===node.kind)?.fields??[];return fields.filter(field=>curveFieldVisible(field,node.parameters)&&fieldValue(node,field)!==undefined).map(field=>{const value=fieldValue(node,field),choice=choicesFor(field).find(choice=>JSON.stringify(choice.value)===JSON.stringify(value));return field.label+': '+(choice?.label??(field.type==='schedule'?'Retained schedule':field.type==='published_source'?'Retained published output':String(value)));}).join(' · ');}
 function render(){code.value=definition.code;name.value=definition.name;renderGraph();renderInspector();renderStatus();setMode(mode);}
 function positionCanvas(){const width=parseFloat(canvas.style.width)||720,height=parseFloat(canvas.style.height)||340;if(fit&&board.clientWidth)zoom=Math.max(.2,Math.min(1,(board.clientWidth-24)/width,(board.clientHeight-24)/height));canvas.style.transform=`scale(${zoom})`;scene.style.width=width*zoom+'px';scene.style.height=height*zoom+'px';zoomLabel.textContent=Math.round(zoom*100)+'%';}
 function renderGraph(){canvas.replaceChildren();layout=curveLayout(definition,layout);const width=Math.max(720,...Object.values(layout).map(p=>p.x+248)),height=Math.max(340,...Object.values(layout).map(p=>p.y+144));canvas.style.width=width+'px';canvas.style.height=height+'px';positionCanvas();
  const ns='http://www.w3.org/2000/svg',svg=document.createElementNS(ns,'svg');svg.classList.add('custom-curve-wires');svg.setAttribute('width',String(width));svg.setAttribute('height',String(height));svg.setAttribute('aria-hidden','true');canvas.append(svg);
  for(const edge of curveEdges(definition)){const from=layout[edge.source],to=layout[edge.target];if(!from||!to)continue;const path=document.createElementNS(ns,'path'),a=from.x+224,b=to.x;path.setAttribute('d',`M ${a} ${from.y+58} C ${a+36} ${from.y+58}, ${b-36} ${to.y+58}, ${b} ${to.y+58}`);if(edge.target===selectedNode||edge.source===selectedNode)path.classList.add('selected');const title=document.createElementNS(ns,'title');title.textContent=edge.source+' → '+edge.target+' · '+edge.port;path.append(title);svg.append(path);}
  for(const node of definition.nodes){const position=layout[node.id],panel=el('section');panel.className='custom-curve-node'+(selectedNode===node.id?' selected':'')+(definition.output===node.id?' output':'');panel.dataset.nodeId=node.id;panel.dataset.kind=node.kind;panel.style.left=position.x+'px';panel.style.top=position.y+'px';panel.setAttribute('aria-label',nodeTitle(node)+' '+node.id);panel.style.cursor='pointer';panel.onclick=event=>{if((event.target as Element).closest('button'))return;selectNode(node.id);};
   const handle=button(nodeTitle(node),'',()=>selectNode(node.id));handle.className='custom-curve-node-handle';handle.setAttribute('aria-label','Select or move '+nodeTitle(node));handle.setAttribute('aria-pressed',String(selectedNode===node.id));handle.onkeydown=event=>{if(o.busy()||!editAllowed()||!['ArrowLeft','ArrowRight','ArrowUp','ArrowDown'].includes(event.key))return;event.preventDefault();layout[node.id]={x:Math.max(0,layout[node.id].x+(event.key==='ArrowRight'?12:event.key==='ArrowLeft'?-12:0)),y:Math.max(0,layout[node.id].y+(event.key==='ArrowDown'?12:event.key==='ArrowUp'?-12:0))};renderGraph();canvas.querySelector<HTMLButtonElement>(`[data-node-id="${CSS.escape(node.id)}"] button`)?.focus();};handle.onpointerdown=event=>{if(o.busy()||!editAllowed()||event.button!==0)return;selectedNode=node.id;drag={id:node.id,pointer:event.pointerId,startX:event.clientX,startY:event.clientY,x:position.x,y:position.y};handle.setPointerCapture(event.pointerId);};
   handle.onpointermove=event=>{if(!drag||drag.id!==node.id||drag.pointer!==event.pointerId)return;layout[node.id]={x:Math.max(0,drag.x+(event.clientX-drag.startX)/zoom),y:Math.max(0,drag.y+(event.clientY-drag.startY)/zoom)};panel.style.left=layout[node.id].x+'px';panel.style.top=layout[node.id].y+'px';};
   const end=(event:PointerEvent)=>{if(!drag||drag.id!==node.id||drag.pointer!==event.pointerId)return;drag=undefined;renderGraph();renderInspector();};handle.onpointerup=end;handle.onpointercancel=end;
   const ports=el('small',(catalog?.operators.find(op=>op.kind===node.kind)?.outputType??'Value')+(definition.output===node.id?' · Curve output':''));panel.append(handle,el('span',node.id),el('p',describe(node)||'Select to configure'),ports);canvas.append(panel);
  }
  if(!definition.nodes.length)canvas.append(el('p','Choose a saved curve, a template in Details, or add an operation to start your curve.'));
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
   const sourceNode=definition.nodes.find(n=>n.id===node.inputs[row.sourcePort]);const contracts=catalog?.sources.find(s=>s.code===sourceNode?.parameters.curve)?.contracts??[];
   const contract=el('select');contract.setAttribute('aria-label',`Contract for connection ${index+1}`);option(contract,'','Choose an exact contract…');for(const item of contracts)option(contract,item.id,item.label);
   if(row.contractId&&!contracts.some(c=>c.id===row.contractId))option(contract,row.contractId,'Retained contract · unavailable in this source');contract.value=row.contractId;contract.required=true;
   const weight=field('');weight.inputMode='decimal';weight.placeholder='Explicit weight';weight.value=row.weight;weight.required=true;weight.setAttribute('aria-label',`Weight for connection ${index+1}`);
   const changed=(key:keyof ContractWeight,value:string|number|null,redraw=false)=>{if(o.busy()||!editAllowed())return;const current=definition.nodes.find(n=>n.id===node.id)?.contractWeights??rows;saveRows(current.map((r,n)=>n===index?{...r,[key]:value}:r),redraw);};
   position.oninput=()=>changed('outputPosition',position.value===''?null:Number(position.value));source.onchange=()=>changed('sourcePort',source.value,true);contract.onchange=()=>changed('contractId',contract.value);weight.oninput=()=>changed('weight',weight.value);
   card.append(label('Output position',position),label('Source connection',source),label('Contract',contract),label('Weight',weight));section.append(card);
  });
  if(!rows.length)section.append(el('p','No contracts mapped yet. Add a connection, then choose its source, contract, output position and weight.'));
  const add=button('Add contract connection','custom-curve-add-weight',()=>saveRows([...currentRows(),{outputPosition:null,sourcePort:'',contractId:'',weight:''}],true));add.className='secondary';section.append(add);properties.append(section);
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
  const ports=[...(operator?.inputPorts??[]),...(operator?.dynamicInputs?Object.keys(node.inputs).filter(name=>!operator.inputPorts.some(p=>p.name===name)).map(name=>({name,label:name.replaceAll('_',' '),type:operator.dynamicInputs!.type})):[])];
  for(const port of ports){const select=el('select');select.dataset.inputPort=port.name;select.setAttribute('aria-label',(port.label??port.name)+' input');option(select,'','Connect '+(port.label??port.name)+'…');
   for(const candidate of definition.nodes){if(candidate.id===node.id)continue;const output=catalog?.operators.find(op=>op.kind===candidate.kind)?.outputType;if(output!==port.type&&port.type!=='any')continue;option(select,candidate.id,nodeTitle(candidate)+' · '+candidate.id);}
   if(node.inputs[port.name]&&!Array.from(select.options).some(op=>op.value===node.inputs[port.name]))option(select,node.inputs[port.name],node.inputs[port.name]+' · verify type');select.value=node.inputs[port.name]??'';
   select.onchange=()=>{if(o.busy()||!editAllowed())return;definition=changeCurveConnection(definition,node.id,port.name,select.value);invalidate();renderGraph();renderInspector();};const connection=label(`${port.label??port.name} · ${port.type}`,select);if(operator?.dynamicInputs){const remove=button('Remove source','',()=>{definition=changeCurveConnection(definition,node.id,port.name,'');invalidate();render();});remove.className='text-button';connection.append(remove);}properties.append(connection);
  }
  if(operator?.dynamicInputs){const spec=operator.dynamicInputs;const add=button('Add source connection','custom-curve-add-source',()=>{let n=1;while(node.inputs[spec.prefix+n]!==undefined)n++;definition={...definition,nodes:definition.nodes.map(current=>current.id===node.id?{...current,inputs:{...current.inputs,[spec.prefix+n]:''}}:current)};invalidate();render();});add.className='secondary';properties.append(add);}
  else if(!ports.length)properties.append(el('p','This operation has no graph inputs.'));
  const output=el('input');output.type='checkbox';output.id='custom-curve-output';output.checked=definition.output===node.id;output.onchange=()=>{if(o.busy()||!editAllowed())return;definition={...definition,output:output.checked?node.id:''};invalidate();renderGraph();};properties.append(label('Use as the curve output',output));
  const remove=button('Remove operation','custom-curve-remove-node',()=>{if(o.busy()||!editAllowed())return;for(const target of definition.nodes)for(const [port,source] of Object.entries(target.inputs))if(source===node.id)definition=changeCurveConnection(definition,target.id,port,'');definition={...definition,output:definition.output===node.id?'':definition.output,nodes:definition.nodes.filter(n=>n.id!==node.id)};delete layout[node.id];selectedNode=definition.nodes[0]?.id??'';invalidate();render();});remove.className='text-button';properties.append(remove);sync();
 }
 function displayEvidence(value:unknown):unknown{if(typeof value==='string'&&/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}/.test(value))return displayInstant(value,o.zone());if(Array.isArray(value))return value.map(displayEvidence);if(value&&typeof value==='object')return Object.fromEntries(Object.entries(value).map(([key,item])=>[key,displayEvidence(item)]));return value;}
 function validate(){if(blend.dirty())throw Error('Build or discard the ingredient changes first.');if(!code.reportValidity()||!name.reportValidity())throw Error('Enter a code and name for this curve.');if(formulaDirty)throw Error('Apply the formula before previewing or saving.');}
 async function preview(retain=false){validate();if(!retain){effectiveCurve=undefined;clearPreview();}const request=guard(),source=o.source();const values=await o.api<Preview>('custom-curves/preview',{definition,snapshotId:source.snapshotId,...(selected&&!dirty?{id:selected.id,version:selected.version}:{})});if(!current(request))return;lastPreview={values,source};showPreview(values,source);}
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
  const id=effectiveCurve??selected?.id;if(!id)throw Error('Choose a saved logical curve first.');const request=guard(),source=o.source();if(!retain)clearPreview();
  const result=await o.api<{activation:ActivationPlan;recipe:Recipe;preview:Preview}>('custom-curves/effective-preview',{curveId:id,snapshotId:source.snapshotId,policy:'complete_effective_timeline_v1'});
  if(!current(request))return;if(result.recipe?.id!==id||result.activation?.curveId!==id||result.preview?.snapshotId!==source.snapshotId)throw Error('The effective preview returned inconsistent selection identities.');
  previewTiles(result.preview.summary);openRecipe(result.recipe);effectiveCurve=id;showActivation(result.activation);lastPreview={values:result.preview,source};showPreview(result.preview,source);
 }
 function showPreview(values:Preview,source:{snapshotId:string;context?:unknown}){const shown=previewTable(values),tiles=previewTiles(values.summary);result.hidden=false;
  summaryTiles.replaceChildren(...tiles.map(spec=>{const tile=renderTile(spec);if(spec.words)tile.classList.add('desk-tile-words');return tile;}));const context=values.context??catalog?.context;receipt.textContent=[values.at?'As of '+displayInstant(values.at,o.zone()):'',context?.effectiveOn?`Context date ${context.effectiveOn}`:'',context?.knownAt?`Known ${displayInstant(context.knownAt,o.zone())}`:'',...(values.warnings??[])].filter(Boolean).join(' · ');
  renderRegularTable(table,'custom-curves-preview',JSON.stringify([o.identity(),source,definition]),shown.headers,shown.rows,[],o.busy);if(values.chart)renderChart(chart,values.chart);else clearChart(chart);lineageBody.replaceChildren();for(const [title,items] of [['Dependencies',values.dependencies],['Source observations',values.inputs],['Evaluation trace',values.trace]] as const){if(items&&(Array.isArray(items)?items.length:Object.keys(items).length)){lineageBody.append(el('h4',title),el('pre',JSON.stringify(displayEvidence(items),null,2)));}}lineage.hidden=!lineageBody.childElementCount;
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
 function reset(){shell.reset();blend.reset();generation++;effectiveCurve=undefined;clearActivation();mode='visual';pending.clear();catalog=undefined;definition=empty();layout={};selected=undefined;selectedNode='';dirty=false;formulaDirty=false;drag=undefined;library.replaceChildren();templates.replaceChildren();operations.replaceChildren();clearPreview();render();notifyMarketScope();}
 function snapshotChanged(){generation++;effectiveCurve=undefined;clearActivation();drag=undefined;clearPreview();catalog=undefined;renderStatus();notifyMarketScope();}
 function contextChanged(){blend.reset();generation++;effectiveCurve=undefined;clearActivation();mode='visual';clearPreview();catalog=undefined;selected=undefined;definition=empty();layout={};selectedNode='';dirty=false;render();notifyMarketScope();}
 function refresh(){renderStatus();if(lastPreview)showPreview(lastPreview.values,lastPreview.source);}
 async function liveRefresh(){generation++;drag=undefined;if(!activationDirty)clearActivation();else activationLoaded=false;renderStatus();if(lastPreview){if(effectiveCurve)await previewEffective(true);else await preview(true);}}
 function openPrepared(prepared:{definition:CurveDefinition;origin:{effectiveOn:string;knownAt:string}},request:ReturnType<typeof guard>){
  if(!current(request)||!catalog)throw Error('The editor or account changed. Review the proposed curve again.');
  if(pending.command)throw Error('Reconcile the unconfirmed curve request before opening a proposed draft.');
  openDefinition(prepared.definition,{},undefined,'operation');dirty=true;blend.reset();
  templateHelp.textContent=`Proposed for ${prepared.origin.effectiveOn}, as known at ${displayInstant(prepared.origin.knownAt,o.zone(),true)}. This is an unpriced draft. Preview calculates under the selected header context.`;
  renderStatus();
 }
 showView(activeView);reset();initialized=true;return {load,reset,place:()=>shell.place(),refresh,snapshotChanged,contextChanged,liveRefresh,sync,dateDefinition,openPrepared,guard,unconfirmed:()=>!!pending.command||dirty||formulaDirty||activationDirty||blend.dirty()};
}
