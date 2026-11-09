import test from 'node:test';
import assert from 'node:assert/strict';
import {riskViews,isRiskView} from '../dist/risk-views.js';
import {riskScopeSummary} from '../dist/risk-scope.js';

test('Risk has one view per function, Reports first',()=>{
 assert.deepEqual([...riskViews],['risk','risk-scenarios','risk-var','risk-runs']);
 assert.equal(isRiskView('risk-var'),true);
 assert.equal(isRiskView('market'),false);
});
test('the scope bar states an empty selection as what it means',()=>{
 assert.equal(riskScopeSummary({portfolios:[],books:[],trades:null}),'All portfolios · All readable books · All trades in these books');
});
test('the scope bar names a portfolio, counts books and trades',()=>{
 assert.equal(riskScopeSummary({portfolios:['Example desk'],books:['Book A','Book B'],trades:3}),'Example desk · 2 books · 3 trades');
 assert.equal(riskScopeSummary({portfolios:['A','B'],books:['Book A'],trades:1}),'2 portfolios · Book A · 1 trade');
 assert.equal(riskScopeSummary({portfolios:[],books:[],trades:0}),'All portfolios · All readable books · No trades chosen');
});
