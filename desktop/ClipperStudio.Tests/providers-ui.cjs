// Provider setup against a local models endpoint. Never contact a paid provider or use a real key.
const {chromium}=require(process.env.PLAYWRIGHT_MODULE||'playwright');
const http=require('node:http'),assert=require('node:assert/strict');
const base=process.env.CLIPPER_QA_URL||'http://127.0.0.1:18765';
(async()=>{
  let delay=0, calls=0;
  const server=http.createServer((req,res)=>{
    assert.equal(req.method,'GET');assert.equal(req.url,'/v1/models');calls++;
    setTimeout(()=>{res.setHeader('Content-Type','application/json');res.end(JSON.stringify({data:[{id:'local-a'},{id:'local-b'}]}));},delay);
  });
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  const url='http://127.0.0.1:'+server.address().port+'/v1';
  const browser=await chromium.launch({channel:'msedge',headless:true});
  try{
    const page=await browser.newPage(),errors=[];page.on('pageerror',e=>errors.push(e.message));
    await page.addInitScript(()=>localStorage.setItem('kairos.uiLanguage','es'));
  await require('./session.cjs')(page,base);
    await page.goto(base+'/#/apis');await page.locator('#aiProvider').waitFor();
    let checks=0;
    for(const width of [360,768,1280]){
      await page.setViewportSize({width,height:900});
      for(const pid of ['anthropic','openai','gemini','openrouter','mistral','deepseek','groq','ollama','custom']){
        await page.locator('#aiProvider').selectOption(pid);
        await page.locator(`#aiCard[data-provider=${pid}]`).waitFor();
        assert.equal(await page.locator('[data-model]').count(),3);
        assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1),`Provider ${pid} overflows at ${width}`);
        checks++;
      }
    }
    await page.locator('#aiUrl').fill(url);await page.locator('#aiLoad').click();
    await page.waitForFunction(()=>document.querySelectorAll('#aiModelList option').length===2);
    for(const tier of ['fast','standard','superior'])await page.locator(`[data-assign=${tier}][data-id=local-a]`).click();
    for(const width of [320,390,768,1280]){await page.setViewportSize({width,height:900});assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1),`Model catalog overflows at ${width}`);}
    await page.locator('[data-price="fast:0"]').fill('0.2');await page.locator('[data-price="fast:1"]').fill('0.8');
    await page.locator('#aiSave').click();
    await page.locator('.ai-status').filter({hasText:'En uso · listo'}).waitFor();
    const cfg=await (await page.request.get(base+'/api/config')).json();
    assert.equal(cfg.provider.id,'custom');assert.equal(cfg.api_key_set,true);
    assert.equal(cfg.tiers[0].model,'local-a');
    await page.reload();await page.locator('#aiCard[data-provider=custom]').waitFor();
    assert.equal(await page.locator('[data-model=fast]').inputValue(),'local-a');
    // A delayed model list from the old provider must not populate the newly selected one.
    delay=250;
    const response=page.waitForResponse(r=>r.url().includes('/api/settings/ai/models?provider=custom'));
    await page.locator('#aiLoad').click();await page.locator('#aiProvider').selectOption('openai');
    await page.locator('#aiCard[data-provider=openai]').waitFor();await response;
    assert.equal(await page.locator('#aiModelList option').count(),0);
    assert.equal(await page.locator('#aiProvider').inputValue(),'openai');
    assert.ok(calls>=2);assert.deepEqual(errors,[]);
    await page.request.put(base+'/api/settings/ai/provider',{data:{provider:'anthropic'}});
    console.log(`Passed ${checks} provider layouts, local model discovery, activation, persistence and stale response isolation (no paid calls).`);
  }finally{await browser.close();server.close();}
})().catch(e=>{console.error(e);process.exitCode=1});
