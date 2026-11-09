import type {DockLayout} from './dock-model.js';

// Curve builder's arrangement: its views above the preview, so an edit and the prices it gives are on one screen.
export const workbenchArrangementKey='openetrm.custom-curves.layout.v2';
export const workbenchLayout=():DockLayout=>({kind:'split',direction:'column',ratio:0.62,children:[
 {kind:'group',tabs:['graph','configuration','versions','lineage','history'],active:'graph'},
 {kind:'group',tabs:['outputs'],active:'outputs'}]});
