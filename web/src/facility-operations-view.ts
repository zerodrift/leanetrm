import {setupDescribedOperations,type DescribedOperationsOptions} from './described-operations-view.js';

/** Physical facility operations retain their native commands and exact source links. */
export function setupFacilityOperations(root:HTMLElement,o:DescribedOperationsOptions){
 const view=setupDescribedOperations(root,o,{kind:'facility',path:'facilities/operations',scopeKey:'facilityId',listKey:'facilities',data:'facility-ops',commandData:'facilityCommand',storage:'openetrm.facility.operations.request.',configurationPermission:'ConfigureFacilityLimits',label:'Physical facility',plural:'physical facilities',configurationHeading:'Facility definitions',operationsHeading:'Facility activity',boundary:'Physical rules and activity belong to the facility. Commercial rights, title and cash remain separate.'});
 return {...view,show:(section:'definitions'|'activity')=>view.show(section)};
}
