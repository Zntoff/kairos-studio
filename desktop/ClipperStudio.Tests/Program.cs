// Integration checks against the real desktop HTTP server, in an isolated library, without paid API calls.
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using ClipperStudio.Ai;
using ClipperStudio.Engine;
using ClipperStudio.Server;

var root = Path.Combine(Path.GetTempPath(), "clipper-tests-" + Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable("CLIPPER_STUDIO_DATA", root);
Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
Directory.CreateDirectory(root);
if (args.Length == 4 && args[0] == "--preview")
{
    Preview.Render(args[1], args[2], args[3]);
    return;
}
var checks = 0;
void Check(bool ok, string message)
{
    if (!ok) throw new Exception(message);
    checks++;
}
if(args.Contains("--composition-only"))
{
    var compositionApi=new Api();await compositionApi.StartAsync();
    try{await VideoProjectChecks.Run(compositionApi,Check);Console.WriteLine($"Passed {checks} composition checks.");}
    finally{await compositionApi.StopAsync();}
    return;
}
Check(J.D(JsonValue.Create(42)) == 42, "Integer JSON numbers must retain their value.");
Check(J.D(JsonValue.Create(42L)) == 42, "Long JSON numbers must retain their value.");
Check(J.D(JsonValue.Create("NaN"), -1) == -1, "Non-finite numbers must be rejected.");
var acousticWords=Whisper.ParseWords(JsonNode.Parse("""
{"transcription":[{"text":" Hola mundo.","offsets":{"from":0,"to":6000},"tokens":[
{"text":"[_BEG_]","offsets":{"from":0,"to":0},"t_dtw":-1},
{"text":" Hola","offsets":{"from":0,"to":2000},"t_dtw":154},
{"text":" mun","offsets":{"from":2000,"to":3000},"t_dtw":304},
{"text":"do","offsets":{"from":3000,"to":5000},"t_dtw":324},
{"text":".","offsets":{"from":5000,"to":6000},"t_dtw":334}]}]}
""")!,6);
Check(acousticWords.Count==2 && acousticWords[1].W=="mundo.","Acoustic BPE tokens must merge into words and preserve punctuation.");
Check(Math.Abs(acousticWords[0].Start-1.5)<.001 && Math.Abs(acousticWords[1].Start-3)<.001,"DTW ticks must map to seconds without stretching over leading silence.");
var pausedCaptions=CaptionKit.CloseGaps(CaptionKit.TimedCues(acousticWords,null,[]),6);
Check(pausedCaptions.Count==2 && J.D(pausedCaptions[0]["end"])<2.1,"Caption highlighting must stop during speech pauses.");
var sameFrame=Whisper.ParseWords(JsonNode.Parse("""{"transcription":[{"tokens":[{"text":" de","offsets":{"from":0,"to":100},"t_dtw":100},{"text":" la","offsets":{"from":100,"to":200},"t_dtw":100}]}]}""")!,3);
Check(sameFrame.Count==1 && sameFrame[0].W=="de la","Words on the same acoustic frame must preserve all spoken text.");
var portableFixture=Path.Combine(root,"portable-paths");Directory.CreateDirectory(portableFixture);
Check(Paths.DataDirectory(portableFixture,null)==Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ClipperStudio"),"Installed builds must preserve the existing per-user library path.");
File.WriteAllText(Path.Combine(portableFixture,"portable.flag"),"");
Check(Paths.DataDirectory(portableFixture,null)==Path.Combine(portableFixture,"data"),"Portable builds must isolate their data beside the executable.");
Check(Paths.DataDirectory(portableFixture,root)==root,"Explicit data overrides must take precedence over portable mode.");
Check(LlmClient.Prompt("analysis").Length > 100, "Published analysis prompt missing.");
Check(File.Exists(Path.Combine(Paths.App, "config", "spoilers.txt")), "Spoiler terms missing.");

// ---- Multi-provider: an OpenAI-compatible server simulated with HttpListener ----
Check(File.Exists(Path.Combine(Paths.App, "config", "providers.json")), "Provider catalog missing.");
foreach (var env in new[] { "OPENAI_API_KEY", "GEMINI_API_KEY", "OPENROUTER_API_KEY" }) Environment.SetEnvironmentVariable(env, null);
{
    var mockPort = 18000 + Random.Shared.Next(2000);
    var mockUrl = $"http://127.0.0.1:{mockPort}/v1";
    var requests = new List<JsonNode>();
    var replies = new Queue<(int status, string body)>();
    using var listener = new HttpListener();
    listener.Prefixes.Add($"http://127.0.0.1:{mockPort}/");
    listener.Start();
    _ = Task.Run(async () =>
    {
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); } catch { break; }
            var body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
            string reply; int status;
            if (ctx.Request.Url!.AbsolutePath.EndsWith("/models")) (status, reply) = (200, """{"data":[{"id":"local-b"},{"id":"local-a"}]}""");
            else { lock (requests) requests.Add(JsonNode.Parse(body)!); (status, reply) = replies.Count > 0 ? replies.Dequeue() : (500, "no reply queued"); }
            ctx.Response.StatusCode = status;
            var bytes = System.Text.Encoding.UTF8.GetBytes(reply);
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    });
    var ai = new AiSettings();
    Check(ai.ActiveId == "anthropic" && AiSettings.Missing(ai.Conf())!.Contains("API key de Anthropic"), "Anthropic stays the default provider and asks for its key.");
    ai.SetProvider("custom", mockUrl + "/", null, null, activate: true);
    Check(AiSettings.Missing(ai.Conf())!.Contains("modelos"), "A provider without models must say which models are missing.");
    var (_, listed) = await ai.TestAsync("custom");
    Check(listed!.SequenceEqual(["local-a", "local-b"]), "Listing models must use the provider's /models, sorted.");
    ai.SetProvider("custom", null, new JsonObject { ["fast"] = "local-a", ["standard"] = "local-b", ["superior"] = "local-b" },
        new JsonObject { ["fast"] = new JsonArray(1.0, 2.0) }, activate: true);
    Check(ai.MissingActive == null && ai.Conf().BaseUrl == mockUrl, "A configured local provider is ready, with its address normalised.");
    var tiers = Pipeline.TierList(ai);
    Check(J.S(tiers[0]!["name"]) == "Rápido" && J.S(tiers[0]!["model"]) == "local-a" && J.S(tiers[1]!["price"]) == "precio no configurado", "Tiers must show the provider's models and prices.");

    var mockSchema = JsonNode.Parse("""{"type":"object","properties":{"n":{"type":"integer"},"m":{"type":["string","null"]}},"required":["n"],"additionalProperties":false}""")!.AsObject();
    var frame = Path.Combine(root, "frame.jpg");
    File.WriteAllBytes(frame, [0xff, 0xd8, 0xff]);
    string Tool(string args) => new JsonObject { ["model"] = "local-b", ["usage"] = new JsonObject { ["prompt_tokens"] = 100, ["completion_tokens"] = 20 },
        ["choices"] = new JsonArray(new JsonObject { ["finish_reason"] = "tool_calls", ["message"] = new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject {
            ["type"] = "function", ["function"] = new JsonObject { ["name"] = "submit_editing", ["arguments"] = args } }) } }) }.ToJsonString();
    replies.Enqueue((400, "This model does not support image_url inputs"));   // first: no vision
    replies.Enqueue((200, Tool("""{"n":1}""")));                               // then a contract failure (n must be 5)
    replies.Enqueue((200, Tool("""{"n":5}""")));
    var logs = new List<string>();
    var llm = new LlmClient(ai);
    var (data, meta) = await llm.DecideAsync("editing", "standard", "System", "User", mockSchema, d => J.D(d["n"]) == 5 ? null : "n must be 5", logs.Add, [frame]);
    Check(J.D(data["n"]) == 5 && meta.Attempts == 2 && meta.Model == "local-b" && meta.Input == 200, "The OpenAI-compatible client must retry contracts and add up tokens.");
    Check(requests[0]["messages"]![1]!["content"] is JsonArray && requests[1]["messages"]![1]!["content"] is JsonValue, "Frames are dropped when the model refuses images.");
    Check(J.S(requests[1]["tool_choice"]!["function"]!["name"]) == "submit_editing" && requests[1]["tools"]![0]!["function"]!["parameters"]!["additionalProperties"] != null, "Forced tool call with the full mockSchema.");
    Check(logs.Any(l => l.Contains("no acepta imágenes")) && J.S(requests[1]["messages"]![1]!["content"]).Contains("rejected by validation") == false, "Image fallback is logged.");
    Check(J.S(requests[2]["messages"]![1]!["content"]).Contains("rejected by validation"), "The validation error is sent back to the model.");

    replies.Enqueue((404, """{"error":{"message":"The model `local-b` does not exist"}}"""));
    try { await llm.DecideAsync("editing", "standard", "S", "U", mockSchema, _ => null, _ => { }); Check(false, "A missing model must fail."); }
    catch (LlmException e) { Check(e.Message.Contains("no encuentra el modelo"), "Missing models are explained: " + e.Message); }

    var gemini = LlmClient.Portable(mockSchema, "gemini");
    Check(gemini["additionalProperties"] == null && J.S(gemini["properties"]!["m"]!["type"]) == "string" && J.B(gemini["properties"]!["m"]!["nullable"]), "Gemini mockSchema: no additionalProperties, nullable instead of null unions.");
    Check(LlmClient.ParseJson("Respuesta: ```json\n{\"a\":1}\n```")?["a"] != null, "JSON written as text is recovered.");
    var usage = ai.Summarize(7);
    Check(J.D(usage["totals"]!["calls"]) == 2 && J.D(usage["totals"]!["unpriced_calls"]) == 2 && J.S(usage["by_model"]![0]!["provider"]) == "custom", "Unpriced local calls are counted but not billed.");
    Check(AiSettings.Hint("AIza-test-key-123") == "AIz…23" && !ai.State().ToJsonString().Contains("test-key"), "Key hints never reveal a short key.");
    replies.Enqueue((400,"model does not support tools"));
    replies.Enqueue((200,"""{"model":"local-b","choices":[{"finish_reason":"stop","message":{"content":"{\"n\":5}"}}]}"""));
    await llm.DecideAsync("editing","standard","S","U",mockSchema,d=>J.D(d["n"])==5?null:"bad",logs.Add);
    Check(requests.Last()["tools"]==null && requests.Last()["tool_choice"]==null,"Models without tools must receive the JSON schema as text.");
    Check(LlmClient.ParseJson("incomplete {")==null,"Malformed text must produce a retryable parse failure.");
    var sharedModels=new JsonObject{["fast"]="shared-model",["standard"]="shared-model",["superior"]="shared-model"};
    ai.SetProvider("custom",null,sharedModels,new JsonObject{["fast"]=new JsonArray(1,2)},true);
    ai.AddUsage("price-test","shared-model",1_000_000,1_000_000,"custom");
    ai.SetProvider("ollama",mockUrl,sharedModels,new JsonObject{["fast"]=new JsonArray(3,4)},true);
    ai.AddUsage("price-test","shared-model",1_000_000,1_000_000,"ollama");
    Check(llm.ModelFor("standard")=="local-b","Changing the active provider must not change an existing execution's models.");
    var priced=J.Items(ai.Summarize(7)["by_model"]).Where(r=>J.S(r["model"])=="shared-model").ToList();
    Check(priced.Count==2 && priced.Sum(r=>J.D(r["cost"]))==10,"Same-named models on different providers must keep separate prices and totals.");
    Check(new LlmClient(ai).Missing==null && ai.Key==null,"Local providers must work without an API key.");
    var localStore=new Store();
    var localVideo=Path.Combine(root,"analysis-local.mp4");File.WriteAllBytes(localVideo,[]);
    var localTranscript=Path.Combine(root,"analysis-local.json");File.WriteAllText(localTranscript,Whisper.Silent(30,"es").ToJsonString());
    localStore.Add("sources",new JsonObject{["id"]="src_local_model",["path"]=localVideo,["duration"]=30,["width"]=640,["height"]=360,["has_audio"]=false,["transcript_path"]=localTranscript});
    var localPipeline=new Pipeline(localStore,ai,new EditorSvc(localStore));
    var localJob=localPipeline.CreateJob("src_local_model",new JsonObject{["analysis_model"]="fast"});
    replies.Enqueue((200,Tool("""{"candidates":[{"start_segment":0,"end_segment":5,"score":90,"category":"test","reason":"Complete idea","hook_idea":"Local test"}]}""").Replace("submit_editing","submit_analysis")));
    await localPipeline.AnalyzeAsync(localJob,_=>{});
    Check(J.S(localStore.Get("jobs",localJob)?["status"])=="awaiting_selection","Desktop analysis must complete with a configured keyless local provider.");
    ai.SetProvider("anthropic", null, null, null, activate: true);
    listener.Stop();
}

var silent = Whisper.Silent(30, "es");
Check(J.Items(silent["segments"]).SelectMany(s => J.Items(s["words"])).Count() == 30, "Silent videos need real cut points for the editing contract.");
Check(CaptionKit.ClipWords(silent, 0, 30).Count == 0, "Silent cut points must never produce caption text.");

// Regression for job_39830c4f13: 576 segments, but Claude supplied a seconds-like 802..850 range.
var analysisSegments = J.Items(Whisper.Silent(2880, "es")["segments"]).ToList();
JsonObject Candidate(double start, double end, int score = 80) => new()
{
    ["start_segment"] = start, ["end_segment"] = end, ["score"] = score, ["category"] = "test", ["reason"] = "Test proposal",
};
var diagnostics = new List<string>();
var proposal = new JsonObject { ["candidates"] = new JsonArray(Candidate(802, 850, 95), Candidate(0, 5, 90), Candidate(2, 7, 85), Candidate(570, 575)) };
Check(Agents.ValidateAnalysis(proposal, analysisSegments, diagnostics.Add) == null, "One bad range must not reject the independent valid moments.");
Check(J.Items(proposal["candidates"]).Count() == 2, "Invalid and overlapping moments must be excluded.");
Check(J.D(proposal["candidates"]![1]!["end_segment"]) == 575, "The last valid segment must be preserved exactly.");
Check(diagnostics.Any(l => l.Contains("802..850")), "Rejected candidate diagnostics must be visible in the run log.");
proposal = new JsonObject { ["candidates"] = new JsonArray(Candidate(802, 850)) };
Check(Agents.ValidateAnalysis(proposal, analysisSegments, _ => { })?.Contains("0..575") == true, "All-invalid proposals must request correction with the actual index bounds.");
Check(J.D(proposal["candidates"]![0]!["start_segment"]) == 802, "Never silently reinterpret timestamps or clamp indexes.");
proposal = new JsonObject { ["candidates"] = new JsonArray(Candidate(0.5, 5), Candidate(0, 1), Candidate(0, 20), JsonValue.Create("invalid")) };
Check(Agents.ValidateAnalysis(proposal, analysisSegments, _ => { }) != null, "Fractional indexes, short/long clips and non-object candidates must be rejected.");
var schema = Agents.AnalysisSchema(576);
Check(J.D(schema["properties"]!["candidates"]!["items"]!["properties"]!["start_segment"]!["maximum"]) == 575, "Claude's tool contract must carry the video's actual segment bounds.");
Check(Schemas.Analysis["properties"]!["candidates"]!["items"]!["properties"]!["start_segment"]!["maximum"] == null, "Per-video bounds must not mutate the shared schema.");

var creativeInput = new Agents.EditInput
{
    ClipId = "test_creative", Source = new JsonObject { ["id"] = "src_creative", ["duration"] = 30, ["width"] = 320, ["height"] = 180 },
    Candidate = new JsonObject { ["start"] = 0, ["end"] = 30 }, Core = (0, 29),
    Words = Enumerable.Range(0, 30).Select(i => new Word("palabra", i, i + .8)).ToList(),
    SourceLanguage = "es", Languages = ["es"], RecentHooks = [], Library = Renderer.Library(),
    Frames = Enumerable.Range(0, 12).Select(i => "frame" + i).ToList(),
};
var creative = (JsonObject)JsonNode.Parse("""
{"start_word":0,"end_word":29,"layout":"gameplay_blur_bg","hook":{"text":{"es":"Mira el giro"},"duration":2},
 "commentary":{"mode":"onscreen","notes":[{"at_word":4,"duration":4,"text":{"es":"El cambio de dirección crea espacio antes de que llegue el rival."}},{"at_word":20,"duration":4,"text":{"es":"La pausa permite ver la reacción y entender por qué funcionó."}}]},
 "captions":{"style":"bounce","highlight_keywords":["giro"]},
 "effects":[{"at_word":1,"type":"punch_in","duration":0.7,"intensity":0.6},{"at_word":14,"type":"face_focus","duration":1.5,"frame_index":5,"region":{"x":0,"y":0,"w":0.25,"h":0.5},"reason":"Visible surprised reaction"}],
 "audio":{"sfx":[{"at_word":1,"id":"whoosh_01","gain":0.5},{"at_word":14,"id":"ding_01","gain":0.4}],"music_id":null},
 "texts":[{"at_word":24,"duration":2,"preset":"label","anim_in":"pop","text":{"es":"Fíjate en el giro"}}],
 "editorial":{"original_angle":"Explicar cómo la pausa y el cambio de dirección crean una oportunidad.","pacing":"dynamic","reason":"Subrayar la preparación, la reacción y el desenlace con ritmos distintos."}}
""")!;
Check(CreativeEdits.Validate(creativeInput, creative, 0, 29) == null, "Valid visually grounded creative edit was rejected.");
var creativeEdl = Agents.ToEdl(creativeInput, creative);
Check(J.S(creativeEdl["effects"]![1]!["type"]) == "face_focus" && J.D(creativeEdl["effects"]![1]!["region"]!["w"]) == .25, "Face region must survive conversion to the EDL.");
Check(J.D(creativeEdl["audio"]!["sfx"]![0]!["gain"]) == .5 && J.S(creativeEdl["texts"]![0]!["anim_in"]) == "pop", "Sound gain and animated callout decisions must be executed.");
creative["effects"]![1]!["frame_index"] = 50;
Check(CreativeEdits.Validate(creativeInput, creative, 0, 29) != null, "Reject facial highlights without a real reference frame.");
creative["effects"]![1]!["frame_index"] = 5;
creative["effects"]![1]!["region"]!["x"] = .95;
Check(CreativeEdits.Validate(creativeInput, creative, 0, 29) != null, "Reject out-of-bounds face regions.");
creative["effects"]![1]!["region"]!["x"] = 0;
creative["effects"]![1]!["at_word"] = 28;
Check(CreativeEdits.Validate(creativeInput, creative, 0, 29) != null, "A face reference from a different moment must be rejected.");
creative["effects"]![1]!["at_word"] = 14;
creative["effects"]![1]!["duration"] = 3;
Check(CreativeEdits.Validate(creativeInput, creative, 0, 29) != null, "Fixed face crops must remain brief.");
creative["effects"]![1]!["duration"] = 1.5;
creativeInput.Frames.Clear();
Check(CreativeEdits.Validate(creativeInput, creative, 0, 29) != null, "Never accept a facial highlight without any source images.");
creativeInput.Frames.AddRange(Enumerable.Range(0, 12).Select(i => "frame" + i));
creative["audio"]!["sfx"] = new JsonArray();
Check(CreativeEdits.Validate(creativeInput, creative, 0, 29) != null, "Dynamic editing must not silently become another mute crop.");
creative["editorial"]!["pacing"] = "calm";
Check(CreativeEdits.Validate(creativeInput, creative, 0, 29) == null, "An explained calm edit may omit sound accents.");
Check(Schemas.Editing["properties"]!["texts"] == null, "Desktop creative schema must not change the Docker contract.");
var frameDecision = (JsonObject)JsonNode.Parse("""{"framing":{"mode":"subject","reason":"Main window","frame_index":0,"subject_region":{"x":0.1,"y":0.1,"w":0.8,"h":0.8}}}""")!;
var framedEdl = new JsonObject();
Framing.Apply(creativeInput, frameDecision, framedEdl);
Check(J.S(framedEdl["layout"]) == "gameplay_blur_bg", "Automatic framing must preserve context that cannot fit vertically.");
frameDecision["framing"]!["subject_region"] = JsonNode.Parse("""{"x":0.55,"y":0.2,"w":0.18,"h":0.3}""");
Framing.Apply(creativeInput, frameDecision, framedEdl);
Check(J.S(framedEdl["layout"]) == "center_crop" && J.D(framedEdl["crop_x"]) > .6, "Primary framing must center the actual subject, not blindly the source center.");
double viewportWidth = (1080.0 / 1920) / (320.0 / 180), viewportLeft = J.D(framedEdl["crop_x"]) * (1 - viewportWidth);
Check(viewportLeft <= .55 && viewportLeft + viewportWidth >= .73, "The proposed subject must remain fully inside the main crop.");
Check(Framing.SubjectFitsSafe(framedEdl, creativeInput.Source), "Automatic vertical framing must put the subject inside the platform work area.");
framedEdl["effects"] = new JsonArray(new JsonObject { ["type"] = "zoom_slow", ["intensity"] = 2 });
frameDecision["framing"]!["subject_region"] = JsonNode.Parse("""{"x":0.55,"y":0.17,"w":0.18,"h":0.86}""");
Framing.Apply(creativeInput, frameDecision, framedEdl);
Check(J.S(framedEdl["layout"]) == "gameplay_blur_bg", "A subject that enters the dead zones during a zoom must use protected full-frame fit.");
var pinnedInput=new Agents.EditInput{ClipId=creativeInput.ClipId,Source=creativeInput.Source,Candidate=creativeInput.Candidate,
    Words=creativeInput.Words,Core=creativeInput.Core,SourceLanguage="es",Languages=["es"],RecentHooks=[],Library=creativeInput.Library,LayoutChoice="center_crop"};
var unverifiedVertical=new JsonObject{["layout"]="center_crop"};
Framing.Apply(pinnedInput,new JsonObject(),unverifiedVertical);
Check(J.S(unverifiedVertical["layout"])=="center_crop","Explicit vertical crop must survive missing subject detection.");
Framing.Apply(pinnedInput,frameDecision,unverifiedVertical);
Check(J.S(unverifiedVertical["layout"])=="center_crop","Explicit vertical crop must survive an oversized subject.");
Check(SafeArea.Top==SafeArea.BottomMargin && SafeArea.Bottom>1500,"Default safe area must be centered and preserve lower canvas content.");
var oldSafe=SafeArea.Clean(JsonNode.Parse("""{"profile":"shorts_reels_centered_v2","top":300,"bottom":680}"""));
Check(J.D(oldSafe["top"])==240 && J.D(oldSafe["bottom"])==240,"Legacy locked margins must migrate to the centered profile.");

foreach (var size in new[] {(1920,1080),(1080,1920),(1080,3840),(4096,720)})
{
    var geometry = Ass.Geom("gameplay_blur_bg", null, size.Item1, size.Item2);
    Check(geometry.FgX>=0 && geometry.FgX+geometry.FgW<=1080 && geometry.FgY>=0 && geometry.FgY+geometry.FgH<=1920 && (geometry.FgW==1080 || geometry.FgH==1920),
        "Complete fit must use the largest proportional image on the full canvas, without a safe-area thumbnail.");
}
var wideFit=Ass.Geom("gameplay_blur_bg",null,1920,1080);
Check(wideFit.FgW==1080 && wideFit.FgH==608 && wideFit.FgX==0 && wideFit.FgY==656,"16:9 footage must fill the width, remain centered and keep its aspect ratio.");
Check(Ass.Geom("center_crop", .99, 1920, 1080).CaptionY <= SafeArea.Bottom-140, "User caption positions must not enter bottom platform controls.");
foreach (var animation in new[] {"none","pop","zoom","slide_up","slide_down","slide_left","slide_right"})
    foreach (var angle in new[] {-45,0,45})
    {
        var tx = JsonNode.Parse("""{"size":200,"font":"anton","x":1,"y":1,"stroke_w":8,"glow":3,"text":{"es":"OBSERVA EL DETALLE IMPORTANTE"}}""")!;
        tx["anim_in"]=animation; tx["rotate"]=angle;
        var p=SafeArea.PlaceText(tx,"es");
        Check(Math.Min(p.X,p.StartX)-p.HalfWidth>=SafeArea.Left && Math.Max(p.X,p.StartX)+p.HalfWidth<=SafeArea.Right &&
              Math.Min(p.Y,p.StartY)-p.HalfHeight>=SafeArea.Top && Math.Max(p.Y,p.StartY)+p.HalfHeight<=SafeArea.Bottom,
            "Text's complete rotated/animated envelope must fit the safe rectangle.");
    }
foreach (var id in new[] { "record_scratch_cc0", "rimshot_cc0", "airhorn_cc0" })
{
    var sound = Renderer.Library()[id];
    Check(J.S(sound["license"]) == "CC0-1.0" && J.S(sound["source_url"]).StartsWith("https://freesound.org/people/"), $"Missing verified source and license for {id}.");
    var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(Paths.Sfx, J.S(sound["file"]))))).ToLowerInvariant();
    Check(hash == J.S(sound["file_sha256"]), $"Sound file does not match its provenance record: {id}.");
}

// Simulate an older desktop library: it has no jobs collection yet.
File.WriteAllText(Path.Combine(root, "library.json"), "{\"sources\":[],\"clips\":[]}");
var store = new Store();
Check(store.List("jobs").Count == 0, "Old libraries must migrate without losing data.");
var transcript = Path.Combine(root, "transcript.json");
File.WriteAllText(transcript, """
{"language":"es","duration":30,"segments":[{"id":0,"start":0,"end":30,"text":"Contenido de prueba","words":[{"w":"Contenido","start":0,"end":1}]}]}
""");
store.Add("sources", new JsonObject { ["id"] = "src_test", ["path"] = Path.Combine(root, "missing.mp4"), ["duration"] = 30,
    ["width"] = 1920, ["height"] = 1080, ["rights_status"] = "own", ["evidence"] = "Prueba sintética", ["language"] = "en" });
JsonObject Job(string id, bool analysis) => new()
{
    ["id"] = id, ["source_id"] = "src_test", ["status"] = "running", ["created_at"] = Store.Now(), ["steps"] = new JsonArray(),
    ["options"] = new JsonObject { ["review"] = true, ["model"] = "standard", ["layout"] = "auto", ["caption_style"] = "auto", ["languages"] = new JsonArray("es") },
    ["analysis"] = analysis ? new JsonObject { ["transcript_path"] = transcript, ["candidates"] = new JsonArray(new JsonObject {
        ["id"] = "cand00", ["start"] = 0, ["end"] = 30, ["score"] = 90, ["category"] = "test" }) } : null,
};
store.Add("jobs", Job("job_test", true));
store.Add("jobs", Job("job_missing", false));
store.Add("clips", new JsonObject { ["id"] = "test_c00", ["job_id"] = "job_test", ["idx"] = 0,
    ["status"] = "approved", ["package_dir"] = root, ["draft"] = new JsonObject() });

var api = new Api();
await api.StartAsync();
using var http = new HttpClient { BaseAddress = new Uri(api.Url) };
await TestSession.Open(http);
Check((await http.GetAsync("/static/locales.tsv")).IsSuccessStatusCode, "Desktop must serve the shared localization catalog.");
Check((await http.GetStringAsync("/static/i18n.js")).Contains("kairos.uiLanguage"), "Desktop must serve the UI-only language runtime.");
using (var demoTrack = await http.GetAsync("/static/demo-source.vtt"))
{
    Check(demoTrack.IsSuccessStatusCode && demoTrack.Content.Headers.ContentType?.MediaType == "text/vtt",
        "Desktop must serve the guided demo's source captions as WebVTT.");
}
async Task<JsonNode> Get(string url) => (await http.GetFromJsonAsync<JsonNode>(url))!;
async Task<JsonNode> Post(string url, object body, HttpStatusCode expected = HttpStatusCode.OK)
{
    var response = await http.PostAsJsonAsync(url, body);
    Check(response.StatusCode == expected, $"{url}: expected {expected}, got {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    return (await response.Content.ReadFromJsonAsync<JsonNode>())!;
}
async Task<JsonNode> Wait(string runId)
{
    for (int i = 0; i < 200; i++)
    {
        var r = await Get("/api/runs/" + runId);
        if (J.S(r["status"]) is "done" or "failed") return r;
        await Task.Delay(25);
    }
    throw new Exception("Run did not finish.");
}
try
{
    var config = await Get("/api/config");
    Check(J.B(config["ai"]) && J.Items(config["tiers"]).Count() == 3, "AI features must be enabled with three model tiers.");
    Check(!J.B(config["claude_review"]), "The UI must disclose manual review.");
    Check(!J.B(config["setup_completed"]), "First launch must request initial setup.");
    var invalidFolder = await http.PutAsJsonAsync("/api/setup", new { output_dir = "relative/path" });
    Check(invalidFolder.StatusCode == HttpStatusCode.BadRequest, "Output directory must be an absolute path.");
    var outputDir = Path.Combine(root, "chosen-output");
    Check((await http.PutAsJsonAsync("/api/setup", new { output_dir = outputDir, transcription_mode = "accurate" })).IsSuccessStatusCode, "Output preferences could not be saved.");
    Check(!J.B((await Get("/api/config"))["setup_completed"]), "Saving a folder must not skip the API key step.");
    Check((await http.PutAsJsonAsync("/api/setup", new { completed = true })).IsSuccessStatusCode, "Skipping setup must work without an API key.");
    config = await Get("/api/config");
    Check(J.B(config["setup_completed"]) && J.S(config["output_dir"]) == outputDir && J.S(config["transcription_mode"]) == "accurate", "Setup preferences were not persisted.");
    Check((await http.PutAsJsonAsync("/api/setup", new { transcription_mode = "invalid" })).StatusCode == HttpStatusCode.BadRequest, "Unknown transcription mode must be rejected.");
    var jobs = await Get("/api/jobs");
    Check(J.Items(jobs).All(j => J.S(j["status"]) == "interrupted"), "Restart must mark unfinished jobs interrupted.");
    Check(J.D(J.Items(jobs).Single(j => J.S(j["id"]) == "job_test")["clip_counts"]?["approved"]) == 1, "Job clip counts missing.");
    var candidates = await Get("/api/jobs/job_test/candidates");
    Check(J.S(candidates["spoken"]?["detected"]) == "es" && J.S(candidates["spoken"]?["declared"]) == "en", "Spoken language and declaration must remain distinct.");
    Check(J.S(candidates["candidates"]?[0]?["text"]) == "Contenido de prueba", "Candidate transcript missing.");
    Check(J.S(candidates["candidates"]?[0]?["clip_id"]) == "test_c00", "Existing clips must be associated by candidate index.");
    Check((await http.GetAsync("/api/jobs/job_missing/candidates")).StatusCode == HttpStatusCode.Conflict, "Unfinished analysis should return 409.");
    Check((await http.GetAsync("/api/jobs/unknown")).StatusCode == HttpStatusCode.NotFound, "Unknown job should return 404.");
    await Post("/api/jobs", new { source_id = "src_test" }, HttpStatusCode.BadRequest);
    await Post("/api/clips/test_c00/creative-version", new { }, HttpStatusCode.BadRequest);
    Check(J.S((await Get("/api/clips/test_c00"))["status"]) == "approved", "An unavailable creative edit must not change the existing approved clip.");
    await Post("/api/jobs/job_test/generate", new { candidates = new[] { "unknown" } }, HttpStatusCode.BadRequest);
    await Post("/api/jobs/job_test/generate", new { candidates = new[] { "cand00" }, model = "invalid" }, HttpStatusCode.BadRequest);
    await Post("/api/jobs/job_test/generate", new { candidates = new[] { "cand00" }, languages = new[] { "../../en" } }, HttpStatusCode.BadRequest);
    // A real generation run must skip already packaged clips, preserving approval and avoiding any API call.
    var run = await Post("/api/jobs/job_test/generate", new { candidates = new[] { "cand00", "cand00" }, model = "fast", languages = new[] { "es" } });
    var finished = await Wait(J.S(run["run_id"]));
    Check(J.S(finished["status"]) == "done" && J.S(finished["job_id"]) == "job_test", "Generation run must finish and retain its job association.");
    Check(J.S((await Get("/api/clips/test_c00"))["status"]) == "approved", "Existing approval must survive generation.");
    var detail = await Get("/api/jobs/job_test");
    Check(J.B(detail["clips"]![0]!["needs_safe_render"]), "Legacy movies must be marked as needing a safe-area rerender.");
    Check(J.Items(detail["job"]?["options"]?["selection"]).Count() == 1, "Selections must be persisted and deduplicated.");
    Check(detail["clips"]?[0]?["draft"] == null && detail["run"] != null, "Job detail must expose public clips and run information.");
    run = await Post("/api/jobs/job_test/resume", new { });
    Check(J.S((await Wait(J.S(run["run_id"])))["status"]) == "done", "Generation must resume from saved options.");
    Check(J.S((await Get("/api/jobs/job_test"))["run"]?["id"]) == J.S(run["run_id"]), "Job detail must show the latest run even when runs start in the same second.");
    run = await Post("/api/jobs/job_missing/resume", new { });
    Check(J.S((await Wait(J.S(run["run_id"])))["status"]) == "failed", "Missing source must fail visibly.");
    Check(J.S((await Get("/api/jobs/job_missing"))["job"]?["status"]) == "failed", "Failed jobs must never remain running.");

    // Generation setup errors (for example, a deleted transcript) must also clear the running status.
    store = new Store();
    var broken = Job("job_broken", true);
    broken["analysis"]!["transcript_path"] = Path.Combine(root, "missing.json");
    // This fixture is added through a fresh server to model persisted state after restarting.
    await api.StopAsync();
    store.Add("jobs", broken);
    api = new Api();
    await api.StartAsync();
    using var restarted = new HttpClient { BaseAddress = new Uri(api.Url) };
await TestSession.Open(restarted);
    var result = await restarted.PostAsJsonAsync("/api/jobs/job_broken/generate", new { candidates = new[] { "cand00" } });
    var brokenRun = (await result.Content.ReadFromJsonAsync<JsonNode>())!;
    for (int i = 0; i < 200; i++)
    {
        var r = (await restarted.GetFromJsonAsync<JsonNode>("/api/runs/" + J.S(brokenRun["run_id"])))!;
        if (J.S(r["status"]) == "failed") break;
        await Task.Delay(25);
    }
    var brokenState = (await restarted.GetFromJsonAsync<JsonNode>("/api/jobs/job_broken"))!;
    Check(J.S(brokenState["job"]?["status"]) == "failed", "Unreadable transcripts must leave a resumable failed job.");

    // Export all the deterministic artifacts and keep the manifest in sync with the human decision.
    store = new Store();
    store.Update("clips", "test_c00", ("edl", new JsonObject { ["languages"] = new JsonArray("es") }),
        ("metadata", new JsonObject { ["es"] = new JsonObject { ["youtube"] = new JsonObject { ["title"] = "Prueba" } } }));
    Directory.CreateDirectory(Path.Combine(root, "es"));
    File.WriteAllText(Path.Combine(root, "es", "clip.srt"), "1\n00:00:00,000 --> 00:00:01,000\nContenido\n");
    new EditorSvc(store).WritePackage("test_c00");
    Check(!J.B(JsonNode.Parse(File.ReadAllText(Path.Combine(root,"safe_area.json")))?["enforced"]), "Updating a legacy package's metadata must not falsely certify its old video as protected.");
    Check(new[] { "edl.json", "review.json", "package.json", "es/metadata.json", "es/captions.srt" }.All(f => File.Exists(Path.Combine(root, f))), "Export package is incomplete.");
    await api.StopAsync();
    api = new Api();
    await api.StartAsync();
    using var exportHttp = new HttpClient { BaseAddress = new Uri(api.Url) };
await TestSession.Open(exportHttp);
    Check((await exportHttp.PostAsJsonAsync("/api/clips/test_c00/reject", new { by = "test", note = "Revisado" })).IsSuccessStatusCode, "Human decision failed.");
    var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "package.json")))!;
    Check(J.S(manifest["status"]) == "rejected_by_human" && J.S(manifest["decided_by"]) == "test", "Export manifest must track human decisions.");

    await api.StopAsync();
    // Regression for 1080x0: repair legacy zero dimensions from a real video, then render zoompan.
    store = new Store();
    Check(store.Settings.SetupCompleted && store.OutputRoot == outputDir, "Setup must survive an application restart.");
    var sample = Path.Combine(root, "render-test.mp4");
    Ff.Run(["-f", "lavfi", "-i", "color=c=blue:s=320x180:r=30:d=3", "-vf", "drawbox=x=0:y=0:w=80:h=90:color=red:t=fill,drawbox=x=0:y=0:w=80:h=3:color=green:t=fill,drawbox=x=0:y=87:w=80:h=3:color=yellow:t=fill", "-c:v", "libx264", sample]);
    store.Add("sources", new JsonObject { ["id"] = "src_render", ["path"] = sample, ["duration"] = 3, ["width"] = 0, ["height"] = 0,
        ["has_audio"] = false, ["language"] = "es", ["rights_status"] = "own", ["evidence"] = "Synthetic test" });
    var editor = new EditorSvc(store);
    var clipId = editor.CreateClip("src_render", 0, 3);
    var row = store.Get("clips", clipId)!;
    var edl = (JsonObject)row["edl"]!;
    edl["layout"] = "gameplay_blur_bg";
    edl["effects"] = new JsonArray(new JsonObject { ["type"] = "punch_in", ["t"] = 0.5, ["duration"] = 1, ["intensity"] = 1 });
    store.Update("clips", clipId, ("status", "error"), ("draft", new JsonObject { ["edl"] = J.Clone(edl), ["tracks"] = J.Clone(row["tracks"]!) }));
    var loaded = editor.Load(clipId);
    Check(J.D(loaded.src["width"]) == 320 && J.D(loaded.src["height"]) == 180, "Legacy source dimensions must be repaired automatically.");
    var positioned = J.Obj(J.Clone(loaded.clip["draft"]?["edl"] ?? edl));
    positioned["hook"] = new JsonObject { ["text"] = new JsonObject { ["es"] = "MOVE ME" }, ["duration"] = 2, ["x"] = .5, ["y"] = .5 };
    var sanitizedPosition = editor.Sanitize(loaded.clip, loaded.src, new JsonObject { ["edl"] = positioned, ["tracks"] = new JsonObject() });
    Check(J.D(sanitizedPosition.edl["hook"]?["y"]) == .5, "Hook position must survive draft sanitization.");
    Check(Ass.Build(sanitizedPosition.edl, null,"es",320,180).Contains("\\pos(540,960)"), "Render must use the saved hook position.");
    Check(SafeArea.Left == SafeArea.RightMargin && SafeArea.CenterX == 540, "Safe-area horizontal margins and center must be symmetric.");
    var settings = store.Settings;
    settings.Encoder = "libx264"; settings.GpuDecode = false; settings.Quality = 28;
    store.Settings = settings;
    var rendered = editor.RenderDraft(clipId, _ => { });
    var renderedPath = Path.Combine(J.S(rendered["dir"]), "es", "clip.mp4");
    var videoInfo = Ff.Probe(renderedPath);
    Check(J.D(videoInfo["width"]) == 1080 && J.D(videoInfo["height"]) == 1920, "Zoompan must produce a real 1080×1920 video.");
    byte[]? edgePixel=null;
    Ff.Exec("ffmpeg",["-v","error","-ss","0.1","-i",renderedPath,"-frames:v","1","-vf","format=rgb24,crop=1:1:20:720","-f","rawvideo","-pix_fmt","rgb24","-"],true,bytes=>edgePixel=bytes);
    Check(edgePixel is {Length:>=3} && edgePixel[0]>180 && edgePixel[2]<80,"The main source must reach the output's left edge at full width, not leave a blurred side gutter.");
    Check(J.S(rendered["dir"]).StartsWith(outputDir + Path.DirectorySeparatorChar), "New renders must use the chosen output folder.");
    Check(store.Get("clips", clipId)!["draft"] == null, "Successful retry must clear the rendered draft.");
    Check(!J.B(editor.State(clipId)["requires_safe_render"]), "A newly rendered package must carry the enforced safe-area profile.");
    var oldFit=store.Get("clips",clipId)!;
    oldFit["edl"]!.AsObject().Remove("fit_profile");
    Check(Framing.NeedsRender(oldFit),"Clips rendered with the old thumbnail-sized fit must be marked for rerender even if their safe-area profile was current.");
    Check(Ass.Geom("gameplay_blur_bg", null, 1000000, 1).FgH >= 2, "Very wide sources must still have a nonzero even foreground height.");

    var retry = editor.State(clipId);
    var edit = (JsonObject)retry["edl"]!;
    edit["effects"] = new JsonArray(new JsonObject { ["type"] = "face_focus", ["t"] = .5, ["duration"] = 1.5,
        ["region"] = new JsonObject { ["x"] = 0, ["y"] = 0, ["w"] = .25, ["h"] = .5 }, ["frame_index"] = 0, ["reason"] = "Test region" });
    edit["audio"]!["sfx"] = new JsonArray(new JsonObject { ["id"] = "record_scratch_cc0", ["t"] = .5, ["gain"] = .5 });
    var label = (JsonObject)J.Clone(J.Items(Studio.Catalog["text_presets"]).First(t => J.S(t["id"]) == "label"));
    label["t"] = 2.1; label["duration"] = .7; label["x"] = .5; label["y"] = .63;
    label["text"] = new JsonObject { ["es"] = "Mira la reacción" }; label["anim_in"] = "pop";
    edit["texts"] = new JsonArray(label);
    edit["editorial"] = new JsonObject { ["original_angle"] = "Stored intention" };
    store.Update("clips", clipId, ("edl", edit));
    editor.SaveDraft(clipId, new JsonObject { ["edl"] = J.Clone(edit), ["tracks"] = J.Clone(retry["tracks"]!) });
    Check(editor.State(clipId)["edl"]!["effects"]![0]!["region"] != null, "Saving an editor draft must preserve face coordinates.");
    editor.RenderDraft(clipId, _ => { });
    var placement = Framing.Face(edit["effects"]![0]!, loaded.src);
    Check(Math.Abs((double)placement.InnerWidth / placement.InnerHeight - 80.0 / 90) < .01, "Face inset must preserve its source aspect ratio.");
    Check(Ass.Geom(edit, 320, 180).TopY - 90 >= placement.Y + placement.Height, "Hook and commentary must clear the face inset.");
    byte[]? PixelAt(string seconds, int x, int y)
    {
        byte[]? data = null;
        Ff.Exec("ffmpeg", ["-v", "error", "-ss", seconds, "-i", renderedPath, "-frames:v", "1", "-vf", $"format=rgb24,crop=1:1:{x}:{y}", "-f", "rawvideo", "-pix_fmt", "rgb24", "-"], true, bytes => data = bytes);
        return data;
    }
    var pixel = PixelAt("1.2", placement.X + placement.Width / 2, placement.Y + placement.Height / 2);
    Check(pixel is { Length: >= 3 } && pixel[0] > 180 && pixel[2] < 80, "The rendered inset must show the selected RED source region, not the blue frame center.");
    pixel = PixelAt("0.1", placement.X + placement.Width / 2, placement.Y + placement.Height / 2);
    Check(pixel is { Length: >= 3 } && pixel[2] > 100 && pixel[0] < 80, "Face inset must not appear outside its scheduled time.");
    pixel = PixelAt("1.2", placement.X + placement.Width / 2, placement.Y + 8 + (int)(placement.InnerHeight * .01));
    Check(pixel is { Length: >= 3 } && pixel[1] > 85 && pixel[0] < 90, "Top edge/headroom of the selected region must not be center-cropped away.");
    pixel = PixelAt("1.2", placement.X + placement.Width / 2, placement.Y + 8 + (int)(placement.InnerHeight * .99));
    Check(pixel is { Length: >= 3 } && pixel[0] > 150 && pixel[1] > 150 && pixel[2] < 90, "Bottom edge of the face region must be retained.");
    var soundLicenses = JsonNode.Parse(File.ReadAllText(Path.Combine(J.S(rendered["dir"]), "sound_licenses.json")))!;
    Check(J.Items(soundLicenses).Any(s => J.S(s["id"]) == "record_scratch_cc0" && J.S(s["license"]) == "CC0-1.0"), "The export must include license evidence for used external sound effects.");
    Check(Ass.Build(edit, null, "es", 320, 180).Contains("\\t("), "Animated callouts must reach the ASS renderer.");
    var unsafeEdit=(JsonObject)J.Clone(store.Get("clips",clipId)!["edl"]!);
    unsafeEdit["layout"]="center_crop";
    unsafeEdit["framing"]=JsonNode.Parse("""{"subject_region":{"x":0,"y":0,"w":0.25,"h":0.5}}""");
    store.Update("clips",clipId,("edl",unsafeEdit));
    editor.SaveDraft(clipId,new JsonObject{["edl"]=J.Clone(unsafeEdit),["tracks"]=J.Clone(retry["tracks"]!)});
    bool rejectedUnsafe=false;
    try {editor.RenderDraft(clipId,_=>{});} catch(EditException){rejectedUnsafe=true;}
    Check(!rejectedUnsafe,"Manual crop must honor the user framing choice even when the subject extends outside the guide.");

    var blackSource = Path.Combine(root,"safe-black.mp4");
    Ff.Run(["-f","lavfi","-i","color=c=black:s=320x180:r=30:d=2","-c:v","libx264",blackSource]);
    var safeSource = Ff.Probe(blackSource); safeSource["path"]=blackSource; safeSource["id"]="safe_black";
    var safetyEdl = EditorSvc.NewEdl("safe_test", safeSource,0,2,"es");
    var safeTexts=new JsonArray();
    foreach(var (animation,x,y) in new[] {("zoom",1,1),("slide_left",1,0),("pop",0,1)})
    {
        var tx=JsonNode.Parse("""{"t":0,"duration":2,"font":"anton","size":180,"rotate":25,"stroke_w":6,"color":"#FFFFFF","stroke":"#000000","text":{"es":"ZONA SEGURA"}}""")!;
        tx["anim_in"]=animation;tx["x"]=x;tx["y"]=y;safeTexts.Add(tx);
    }
    safetyEdl["texts"]=safeTexts;
    var safetyWork=Paths.Dir("safety-render");
    var safetyReport=Renderer.RenderClip(safetyEdl,safeSource,null,"es",safetyWork,new RenderSettings{Encoder="libx264",GpuDecode=false,Quality=20},_=>{});
    foreach(var time in new[]{"0.10","0.18","0.26","0.50"})
    {
        byte[]? rgb=null;
        Ff.Exec("ffmpeg",["-v","error","-ss",time,"-i",safetyReport.Path,"-frames:v","1","-f","rawvideo","-pix_fmt","rgb24","-"],true,bytes=>rgb=bytes);
        Check(rgb?.Length==1080*1920*3,"Safe-area raster test must decode a full output frame.");
        int outsideMax=0,insideMax=0;
        for(int y=0;y<1920;y++)for(int x=0;x<1080;x++)
        {
            int offset=(y*1080+x)*3,value=Math.Max(rgb![offset],Math.Max(rgb[offset+1],rgb[offset+2]));
            if(x<SafeArea.Left || x>=SafeArea.Right || y<SafeArea.Top || y>=SafeArea.Bottom)outsideMax=Math.Max(outsideMax,value);
            else insideMax=Math.Max(insideMax,value);
        }
        Check(outsideMax<60 && insideMax>150,"Actual animated text pixels must stay out of platform dead zones.");
    }
    api = new Api();
    await api.StartAsync();
    await LocaleChecks.Run(api, Check);
    await ProjectChecks.Run(api.Url, sample, Check);
    await EditorFeatureChecks.Run(api.Url, sample, Check);
    await DeletionChecks.Run(api.Url, Check);
    await VideoProjectChecks.Run(api, Check);
    foreach(var device in J.Items(Hw.DeviceList()).Where(d=>J.B(d["available"])))
    {
        var selected=new RenderSettings{Device=J.S(device["id"]),Quality=24,GpuDecode=false};
        var hardwareReport=Renderer.RenderClip(safetyEdl,safeSource,null,"es",Paths.Dir("hardware-render",J.S(device["id"]).Replace(":","-")),selected,Console.WriteLine);
        Check(hardwareReport.Encoder==J.S(device["encoder"]),"A detected GPU must complete the actual filtered render on the selected device.");
    }
    Console.WriteLine($"Passed {checks} desktop integration checks (no API calls).");
}
finally
{
    await api.StopAsync();
    // Preserve the temporary fixture if a check fails so the failure can be inspected.
}
