import test from 'node:test';
import assert from 'node:assert/strict';
import {compareDecimalStrings,pivotMatrix} from '../dist/analytics-model.js';
import * as analytics from '../dist/analytics-model.js';
import {chartOptions} from '../dist/chart-view.js';

test('numeric grid ordering preserves decimal strings beyond browser precision',()=>{
 const values=['2','10','-0.01','0','9007199254740993','9007199254740992','0.000000000000000002','0.000000000000000001'];
 assert.deepEqual(values.toSorted(compareDecimalStrings),['-0.01','0','0.000000000000000001','0.000000000000000002','2','10','9007199254740992','9007199254740993']);
 assert.equal(compareDecimalStrings('1.000','01'),0);
});
test('cross-tab places exact server values without adding or merging equal display-unit labels',()=>{
 const common={bookId:'a',book:'A',unit:'EA',currency:'TST',currencyReference:{code:'c'},quantitySpecification:{code:'q'}};
 const groups=[{...common,contractId:'jan',deliveryFrom:'2021-01-01',deliveryTo:'2021-02-01',quantity:'80',executionPnl:'0.000000000000000001'},
 {...common,contractId:'feb',deliveryFrom:'2021-02-01',deliveryTo:'2021-03-01',quantity:'-40',executionPnl:'-4'},
 {...common,quantitySpecification:{code:'other'},contractId:'jan',deliveryFrom:'2021-01-01',deliveryTo:'2021-02-01',quantity:'3',executionPnl:'2'}];
 const result=pivotMatrix(groups,['book','contract'],['contract'],['quantity','executionPnl']);
 assert.equal(result.rows.length,2);assert.equal(result.columns.length,7);
 assert.equal(result.rows[0][result.columns[3].key],'80');assert.equal(result.rows[0][result.columns[4].key],'0.000000000000000001');
 assert.equal(result.rows[1][result.columns[5].key],null);
 assert.throws(()=>pivotMatrix([...groups,groups[0]],['book','contract'],['contract'],['quantity']));
});
test('chart standard labels date meaning and units, preserves gaps and exact tooltip values',()=>{
 const option=chartOptions({title:'Curve',axis:'delivery',unit:'TST/EA',series:[{name:'Selected',points:[{x:'Jan',y:'0.123456789123456789'},{x:'Feb',y:null},{x:'Mar',y:'2'}]}]});
 assert.equal(option.xAxis.name,'Delivery period');assert.equal(option.yAxis.name,'TST/EA');
 assert.equal(option.series[0].connectNulls,false);assert.equal(option.series[0].data[1],null);
 assert.ok(option.tooltip.formatter([{seriesIndex:0,dataIndex:0}]).includes('0.123456789123456789'));
 assert.equal(chartOptions({title:'History',axis:'observation',unit:'TST/EA',series:[]}).xAxis.name,'Observation date');
});
test('book scenario chart labels categories without claiming a date axis',()=>{
 const option=chartOptions({title:'Shock changes',axis:'category',unit:'TST',kind:'bar',series:[{name:'Up',points:[{x:'book-a',label:'Book A',y:'4'}]}]});
 assert.equal(option.xAxis.name,'Book');assert.equal(option.xAxis.axisLabel.formatter('book-a'),'Book A');assert.equal(option.yAxis.scale,false);
});

test('distinct exact chart categories survive identical display labels and hostile tooltips are text',()=>{
 const option=chartOptions({title:'Curve',axis:'delivery',unit:'EA',series:[{name:'<img src=x>',points:[{x:'first',label:'Jan',y:'1'},{x:'second',label:'Jan',y:'2'}]}]});
 assert.deepEqual(option.xAxis.data,['first','second']);assert.deepEqual(option.series[0].data,[1,2]);assert.equal(option.xAxis.axisLabel.formatter('second'),'Jan');assert.equal(option.dataZoom[1].labelFormatter(1,'second'),'Jan');assert.equal(option.xAxis.axisPointer.label.formatter({value:'second'}),'Jan');
 assert.ok(!option.tooltip.formatter([{seriesIndex:0,dataIndex:0}]).includes('<img'));
 assert.throws(()=>chartOptions({title:'Bad',axis:'delivery',unit:'EA',series:[{name:'A',points:[{x:'same',y:'1'},{x:'same',y:'2'}]}]}));
});
test('position bars use a zero baseline while price lines can scale to their range',()=>{
 const spec={title:'Positions',axis:'delivery',unit:'EA',series:[{name:'A',points:[{x:'Jan',y:'440'}]}]};
 assert.equal(chartOptions({...spec,kind:'bar'}).yAxis.scale,false);
 assert.equal(chartOptions({...spec,kind:'line'}).yAxis.scale,true);
});
test('custom curve chart accepts the served category axis name',()=>{const option=chartOptions({title:'Rolling curve',axis:'category',axisName:'Contract position',unit:'USD/MMBtu',series:[]});assert.equal(option.xAxis.name,'Contract position');});

test('short plots hide redundant zoom sliders and units stay above large axis labels',()=>{
 const spec={title:'Positions',axis:'category',unit:'MWh',kind:'bar',series:[{name:'Book A',points:[{x:'a',y:'4000000'}]}]};
 const option=chartOptions(spec);assert.equal(option.yAxis.nameLocation,'end');assert.equal(option.dataZoom[1].show,false);
 const long=chartOptions({...spec,series:[{name:'Book A',points:Array.from({length:12},(_,i)=>({x:String(i),y:'1'}))}]});
 assert.equal(long.dataZoom[1].show,true);
});

test('price pivot matrices preserve quote multiplier partitions without merging prices',()=>{
 const unit={code:'energy',revision:{catalogId:'catalog',revisionId:'revision'}},currency={...unit,code:'usd'};
 const rows=['1','100'].map((quoteMultiplier,i)=>({bookId:'book-a',book:'Book A',instrumentType:'future',quantitySpecification:unit,currencyReference:currency,unit:'MWh',currency:'USD',quoteMultiplier,priceUnit:'USD/MWh × '+quoteMultiplier,executionPriceMean:String(i+1)}));
 const result=pivotMatrix(rows,['book','instrumentType'],['instrumentType'],['executionPriceMean']);
 assert.equal(result.rows.length,2);assert.ok(result.columns.some(c=>c.key==='priceUnit'));
 assert.equal(result.rows[0].priceUnit,'USD/MWh × 1');assert.equal(result.rows[1].priceUnit,'USD/MWh × 100');
});

test('pivot measure headers share an exact column group while export labels remain complete',()=>{
 const groups=[{bookId:'a',book:'Book A',contractId:'jan',deliveryFrom:'2021-01-01',deliveryTo:'2021-02-01',unit:'EA',currency:'TST',quantitySpecification:{code:'q'},currencyReference:{code:'c'},quantity:'3',executionPnl:'4'}];
 const result=pivotMatrix(groups,['book','contract'],['contract'],['quantity','executionPnl']);
 const measures=result.columns.filter(c=>c.numeric);
 assert.ok(measures[0].groupKey);assert.equal(measures[0].groupKey,measures[1].groupKey);
 assert.equal(measures[0].groupTitle,'Jan 2021');assert.equal(measures[0].measureTitle,'Quantity');
 assert.equal(measures[1].title,'Jan 2021 · Execution P&L');
});

test('pivot charts use the selected row and column axes, preserve gaps and exact values',()=>{
 const base={instrumentType:'future',quantitySpecification:{code:'q'},currencyReference:{code:'c'},unit:'EA',currency:'TST',bookId:'a',book:'Same book label'};
 const jan={contractId:'jan',deliveryFrom:'2021-01-01',deliveryTo:'2021-02-01'},feb={contractId:'feb',deliveryFrom:'2021-02-01',deliveryTo:'2021-03-01'};
 const groups=[{...base,...jan,quantity:'9007199254740993.000000000000000001'},{...base,...feb,quantity:null},{...base,...jan,bookId:'b',quantity:'2'}];
 const [panel]=analytics.pivotChartPanels(groups,['book','contract'],['contract'],'quantity');
 assert.equal(panel.axis,'delivery');assert.equal(panel.series.length,2);
 assert.notEqual(panel.series[0].id,panel.series[1].id);
 assert.deepEqual(panel.series[0].points.map(p=>p.y),['9007199254740993.000000000000000001',null]);
 assert.equal(panel.series[1].points[1].y,null);
 assert.throws(()=>analytics.pivotChartPanels([...groups,groups[0]],['book','contract'],['contract'],'quantity'));
});

test('price charts keep exact reference and quote multiplier partitions separate',()=>{
 const base={instrumentType:'financial_swap',bookId:'a',book:'Book A',unit:'MWh',currency:'USD',quantitySpecification:{code:'q',revision:'one'},currencyReference:{code:'usd'},fixedPriceMean:'2.500000000000000001'};
 const panels=analytics.pivotChartPanels([{...base,quoteMultiplier:'1',priceUnit:'USD/MWh'},{...base,quoteMultiplier:'100',priceUnit:'USD/MWh × 100'},{...base,quantitySpecification:{code:'q',revision:'two'},quoteMultiplier:'1',priceUnit:'USD/MWh'}],['book'],[],'fixedPriceMean');
 assert.equal(panels.length,3);assert.equal(panels[1].unit,'USD/MWh × 100');
 assert.ok(panels.every(p=>p.series[0].points[0].y==='2.500000000000000001'));
});

test('heatmaps preserve missing cells and show exact safe tooltip values',()=>{
 const option=chartOptions({title:'Pivot',axis:'delivery',unit:'TST',kind:'heatmap',series:[{id:'a',name:'<Book A>',points:[{x:'jan',label:'Jan',y:'-0.000000000000000001'},{x:'feb',label:'Feb',y:null}]},{id:'b',name:'Book B',points:[{x:'jan',label:'Jan',y:'2'},{x:'feb',label:'Feb',y:'0'}]}]});
 assert.equal(option.series[0].type,'heatmap');assert.equal(option.yAxis.type,'category');
 assert.deepEqual(option.series[0].data.map(d=>d.slice(0,2)),[[0,0],[0,1],[1,1]]);
 const tooltip=option.tooltip.formatter({value:option.series[0].data[0]});
 assert.ok(tooltip.includes('-0.000000000000000001'));assert.ok(!tooltip.includes('<Book A>'));
 assert.equal(option.visualMap.min,-option.visualMap.max);
});

test('rational server amounts sort exactly and plot approximate pixels with exact tooltips',()=>{
 assert.equal(compareDecimalStrings('1/3','0.333333333333333333'),1);
 assert.equal(compareDecimalStrings('-2/3','-0.6'),-1);
 const option=chartOptions({title:'Exact mean',axis:'delivery',unit:'TST/EA',series:[{name:'Mean',points:[{x:'Jan',y:'1/3'}]}]});
 assert.ok(Math.abs(option.series[0].data[0]-1/3)<Number.EPSILON);
 assert.match(option.tooltip.formatter([{seriesName:'Mean',seriesIndex:0,dataIndex:0,axisValue:'Jan'}]),/1\/3/);
});

test('a bounded large rational plots its finite ratio without overflowing either integer',()=>{
 const n='1'+'0'.repeat(399)+'1',d='1'+'0'.repeat(400);
 const option=chartOptions({title:'Near one',axis:'delivery',unit:'TST/EA',series:[{name:'Exact',points:[{x:'Jan',y:n+'/'+d}]}]});
 assert.equal(option.series[0].data[0],1);
});

test('finite rational chart coordinates remain finite at the Number exponent boundary',()=>{
 const value='1'+'0'.repeat(399)+'/'+'9'+'0'.repeat(90);
 const option=chartOptions({title:'Large ratio',axis:'delivery',unit:'TST/EA',series:[{name:'Exact',points:[{x:'Jan',y:value}]}]});
 assert.ok(Number.isFinite(option.series[0].data[0])&&option.series[0].data[0]>1e308);
});

test('cross-tab gives every column group its own count of positions it leaves out',()=>{
 const common={bookId:'a',book:'Book A',unit:'EA',currency:'TST',quantitySpecification:{code:'q'},currencyReference:{code:'c'}};
 const groups=[{...common,contractId:'jan',deliveryFrom:'2021-01-01',deliveryTo:'2021-02-01',executionPnl:null,unvaluedPositions:4},
  {...common,contractId:'feb',deliveryFrom:'2021-02-01',deliveryTo:'2021-03-01',executionPnl:'8.16',unvaluedPositions:0}];
 const result=pivotMatrix(groups,['book','contract'],['contract'],['executionPnl','unvaluedPositions']);
 const counts=result.columns.filter(c=>c.measure==='unvaluedPositions');
 assert.deepEqual(counts.map(c=>c.title),['Jan 2021 · Not valued','Feb 2021 · Not valued']);
 assert.deepEqual(counts.map(c=>result.rows[0][c.key]),['4','0']);
});

test('a commodity pivot tells commodities apart by code and labels them by name',()=>{
 const common={instrumentType:'future',unit:'EA',currency:'TST',quantitySpecification:{code:'q'},currencyReference:{code:'c'}};
 const groups=[{...common,commodity:{code:'gas',name:'Same label'},quantity:'80'},{...common,commodity:{code:'power',name:'Same label'},quantity:'3'},
  {...common,instrumentType:'financial_swap',commodity:{code:'gas',name:'Same label'},quantity:'5'}];
 const result=pivotMatrix(groups,['commodity','instrumentType'],['instrumentType'],['quantity']);
 assert.equal(result.columns[0].title,'Commodity');assert.deepEqual(result.rows.map(r=>r.commodity),['Same label','Same label','Same label']);
 const future=result.columns.find(c=>c.measure==='quantity'&&c.groupTitle==='Future');
 assert.deepEqual(result.rows.map(r=>r[future.key]).filter(value=>value!==null),['80','3']);
 const [panel]=analytics.pivotChartPanels(groups.slice(0,2),['commodity'],[],'quantity');
 assert.deepEqual(panel.series[0].points.map(p=>[p.label,p.y]),[['Same label','80'],['Same label','3']]);
 assert.equal(new Set(panel.series[0].points.map(p=>p.x)).size,2);
 assert.throws(()=>pivotMatrix([{...groups[0],commodity:undefined}],['commodity','instrumentType'],['instrumentType'],['quantity']));
});
