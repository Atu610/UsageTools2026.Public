// Executes the actual in-page request script, using synthetic account data only.
'use strict';
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm'),assert=require('node:assert/strict');
const source=fs.readFileSync(path.join(__dirname,'../Services/ClaudeWebViewUsageFetcher.cs'),'utf8');
const script=source.match(/internal const string ModelsRequestScript = """\r?\n([\s\S]*?)\r?\n\s*""";/)?.[1];
assert.ok(script,'actual model script is available');
const results=[];
const model={model:'claude-opus-test',name:'Claude Test',inactive:false,overflow:false,email:'PRIVATE_MODEL_FIELD'};
const membership=(id,models=[model])=>({organization:{uuid:id,claude_ai_bootstrap_models_config:models,privateData:'PRIVATE_ORG_FIELD'}});
async function run(body,{status=200,reject=false,badJson=false}={}){
 const context={window:{},fetch:async(url,options)=>{
  assert.equal(url,'https://claude.ai/api/account');assert.equal(options.credentials,'include');assert.equal(options.cache,'no-store');
  if(reject)throw Error('PRIVATE_NETWORK_DETAIL');
  return {ok:status===200,status,json:async()=>{if(badJson)throw Error('PRIVATE_BODY');return body;}};
 }};
 vm.runInNewContext(script.replace('__ORG__',JSON.stringify('current-org')),context,{timeout:1000});
 for(let i=0;i<50&&context.window.__ut2026Models===undefined;i++)await new Promise(r=>setImmediate(r));
 assert.notEqual(context.window.__ut2026Models,undefined,'request completes');
 assert.ok(!context.window.__ut2026Models.includes('PRIVATE'),'private fields never leave the page');
 return JSON.parse(context.window.__ut2026Models);
}
function pass(name){results.push('PASS '+name);console.log(results.at(-1));}
(async()=>{
 const root=await run({memberships:[membership('other-org',[{...model,name:'Other account'}]),membership('current-org')],email:'PRIVATE_EMAIL'});
 assert.deepEqual(root.models,[{model:model.model,name:model.name,inactive:false,overflow:false}]);pass('live_root_memberships_shape_and_current_org_only');
 const wrapped=await run({account:{memberships:[membership('current-org')],email:'PRIVATE_EMAIL'}});assert.deepEqual(wrapped.models,root.models);pass('wrapped_legacy_response_compatible');
 const wrong=await run({memberships:[membership('other-org')],account:{memberships:[membership('current-org')]}});assert.equal(wrong.models,null);pass('authoritative_root_does_not_fall_back_to_other_identity');
 for(const body of [null,{}, {memberships:{}},{memberships:[null,membership('current-org',null)]}])assert.equal((await run(body)).models,null);pass('missing_or_invalid_fields_remain_unknown');
 assert.deepEqual((await run({memberships:[membership('current-org',[])]})).models,[]);pass('explicit_empty_model_list_preserved');
 assert.equal((await run({memberships:[membership('current-org',Array.from({length:100},()=>model))]})).models.length,40);pass('model_list_bounded');
 const flags=await run({memberships:[membership('current-org',[null,{...model,inactive:true,overflow:true}])]});assert.equal(flags.models.length,1);assert.equal(flags.models[0].inactive,true);assert.equal(flags.models[0].overflow,true);pass('inactive_overflow_flags_and_null_entries');
 assert.equal((await run({}, {status:403})).diagnostic,'http=403');pass('http_failure_is_distinguished');
 for(const options of [{reject:true},{badJson:true}])assert.equal((await run({},options)).diagnostic,'request_or_json_failed');pass('network_and_json_failure_without_private_error');
})().catch(e=>{results.push('FAIL '+e.message);console.error(e);process.exitCode=1;}).finally(()=>{
 const out=path.resolve(process.argv[2]||path.join(__dirname,'../Reports/Release'));
 fs.mkdirSync(out,{recursive:true});
 fs.writeFileSync(path.join(out,'claude-model-fetch-tests.txt'),results.join('\n')+'\n');
});
