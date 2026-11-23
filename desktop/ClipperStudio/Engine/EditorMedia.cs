using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ClipperStudio.Engine;

/// <summary>Project-owned media only. Drafts contain IDs, never client-supplied paths.</summary>
public static class EditorMedia
{
    public static string PathFor(JsonNode resource)
    {
        string project = J.S(resource["project_id"]), id = J.S(resource["id"]), ext = J.S(resource["extension"]);
        if (!Regex.IsMatch(project, "^[A-Za-z0-9_-]+$") || !Regex.IsMatch(id, "^[A-Za-z0-9_-]+$") ||
            !Regex.IsMatch(ext, @"^\.[a-z0-9]+$")) throw new EditException("Recurso no válido.");
        return Path.Combine(Paths.Data, "projects", project, "resources", id + ext);
    }

    public static JsonArray Clean(Store store, JsonNode source, JsonNode? raw, double duration)
    {
        var result = new JsonArray();
        foreach (var item in J.Items(raw).Take(200))
        {
            var resource = store.Get("resources", J.S(item["resource_id"]));
            if (resource == null || J.S(resource["project_id"]) != J.S(source["project_id"], Store.DefaultProject) ||
                J.S(resource["kind"]) is not ("image" or "video" or "audio") || !File.Exists(PathFor(resource)))
                throw new EditException("El recurso no pertenece al proyecto o ya no existe.");
            double t = J.Num(item["t"], 0, 0, duration), length = J.Num(item["duration"], 5, 0, duration-t);
            double offset = J.Num(item["offset"], 0, 0, 86400);
            if (J.S(resource["kind"]) != "image")
            {
                double available = J.D(resource["duration"]);
                if (available <= 0)
                {
                    var (code, json, _) = Ff.Exec("ffprobe", ["-v","error","-show_format","-show_streams","-of","json",PathFor(resource)]);
                    if (code != 0) throw new EditException("No se pudo leer el recurso de audio o video.");
                    var info = JsonNode.Parse(json)!;
                    available = J.D(info["format"]?["duration"]);
                    store.Update("resources", J.S(resource["id"]), ("duration", JsonValue.Create(available)),
                        ("has_audio", JsonValue.Create(J.Items(info["streams"]).Any(s => J.S(s["codec_type"])=="audio"))));
                }
                if (offset >= available) throw new EditException("El inicio del recurso supera su duración.");
                length = Math.Min(length, available-offset);
            }
            if (length < .1) continue;
            result.Add(new JsonObject { ["resource_id"] = J.S(resource["id"]), ["t"] = t,
                ["duration"] = length, ["offset"] = offset, ["gain"] = J.Num(item["gain"], 1, 0, 3) });
        }
        return result;
    }

    public static (List<string> inputs, string video, string audio) Compose(JsonNode edl, string video, string audio, int firstInput, List<string> graph, int fps)
    {
        List<string> inputs = [];
        var store = new Store();
        int i = 0;
        foreach (var item in J.Items(edl["media"]))
        {
            var resource = store.Get("resources", J.S(item["resource_id"])) ?? throw new EditException("No se encuentra un recurso de la línea de tiempo.");
            var source = store.Source(J.S(edl["source"]?["id"])) ?? throw new EditException("No se encuentra el video fuente.");
            if (J.S(resource["project_id"]) != J.S(source["project_id"], Store.DefaultProject)) throw new EditException("Recurso de otro proyecto.");
            int index = firstInput + i;
            double t = J.D(item["t"]), duration = J.D(item["duration"]);
            string kind = J.S(resource["kind"]), path = PathFor(resource);
            if (kind == "image") inputs.AddRange(["-loop", "1"]);
            else inputs.AddRange(["-ss", J.F(J.D(item["offset"]))]);
            inputs.AddRange(["-t", J.F(duration), "-i", path]);
            if (kind is "image" or "video")
            {
                graph.Add($"[{index}:v]fps={fps},scale=1080:1920:force_original_aspect_ratio=decrease,pad=1080:1920:(ow-iw)/2:(oh-ih)/2,setsar=1,setpts=PTS-STARTPTS+{J.F(t)}/TB[media{i}]");
                graph.Add($"[{video}][media{i}]overlay=eof_action=pass:enable='gte(t,{J.F(t)})*lt(t,{J.F(t+duration)})'[mediaV{i}]");
                video = $"mediaV{i}";
            }
            if (kind == "audio" || kind == "video" && J.B(resource["has_audio"]))
            {
                graph.Add($"[{index}:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,asetpts=PTS-STARTPTS,volume={J.F(J.D(item["gain"],1))},adelay={(int)Math.Round(t*1000)}:all=1[mediaA{i}]");
                graph.Add($"{audio}[mediaA{i}]amix=inputs=2:duration=first:normalize=0[mediaMix{i}]");
                audio = $"[mediaMix{i}]";
            }
            i++;
        }
        return (inputs, video, audio);
    }
}
