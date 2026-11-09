import test from 'node:test';
import assert from 'node:assert/strict';
import {contractPriceStatusRows} from '../dist/contract-price-status.js';
test('saved release states retain pending contracts without fabricating prices or publication dates',()=>{
 const request={snapshotId:'close',curveId:'saved',version:2,nodeId:'source'};
 const rows=[{nodeId:'source',curveCode:'hub',targetContractId:'jan',sourceContractId:'jan',deliveryFrom:'2021-01-01',deliveryTo:'2021-02-01',expiry:'2020-12-29T00:00:00Z',state:'awaiting_publication',publicationId:'report',publicationVersion:1,index:'hub',occurrenceId:'jan-report',expectedPublicationAt:'2021-01-04T15:00:00Z',publishedAt:null,releaseId:null,fixingPrice:null,selectedAsDependency:false}];
 const response={...request,rows};delete response.nodeId;
 const result=contractPriceStatusRows(response,request,[{id:'jan',label:'January'}]);
 assert.equal(result[0][0],'January');assert.equal(result[0][2],'Awaiting publication');assert.equal(result[0][3],null);assert.equal(result[0][4],rows[0].expectedPublicationAt);
 rows[0]={...rows[0],state:'publication_schedule_missing',occurrenceId:null,expectedPublicationAt:null};
 assert.equal(contractPriceStatusRows(response,request,[])[0][2],'Publication schedule missing');
 for(const invalid of [{...response,curveId:'other'},{...response,snapshotId:'new-close'},{...response,version:3},{...response,rows:[{...rows[0],state:'final'}]}])assert.throws(()=>contractPriceStatusRows(invalid,request,[]),/inconsistent/);
 const fixed={...rows[0],state:'final',occurrenceId:'jan-report',publishedAt:'2021-01-04T15:00:00Z',releaseId:'final',fixingPrice:'2.750000'};
 assert.equal(contractPriceStatusRows({...response,rows:[fixed]},request,[])[0][3],'2.750000');
 assert.equal(contractPriceStatusRows({...response,rows:[fixed]},request,[])[0][7],'Not selected for this valuation');
 assert.equal(contractPriceStatusRows({...response,rows:[{...fixed,selectedAsDependency:true}]},request,[])[0][7],'Selected for this valuation');
});
