using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using ClipperStudio.Engine;
using ClipperStudio.Server;
using ClipperStudio.Ai;

static class VideoProjectChecks
{
    public static async Task Run(Api api,Action<bool,string> check)
    {
        using var http=new HttpClient{BaseAddress=new Uri(api.Url)};await TestSession.Open(http);
        async Task<JsonNode> Get(string path)=>(await http.GetFromJsonAsync<JsonNode>(path))!;
        var clips=J.Items(await Get("/api/clips")).Count();
        var sources=J.Items(await Get("/api/sources")).Count();
        var response=await http.PostAsJsonAsync("/api/projects",new{name="Video compositions"});
        var project=(await response.Content.ReadFromJsonAsync<JsonNode>())!;string pid=J.S(project["id"]);
        var work=Paths.Dir("composition-tests");var image=Path.Combine(work,"green.png");var sound=Path.Combine(work,"tone.wav");
        Ff.Run(["-f","lavfi","-i","color=c=lime:s=320x180","-frames:v","1",image]);
        Ff.Run(["-f","lavfi","-i","sine=frequency=500:duration=2",sound]);
        async Task<JsonNode> Upload(string path)
        {
            using var form=new MultipartFormDataContent();form.Add(new ByteArrayContent(File.ReadAllBytes(path)),"file",Path.GetFileName(path));
            var r=await http.PostAsync($"/api/projects/{pid}/resources",form);check(r.IsSuccessStatusCode,"Composition media upload failed.");
            return (await r.Content.ReadFromJsonAsync<JsonNode>())!;
        }
        var img=await Upload(image);var audio=await Upload(sound);
        async Task<JsonNode> Create(string ratio,string format="mp4")
        {
            var r=await http.PostAsJsonAsync($"/api/projects/{pid}/videos",new{name="Video "+ratio,settings=new{ratio,format,fps=24,resolution=720,quality="draft"}});
            check(r.IsSuccessStatusCode,"Blank video creation failed.");return (await r.Content.ReadFromJsonAsync<JsonNode>())!;
        }
        var video=await Create("16:9");string id=J.S(video["id"]);
        check(!J.Items(video["document"]?["items"]).Any() && J.S(video["project_id"])==pid,"New video must start empty inside its workspace.");
        check(J.Items(await Get("/api/clips")).Count()==clips && J.Items(await Get("/api/sources")).Count()==sources,"Creating a composition must not create a source or short.");
        check(!J.Items(await Get($"/api/projects/{Store.DefaultProject}/videos")).Any(),"Video composition lists must be isolated by project.");
        check((await http.PostAsync($"/api/video-projects/{id}/render",null)).StatusCode==HttpStatusCode.BadRequest,"Empty compositions must reject export.");
        var doc=new JsonObject{["duration"]=2,["items"]=new JsonArray(
            new JsonObject{["id"]="picture",["asset_id"]=J.S(img["id"]),["start"]=0,["offset"]=0,["duration"]=2,["gain"]=1,["fit"]="cover"},
            new JsonObject{["id"]="sound",["asset_id"]=J.S(audio["id"]),["start"]=0,["offset"]=0,["duration"]=2,["gain"]=.5})};
        response=await http.PutAsJsonAsync($"/api/video-projects/{id}",new{revision=0,document=doc});
        check(response.IsSuccessStatusCode,"Composition draft save failed: "+await response.Content.ReadAsStringAsync());
        check((await http.PutAsJsonAsync($"/api/video-projects/{id}",new{revision=0,document=doc})).StatusCode==HttpStatusCode.BadRequest,"Stale composition revisions must not overwrite newer edits.");
        foreach(var ratio in new[]{"16:9","9:16","1:1"})
        {
            var current=ratio=="16:9"?await Get($"/api/video-projects/{id}"):await Create(ratio,ratio=="1:1"?"mov":"mp4");string vid=J.S(current["id"]);
            if(vid!=id)check((await http.PutAsJsonAsync($"/api/video-projects/{vid}",new{revision=0,document=doc})).IsSuccessStatusCode,"Alternate ratio save failed.");
            var run=(await(await http.PostAsync($"/api/video-projects/{vid}/render",null)).Content.ReadFromJsonAsync<JsonNode>())!;
            JsonNode status;
            do{await Task.Delay(100);status=await Get("/api/runs/"+J.S(run["run_id"]));}while(J.S(status["status"]) is "queued" or "running");
            check(J.S(status["status"])=="done","Composition export failed: "+J.S(status["error"]));
            var bytes=await http.GetByteArrayAsync($"/api/video-projects/{vid}/export");var path=Path.Combine(work,vid+"."+J.S(current["settings"]?["format"]));File.WriteAllBytes(path,bytes);
            var probe=Ff.Probe(path);
            check(J.D(probe["width"])==J.D(current["settings"]?["width"]) && J.D(probe["height"])==J.D(current["settings"]?["height"]) && J.D(probe["fps"])==24,"Export must honor the selected canvas, file format and frame rate.");
            check(Renderer.Measure(path,-14,-1)?["input_i"] > -45,"Composition export must mix imported audio.");
        }
        string redPath=Path.Combine(work,"red-cut.mp4"),greenPath=Path.Combine(work,"green-cut.mp4");
        Ff.Run(["-f","lavfi","-i","color=c=red:s=320x180:r=24:d=1","-c:v","libx264",redPath]);
        Ff.Run(["-f","lavfi","-i","color=c=lime:s=320x180:r=30:d=1","-c:v","libx264",greenPath]);
        var red=await Upload(redPath);
        var green=await Upload(greenPath);
        var cuts=await Create("16:9");
        string cutsId=J.S(cuts["id"]);
        JsonObject Cut(string key,JsonNode media,double start,double length,string fit)=>new(){
            ["id"]=key,["asset_id"]=J.S(media["id"]),["start"]=start,["offset"]=0,["duration"]=length,["fit"]=fit};
        var cutDoc=new JsonObject{["duration"]=1.5,["items"]=new JsonArray(
            Cut("red",red,0,.5833,"cover"),Cut("green",green,.5834,.4166,"blur"),Cut("after-gap",red,1.25,.25,"cover"))};
        check((await http.PutAsJsonAsync($"/api/video-projects/{cutsId}",new{revision=0,document=cutDoc})).IsSuccessStatusCode,
            "Adjacent-cut fixture did not save.");
        var cutRun=(await(await http.PostAsync($"/api/video-projects/{cutsId}/render",null)).Content.ReadFromJsonAsync<JsonNode>())!;
        JsonNode cutStatus;
        do{await Task.Delay(100);cutStatus=await Get("/api/runs/"+J.S(cutRun["run_id"]));}while(J.S(cutStatus["status"]) is "queued" or "running");
        check(J.S(cutStatus["status"])=="done","Adjacent-cut render failed: "+J.S(cutStatus["error"]));
        var cutPath=Path.Combine(work,"cut-export.mp4");
        File.WriteAllBytes(cutPath,await http.GetByteArrayAsync($"/api/video-projects/{cutsId}/export"));
        byte[]? cutFrames=null;
        Ff.Exec("ffmpeg",["-v","error","-i",cutPath,"-vf","crop=2:2,scale=1:1,format=rgb24","-f","rawvideo","-"],true,b=>cutFrames=b);
        check(cutFrames?.Length==36*3,"Cut regression needs every exported frame.");
        bool HasColor(int frame,int channel)=>cutFrames![frame*3+channel]>180 && cutFrames[frame*3+(1-channel)]<50;
        check(Enumerable.Range(0,14).All(f=>HasColor(f,0)) && Enumerable.Range(14,10).All(f=>HasColor(f,1)),
            "Adjacent mixed-FPS clips must fill every frame, including the cut.");
        check(Enumerable.Range(24,6).All(f=>cutFrames!.Skip(f*3).Take(3).All(c=>c<25)) &&
            Enumerable.Range(30,6).All(f=>HasColor(f,0)),"The export must preserve intentional gaps.");
        // A private-project asset must never be readable through another composition.
        var outside=await(await http.PostAsJsonAsync($"/api/projects/{Store.DefaultProject}/videos",new{name="Outside",settings=new{ratio="16:9"}})).Content.ReadFromJsonAsync<JsonNode>();
        check((await http.PutAsJsonAsync("/api/video-projects/"+J.S(outside!["id"]),new{revision=0,document=doc})).StatusCode==HttpStatusCode.BadRequest,"Cross-project media injection must fail.");
        var service=new VideoProjects(new Store());var record=service.Get(id);
        var contract=await Get($"/api/video-projects/{id}/ai/tools");
        check(J.S(contract["contract"])==EditorTools.Version&&J.Items(contract["tools"]).Any(t=>J.S(t["name"])=="editor_trim_media"),"The local API must expose the same typed registry used by the AI executor.");
        // The same Shorts tools must persist and reach composition exports, at the chosen aspect ratio.
        var studioVideo=await Create("16:9");string studioId=J.S(studioVideo["id"]);
        var studioState=await Get("/api/video-projects/"+studioId);
        check(J.Items(studioState["studio"]?["catalog"]?["effects"]).Any(x=>J.S(x["id"])=="face_focus"),"Composition catalog must include the Shorts reaction tool.");
        var studioDoc=(JsonObject)J.Clone(studioState["document"]!);studioDoc["duration"]=2;studioDoc["items"]=J.Clone(doc["items"]!);
        var toolEdl=studioDoc["tools"]!["edl"]!;
        toolEdl["texts"]=JsonNode.Parse("""[{"t":0,"duration":2,"text":{"en":"SHARED TOOLS"},"x":0.5,"y":0.5,"font":"anton","size":100,"color":"#FFFFFF","stroke":"#000000","stroke_w":0,"anim_in":"none","anim_out":"none"}]""");
        toolEdl["color"]!["saturation"]=-1;toolEdl["color"]!["vignette"]=.2;
        toolEdl["effects"]=JsonNode.Parse("""[{"type":"punch_in","t":0,"duration":0.5,"intensity":0.5},{"type":"face_focus","t":0.1,"duration":0.5,"intensity":1,"region":{"x":0.1,"y":0.1,"w":0.4,"h":0.5}}]""");
        toolEdl["progress"]!["on"]=true;toolEdl["transitions"]!["intro"]="fade";toolEdl["transitions"]!["intro_d"]=.2;
        toolEdl["audio"]!["voice_enhance"]=true;toolEdl["audio"]!["normalize_lufs"]=-16;
        string? music=Renderer.Library().Values.FirstOrDefault(x=>J.S(x["kind"])=="music") is {} entry?J.S(entry["id"]):null;
        toolEdl["audio"]!["music_id"]=music;toolEdl["audio"]!["music_gain"]=.2;
        studioDoc["tools"]!["track"]=JsonNode.Parse("""{"word_timed":true,"cues":[{"start":0.2,"end":1.8,"words":[{"w":"CAPTION","start":0.2,"end":1,"hl":true},{"w":"WORDS","start":1,"end":1.8,"hl":false}]}]}""");
        response=await http.PutAsJsonAsync("/api/video-projects/"+studioId,new{revision=0,document=studioDoc});
        check(response.IsSuccessStatusCode,"Saving shared Shorts controls failed: "+await response.Content.ReadAsStringAsync());
        var reopened=await Get("/api/video-projects/"+studioId);
        check(J.S(reopened["document"]?["tools"]?["edl"]?["texts"]?[0]?["text"]?["en"])=="SHARED TOOLS" && J.Items(reopened["document"]?["tools"]?["track"]?["cues"]).Count()==1,"Text and captions must survive a composition reload.");
        var studioRun=(await(await http.PostAsync("/api/video-projects/"+studioId+"/render",null)).Content.ReadFromJsonAsync<JsonNode>())!;
        JsonNode studioStatus;
        do{await Task.Delay(100);studioStatus=await Get("/api/runs/"+J.S(studioRun["run_id"]));}while(J.S(studioStatus["status"]) is "queued" or "running");
        check(J.S(studioStatus["status"])=="done","Shared tool export failed: "+J.S(studioStatus["error"]));
        var studioPath=Path.Combine(work,"shared-tools.mp4");File.WriteAllBytes(studioPath,await http.GetByteArrayAsync("/api/video-projects/"+studioId+"/export"));
        byte[]? frame=null;Ff.Exec("ffmpeg",["-v","error","-ss","0.8","-i",studioPath,"-frames:v","1","-f","rawvideo","-pix_fmt","rgb24","-"],true,b=>frame=b);
        check(frame?.Length==1280*720*3,"Shared tool export must retain the composition dimensions.");
        int white=0;for(int i=0;i<frame!.Length;i+=3)if(frame[i]>230&&frame[i+1]>230&&frame[i+2]>230)white++;
        check(white>500,"Actual text and caption pixels must appear in the exported video.");
        int corner=(80*1280+80)*3;check(Math.Abs(frame[corner]-frame[corner+1])<8 && Math.Abs(frame[corner+1]-frame[corner+2])<8,"Color controls must affect actual exported media.");
        check(Ass.W==1080 && Ass.H==1920,"Composition ASS scopes must leave Shorts dimensions unchanged.");

        // A local provider stub proves real billing-ledger integration without paid calls.
        int port=23000+Random.Shared.Next(2000),calls=0;
        using var listener=new HttpListener();listener.Prefixes.Add($"http://127.0.0.1:{port}/");listener.Start();
        var server=Task.Run(async()=>{
            while(listener.IsListening)
            {
                HttpListenerContext context;try{context=await listener.GetContextAsync();}catch{break;}
                _=await new StreamReader(context.Request.InputStream).ReadToEndAsync();Interlocked.Increment(ref calls);
                string arguments="""{"item_id":"sound","gain":0.25}""";
                var reply=new JsonObject{["model"]="composition-test",["usage"]=new JsonObject{["prompt_tokens"]=100,["completion_tokens"]=25},
                    ["choices"]=new JsonArray(new JsonObject{["finish_reason"]="tool_calls",["message"]=new JsonObject{["tool_calls"]=new JsonArray(new JsonObject{["id"]="volume_1",["type"]="function",["function"]=new JsonObject{["name"]="editor_set_media_volume",["arguments"]=arguments}},new JsonObject{["id"]="finish_1",["type"]="function",["function"]=new JsonObject{["name"]="editor_finish",["arguments"]="""{"summary":"Reduce audio to 25%."}"""}})}})};
                var bytes=Encoding.UTF8.GetBytes(reply.ToJsonString());context.Response.ContentType="application/json";await context.Response.OutputStream.WriteAsync(bytes);context.Response.Close();
            }
        });
        var ai=new AiSettings();ai.SetProvider("custom",$"http://127.0.0.1:{port}/v1",new JsonObject{["fast"]="composition-test",["standard"]="composition-test",["superior"]="composition-test"},new JsonObject{["fast"]=new JsonArray(1.0,2.0)},activate:true);
        var request=new{request_id=Guid.NewGuid().ToString(),prompt="Set the sound volume to 25%",tier="fast",document=doc,selected_item_id="sound"};
        var proposalResponse=await http.PostAsJsonAsync($"/api/video-projects/{id}/ai",request);
        check(proposalResponse.IsSuccessStatusCode,"AI proposal failed: "+await proposalResponse.Content.ReadAsStringAsync());
        var proposal=(await proposalResponse.Content.ReadFromJsonAsync<JsonNode>())!;
        check(J.D(proposal["document"]?["items"]?[1]?["gain"])==.25 && J.D(proposal["usage"]?["input_tokens"])==100,"AI proposals must contain validated edits and actual token usage.");
        check(J.D((await Get($"/api/video-projects/{id}"))["document"]?["items"]?[1]?["gain"])==.5,"Generating a paid proposal must not apply it automatically.");
        check((await http.PostAsJsonAsync($"/api/video-projects/{id}/ai",request)).IsSuccessStatusCode && calls==1,"Retrying an AI request ID must not bill a second call.");
        check(File.ReadLines(Path.Combine(Paths.Data,"usage.jsonl")).Any(l=>l.Contains("video_editor")&&l.Contains("composition-test")),"Editor AI calls must appear in the shared usage ledger.");
        if(Environment.GetEnvironmentVariable("CLIPPER_COMPOSITION_UI_TEST") is string script)
        {
            var info=new System.Diagnostics.ProcessStartInfo("node"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};info.ArgumentList.Add(script);
            info.Environment["CLIPPER_QA_URL"]=api.Url;info.Environment["CLIPPER_QA_VIDEO"]=id;info.Environment["CLIPPER_QA_PROJECT"]=pid;
            info.Environment["CLIPPER_QA_CUTS"]=cutsId;
            using var process=System.Diagnostics.Process.Start(info)!;var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();await process.WaitForExitAsync();Console.WriteLine(await stdout);check(process.ExitCode==0,"Composition browser checks failed: "+await stderr);
        }
        listener.Stop();await server;
        await EditorToolChecks.Run(service,record,doc,check);
        check((await http.DeleteAsync("/api/library/projects/"+pid)).IsSuccessStatusCode,"Deleting a composition workspace failed.");
        check((await http.GetAsync("/api/video-projects/"+id)).StatusCode==HttpStatusCode.NotFound,"Project deletion must remove its video compositions.");
    }
}
