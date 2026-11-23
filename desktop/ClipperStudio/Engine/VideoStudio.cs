using System.Text.Json.Nodes;

namespace ClipperStudio.Engine;

/// <summary>Uses the Shorts catalog, sanitizer, ASS layout and audio mixer for composition tools.</summary>
public static class VideoStudio
{
    public static int Height(JsonNode? settings)=>Math.Max(400,(int)Math.Round(1080*J.D(settings?["height"],1080)/J.D(settings?["width"],1920)));
    public static JsonObject Catalog(JsonNode settings)
    {
        var fonts=new JsonObject();
        foreach(var f in Studio.Fonts.Values)fonts[f.Id]=new JsonObject{["label"]=f.Label,["family"]=f.Family,["scale"]=Studio.FontScale(f.Path)};
        var catalog=(JsonObject)J.Clone(Studio.Catalog);
        ((JsonArray)catalog["effects"]!).Add(J.Clone(Studio.FaceFocus));
        return new JsonObject{["catalog"]=catalog,["fonts"]=fonts,["height"]=Height(settings),
            ["library"]=new JsonArray(Renderer.Library().Values.Select(J.Clone).ToArray())};
    }
    public static JsonObject Clean(Store store,JsonNode? raw,double duration,JsonNode? settings)
    {
        int height=Height(settings);
        var src=new JsonObject{["id"]="composition",["duration"]=duration,["width"]=1080,["height"]=height};
        var basis=EditorSvc.NewEdl("composition",src,0,duration,"en");
        var edl=(JsonObject)J.Clone(raw?["edl"]??basis);
        edl["source"]=new JsonObject{["start"]=0,["end"]=duration};
        edl["layout"]="center_crop";edl["media"]=new JsonArray();
        var safe=edl["safe_area"] as JsonObject ?? new JsonObject();
        int maxVertical=Math.Max(0,(height-300)/2);
        safe["profile"]=SafeArea.Profile;
        safe["left"]=J.Num(safe["left"],100,0,300);safe["right"]=J.Num(safe["right"],100,0,300);
        safe["top"]=J.Num(safe["top"],Math.Min(maxVertical,height*.125),0,maxVertical);
        safe["bottom"]=J.Num(safe["bottom"],Math.Min(maxVertical,height*.125),0,maxVertical);
        edl["safe_area"]=safe.Parent==edl?safe:J.Clone(safe);
        var clip=new JsonObject{["id"]="composition",["edl"]=basis};
        var tracks=new JsonObject{["en"]=raw?["track"] is {} track?J.Clone(track):new JsonObject{["cues"]=new JsonArray(),["word_timed"]=false}};
        using var canvas=Ass.UseCanvas(1080,height);
        var clean=new EditorSvc(store).Sanitize(clip,src,new JsonObject{["edl"]=edl,["tracks"]=tracks},composition:true);
        // A draft must be exportable; text layout errors are shown before a render is started.
        _=Ass.Build(clean.edl,clean.tracks["en"],"en",1080,height);
        return new JsonObject{["edl"]=clean.edl,["track"]=J.Clone(clean.tracks["en"]!)};
    }
    public static bool HasContent(JsonNode doc)=>J.Items(doc["items"]).Any() ||
        J.Items(doc["tools"]?["edl"]?["texts"]).Any() || J.Items(doc["tools"]?["track"]?["cues"]).Any() ||
        J.S(doc["tools"]?["edl"]?["hook"]?["text"]?["en"])!="" || J.Items(doc["tools"]?["edl"]?["commentary"]?["notes"]).Any();

    public static string Picture(JsonNode edl,JsonNode track,JsonNode settings,string picture,List<string> inputs,List<string> graph,string work)
    {
        int w=(int)J.D(settings["width"]),h=(int)J.D(settings["height"]),fps=(int)J.D(settings["fps"]),logicalHeight=Height(settings);
        string N(double n)=>J.F(n,"0.####");
        using var canvas=Ass.UseCanvas(1080,logicalHeight);
        using var safe=SafeArea.Use(edl);
        var faces=J.Items(edl["effects"]).Where(e=>J.S(e["type"])=="face_focus").ToList();
        if(faces.Count>0)
        {
            graph.Add($"[{picture}]split={faces.Count+1}[studioMain]"+string.Concat(faces.Select((_,i)=>$"[studioFace{i}]")));picture="studioMain";
            for(int i=0;i<faces.Count;i++)
            {
                var e=faces[i];var r=e["region"]!;
                int cw=Math.Clamp((int)(J.D(r["w"])*w)/2*2,2,w/2*2),ch=Math.Clamp((int)(J.D(r["h"])*h)/2*2,2,h/2*2);
                int x=Math.Clamp((int)(J.D(r["x"])*w)/2*2,0,w-cw),y=Math.Clamp((int)(J.D(r["y"])*h)/2*2,0,h-ch);
                var box=Framing.Face(e,new JsonObject{["width"]=w,["height"]=h});double factor=w/1080.0;
                int fw=Math.Max(2,(int)(box.InnerWidth*factor)/2*2),fh=Math.Max(2,(int)(box.InnerHeight*factor)/2*2),pad=Math.Max(2,(int)(8*factor));
                double t=J.D(e["t"]),end=t+J.D(e["duration"]);
                graph.Add($"[studioFace{i}]crop={cw}:{ch}:{x}:{y},scale={fw}:{fh},format=rgba,pad={fw+pad*2}:{fh+pad*2}:{pad}:{pad}:color=0xFFE500,fade=t=in:st={N(t)}:d=0.12:alpha=1,fade=t=out:st={N(end-.12)}:d=0.12:alpha=1[studioInset{i}]");
                graph.Add($"[{picture}][studioInset{i}]overlay={N(box.X*factor)}:{N(box.Y*factor)}:enable='between(t,{N(t)},{N(end)})':eof_action=pass[studioFocused{i}]");picture=$"studioFocused{i}";
            }
        }
        var filters=new List<string>();
        if(Renderer.Motion(edl) is {} motion)
            filters.Add($"zoompan=z='{motion.z}':x='iw/2-iw/zoom/2+({motion.dx})*(iw/zoom)/ow':y='ih/2-ih/zoom/2+({motion.dy})*(ih/zoom)/oh':d=1:s={w}x{h}:fps={fps}");
        if(Studio.ColorFilter(edl["color"]) is string grade)filters.Add(grade);
        filters.AddRange(Renderer.TimedFilters(edl));
        if(filters.Count>0){graph.Add($"[{picture}]{string.Join(',',filters)}[studioGraded]");picture="studioGraded";}
        double vignette=Studio.ColorParams(edl["color"])["vignette"];
        if(vignette>0)
        {
            int index=inputs.Count(x=>x=="-i");inputs.AddRange(["-loop","1","-i",Path.Combine(Paths.StudioDir,"vignette.png")]);
            graph.Add($"[{index}:v]scale={w}:{h},format=rgba,colorchannelmixer=aa={N(vignette)}[studioVignette]");
            graph.Add($"[{picture}][studioVignette]overlay=shortest=1[studioVig]");picture="studioVig";
        }
        string ass=Path.Combine(work,"overlays.ass");File.WriteAllText(ass,Ass.Build(edl,track,"en",1080,logicalHeight));
        var tail=new List<string>{$"ass='{Ff.FilterPath(ass)}':fontsdir='{Ff.FilterPath(Paths.Fonts)}'"};
        var tr=edl["transitions"];double duration=Renderer.Duration(edl);
        if(J.S(tr?["intro"])=="fade")tail.Add($"fade=t=in:st=0:d={N(Math.Min(duration,J.D(tr!["intro_d"],.4)))}");
        if(J.S(tr?["outro"])=="fade"){double d=Math.Min(duration,J.D(tr!["outro_d"],.4));tail.Add($"fade=t=out:st={N(duration-d)}:d={N(d)}");}
        tail.Add("format=yuv420p");graph.Add($"[{picture}]{string.Join(',',tail)}[studioPicture]");return "studioPicture";
    }
}
