// Protocol choices only. Expiry and status are supplied by the managed backend.
export type ContractDisplaySelection={version:'curve_display_v1';contracts:'captured_contracts_with_status_v1'|'eligible_contracts_v1'};
export type ContractDisplay={selection:ContractDisplaySelection;capturedCount:number;eligibleCount:number;contracts:{contractId:string;deliveryFrom:string;deliveryTo:string;expiry:string;expired:boolean;status:string;unavailableReason:string|null}[]};
export const defaultContractDisplay=():ContractDisplaySelection=>({version:'curve_display_v1',contracts:'eligible_contracts_v1'});
export function readContractDisplay(value:unknown):ContractDisplaySelection{
 const v=value as Partial<ContractDisplaySelection>|null;
 if(!v||v.version!=='curve_display_v1'||!['captured_contracts_with_status_v1','eligible_contracts_v1'].includes(v.contracts??''))throw Error('Choose a supported curve contract view.');
 return {version:v.version,contracts:v.contracts!};
}
