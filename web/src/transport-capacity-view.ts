import {setupDescribedOperations,type DescribedOperationsOptions} from './described-operations-view.js';

/** Commercial capacity claims do not record physical flow, title or financial cash. */
export function setupTransportCapacity(root:HTMLElement,o:DescribedOperationsOptions){
 return setupDescribedOperations(root,o,{kind:'transport',path:'gas/transport/capacity',scopeKey:'entitlementId',listKey:'entitlements',data:'transport-capacity',commandData:'transportCommand',storage:'openetrm.transport.capacity.request.',configurationPermission:'ConfigureGasServices',label:'Transport capacity',plural:'transport entitlements',configurationHeading:'Entitlement definitions',operationsHeading:'Chosen capacity claims',boundary:'Commercial entitlement and chosen capacity claims. This does not record physical transport, title, custody or cash.'});
}
