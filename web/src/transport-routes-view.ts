import {setupDescribedOperations,type DescribedOperationsOptions} from './described-operations-view.js';

/** Retained chosen routes refer to exact native capacity claims. */
export function setupTransportRoutes(root:HTMLElement,o:DescribedOperationsOptions){
 return setupDescribedOperations(root,o,{kind:'route',path:'gas/transport/routes',scopeKey:'routeId',listKey:'routes',data:'transport-routes',commandData:'routeCommand',storage:'openetrm.transport.routes.request.',configurationPermission:'ConfigureGasServices',label:'Transport route plans',plural:'transport routes',configurationHeading:'Route definitions',operationsHeading:'Chosen route plans',boundary:'Manually selected routes over existing capacity claims. Nominations, measured transport, title, custody and cash remain separate.'});
}
