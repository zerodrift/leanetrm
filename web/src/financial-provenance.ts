import type {Reference} from './desk-model.js';
export type PriceUnitReference={currency:Reference;quantity:Reference;multiplier:string};
export const exactReferenceKey=(r:Reference)=>JSON.stringify([r.revision.catalogId,r.revision.revisionId,r.code]);
const fields=(prefix:string,r:Reference|undefined)=>({[prefix+'CatalogId']:r?.revision.catalogId??'',[prefix+'RevisionId']:r?.revision.revisionId??'',[prefix+'Code']:r?.code??''});
/** Exact export identities stay separate from human labels and from the holding's external quantity basis. */
export function financialReferenceFields(row:{currencyReference?:Reference;quantitySpecification?:Reference;priceUnitReference?:PriceUnitReference|null}):Record<string,string>{
 return {...fields('currency',row.currencyReference),...fields('quantity',row.quantitySpecification),...fields('priceCurrency',row.priceUnitReference?.currency),...fields('priceQuantity',row.priceUnitReference?.quantity),priceMultiplier:row.priceUnitReference?.multiplier??''};
}
