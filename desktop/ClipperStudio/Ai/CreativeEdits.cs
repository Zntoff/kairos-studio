using System.Text.Json.Nodes;
using ClipperStudio.Engine;

namespace ClipperStudio.Ai;

/// <summary>Desktop creative decisions, grounded in timestamped source images.</summary>
public static class CreativeEdits
{
    public static JsonObject Schema()
    {
        var schema = (JsonObject)J.Clone(Schemas.Editing);
        var p = schema["properties"]!;
        var fx = p["effects"]!["items"]!["properties"]!;
        ((JsonArray)fx["type"]!["enum"]!).Add("face_focus");
        fx["intensity"] = JsonNode.Parse("""{"type":"number","minimum":0.2,"maximum":2}""");
        fx["frame_index"] = JsonNode.Parse("""{"type":"integer","minimum":0,"description":"For face_focus: index of an attached image showing this face."}""");
        fx["reason"] = JsonNode.Parse("""{"type":"string","maxLength":180}""");
        fx["region"] = JsonNode.Parse("""
        {"type":"object","description":"face_focus only: normalized head-and-shoulders crop in the ORIGINAL source image. x,y are the top-left, w,h the dimensions. Must fit inside the image; do not invent a face.",
         "properties":{"x":{"type":"number","minimum":0,"maximum":1},"y":{"type":"number","minimum":0,"maximum":1},
          "w":{"type":"number","minimum":0.04,"maximum":1},"h":{"type":"number","minimum":0.04,"maximum":1}},"required":["x","y","w","h"]}
        """);
        p["framing"] = JsonNode.Parse("""
        {"type":"object","properties":{"mode":{"type":"string","enum":["fit","subject"]},
         "reason":{"type":"string","maxLength":240},"frame_index":{"type":"integer","minimum":0},
         "subject_region":{"type":"object","description":"Normalized rectangle containing ALL essential action/UI, not just a point. Keep the whole source (fit) if several windows or people matter.",
          "properties":{"x":{"type":"number","minimum":0,"maximum":1},"y":{"type":"number","minimum":0,"maximum":1},"w":{"type":"number","minimum":0.04,"maximum":1},"h":{"type":"number","minimum":0.04,"maximum":1}},"required":["x","y","w","h"]}},"required":["mode","reason"]}
        """);
        p["framing"]!["properties"]!["subject_track"] = new JsonObject {
            ["type"] = "array", ["maxItems"] = 32, ["items"] = new JsonObject {
                ["type"] = "object", ["properties"] = new JsonObject {
                    ["frame_index"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 },
                    ["region"] = J.Clone(p["framing"]!["properties"]!["subject_region"]!) },
                ["required"] = new JsonArray("frame_index", "region") } };
        p["audio"]!["properties"]!["sfx"]!["items"]!["properties"]!["gain"] = JsonNode.Parse("""{"type":"number","minimum":0.15,"maximum":1.2}""");
        p["texts"] = JsonNode.Parse("""
        {"type":"array","maxItems":3,"items":{"type":"object","properties":{
          "at_word":{"type":"integer","minimum":0},"duration":{"type":"number","minimum":0.6,"maximum":3},
          "preset":{"type":"string","enum":["label","comic","hand","neon"]},
          "text":{"type":"object","additionalProperties":{"type":"string"}},
          "anim_in":{"type":"string","enum":["pop","zoom","slide_up","slide_left"]}},"required":["at_word","duration","preset","text","anim_in"]}}
        """);
        p["editorial"] = JsonNode.Parse("""
        {"type":"object","properties":{"original_angle":{"type":"string","minLength":20,"maxLength":400},
         "pacing":{"type":"string","enum":["dynamic","calm"]},"reason":{"type":"string","minLength":15,"maxLength":300}},
         "required":["original_angle","pacing","reason"]}
        """);
        foreach (var field in new[] { "audio", "effects", "texts", "editorial", "framing" }) ((JsonArray)schema["required"]!).Add(field);
        return schema;
    }

    public static string? RegionError(JsonNode? r)
    {
        if (r is not JsonObject) return "Missing face region (x,y,w,h).";
        double x = J.D(r["x"], -1), y = J.D(r["y"], -1), w = J.D(r["w"], -1), h = J.D(r["h"], -1);
        return x < 0 || y < 0 || w < .04 || h < .04 || x + w > 1.000001 || y + h > 1.000001
            ? "Face region must fit inside the source image; x/y >= 0, w/h >= 0.04, x+w <= 1, y+h <= 1." : null;
    }

    public static string? Validate(Agents.EditInput input, JsonObject d, int sw, int ew)
    {
        if (d["framing"] is { } framing)
        {
            if (framing is not JsonObject) return "framing must be an object with mode, reason and an optional subject_region.";
            if (J.S(framing["mode"]) is not ("fit" or "subject")) return "Choose framing.mode = fit or subject.";
            if (framing["subject_region"] != null && RegionError(framing["subject_region"]) is { } regionError) return "framing.subject_region: " + regionError;
            var seen = new HashSet<int>();
            foreach (var point in J.Items(framing["subject_track"]))
            {
                double index = J.D(point["frame_index"], -1);
                if (index < 0 || index != Math.Truncate(index) || index >= input.Frames.Count || !seen.Add((int)index) || RegionError(point["region"]) != null)
                    return "subject_track needs unique real frame indices and valid source regions.";
            }
            if (J.S(framing["mode"]) == "subject")
            {
                if (RegionError(framing["subject_region"]) is { } error) return "framing.subject_region: " + error;
                double frame = J.D(framing["frame_index"], -1);
                if (frame < 0 || frame != Math.Truncate(frame) || frame >= input.Frames.Count) return "The primary framing needs a real source frame_index.";
            }
        }
        var editorial = d["editorial"];
        if (J.S(editorial?["original_angle"]).Trim().Length < 20) return "editorial.original_angle must explain the original analysis or observation this edit adds.";
        if (J.S(editorial?["pacing"]) is not ("dynamic" or "calm") || J.S(editorial?["reason"]).Trim().Length < 15)
            return "Choose editorial.pacing (dynamic/calm) and explain the rhythm for this particular moment.";
        var effects = J.Items(d["effects"]).ToList();
        var sounds = J.Items(d["audio"]?["sfx"]).ToList();
        if (J.S(editorial?["pacing"]) == "dynamic" && (effects.Count < 2 || sounds.Count < (input.Library.Values.Any(s => J.S(s["kind"]) == "sfx") ? 2 : 0)))
            return "A dynamic edit needs at least two motivated visual beats and two licensed SFX (setup/reaction/payoff); use calm only when the scene genuinely calls for restraint.";
        if (effects.Count > 6 || sounds.Count > 8) return "Keep at most 6 visual effects and 8 sound effects.";
        var faces = effects.Where(e => J.S(e["type"]) == "face_focus").OrderBy(e => J.D(e["at_word"])).ToList();
        if (faces.Count > 3) return "At most three brief face highlights per clip.";
        double previousEnd = -1;
        foreach (var effect in effects)
        {
            if (Studio.Effect(J.S(effect["type"])) == null) return "Unsupported visual effect type.";
            var duration = J.D(effect["duration"]);
            if (duration < .2 || duration > 4) return "Visual effects must last 0.2–4 seconds.";
        }
        foreach (var face in faces)
        {
            if (RegionError(face["region"]) is { } problem) return problem;
            var frameIndex = J.D(face["frame_index"], -1);
            if (frameIndex != Math.Truncate(frameIndex) || frameIndex < 0 || frameIndex >= input.Frames.Count) return "face_focus.frame_index must identify an attached source image showing the face.";
            if (J.S(face["reason"]).Trim().Length < 8) return "Explain the visible facial reaction for every face_focus.";
            double at = J.D(face["at_word"], -1), duration = J.D(face["duration"]);
            if (at != Math.Truncate(at) || at < sw || at > ew) return "face_focus.at_word must be a word index inside this clip.";
            double t = input.Words[(int)at].Start;
            double a = J.D(input.Candidate["start"]), b = J.D(input.Candidate["end"]);
            double frameTime = a + (b - a) * (frameIndex + .5) / input.Frames.Count;
            if (Math.Abs(t - frameTime) > Math.Max(1, (b - a) / input.Frames.Count)) return "Anchor face_focus near its reference image time; the crop is not a face tracker.";
            if (duration > 2.5 || t < previousEnd) return "Face highlights must be non-overlapping and at most 2.5 seconds long.";
            previousEnd = t + duration;
        }
        var texts = J.Items(d["texts"]).ToList();
        if (J.S(editorial?["pacing"]) == "dynamic" && texts.Count == 0) return "A dynamic edit needs at least one short animated callout that adds to the scene, not a duplicate subtitle.";
        if (texts.Count > 3) return "At most three short animated callouts.";
        foreach (var text in texts)
        {
            double at = J.D(text["at_word"], -1);
            if (at != Math.Truncate(at) || at < sw || at > ew || J.D(text["duration"]) is < .6 or > 3) return "Callouts must use a valid word anchor and last 0.6–3 seconds.";
            if (!new[] { "label", "comic", "hand", "neon" }.Contains(J.S(text["preset"])) || !new[] { "pop", "zoom", "slide_up", "slide_left" }.Contains(J.S(text["anim_in"]))) return "Choose a supported callout preset and animation.";
            foreach (var lang in input.Languages)
                if (J.S(text["text"]?[lang]).Trim().Length is < 1 or > 45) return $"Callout text in {lang} must have 1–45 characters.";
        }
        return null;
    }

    public static void Apply(JsonObject edl, JsonObject decision, Func<int, double> time)
    {
        var effects = J.Items(edl["effects"]).ToList();
        var originals = J.Items(decision["effects"]).OrderBy(e => time((int)J.D(e["at_word"]))).ToList();
        for (int i = 0; i < effects.Count; i++)
        {
            effects[i]["intensity"] = J.Num(originals[i]["intensity"], 1, .2, 2);
            if (J.S(effects[i]["type"]) != "face_focus") continue;
            foreach (var key in new[] { "region", "frame_index", "reason" }) effects[i][key] = J.Clone(originals[i][key]!);
            effects[i]["duration"] = Math.Min(2.5, J.D(effects[i]["duration"]));
        }
        var sounds = J.Items(edl["audio"]!["sfx"]).ToList();
        var rawSounds = J.Items(decision["audio"]?["sfx"]).OrderBy(s => time((int)J.D(s["at_word"]))).ToList();
        for (int i = 0; i < sounds.Count; i++) sounds[i]["gain"] = J.Num(rawSounds[i]["gain"], .65, .15, 1.2);
        edl["audio"]!["music_gain"] = .4;
        var texts = new JsonArray();
        foreach (var raw in J.Items(decision["texts"]))
        {
            var preset = J.Items(Studio.Catalog["text_presets"]).First(p => J.S(p["id"]) == J.S(raw["preset"]));
            var text = (JsonObject)J.Clone(preset);
            foreach (var key in new[] { "id", "label", "sample" }) text.Remove(key);
            text["t"] = time((int)J.D(raw["at_word"]));
            text["duration"] = Math.Min(J.D(raw["duration"]), Renderer.Duration(edl) - J.D(text["t"]));
            text["text"] = J.Clone(raw["text"]!); text["x"] = .5; text["y"] = .63;
            text["anim_in"] = J.S(raw["anim_in"]); text["anim_out"] = "fade";
            texts.Add(text);
        }
        edl["texts"] = texts;
        edl["editorial"] = J.Clone(decision["editorial"]!);
    }
}
