import type {ReferenceRevision,ReferenceSource} from './market-view.js';
// Category catalogs are selected by the server; pricing, units and inspection may name different exact revisions.
export function sameReferenceSource(owner:{profile:string;referenceSources?:Record<string,ReferenceSource>},table:string,
 read:{referenceRevision?:ReferenceRevision;definitionRevisionId?:string;profile:string;context:unknown},context:unknown){
 const expected=owner.referenceSources?.[table];
 return !!expected&&!!expected.referenceRevision?.catalogId&&!!expected.referenceRevision.revisionId&&!!expected.definitionRevisionId
  &&read.referenceRevision?.catalogId===expected.referenceRevision.catalogId&&read.referenceRevision?.revisionId===expected.referenceRevision.revisionId
  &&read.definitionRevisionId===expected.definitionRevisionId&&read.profile===owner.profile&&JSON.stringify(read.context)===JSON.stringify(context);
}
