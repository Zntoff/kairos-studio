using System.Text.Json;
using System.Text.Json.Nodes;
using ClipperStudio.Engine;

namespace ClipperStudio.Ai;

// One registry is used for discovery, provider function definitions, validation
// and execution. This is a local tool contract, not an external MCP transport.
public sealed class EditorTools(VideoProjects service,JsonObject record,JsonNode baseline)
{
    public const string Version="kairos-editor-tools/1";
    public JsonObject Document {get;private set;}=service.Clean(J.S(record["project_id"]),baseline,record["settings"]);
    public JsonArray Operations {get;}=[];
    public JsonArray Trace {get;}=[];
    public string? Summary {get;private set;}
    readonly HashSet<string> loadedCategories=[];
    static string Category(string name)=>name switch
    {
        "editor_add_text" or "editor_update_text"=>"text",
        "editor_add_effect" or "editor_remove_layer"=>"effects",
        "editor_set_audio" or "editor_add_sound"=>"audio",
        "editor_set_color" or "editor_set_captions" or "editor_set_transitions" or "editor_set_progress" or "editor_set_hook"=>"appearance",
        "editor_add_media" or "editor_duplicate_media" or "editor_set_media_framing" or "editor_set_duration"=>"media",
        _=>"core"
    };
    public JsonArray AvailableDefinitions()=>new(J.Items(Definitions()).Where(t=>Category(J.S(t["name"]))=="core"||loadedCategories.Contains(Category(J.S(t["name"])))).Select(J.Clone).ToArray());
    string Project=>J.S(record["project_id"]);
    JsonObject Edl=>Document["tools"]!["edl"]!.AsObject();
    static JsonObject Fields(params (string name,JsonObject schema)[] fields)=>new(fields.Select(x=>KeyValuePair.Create(x.name,(JsonNode?)x.schema)));
    static JsonObject Number(string description,double min=0,double max=86400)=>new(){["type"]="number",["description"]=description,["minimum"]=min,["maximum"]=max};
    static JsonObject Text(string description,int max=100)=>new(){["type"]="string",["description"]=description,["maxLength"]=max,["minLength"]=1};
    static JsonObject Choice(string description,params string[] values)=>new(){["type"]="string",["description"]=description,["enum"]=new JsonArray(values.Select(x=>(JsonNode)x).ToArray())};
    static JsonObject Flag(string description)=>new(){["type"]="boolean",["description"]=description};
    static JsonObject Id()=>Text("Exact item ID from the timeline or current selection. Never an asset ID.",80);
    static JsonObject Index()=>new(){["type"]="integer",["description"]="Current zero-based layer index returned by editor_inspect. Inspect again after removing layers.",["minimum"]=0,["maximum"]=199};
    static string[] CatalogIds(string section)=>section=="caption_styles"?Studio.Catalog[section]!.AsObject().Select(x=>x.Key).ToArray():J.Items(Studio.Catalog[section]).Select(x=>J.S(x["id"])).ToArray();
    public static JsonArray Definitions()
    {
        var tools=new JsonArray();
        void Add(string name,string description,JsonObject fields,string required="",bool readOnly=false)
        {
            tools.Add(new JsonObject{["name"]=name,["description"]=description,
                ["inputSchema"]=new JsonObject{["type"]="object",["properties"]=fields,["required"]=new JsonArray(required.Split(',',StringSplitOptions.RemoveEmptyEntries).Select(x=>(JsonNode)x).ToArray()),["additionalProperties"]=false},
                ["annotations"]=new JsonObject{["readOnlyHint"]=readOnly,["destructiveHint"]=name.Contains("remove"),["idempotentHint"]=readOnly||name.Contains("set_")||name.Contains("update_"),["openWorldHint"]=false}});
        }
        Add("editor_discover_tools","Load specialized functions into the next turn: media=insertion, duplicate, framing, total duration; text=create/update text; effects=add effects/remove layers; audio=mix/music/SFX; appearance=color, captions, transitions, progress, hook. Load only categories needed.",Fields(("category",Choice("Tool group to make available.","media","text","effects","audio","appearance"))),"category",true);
        var paging=Fields(("offset",Number("Zero-based pagination offset; default 0.",0,100000)),("limit",Number("Maximum returned entries; default 12, maximum 30.",1,30)));
        paging["offset"]!["type"]="integer";paging["limit"]!["type"]="integer";
        var inspect=(JsonObject)paging.DeepClone();inspect["section"]=Choice("Read only the relevant document section.","timeline","text","effects","audio","color","captions","transitions","progress","hook");
        Add("editor_inspect","Read current timeline items or one tool section, including layer indexes and exact settings. Paginated lists; no mutation.",inspect,"section",true);
        var assets=(JsonObject)paging.DeepClone();assets["kind"]=Choice("Optional media type filter.","video","audio","image");assets["query"]=Text("Optional case-insensitive name filter.",100);
        Add("editor_find_assets","Find existing project media to insert. Returns asset IDs, names, kinds and source durations; never file paths.",assets,readOnly:true);
        var catalog=(JsonObject)paging.DeepClone();catalog["kind"]=Choice("Only fetch the catalog needed for this request.","effects","text_presets","fonts","looks","caption_styles","music","sfx");
        Add("editor_get_catalog","Discover supported style/effect/preset/font/sound IDs and limits. Do not fetch every catalog.",catalog,"kind",true);
        Add("editor_trim_media","Set source offset and retained duration of one media item. Does not move its timeline start. Extends timeline if needed; source bounds are enforced.",Fields(("item_id",Id()),("source_offset_seconds",Number("Start within the original source file, in seconds.")),("duration_seconds",Number("Retained source duration in seconds.",.05))),"item_id,source_offset_seconds,duration_seconds");
        Add("editor_move_media","Move one existing media item to an absolute timeline time; preserve source trim, duration and gain. Extends timeline if needed.",Fields(("item_id",Id()),("start_seconds",Number("Absolute timeline start in seconds."))),"item_id,start_seconds");
        Add("editor_set_media_volume","Set one media item's linear volume: 1=100%, 0.25=25%, 0=mute. Does not change global voice or music gain.",Fields(("item_id",Id()),("gain",Number("Absolute linear gain, not a percentage or decibels.",0,3))),"item_id,gain");
        Add("editor_split_media","Split one item at an absolute timeline time. Preserves source-relative offsets. Returns both item IDs.",Fields(("item_id",Id()),("at_seconds",Number("Absolute timeline split time, strictly inside this item."))),"item_id,at_seconds");
        Add("editor_duplicate_media","Copy one media item to a new absolute start, preserving its source trim, framing and volume. Creates an independent item ID.",Fields(("item_id",Id()),("start_seconds",Number("Absolute start of the copy in timeline seconds."))),"item_id,start_seconds");
        Add("editor_remove_media","Remove one timeline item; keep its source file and all other items. Undo remains available in the editor.",Fields(("item_id",Id())),"item_id");
        Add("editor_add_media","Insert an existing project asset. Optional offset defaults to 0; duration defaults to the remaining source, or 5 seconds for images. Extends timeline if needed.",Fields(("asset_id",Text("Exact asset ID from editor_find_assets.",80)),("start_seconds",Number("Absolute timeline insertion time.")),("source_offset_seconds",Number("Source offset, not timeline time; default 0.")),("duration_seconds",Number("Retained duration; must fit the source.",.05)),("gain",Number("Linear gain; default 1.",0,3)),("fit",Choice("Framing mode; default contain.","contain","cover","blur"))),"asset_id,start_seconds");
        Add("editor_set_media_framing","Change one media item's framing. contain keeps all content, cover crops to fill, blur fits over a blurred background. Positions are normalized.",Fields(("item_id",Id()),("fit",Choice("How the picture fills the canvas.","contain","cover","blur")),("x",Number("Horizontal crop center, 0=left, 1=right; omitted preserves current value.",0,1)),("y",Number("Vertical crop center, 0=top, 1=bottom; omitted preserves current value.",0,1))),"item_id,fit");
        Add("editor_set_duration","Set total composition duration. Must include every media item and visible layer; this tool never silently trims layers.",Fields(("duration_seconds",Number("New total timeline duration.",.1))),"duration_seconds");
        JsonObject TextFields(bool update)
        {
            var f=Fields(("text",Text("Literal on-screen text; stored in the composition's content language.",200)),("start_seconds",Number("Absolute timeline start.")),("duration_seconds",Number("On-screen duration.",.2)),("x",Number("Horizontal center, normalized 0..1.",0,1)),("y",Number("Vertical center, normalized 0..1.",0,1)),("font",Choice("Bundled font ID.",Studio.Fonts.Keys.ToArray())),("size",Number("Font size in logical canvas pixels.",16,300)),("color",new JsonObject{["type"]="string",["pattern"]="^#[0-9a-fA-F]{6}$",["description"]="Text color as #RRGGBB."}),
                ("animation_in",Choice("Entrance animation.","none","fade","pop","zoom","slide_up","slide_down","slide_left","slide_right")),("animation_out",Choice("Exit animation.","none","fade","shrink")));
            if(update)f["index"]=Index();else f["preset"]=Choice("Optional starting style; defaults to the first built-in text preset.",CatalogIds("text_presets"));return f;
        }
        Add("editor_add_text","Create an animated text layer using local preset defaults. Specify literal text and exact timing; omit styling you do not need to change.",TextFields(false),"text,start_seconds,duration_seconds");
        Add("editor_update_text","Update only supplied properties of an existing text layer. Keeps all unspecified formatting and timing.",TextFields(true),"index");
        var region=new JsonObject{["type"]="object",["properties"]=Fields(("x",Number("Region left.",0,1)),("y",Number("Region top.",0,1)),("w",Number("Region width.",.04,1)),("h",Number("Region height.",.04,1))),["required"]=new JsonArray("x","y","w","h"),["additionalProperties"]=false};
        Add("editor_add_effect","Add a timed visual effect. Use effect-specific min/max duration from the catalog. face_focus requires an explicit region; never claim to detect a face from metadata.",Fields(("effect_id",Choice("Supported effect ID.",CatalogIds("effects").Append("face_focus").Distinct().ToArray())),("start_seconds",Number("Timeline start.")),("duration_seconds",Number("Effect duration; effect-specific limits also apply.",.1,60)),("intensity",Number("Effect strength; default 1.",.2,2)),("region",region)),"effect_id,start_seconds,duration_seconds");
        Add("editor_remove_layer","Remove a text, effect or sound by its current index. Only this layer is removed; inspect again before using shifted indexes.",Fields(("kind",Choice("Layer collection.","text","effect","sound")),("index",Index())),"kind,index");
        var color=Fields(("look",Choice("Built-in color look.",CatalogIds("looks"))),("look_amount",Number("Look strength.",0,1)));
        foreach(string key in new[]{"brightness","contrast","saturation","temperature","tint","fade","vignette"})color[key]=Number("Adjustment; 0 is neutral. Unspecified values are preserved.",key is "fade" or "vignette"?0:-1,1);
        Add("editor_set_color","Change only the supplied color look/adjustments for the composition. All values are normalized, not percentages.",color);
        Add("editor_set_audio","Change supplied composition mix settings. These affect the whole composition, unlike editor_set_media_volume. Empty music_id disables background music.",Fields(("voice_gain",Number("Global source-audio linear gain.",0,3)),("music_id",new JsonObject{["type"]="string",["maxLength"]=100,["description"]="Existing music library ID, or empty string to disable."}),("music_gain",Number("Global music linear gain.",0,3)),("duck_music",Flag("Lower background music while voice is present.")),("voice_enhance",Flag("Voice enhancement applied on export.")),("normalize_lufs",Number("Export normalization target in LUFS.",-24,-8))));
        Add("editor_add_sound","Insert one existing sound effect from the local SFX catalog. Does not generate speech or sound.",Fields(("sound_id",Text("SFX ID from editor_get_catalog(kind=sfx).")),("start_seconds",Number("Absolute timeline start.")),("gain",Number("Linear gain; default 1.",0,3))),"sound_id,start_seconds");
        Add("editor_set_captions","Style and position existing captions. Does not transcribe, translate or invent word timing.",Fields(("style",Choice("Caption style ID.",CatalogIds("caption_styles"))),("scale",Number("Size multiplier.",.5,1.6)),("x",Number("Horizontal center.",0,1)),("y",Number("Vertical center.",.05,.95))));
        Add("editor_set_transitions","Set composition intro/outro transitions; preserve omitted values. Durations are seconds.",Fields(("intro",Choice("Intro transition.","none","fade","flash")),("intro_duration_seconds",Number("Intro transition duration.",.1,2)),("outro",Choice("Outro transition.","none","fade")),("outro_duration_seconds",Number("Outro transition duration.",.1,2))));
        Add("editor_set_progress","Enable or disable the composition progress bar and change supplied appearance fields.",Fields(("enabled",Flag("Whether the bar is visible.")),("color",new JsonObject{["type"]="string",["pattern"]="^#[0-9a-fA-F]{6}$"}),("position",Choice("Edge of the canvas.","top","bottom")),("height",Number("Bar height in logical pixels.",4,40))),"enabled");
        Add("editor_set_hook","Set the opening headline in its existing hook layer. Duration cannot exceed 8 seconds or the composition length.",Fields(("text",Text("Literal opening headline.",120)),("duration_seconds",Number("Headline duration; zero hides it.",0,8)),("x",Number("Horizontal center.",0,1)),("y",Number("Vertical center.",0,1))),"text,duration_seconds");
        Add("editor_finish","Finish the current edit transaction with a short user-language summary, or a clarifying question if no edits are possible. Call last, after successful edit calls, in the same batch when possible.",Fields(("summary",Text("Explain only changes actually performed or limitations/questions.",1200))),"summary",true);
        return tools;
    }

    public static void Validate(JsonNode? value,JsonObject schema,string path="arguments")
    {
        string type=J.S(schema["type"]);var kind=value?.GetValueKind();
        if(type=="object")
        {
            if(value is not JsonObject o)throw new EditException(path+" must be an object.");
            var properties=schema["properties"]!.AsObject();
            foreach(var required in J.Items(schema["required"]))if(!o.ContainsKey(J.S(required)))throw new EditException(path+" is missing required field "+J.S(required)+".");
            foreach(var field in o){if(properties[field.Key] is not JsonObject fieldSchema)throw new EditException(path+" has unknown field "+field.Key+". Use only the tool's documented parameters.");Validate(field.Value,fieldSchema,path+"."+field.Key);}
        }
        else if(type is "number" or "integer")
        {
            double n=J.D(value,double.NaN);
            if(kind!=JsonValueKind.Number||!double.IsFinite(n)||type=="integer"&&n!=Math.Truncate(n))throw new EditException(path+" must be a finite "+type+".");
            if(n<J.D(schema["minimum"],double.MinValue)||n>J.D(schema["maximum"],double.MaxValue))throw new EditException(path+" must be between "+J.D(schema["minimum"])+" and "+J.D(schema["maximum"])+".");
        }
        else if(type=="boolean" && kind is not (JsonValueKind.True or JsonValueKind.False))throw new EditException(path+" must be boolean.");
        else if(type=="string")
        {
            string text=J.S(value);
            if(kind!=JsonValueKind.String||text.Length<J.D(schema["minLength"])||text.Length>J.D(schema["maxLength"],int.MaxValue))throw new EditException(path+" has an invalid string length.");
            if(schema["pattern"] is {} pattern&&!System.Text.RegularExpressions.Regex.IsMatch(text,J.S(pattern)))throw new EditException(path+" does not match "+J.S(pattern)+".");
        }
        if(schema["enum"] is JsonArray options&&!options.Any(option=>JsonNode.DeepEquals(option,value)))throw new EditException(path+" must be one of: "+string.Join(", ",options.Select(x=>J.S(x)))+".");
    }

    static JsonObject Page(IEnumerable<JsonNode> rows,int offset,int limit)
    {
        var all=rows.ToList();return new JsonObject{["items"]=new JsonArray(all.Skip(offset).Take(limit).Select(J.Clone).ToArray()),["total"]=all.Count,["next_offset"]=offset+limit<all.Count?offset+limit:null};
    }
    public JsonObject InitialState(JsonNode request)
    {
        string selected=J.S(request["selected_item_id"]);double cursor=J.D(request["cursor"]);
        var items=J.Items(Document["items"]).OrderBy(x=>J.S(x["id"])==selected?0:Math.Abs(J.D(x["start"])-cursor)+1).Take(12);
        return new JsonObject{["duration_seconds"]=J.D(Document["duration"]),["cursor_seconds"]=cursor,["selected_item_id"]=selected,
            ["selected_layer"]=request["selected_tool"] is JsonObject layer?new JsonObject{["kind"]=J.S(layer["kind"])[..Math.Min(20,J.S(layer["kind"]).Length)],["index"]=Math.Clamp((int)J.D(layer["index"]),0,199)}:null,["media_count"]=J.Items(Document["items"]).Count(),["nearby_media"]=new JsonArray(items.Select(J.Clone).ToArray()),
            ["text_layers"]=Page(J.Items(Edl["texts"]).Select((x,i)=>(JsonNode)new JsonObject{["index"]=i,["text"]=J.S(x["text"]?["en"]),["start_seconds"]=J.D(x["t"]),["duration_seconds"]=J.D(x["duration"])}),0,6),
            ["effect_count"]=J.Items(Edl["effects"]).Count(),["sound_count"]=J.Items(Edl["audio"]?["sfx"]).Count(),["caption_count"]=J.Items(Document["tools"]?["track"]?["cues"]).Count()};
    }
    JsonNode Read(string name,JsonObject args)
    {
        int offset=(int)J.D(args["offset"]),limit=(int)J.D(args["limit"],12);
        if(name=="editor_find_assets")
            return Page(J.Items(service.Assets(Project)).Where(x=>(args["kind"]==null||J.S(x["kind"])==J.S(args["kind"]))&&J.S(x["name"]).Contains(J.S(args["query"]),StringComparison.OrdinalIgnoreCase))
                .Select(x=>(JsonNode)new JsonObject{["asset_id"]=J.S(x["id"]),["name"]=J.S(x["name"]),["kind"]=J.S(x["kind"]),["duration_seconds"]=J.D(x["duration"]),["has_audio"]=J.B(x["has_audio"])}),offset,limit);
        if(name=="editor_get_catalog")
        {
            string kind=J.S(args["kind"]);IEnumerable<JsonNode> entries;
            if(kind is "music" or "sfx")entries=Renderer.Library().Values.Where(x=>J.S(x["kind"])==kind).Select(x=>(JsonNode)new JsonObject{["id"]=J.S(x["id"]),["description"]=J.S(x["description"]),["duration_seconds"]=J.D(x["duration"])});
            else if(kind=="fonts")entries=Studio.Fonts.Values.Select(x=>(JsonNode)new JsonObject{["id"]=x.Id,["label"]=x.Label});
            else if(kind=="caption_styles")entries=Studio.Catalog[kind]!.AsObject().Select(x=>(JsonNode)new JsonObject{["id"]=x.Key,["label"]=J.S(x.Value?["label"])});
            else entries=J.Items(Studio.Catalog[kind]).Select(x=>{var item=new JsonObject{["id"]=J.S(x["id"]),["label"]=J.S(x["label"])};foreach(string key in new[]{"desc","min","max","d"})if(x[key]!=null)item[key]=x[key]!.DeepClone();return (JsonNode)item;});
            return Page(entries,offset,limit);
        }
        string section=J.S(args["section"]);
        JsonNode data=section switch{"timeline"=>Document["items"]!,"text"=>Edl["texts"]!,"captions"=>Document["tools"]!["track"]!,_=>Edl[section]!};
        if(data is JsonArray array)return Page(J.Items(array).Select((x,i)=>(JsonNode)new JsonObject{["index"]=i,["value"]=J.Clone(x)}),offset,limit);
        if(section=="captions"){var copy=(JsonObject)data.DeepClone();copy["cues"]=Page(J.Items(data["cues"]),offset,limit);return copy;}
        if(section=="audio"){var copy=(JsonObject)data.DeepClone();copy["sfx"]=Page(J.Items(data["sfx"]).Select((x,i)=>(JsonNode)new JsonObject{["index"]=i,["value"]=J.Clone(x)}),offset,limit);return copy;}
        return data.DeepClone();
    }

    public JsonObject Execute(string name,JsonObject arguments)
    {
        var definition=J.Items(Definitions()).FirstOrDefault(t=>J.S(t["name"])==name)??throw new EditException("Unknown editor tool: "+name+". Use the advertised tool names.");
        Validate(arguments,definition["inputSchema"]!.AsObject());
        if(name=="editor_discover_tools")
        {
            string category=J.S(arguments["category"]);loadedCategories.Add(category);
            return new JsonObject{["loaded_category"]=category,["available_tools"]=new JsonArray(J.Items(Definitions()).Where(t=>Category(J.S(t["name"]))==category).Select(t=>(JsonNode)J.S(t["name"])).ToArray())};
        }
        if(Category(name)!="core"&&!loadedCategories.Contains(Category(name)))throw new EditException("Call editor_discover_tools(category="+Category(name)+") to load this tool's exact schema first.");
        if(name=="editor_finish"){Summary=J.S(arguments["summary"]);return new JsonObject{["finished"]=true,["changes"]=Operations.Count};}
        if(J.B(definition["annotations"]?["readOnlyHint"]))return Read(name,arguments).AsObject();
        if(Operations.Count>=30)throw new EditException("At most 30 edit operations per request. Finish this edit before requesting more.");
        var next=(JsonObject)Document.DeepClone();var edl=next["tools"]!["edl"]!.AsObject();var items=next["items"]!.AsArray();
        JsonObject Item()=>items.OfType<JsonObject>().FirstOrDefault(x=>J.S(x["id"])==J.S(arguments["item_id"]))??throw new EditException("Unknown item_id. Call editor_inspect(section=timeline) for valid IDs.");
        double D(string key,double fallback=0)=>J.D(arguments[key],fallback);
        var op=new JsonObject{["item_id"]=J.S(arguments["item_id"],"studio"),["tool_name"]=name};
        void Extend(double end){if(end>86400)throw new EditException("The edit exceeds the 24-hour timeline limit.");next["duration"]=Math.Max(J.D(next["duration"]),end);}
        void Bounds(double t,double duration){if(t+duration>J.D(next["duration"])+.0001)throw new EditException("The layer exceeds the timeline. Call editor_set_duration first, or use a shorter duration.");}
        void Patch(string section,Dictionary<string,string>? rename=null)
        {
            if(arguments.Count==0)throw new EditException("Supply at least one setting to change.");
            var payload=new JsonObject();foreach(var field in arguments){string key=rename?.GetValueOrDefault(field.Key)??field.Key;edl[section]![key]=field.Value!.DeepClone();payload[key]=field.Value.DeepClone();}
            op["action"]="set_tool";op["section"]=section;op["payload"]=payload;
        }
        switch(name)
        {
            case "editor_set_media_volume":Item()["gain"]=D("gain");op["action"]="volume";op["value"]=D("gain");break;
            case "editor_move_media":{var item=Item();item["start"]=D("start_seconds");Extend(D("start_seconds")+J.D(item["duration"]));op["action"]="move";op["value"]=D("start_seconds");break;}
            case "editor_trim_media":{var item=Item();item["offset"]=D("source_offset_seconds");item["duration"]=D("duration_seconds");Extend(J.D(item["start"])+D("duration_seconds"));op["action"]="trim";op["offset"]=D("source_offset_seconds");op["duration"]=D("duration_seconds");break;}
            case "editor_remove_media":items.Remove(Item());op["action"]="delete";break;
            case "editor_set_media_framing":{var item=Item();item["fit"]=J.S(arguments["fit"]);item["crop_x"]=D("x",J.D(item["crop_x"],.5));item["crop_y"]=D("y",J.D(item["crop_y"],.5));op["action"]="crop";op["mode"]=J.S(item["fit"]);op["x"]=J.D(item["crop_x"]);op["y"]=J.D(item["crop_y"]);break;}
            case "editor_split_media":
            {
                var item=Item();double at=D("at_seconds")-J.D(item["start"]),length=J.D(item["duration"]);
                if(at<.05||at>length-.05)throw new EditException("at_seconds must lie at least 0.05 seconds inside each edge of the item.");
                var right=(JsonObject)item.DeepClone();right["id"]="item_"+Guid.NewGuid().ToString("N");right["start"]=D("at_seconds");right["offset"]=J.D(item["offset"])+at;right["duration"]=length-at;
                item["duration"]=at;items.Insert(items.IndexOf(item)+1,right);op["action"]="split";op["value"]=at;op["created_item_id"]=J.S(right["id"]);break;
            }
            case "editor_duplicate_media":case "editor_add_media":
            {
                JsonObject item;
                if(name=="editor_duplicate_media")item=(JsonObject)Item().DeepClone();
                else
                {
                    var asset=service.Asset(Project,J.S(arguments["asset_id"]));double offset=D("source_offset_seconds"),duration=D("duration_seconds",J.S(asset["kind"])=="image"?5:J.D(asset["duration"])-offset);
                    if(duration<.05)throw new EditException("Source offset leaves no media to insert.");
                    item=new JsonObject{["asset_id"]=J.S(arguments["asset_id"]),["offset"]=offset,["duration"]=duration,["gain"]=D("gain",1),["fit"]=J.S(arguments["fit"],"contain"),["crop_x"]=.5,["crop_y"]=.5};
                }
                item["id"]="item_"+Guid.NewGuid().ToString("N");item["start"]=D("start_seconds");Extend(J.D(item["start"])+J.D(item["duration"]));items.Add(item);
                op["action"]="add_media";op["item_id"]=J.S(item["id"]);op["payload"]=item.DeepClone();break;
            }
            case "editor_set_duration":
            {
                double end=D("duration_seconds");
                bool outside=J.Items(items).Any(x=>J.D(x["start"])+J.D(x["duration"])>end+.0001)||new[]{"texts","effects"}.Any(k=>J.Items(edl[k]).Any(x=>J.D(x["t"])+J.D(x["duration"])>end+.0001))||J.Items(edl["commentary"]?["notes"]).Any(x=>J.D(x["t"])+J.D(x["duration"])>end+.0001)||J.Items(next["tools"]?["track"]?["cues"]).Any(x=>J.D(x["end"])>end+.0001)||J.D(edl["hook"]?["duration"])>end;
                if(outside)throw new EditException("The requested duration would truncate existing content. Trim or remove that content first.");
                foreach(var sound in J.Items(edl["audio"]?["sfx"]))if(J.D(sound["t"])+J.D(Renderer.Library().GetValueOrDefault(J.S(sound["id"]))?["duration"])>end+.0001)throw new EditException("The duration would truncate a sound effect.");
                next["duration"]=end;op["action"]="duration";op["value"]=end;break;
            }
            case "editor_add_text":case "editor_update_text":
            {
                JsonObject layer;var list=edl["texts"]!.AsArray();
                if(name=="editor_update_text")
                {int index=(int)D("index");if(index>=list.Count)throw new EditException("Unknown text index; inspect the text section.");if(arguments.Count<2)throw new EditException("Supply at least one text property to update.");layer=list[index]!.AsObject();op["index"]=index;}
                else{layer=(JsonObject)(J.Items(Studio.Catalog["text_presets"]).FirstOrDefault(x=>J.S(x["id"])==J.S(arguments["preset"]))??J.Items(Studio.Catalog["text_presets"]).First()).DeepClone();layer.Remove("id");layer.Remove("label");layer.Remove("sample");list.Add(layer);}
                foreach(var field in arguments)
                {
                    if(field.Key is "index" or "preset")continue;
                    string key=field.Key switch{"start_seconds"=>"t","duration_seconds"=>"duration","animation_in"=>"anim_in","animation_out"=>"anim_out",_=>field.Key};
                    layer[key]=key=="text"?new JsonObject{["en"]=J.S(field.Value)}:field.Value!.DeepClone();
                }
                Bounds(J.D(layer["t"]),J.D(layer["duration"]));
                if(list.Count>30)throw new EditException("At most 30 text layers are supported. Remove a layer first.");
                op["action"]=name=="editor_add_text"?"add_text":"update_layer";op["section"]="texts";op["payload"]=layer.DeepClone();break;
            }
            case "editor_add_effect":
            {
                string id=J.S(arguments["effect_id"]);var spec=Studio.Effect(id)??throw new EditException("Unknown effect ID.");double duration=D("duration_seconds");
                if(duration<J.D(spec["min"])||duration>J.D(spec["max"]))throw new EditException($"{id} duration must be {J.D(spec["min"])}..{J.D(spec["max"])} seconds.");
                Bounds(D("start_seconds"),duration);var layer=new JsonObject{["t"]=D("start_seconds"),["duration"]=duration,["type"]=id,["intensity"]=D("intensity",1)};
                if(id=="face_focus"){if(arguments["region"]==null)throw new EditException("face_focus needs an explicit region. No face has been detected.");if(CreativeEdits.RegionError(arguments["region"]) is {} error)throw new EditException(error);layer["region"]=arguments["region"]!.DeepClone();}
                if(J.Items(edl["effects"]).Count()>=40)throw new EditException("At most 40 effects are supported.");edl["effects"]!.AsArray().Add(layer);op["action"]="add_effect";op["section"]="effects";op["payload"]=layer.DeepClone();break;
            }
            case "editor_add_sound":
            {
                if(!Renderer.Library().TryGetValue(J.S(arguments["sound_id"]),out var sound)||J.S(sound["kind"])!="sfx")throw new EditException("Unknown SFX ID. Query editor_get_catalog(kind=sfx).");
                Bounds(D("start_seconds"),J.D(sound["duration"]));if(J.Items(edl["audio"]!["sfx"]).Count()>=40)throw new EditException("At most 40 sounds are supported.");
                var layer=new JsonObject{["t"]=D("start_seconds"),["id"]=J.S(arguments["sound_id"]),["gain"]=D("gain",1)};edl["audio"]!["sfx"]!.AsArray().Add(layer);op["action"]="add_sfx";op["section"]="sfx";op["payload"]=layer.DeepClone();break;
            }
            case "editor_remove_layer":
            {
                string kind=J.S(arguments["kind"]),section=kind switch{"text"=>"texts","effect"=>"effects",_=>"sfx"};var list=(section=="sfx"?edl["audio"]!["sfx"]:edl[section])!.AsArray();int index=(int)D("index");
                if(index>=list.Count)throw new EditException("Layer index no longer exists; inspect this section again.");list.RemoveAt(index);op["action"]="remove_tool";op["section"]=section;op["value"]=index;break;
            }
            case "editor_set_color":Patch("color");break;
            case "editor_set_captions":Patch("captions");break;
            case "editor_set_audio":
                if(arguments["music_id"]!=null&&J.S(arguments["music_id"])!=""&&(!Renderer.Library().TryGetValue(J.S(arguments["music_id"]),out var music)||J.S(music["kind"])!="music"))throw new EditException("Unknown music ID. Query editor_get_catalog(kind=music).");Patch("audio");break;
            case "editor_set_transitions":Patch("transitions",new(){["intro_duration_seconds"]="intro_d",["outro_duration_seconds"]="outro_d"});break;
            case "editor_set_progress":Patch("progress",new(){["enabled"]="on"});break;
            case "editor_set_hook":
            {
                Bounds(0,D("duration_seconds"));var patch=(JsonObject)arguments.DeepClone();patch["text"]=new JsonObject{["en"]=J.S(arguments["text"])};patch["duration"]=D("duration_seconds");patch.Remove("duration_seconds");
                foreach(var field in patch)edl["hook"]![field.Key]=field.Value!.DeepClone();op["action"]="set_tool";op["section"]="hook";op["payload"]=patch;break;
            }
            default:throw new EditException("Tool has no executor: "+name);
        }
        Document=service.Clean(Project,next,record["settings"]);
        if(J.S(op["action"]) is "add_text" or "update_layer")
            op["index"]=J.Items(Document["tools"]!["edl"]!["texts"]).ToList().FindLastIndex(x=>Math.Abs(J.D(x["t"])-J.D(op["payload"]?["t"]))<.001&&J.S(x["text"]?["en"])==J.S(op["payload"]?["text"]?["en"]));
        Operations.Add(op);
        return new JsonObject{["ok"]=true,["change_number"]=Operations.Count,["item_id"]=J.S(op["item_id"]),["created_item_id"]=op["created_item_id"]?.DeepClone(),["layer_index"]=op["index"]?.DeepClone(),["duration_seconds"]=J.D(Document["duration"])};
    }
}
