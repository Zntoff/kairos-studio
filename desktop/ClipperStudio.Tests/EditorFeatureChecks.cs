using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ClipperStudio.Engine;
using ClipperStudio.Server;

static class EditorFeatureChecks
{
    public static async Task Run(string url, string sample, Action<bool,string> check)
    {
        using var http = new HttpClient { BaseAddress = new Uri(url) };
        await TestSession.Open(http);
        async Task<JsonNode> Upload(string path)
        {
            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(File.ReadAllBytes(path)), "file", Path.GetFileName(path));
            var response = await http.PostAsync($"/api/projects/{Store.DefaultProject}/resources", form);
            check(response.IsSuccessStatusCode, "Editor resource upload: " + await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<JsonNode>())!;
        }
        var work = Paths.Dir("editor-features");
        var image = Path.Combine(work,"green.png");
        var sound = Path.Combine(work,"tone.wav");
        Ff.Run(["-f","lavfi","-i","color=c=lime:s=180x320","-frames:v","1",image]);
        Ff.Run(["-f","lavfi","-i","sine=frequency=440:duration=2",sound]);
        var imageResource = await Upload(image);
        var audioResource = await Upload(sound);
        var videoResource = await Upload(sample);
        check(J.D(audioResource["duration"]) > 1.9 && J.D(videoResource["duration"]) >= 3,
            "Import must probe audio-only and video resource durations.");
        var response = await http.PostAsJsonAsync("/api/sources/from-path", new { path=sample,rights="own",evidence="Editor feature fixture" });
        var src = (await response.Content.ReadFromJsonAsync<JsonNode>())!;
        var store = new Store();
        var editor = new EditorSvc(store);
        string id = editor.CreateClip(J.S(src["id"]),0,3);
        var state = editor.State(id);
        var edl = (JsonObject)J.Clone(state["edl"]!);
        edl["safe_area"] = new JsonObject { ["left"]=120,["right"]=200,["top"]=100,["bottom"]=360,["locked"]=false };
        edl["editor_mode"] = "long";
        edl["media"] = new JsonArray(
            new JsonObject { ["resource_id"]=J.S(imageResource["id"]),["t"]=0,["duration"]=1 },
            new JsonObject { ["resource_id"]=J.S(videoResource["id"]),["t"]=1,["duration"]=1,["offset"]=1 },
            new JsonObject { ["resource_id"]=J.S(audioResource["id"]),["t"]=.2,["duration"]=2,["gain"]=.5 });
        editor.SaveDraft(id,new JsonObject { ["edl"]=edl,["tracks"]=J.Clone(state["tracks"]!) });
        var reopened = editor.State(id);
        check(J.D(reopened["frame"]?["safe"]?["bottom"])==360 && !J.B(reopened["edl"]?["safe_area"]?["locked"]),"Unlocked custom safe area must survive draft save and reopen.");
        var render = editor.RenderDraft(id,_=>{});
        var path = Path.Combine(J.S(render["dir"]),"es","clip.mp4");
        // Source language can vary with registration defaults.
        path = Directory.GetFiles(J.S(render["dir"]),"clip.mp4",SearchOption.AllDirectories).Single();
        byte[]? pixel=null;
        Ff.Exec("ffmpeg",["-v","error","-ss","0.5","-i",path,"-frames:v","1","-vf","format=rgb24,crop=1:1:540:960","-f","rawvideo","-pix_fmt","rgb24","-"],true,b=>pixel=b);
        check(pixel is {Length:>=3} && pixel[1]>180 && pixel[0]<50,"Imported image must appear in actual exported frames.");
        var final = editor.State(id);
        check(J.D(final["edl"]?["safe_area"]?["left"])==120 && J.S(final["edl"]?["editor_mode"])=="long" && J.Items(final["edl"]?["media"]).Count()==3,"Render must preserve custom margins, long mode and all media layers.");
        check(Renderer.Measure(path,-14,-1)?["input_i"] > -45,"Imported audio must be audible even when the source is silent.");
        var unknown = (JsonObject)J.Clone(edl);
        unknown["media"]![0]!["resource_id"]="missing";
        bool rejected=false;
        try{editor.SaveDraft(id,new JsonObject { ["edl"]=unknown });}catch(EditException){rejected=true;}
        check(rejected,"Draft must reject nonexistent media IDs.");
        // Timeline availability and validation for multi-hour sources, without a multi-hour encode.
        var longSource=(JsonObject)J.Clone(src);longSource["id"]="src_long_fixture";longSource["duration"]=7200;
        store.Add("sources",longSource);
        string longId=editor.CreateClip("src_long_fixture",0,7200);
        var longState=editor.State(longId);
        check(J.D(longState["window"]?["a"])==0 && J.D(longState["window"]?["b"])==7200 && Renderer.Duration(longState["edl"]!)==7200,"Long editor must expose and accept the complete multi-hour source.");
        var trackEdl=EditorSvc.NewEdl("track",src.AsObject(),0,3,"es");
        trackEdl["layout"]="center_crop";
        trackEdl["framing"]=JsonNode.Parse("""{"track":[{"source_time":0,"x":0,"y":0.5},{"source_time":3,"x":1,"y":0.5}]}""");
        var trackReport=Renderer.RenderClip(trackEdl,src,null,"es",Path.Combine(work,"track"),new RenderSettings{Encoder="libx264",GpuDecode=false},_=>{});
        check(Ff.Probe(trackReport.Path)["width"]!.GetValue<int>()==1080 && Framing.CropExpression(trackEdl,"x").Contains("clip"),"Tracked vertical expressions must execute successfully in FFmpeg.");
        var samples = new List<byte[]>();
        foreach (var time in new[] { "0.1", "2.8" })
            Ff.Exec("ffmpeg",["-v","error","-ss",time,"-i",trackReport.Path,"-frames:v","1","-vf","format=rgb24,crop=1:1:540:600","-f","rawvideo","-pix_fmt","rgb24","-"],true,b=>samples.Add(b));
        check(samples.Count==2 && samples[0][0]>180 && samples[0][2]<60 && samples[1][2]>180 && samples[1][0]<60,
            "Exported tracking must visibly pan from the red subject at the left to the blue region at the right.");
        // Optional real-browser exercise in the same isolated server/library.
        if(Environment.GetEnvironmentVariable("CLIPPER_EDITOR_UI_TEST") is string script)
        {
            var uiApi=new Api();await uiApi.StartAsync();
            var info=new System.Diagnostics.ProcessStartInfo("node") { UseShellExecute=false,CreateNoWindow=true };
            info.ArgumentList.Add(script);info.Environment["CLIPPER_QA_URL"]=uiApi.Url;info.Environment["CLIPPER_QA_CLIP"]=id;
            using var process=System.Diagnostics.Process.Start(info)!;await process.WaitForExitAsync();
            await uiApi.StopAsync();
            check(process.ExitCode==0,"Long editor browser interaction checks must pass.");
        }
    }
}
