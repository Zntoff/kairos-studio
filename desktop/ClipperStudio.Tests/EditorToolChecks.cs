using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ClipperStudio.Ai;
using ClipperStudio.Engine;

static class EditorToolChecks
{
    public static async Task Run(VideoProjects service,JsonObject record,JsonNode baseline,Action<bool,string> check)
    {
        var tools=EditorTools.Definitions();
        check(J.Items(tools).All(t=>J.S(t["name"]).StartsWith("editor_")&&t["annotations"]!=null&&!J.B(t["inputSchema"]?["additionalProperties"])),"All editor tools need distinct names, closed schemas and access annotations.");
        var session=new EditorTools(service,record,baseline);
        check(!J.Items(session.AvailableDefinitions()).Any(t=>J.S(t["name"])=="editor_add_text"),"Specialized schemas must load on demand rather than occupy every request.");
        session.Execute("editor_set_media_volume",JsonNode.Parse("""{"item_id":"sound","gain":0.25}""")!.AsObject());
        check(J.D(session.Document["items"]?[1]?["gain"])==.25&&J.D(baseline["items"]?[1]?["gain"])==.5,"A typed volume call must edit only the private draft and preserve the baseline.");
        void Rejected(string name,string arguments)
        {
            string before=session.Document.ToJsonString();bool rejected=false;
            try{session.Execute(name,JsonNode.Parse(arguments)!.AsObject());}catch(EditException){rejected=true;}
            check(rejected&&session.Document.ToJsonString()==before,"Invalid tool input must fail without changing the draft: "+arguments);
        }
        Rejected("editor_set_media_volume","""{"item_id":"sound","gain":500}""");
        Rejected("editor_set_media_volume","""{"item_id":"sound","gain":"0.5"}""");
        Rejected("editor_set_media_volume","""{"item_id":"sound","gain":0.5,"payload":{}}""");
        Rejected("editor_trim_media","""{"item_id":"sound","source_offset_seconds":1.9,"duration_seconds":1}""");
        Rejected("editor_inspect","""{"section":"timeline","limit":1.5}""");
        session.Execute("editor_discover_tools",new JsonObject{["category"]="media"});
        session.Execute("editor_trim_media",JsonNode.Parse("""{"item_id":"sound","source_offset_seconds":0.2,"duration_seconds":1.5}""")!.AsObject());
        session.Execute("editor_move_media",JsonNode.Parse("""{"item_id":"sound","start_seconds":0.3}""")!.AsObject());
        session.Execute("editor_split_media",JsonNode.Parse("""{"item_id":"sound","at_seconds":0.8}""")!.AsObject());
        check(Math.Abs(J.D(session.Document["items"]?[2]?["offset"])-.7)<.0001,"Absolute split time must map to the correct source-relative offset.");
        var duplicated=session.Execute("editor_duplicate_media",JsonNode.Parse("""{"item_id":"sound","start_seconds":3}""")!.AsObject());
        check(J.D(session.Document["duration"])==3.5&&J.S(duplicated["item_id"])!="sound","Duplication must produce an independent ID and extend the composition.");
        Rejected("editor_set_duration","""{"duration_seconds":1}""");
        session.Execute("editor_discover_tools",new JsonObject{["category"]="text"});
        check(J.Items(session.AvailableDefinitions()).Any(t=>J.S(t["name"])=="editor_add_text"),"Discovery must enable the actual native function schema in the next model request.");
        session.Execute("editor_add_text",JsonNode.Parse("""{"text":"Título literal","start_seconds":0,"duration_seconds":1.2,"animation_in":"pop"}""")!.AsObject());
        session.Execute("editor_update_text",JsonNode.Parse("""{"index":0,"text":"Texto corregido"}""")!.AsObject());
        check(J.S(session.Document["tools"]?["edl"]?["texts"]?[0]?["text"]?["en"])=="Texto corregido"&&J.S(session.Document["tools"]?["edl"]?["texts"]?[0]?["anim_in"])=="pop","Text updates must preserve unspecified formatting.");
        Rejected("editor_update_text","""{"index":0,"color":"red"}""");
        session.Execute("editor_discover_tools",new JsonObject{["category"]="appearance"});
        session.Execute("editor_set_color",JsonNode.Parse("""{"look":"warm","look_amount":0.6}""")!.AsObject());
        check(J.S(session.Document["tools"]?["edl"]?["color"]?["look"])=="warm","Color must execute through the registry rather than a generic arbitrary patch.");
        session.Execute("editor_discover_tools",new JsonObject{["category"]="audio"});
        session.Execute("editor_set_audio",new JsonObject{["voice_gain"]=.7});
        check(J.D(session.Document["tools"]?["edl"]?["audio"]?["voice_gain"])==.7,"Global audio must use its own typed tool, separate from per-item volume.");
        Rejected("editor_set_audio","""{"music_id":"nonexistent"}""");
        session.Execute("editor_discover_tools",new JsonObject{["category"]="effects"});
        session.Execute("editor_add_effect",JsonNode.Parse("""{"effect_id":"punch_in","start_seconds":0.1,"duration_seconds":0.5}""")!.AsObject());
        check(J.Items(session.Document["tools"]?["edl"]?["effects"]).Count()==1,"Effect insertion must use catalog limits and affect the real export document.");
        Rejected("editor_add_effect","""{"effect_id":"punch_in","start_seconds":0,"duration_seconds":30}""");
        var page=session.Execute("editor_inspect",JsonNode.Parse("""{"section":"timeline","offset":1,"limit":1}""")!.AsObject());
        check(J.Items(page["items"]).Count()==1&&J.D(page["total"])==4&&J.D(page["next_offset"])==2,"Inspection must paginate without dropping count/cursor metadata.");
        var assets=session.Execute("editor_find_assets",new JsonObject{["limit"]=1});
        check(J.Items(assets["items"]).Count()==1&&!assets.ToJsonString().Contains("path")&&!assets.ToJsonString().Contains(Paths.Data),"Media discovery must return bounded metadata without exposing local paths.");
        var state=session.InitialState(new JsonObject{["selected_item_id"]="sound"});
        int eagerBytes=Encoding.UTF8.GetByteCount(VideoStudio.Catalog(record["settings"]!).ToJsonString()+baseline.ToJsonString());
        int compactBytes=Encoding.UTF8.GetByteCount(state.ToJsonString()+new EditorTools(service,record,baseline).AvailableDefinitions().ToJsonString());
        check(compactBytes<eagerBytes,"Initial context plus core schemas must be smaller than eager document/catalog loading.");
        Console.WriteLine($"Editor context fixture: {eagerBytes} eager bytes vs {compactBytes} compact bytes (not a token estimate).");

        int port=26000+Random.Shared.Next(1000);var requests=new List<JsonNode>();
        using var listener=new HttpListener();listener.Prefixes.Add($"http://127.0.0.1:{port}/");listener.Start();
        JsonObject Call(string id,string name,string args)=>new(){["id"]=id,["type"]="function",["function"]=new JsonObject{["name"]=name,["arguments"]=args}};
        var rounds=new Queue<JsonArray>();
        rounds.Enqueue(new JsonArray(Call("discover","editor_discover_tools","""{"category":"text"}""")));
        rounds.Enqueue(new JsonArray(Call("bad-text","editor_add_text","""{"text":"Hola","start_seconds":0,"duration_seconds":-1}""")));
        rounds.Enqueue(new JsonArray(Call("inspect","editor_inspect","""{"section":"timeline"}"""),Call("early-finish","editor_finish","""{"summary":"Listo"}""")));
        rounds.Enqueue(new JsonArray(Call("fixed-text","editor_add_text","""{"text":"Hola","start_seconds":0,"duration_seconds":1}"""),Call("finish","editor_finish","""{"summary":"Añadí el título."}""")));
        var server=Task.Run(async()=>
        {
            while(listener.IsListening)
            {
                HttpListenerContext context;try{context=await listener.GetContextAsync();}catch{break;}
                requests.Add(JsonNode.Parse(await new StreamReader(context.Request.InputStream).ReadToEndAsync())!);
                if(context.Request.Url!.AbsolutePath.Contains("/reject/")){context.Response.StatusCode=400;var rejected=Encoding.UTF8.GetBytes("{\"error\":{\"message\":\"tools not supported\"}}");await context.Response.OutputStream.WriteAsync(rejected);context.Response.Close();continue;}
                JsonObject reply;
                if(context.Request.Url!.AbsolutePath.EndsWith("/messages"))reply=JsonNode.Parse("""{"model":"tool-test","stop_reason":"tool_use","usage":{"input_tokens":7,"output_tokens":3},"content":[{"type":"tool_use","id":"anthropic-finish","name":"editor_finish","input":{"summary":"Listo"}}]}""")!.AsObject();
                else reply=new JsonObject{["model"]="tool-test",["usage"]=new JsonObject{["prompt_tokens"]=100,["completion_tokens"]=25},["choices"]=new JsonArray(new JsonObject{["finish_reason"]="tool_calls",["message"]=new JsonObject{["content"]="",["tool_calls"]=rounds.Count>0?rounds.Dequeue():new JsonArray(Call("empty","editor_finish","""{"summary":"Listo"}"""))}})};
                var bytes=Encoding.UTF8.GetBytes(reply.ToJsonString());context.Response.ContentType="application/json";await context.Response.OutputStream.WriteAsync(bytes);context.Response.Close();
            }
        });
        try
        {
            var ai=new AiSettings();ai.SetProvider("custom",$"http://127.0.0.1:{port}/v1",new JsonObject{["fast"]="tool-test",["standard"]="tool-test",["superior"]="tool-test"},null,true);
            var proposed=await VideoAssistant.Propose(service,ai,J.S(record["id"]),new JsonObject{["prompt"]="Pon un título Hola",["tier"]="fast",["document"]=baseline.DeepClone()});
            check(J.D(proposed["usage"]?["attempts"])==4&&J.D(proposed["usage"]?["input_tokens"])==400,"Inspection must not allow finishing before the failed edit is corrected.");
            check(J.Items(proposed["operations"]).Count()==1&&J.S(proposed["document"]?["tools"]?["edl"]?["texts"]?[0]?["text"]?["en"])=="Hola","A rejected text call must not create a duplicate layer when corrected.");
            var trace=J.Items(proposed["tool_calls"]).ToList();
            check(trace.Count(t=>J.B(t["is_error"]))==2&&trace.Any(t=>J.S(t["name"])=="editor_finish"&&J.B(t["is_error"])),"The trace must show both the invalid edit and the rejected finish.");
            check(!requests[0]["tools"]!.ToJsonString().Contains("editor_add_text")&&requests[1]["tools"]!.ToJsonString().Contains("editor_add_text"),"Discovery must change the real function list sent to the provider.");
            check(J.Items(requests[2]["messages"]).Any(m=>J.S(m["role"])=="tool"&&J.S(m["content"]).Contains("must be between")),"Validation errors must return through the native tool-result channel.");
            check(J.D(service.Get(J.S(record["id"]))["document"]?["items"]?[1]?["gain"])==.5,"A multi-turn agent must not mutate the saved project before UI application.");
            var conf=ai.Conf() with {Kind="anthropic",BaseUrl=$"http://127.0.0.1:{port}",NeedsKey=false,Key="test"};
            var turns=new JsonArray(new JsonObject{["role"]="user",["content"]="test"},new JsonObject{["role"]="assistant",["content"]="",["tool_calls"]=new JsonArray(Call("read-1","editor_inspect","""{"section":"audio"}"""))},new JsonObject{["role"]="tool",["tool_call_id"]="read-1",["content"]="{}"});
            var anthropic=await new LlmClient(ai,conf).ToolTurnAsync("fast","test",turns,new EditorTools(service,record,baseline).AvailableDefinitions());
            check(anthropic.Input==7&&J.S(anthropic.Message["tool_calls"]?[0]?["function"]?["name"])=="editor_finish","The Anthropic adapter must preserve native tool-use names and actual usage.");
            var wire=requests.Last();check(J.S(wire["messages"]?[2]?["content"]?[0]?["tool_use_id"])=="read-1"&&wire["tools"]?[0]?["input_schema"]!=null,"Anthropic tool results must match their tool-use IDs and use input_schema.");
            bool unsupported=false;try{await new LlmClient(ai,ai.Conf() with {BaseUrl=$"http://127.0.0.1:{port}/reject/v1"}).ToolTurnAsync("fast","test",new JsonArray(new JsonObject{["role"]="user",["content"]="test"}),session.AvailableDefinitions());}catch(LlmException e){unsupported=e.Message.Contains("compatible");}
            check(unsupported,"Models without tool support must return an actionable error instead of reverting to untyped editing JSON.");
        }
        finally{listener.Stop();await server;}
    }
}
