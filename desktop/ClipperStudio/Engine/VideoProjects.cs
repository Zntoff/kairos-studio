using System.Text.Json.Nodes;

namespace ClipperStudio.Engine;

/// <summary>Standalone video compositions inside a workspace project. No source clip or AI generation is required.</summary>
public class VideoProjects(Store store)
{
    readonly object gate = new();
    public static JsonObject Settings(JsonNode? raw)
    {
        string ratio = J.S(raw?["ratio"], "16:9"), format = J.S(raw?["format"], "mp4"), quality = J.S(raw?["quality"], "standard");
        int fps = (int)J.D(raw?["fps"], 30), resolution = (int)J.D(raw?["resolution"], 1080);
        if (!new[] { "16:9", "9:16", "1:1", "4:5", "21:9" }.Contains(ratio) ||
            !new[] { "mp4", "mov" }.Contains(format) || !new[] { "draft", "standard", "high" }.Contains(quality) ||
            !new[] {24,25,30,50,60}.Contains(fps) || !new[] {720,1080,2160}.Contains(resolution))
            throw new EditException("Invalid video settings.");
        var parts = ratio.Split(':').Select(int.Parse).ToArray();
        double scale = (double)resolution / Math.Min(parts[0], parts[1]);
        return new JsonObject { ["ratio"] = ratio, ["format"] = format, ["quality"] = quality, ["fps"] = fps,
            ["resolution"] = resolution, ["width"] = (int)Math.Round(parts[0]*scale/2)*2, ["height"] = (int)Math.Round(parts[1]*scale/2)*2 };
    }
    public JsonObject Get(string id)
    {
        var record=store.Get("video_projects",id)??throw new KeyNotFoundException(id);
        var doc=record["document"]!.AsObject();
        if(doc["tools"]==null)doc["tools"]=VideoStudio.Clean(store,null,J.D(doc["duration"]),record["settings"]);
        if(doc["chat"]==null)doc["chat"]=new JsonArray();
        return record;
    }
    public JsonObject Create(string project, JsonNode raw)
    {
        if (store.Get("projects",project)==null) throw new KeyNotFoundException(project);
        string name=J.S(raw["name"]).Trim();
        if(name.Length is <1 or >100)throw new EditException("Use a video name of 1–100 characters.");
        var record=new JsonObject { ["id"]="video_"+Guid.NewGuid().ToString("N"), ["project_id"]=project,["name"]=name,
            ["settings"]=Settings(raw["settings"]),["revision"]=0,["created_at"]=Store.Now(),["updated_at"]=Store.Now(),
            ["document"]=new JsonObject{["duration"]=60,["items"]=new JsonArray()} };
        store.Add("video_projects",record);return record;
    }
    public JsonObject Asset(string project,string id)
    {
        var asset=store.Get("resources",id) ?? store.Get("sources",id) ?? throw new EditException("Media asset no longer exists.");
        if(J.S(asset["project_id"],Store.DefaultProject)!=project)throw new EditException("Media asset belongs to another project.");
        bool source=store.Get("sources",id)!=null;
        string kind=source?"video":J.S(asset["kind"]);
        if(kind is not ("image" or "audio" or "video"))throw new EditException("Unsupported media asset.");
        var result=(JsonObject)J.Clone(asset);
        string path=source?J.S(asset["path"]):EditorMedia.PathFor(asset);
        if(!File.Exists(path))throw new EditException("Media asset file is missing.");
        result["path"]=path;result["kind"]=kind;
        result["url"]=source?$"/api/sources/{id}/video":$"/api/projects/{project}/resources/{id}/file";
        result["name"]=source?Path.GetFileName(path):J.S(asset["name"]);
        if(kind!="image" && J.D(result["duration"])<=0)
        {
            var (code,json,_)=Ff.Exec("ffprobe",["-v","error","-show_format","-show_streams","-of","json",path]);
            if(code!=0)throw new EditException("Could not inspect media asset.");
            var probe=JsonNode.Parse(json)!;result["duration"]=J.D(probe["format"]?["duration"]);
            result["has_audio"]=J.Items(probe["streams"]).Any(s=>J.S(s["codec_type"])=="audio");
        }
        return result;
    }
    public JsonArray Assets(string project)
    {
        var result=new JsonArray();
        foreach(var row in store.List("resources",project).Concat(store.List("sources",project)))
            try { var asset=Asset(project,J.S(row["id"]));asset.Remove("path");result.Add(asset); } catch(EditException) { }
        return result;
    }
    static double Number(JsonNode row,string key,double fallback,double min,double max)
    {
        double value=J.D(row[key],fallback);
        if(value<min || value>max)throw new EditException($"Invalid {key}: {value}.");
        return Math.Round(value,4);
    }
    public JsonObject Clean(string project,JsonNode raw,JsonNode? settings=null)
    {
        if(raw is not JsonObject)throw new EditException("Invalid video document.");
        double duration=Number(raw,"duration",60,.1,86400);
        if(raw["items"] is not JsonArray input || input.Count>200)throw new EditException("Use at most 200 media items.");
        var items=new JsonArray();var ids=new HashSet<string>();
        foreach(var row in input)
        {
            if(row is not JsonObject)throw new EditException("Invalid media item.");
            string id=J.S(row["id"]),assetId=J.S(row["asset_id"]);
            if(!System.Text.RegularExpressions.Regex.IsMatch(id,"^[A-Za-z0-9_-]{1,80}$") || !ids.Add(id))throw new EditException("Media item IDs must be unique and valid.");
            var asset=Asset(project,assetId);
            double start=Number(row,"start",0,0,duration-.05),offset=Number(row,"offset",0,0,86400);
            double length=Number(row,"duration",5,.05,duration-start);
            if(J.S(asset["kind"])!="image" && offset+length>J.D(asset["duration"])+.02)throw new EditException("Trim exceeds the media asset duration.");
            string fit=J.S(row["fit"],"contain");if(fit is not ("contain" or "cover" or "blur"))throw new EditException("Invalid crop mode.");
            items.Add(new JsonObject{["id"]=id,["asset_id"]=assetId,["start"]=start,["offset"]=offset,["duration"]=length,
                ["gain"]=Number(row,"gain",1,0,3),["fit"]=fit,["crop_x"]=Number(row,"crop_x",.5,0,1),["crop_y"]=Number(row,"crop_y",.5,0,1)});
        }
        var chat=new JsonArray();
        foreach(var message in J.Items(raw["chat"]).TakeLast(40))
        {
            string role=J.S(message["role"]),content=J.S(message["content"]);
            if(role is not ("user" or "assistant") || content.Length>3000)throw new EditException("Invalid chat message.");
            chat.Add(new JsonObject{["role"]=role,["content"]=content,["usage"]=message["usage"] is {} usage?J.Clone(usage):null});
        }
        return new JsonObject{["duration"]=duration,["items"]=items,["tools"]=VideoStudio.Clean(store,raw["tools"],duration,settings),["chat"]=chat};
    }
    public JsonObject Save(string id,JsonNode raw)
    {
        lock(gate)
        {
            var record=Get(id);
            if(J.D(raw["revision"],-1)!=J.D(record["revision"]))throw new EditException("This video changed in another window. Reload before saving.");
            var document=Clean(J.S(record["project_id"]),raw["document"] ?? new JsonObject(),record["settings"]);
            store.Update("video_projects",id,("document",document),("revision",JsonValue.Create((int)J.D(record["revision"])+1)),("updated_at",JsonValue.Create(Store.Now())));
            return Get(id);
        }
    }
    public string Render(JsonObject record,Action<string> log)
    {
        string project=J.S(record["project_id"]),id=J.S(record["id"]);
        var document=Clean(project,record["document"]!,record["settings"]);var settings=Settings(record["settings"]);
        var items=J.Items(document["items"]).ToList();
        if(!VideoStudio.HasContent(document))throw new EditException("Add media before exporting the video.");
        int w=(int)J.D(settings["width"]),h=(int)J.D(settings["height"]),fps=(int)J.D(settings["fps"]);
        double duration=J.D(document["duration"]);
        string N(double n)=>J.F(n,"0.####");
        // Persisted times have four decimal places. Treat their rounding as
        // part of the same frame when mapping adjacent cuts onto the output grid.
        long FrameAt(double seconds)=>(long)Math.Ceiling((seconds-.00011)*fps);
        List<string> inputs=["-f","lavfi","-i",$"color=c=black:s={w}x{h}:r={fps}:d={N(duration)}",
            "-f","lavfi","-i",$"anullsrc=r=48000:cl=stereo:d={N(duration)}"];
        List<string> graph=["[0:v]setpts=PTS-STARTPTS[base]","[1:a]anull[silence]"];
        var sound=new List<string>{"[silence]"};string picture="base";
        for(int i=0;i<items.Count;i++)
        {
            var item=items[i];var asset=Asset(project,J.S(item["asset_id"]));string kind=J.S(asset["kind"]);
            int index=i+2;double start=J.D(item["start"]),length=J.D(item["duration"]);
            if(kind=="image")inputs.AddRange(["-loop","1"]);else inputs.AddRange(["-ss",N(J.D(item["offset"]))]);
            inputs.AddRange(["-t",N(length),"-i",J.S(asset["path"])]);
            if(kind is "video" or "image")
            {
                long firstFrame=FrameAt(start),endFrame=FrameAt(start+length);
                string placement=$"settb=1/{fps},setpts=N+{firstFrame}";
                string scale=J.S(item["fit"])=="cover" ? $"scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h}:(iw-{w})*{N(J.D(item["crop_x"],.5))}:(ih-{h})*{N(J.D(item["crop_y"],.5))}" :
                    $"scale={w}:{h}:force_original_aspect_ratio=decrease,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2";
                if(J.S(item["fit"])=="blur")
                {
                    graph.Add($"[{index}:v]fps={fps},split=2[fitBgSrc{i}][fitFgSrc{i}]");
                    graph.Add($"[fitBgSrc{i}]scale={w/4}:{h/4}:force_original_aspect_ratio=increase,crop={w/4}:{h/4},gblur=sigma=9,scale={w}:{h},eq=brightness=-0.16:saturation=0.85[fitBg{i}]");
                    graph.Add($"[fitFgSrc{i}]scale={w}:{h}:force_original_aspect_ratio=decrease[fitFg{i}]");
                    graph.Add($"[fitBg{i}][fitFg{i}]overlay=(W-w)/2:(H-h)/2,setsar=1,format=yuv420p,{placement}[item{i}]");
                }
                else graph.Add($"[{index}:v]fps={fps},{scale},setsar=1,format=yuv420p,{placement}[item{i}]");
                graph.Add($"[{picture}][item{i}]overlay=eof_action=repeat:enable='gte(round(t*{fps}),{firstFrame})*lt(round(t*{fps}),{endFrame})'[layer{i}]");picture=$"layer{i}";
            }
            if(kind=="audio" || kind=="video" && J.B(asset["has_audio"]))
            {
                graph.Add($"[{index}:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,asetpts=PTS-STARTPTS,volume={N(J.D(item["gain"],1))},adelay={(int)Math.Round(start*1000)}:all=1[a{i}]");sound.Add($"[a{i}]");
            }
        }
        graph.Add($"{string.Concat(sound)}amix=inputs={sound.Count}:duration=first:normalize=0[compositionAudio]");
        var tools=document["tools"]!;
        var mix=Renderer.MixGraph(tools["edl"]!,true,inputs.Count(x=>x=="-i"),"[compositionAudio]");
        inputs.AddRange(mix.inputs);graph.Add(mix.graph);
        // Dynamic normalization can produce non-finite samples for a short, silent composition.
        string normalize=sound.Count>1||mix.inputs.Count>0
            ? $"loudnorm=I={N(J.D(tools["edl"]?["audio"]?["normalize_lufs"],-14))}:TP=-1:LRA=11," : "";
        graph.Add($"{mix.label}{normalize}aresample=48000[audio]");
        picture=VideoStudio.Picture(tools["edl"]!,tools["track"]!,settings,picture,inputs,graph,Paths.Dir("projects",project,"videos",id,Guid.NewGuid().ToString("N")));
        string output=Path.Combine(Paths.Dir("projects",project,"videos",id),Guid.NewGuid().ToString("N")+"."+J.S(settings["format"]));
        var renderSettings=store.Settings;renderSettings.Quality=J.S(settings["quality"]) switch{"draft"=>28,"high"=>16,_=>20};
        log($"Export {w}x{h}, {fps} fps, {N(duration)} s");
        try
        {
            var encoders=Hw.Chain(renderSettings.ResolvedEncoder).ToList();
            for(int attempt=0;attempt<encoders.Count;attempt++)
            {
                string encoder=encoders[attempt];
                try
                {
                    Ff.Run(["-loglevel","error",..Hw.DeviceInit(renderSettings.Device,encoder),..inputs,"-filter_complex",string.Join(';',graph),"-map",$"[{picture}]","-map","[audio]","-t",N(duration),
                        ..Hw.EncoderArgs(encoder,renderSettings.Quality),..Hw.DeviceOutput(renderSettings.Device,encoder),"-pix_fmt","yuv420p","-c:a","aac","-b:a","192k","-movflags","+faststart",output]);
                    break;
                }
                catch(FfmpegException) when(attempt+1<encoders.Count){log("Retrying export with "+encoders[attempt+1]);}
            }
            var probe=Ff.Probe(output);
            if(J.D(probe["width"])!=w || J.D(probe["height"])!=h || Math.Abs(J.D(probe["fps"])-fps)>.1)throw new EditException("Export format verification failed.");
            store.Update("video_projects",id,("export_path",JsonValue.Create(output)),("export_revision",J.Clone(record["revision"]!)));
            return output;
        }
        catch {if(File.Exists(output))File.Delete(output);throw;}
    }
}
