// Boot the extracted archive, not the publish/build folder. All requests stay below a version prefix.
import { chromium, firefox, webkit } from 'playwright';
import { createServer } from 'node:http';
import { readFile, mkdir, writeFile, mkdtemp, rm } from 'node:fs/promises';
import { resolve, extname, sep } from 'node:path';
import { tmpdir } from 'node:os';
import assert from 'node:assert/strict';

const [directory,product,reportFile]=process.argv.slice(2);
assert.ok(directory && ['writer','cell','launcher'].includes(product) && reportFile);
const root=resolve(directory),prefix='/producer/verified-version/';
await mkdir(resolve(reportFile,'..'),{recursive:true});
const mime={'.html':'text/html','.js':'text/javascript','.json':'application/json','.wasm':'application/wasm','.css':'text/css','.svg':'image/svg+xml','.woff2':'font/woff2'};
const server=createServer(async(request,response)=>{
    try{
        const url=new URL(request.url,'http://localhost');
        if(!url.pathname.startsWith(prefix)){response.writeHead(404).end();return;}
        const path=resolve(root,decodeURIComponent(url.pathname.slice(prefix.length))||'index.html');
        if(!path.startsWith(root+sep)){response.writeHead(403).end();return;}
        const data=await readFile(path);response.writeHead(200,{'Content-Type':mime[extname(path)]||'application/octet-stream'}).end(data);
    }catch{response.writeHead(404).end();}
});
await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
const origin=`http://127.0.0.1:${server.address().port}${prefix}`,runs=[];
try{for(const [name,engine]of Object.entries({chromium,firefox,webkit})){
    const profile=await mkdtemp(resolve(tmpdir(),'broiler-archive-'));
    const context=await engine.launchPersistentContext(profile,{viewport:{width:1280,height:960}}),run={engine:name,version:context.browser().version()};
    try{
        const page=await context.newPage(),errors=[];
        page.on('pageerror',error=>errors.push(String(error)));
        page.on('console',message=>{if(message.type()==='error')errors.push(message.text());});
        page.on('requestfailed',request=>errors.push(request.url()));
        page.on('response',response=>{if(response.status()>=400)errors.push(response.url());});
        await page.goto(origin,{waitUntil:'domcontentloaded'});
        if(product==='launcher'){
            await page.waitForFunction(()=>document.body.dataset.ready==='true');
            assert.equal(await page.getByRole('link',{name:'Open Writer',exact:true}).getAttribute('href'),'https://writer.broiler.app/');
        }else{
            const id=product==='cell'?'cells':'writer';
            await page.waitForFunction(id=>document.getElementById(id+'-status')?.hidden,id);
            await page.locator('canvas').click({position:{x:130,y:160}});
            await page.keyboard.press('Control+n');
            await page.waitForFunction(id=>document.getElementById(id+'-operation').dataset.outcome==='New',id);
            await page.locator('#'+id+'-text-input').focus();await page.keyboard.insertText('Archive smoke ÄΩ');
            const entry=await page.evaluate(()=>document.querySelector('script[type=module][src]').src);
            const text=await page.evaluate(async({entry,product})=>{
                const app=await import(entry);
                for(let i=0;i<100;i++){
                    const value=product==='writer'?app.documentPreview():JSON.parse(app.recoverySnapshot().payload)[0][0];
                    if(value==='Archive smoke ÄΩ')return value;
                    await new Promise(resolve=>setTimeout(resolve,50));
                }
                return '';
            },{entry,product});
            assert.equal(text,'Archive smoke ÄΩ');
        }
        assert.deepEqual(errors,[]);run.pass=true;
        await page.screenshot({path:resolve(reportFile+'.'+name+'.png')});
    }catch(error){run.pass=false;run.error=String(error);}
    finally{await context.close();await rm(profile,{recursive:true,force:true,maxRetries:5});runs.push(run);console.log(JSON.stringify(run));}
}}finally{await new Promise(resolve=>server.close(resolve));await mkdir(resolve(reportFile,'..'),{recursive:true});await writeFile(reportFile,JSON.stringify({product,root,runs},null,2));}
if(runs.some(run=>!run.pass))process.exitCode=1;
