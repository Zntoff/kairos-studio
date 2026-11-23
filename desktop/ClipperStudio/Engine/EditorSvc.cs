// What the editor needs from the engine: state of a clip, draft validation, render, and the media
// (frames, waveform, thumbnails) it draws. Port of clipper/editor.py, without the review subagents.
using System.Text.Json.Nodes;

namespace ClipperStudio.Engine;

public class EditException(string message) : Exception(message);

public class EditorSvc(Store store)
{
    public const double Pad = 60, MinDur = 3, MaxDur = 86400, RecommendedMax = 90;
    const int TextMax = 200;
    static readonly int[] FrameHeights = [72, 120, 240, 480];
    static readonly string[] Editable = ["pending_approval", "needs_human", "approved", "rejected", "rejected_by_human", "error"];
    static readonly string[] HexFields = ["color", "stroke"];
    static readonly object ProxyLock = new();

    public (JsonObject clip, JsonObject src) Load(string clipId)
    {
        var clip = store.Get("clips", clipId) ?? throw new KeyNotFoundException(clipId);
        if (clip["edl"] is not JsonObject || !Editable.Contains(J.S(clip["status"])))
            throw new EditException($"Este clip todavía no se puede editar (estado: {J.S(clip["status"])}).");
        var src = store.Source(J.S(clip["edl"]!["source"]!["id"]));
        if (src == null || !File.Exists(J.S(src["path"]))) throw new EditException("No se encuentra el video fuente de este clip.");
        return (clip, src);
    }

    // ---------- new clips ----------
    public static JsonObject NewEdl(string clipId, JsonObject src, double start, double end, string lang)
    {
        bool vertical = J.D(src["height"]) > J.D(src["width"]) * 1.2;
        return new JsonObject
        {
            ["clip_id"] = clipId,
            ["source"] = new JsonObject { ["id"] = J.S(src["id"]), ["start"] = Math.Round(start, 3), ["end"] = Math.Round(end, 3) },
            ["source_language"] = lang, ["languages"] = new JsonArray(lang),
            ["layout"] = vertical ? "center_crop" : "gameplay_blur_bg", ["crop_x"] = 0.5, ["crop_y"] = 0.5,
            ["hook"] = new JsonObject { ["text"] = new JsonObject { [lang] = "" }, ["duration"] = 0.0 },
            ["commentary"] = new JsonObject { ["mode"] = "onscreen", ["notes"] = new JsonArray() },
            ["captions"] = new JsonObject { ["style"] = "bold", ["highlight_keywords"] = new JsonArray(), ["y"] = null, ["scale"] = 1.0 },
            ["audio"] = new JsonObject
            {
                ["normalize_lufs"] = -14.0, ["music_id"] = null, ["duck_music"] = true, ["sfx"] = new JsonArray(),
                ["voice_gain"] = 1.0, ["music_gain"] = 1.0, ["voice_enhance"] = false,
            },
            ["effects"] = new JsonArray(), ["texts"] = new JsonArray(),
            ["color"] = new JsonObject { ["look"] = "none", ["look_amount"] = 1.0 },
            ["transitions"] = new JsonObject { ["intro"] = "none", ["intro_d"] = 0.4, ["outro"] = "none", ["outro_d"] = 0.4 },
            ["progress"] = new JsonObject { ["on"] = false, ["color"] = "#FFE500", ["position"] = "top", ["height"] = 12 },
            ["risk"] = new JsonObject { ["spoiler"] = false, ["rights_ok"] = true, ["notes"] = "" },
            ["revision"] = 0,
        };
    }

    public string CreateClip(string sourceId, double start, double duration)
    {
        var src = store.Get("sources", sourceId) ?? throw new KeyNotFoundException(sourceId);
        double total = J.D(src["duration"]);
        start = Math.Max(0, Math.Min(start, total - MinDur));
        double end = Math.Min(total, start + Math.Max(MinDur, Math.Min(MaxDur, duration)));
        if (end - start < MinDur) throw new EditException($"El video es demasiado corto: un clip dura al menos {MinDur:0} s.");
        var id = $"{J.S(src["id"]).Replace("src_", "")}_m{Guid.NewGuid().ToString("N")[..5]}";
        var lang = J.S(src["language"]) is { Length: > 0 } l ? l : "es";
        store.Add("clips", new JsonObject
        {
            ["id"] = id, ["job_id"] = "manual", ["status"] = "needs_human", ["edl"] = NewEdl(id, src, start, end, lang),
            ["tracks"] = new JsonObject { [lang] = new JsonObject { ["lang"] = lang, ["style"] = "bold", ["word_timed"] = true, ["cues"] = new JsonArray() } },
            ["draft"] = null, ["package_dir"] = null, ["review"] = null, ["metadata"] = new JsonObject(), ["created_at"] = Store.Now(),
        });
        return id;
    }

    // ---------- state ----------
    public JsonObject State(string clipId)
    {
        var (clip, src) = Load(clipId);
        var draft = clip["draft"] as JsonObject;
        var edl = (draft?["edl"] ?? clip["edl"])!;
        var tracks = (draft?["tracks"] ?? clip["tracks"] ?? new JsonObject())!;
        double lo = Math.Min(J.D(edl["source"]!["start"]), J.D(clip["edl"]!["source"]!["start"])), hi = Math.Max(J.D(edl["source"]!["end"]), J.D(clip["edl"]!["source"]!["end"]));
        double a = 0, b = J.D(src["duration"]);
        using var safeScope = SafeArea.Use(edl);
        int sw = (int)J.D(src["width"]), sh = (int)J.D(src["height"]);
        var fonts = new JsonObject();
        foreach (var f in Studio.Fonts.Values) fonts[f.Id] = new JsonObject { ["label"] = f.Label, ["scale"] = Studio.FontScale(f.Path) };
        var catalog = new JsonObject();
        foreach (var k in new[] { "looks", "effects", "fx", "text_presets", "anim_in", "anim_out", "anim", "transitions", "color_params" }) catalog[k] = J.Clone(Studio.Catalog[k]!);
        ((JsonArray)catalog["effects"]!).Add(J.Clone(Studio.FaceFocus));
        var renders = new JsonObject();
        var pkg = J.S(clip["package_dir"]);
        foreach (var lang in J.Items(clip["edl"]!["languages"]).Select(n => J.S(n)))
            if (pkg != "" && File.Exists(Path.Combine(pkg, lang, "clip.mp4"))) renders[lang] = $"/api/clips/{clipId}/file/{lang}/video";
        var words = new JsonArray();
        foreach (var w in J.Items(src["words"]))
            if (J.D(w["end"]) >= a && J.D(w["start"]) <= b) words.Add(J.Clone(w));
        return new JsonObject
        {
            ["clip"] = new JsonObject { ["id"] = clipId, ["status"] = J.S(clip["status"]), ["job_id"] = J.S(clip["job_id"]), ["review"] = clip["review"] == null ? null : J.Clone(clip["review"]!) },
            ["edl"] = J.Clone(edl), ["tracks"] = J.Clone(tracks), ["has_draft"] = draft != null, ["draft_saved_at"] = draft?["saved_at"] == null ? null : J.S(draft["saved_at"]),
            ["requires_safe_render"] = Framing.NeedsRender(clip),
            ["source"] = new JsonObject
            {
                ["project_id"] = J.S(src["project_id"], Store.DefaultProject), ["id"] = J.S(src["id"]), ["duration"] = J.D(src["duration"]), ["width"] = sw, ["height"] = sh,
                ["has_audio"] = J.B(src["has_audio"]), ["video_url"] = $"/api/sources/{J.S(src["id"])}/video",
            },
            ["window"] = new JsonObject { ["a"] = Math.Round(a, 3), ["b"] = Math.Round(b, 3) }, ["words"] = words,
            ["library"] = new JsonArray(Renderer.Library().Values.Select(e => (JsonNode)new JsonObject
            {
                ["id"] = J.S(e["id"]), ["kind"] = J.S(e["kind"]), ["description"] = J.S(e["description"]), ["duration"] = J.D(e["duration"]), ["gain"] = J.D(e["gain"], 0.6),
                ["license"] = J.S(e["license"]), ["author"] = J.S(e["author"]), ["source_url"] = J.S(e["source_url"]),
            }).ToArray()),
            ["frame"] = new JsonObject
            {
                ["width"] = Ass.W, ["height"] = Ass.H, ["fps"] = store.Settings.Fps,
                ["safe"] = SafeArea.Json(),
            },
            ["geometry"] = new JsonObject { ["gameplay_blur_bg"] = Ass.Geom("gameplay_blur_bg", null, sw, sh).Json(), ["center_crop"] = Ass.Geom("center_crop", null, sw, sh).Json() },
            ["styles"] = J.Clone(Studio.Catalog["caption_styles"]!), ["fonts"] = fonts, ["catalog"] = catalog,
            ["text_layout"] = new JsonObject { ["margin"] = Ass.TextMargin, ["box_pad"] = Ass.TextBoxPad },
            ["limits"] = new JsonObject { ["min"] = MinDur, ["max"] = MaxDur, ["recommended_max"] = RecommendedMax },
            ["renders"] = renders,
        };
    }

    // ---------- draft ----------
    static JsonObject CleanText(JsonNode? text, List<string> langs, bool keepLines = false)
    {
        var o = new JsonObject();
        foreach (var lang in langs)
        {
            var raw = J.S(text?[lang]);
            var clean = keepLines
                ? string.Join('\n', raw.Replace("\r", "").Split('\n').Select(l => string.Join(' ', l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))))
                : string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            o[lang] = (clean.Length > TextMax ? clean[..TextMax] : clean).Trim();
        }
        return o;
    }

    static bool Hex(string s) => System.Text.RegularExpressions.Regex.IsMatch(s, "^#[0-9A-Fa-f]{6}$");
    static string OneOf(JsonNode? n, string def, params string[] allowed) => allowed.Contains(J.S(n)) ? J.S(n) : def;

    /// <summary>Turn what the interface sent into a valid EDL and caption tracks. Fields a person may
    /// not change (source id, languages, risk) always come from the stored clip.</summary>
    public (JsonObject edl, JsonObject tracks) Sanitize(JsonObject clip, JsonObject src, JsonNode payload, bool composition = false)
    {
        var baseEdl = clip["edl"]!;
        var raw = J.Obj(payload["edl"]);
        var langs = J.Items(baseEdl["languages"]).Select(n => J.S(n)).ToList();
        if (raw["source"]?["start"] is not JsonValue || raw["source"]?["end"] is not JsonValue) throw new EditException("Falta el rango del clip (source.start / source.end).");
        double start = Math.Max(0, J.D(raw["source"]!["start"])), end = Math.Min(J.D(src["duration"]), J.D(raw["source"]!["end"])), dur = end - start;
        if (dur < (composition ? .1 : MinDur) || dur > MaxDur) throw new EditException($"El clip duraría {dur:0.0} s; debe durar entre {MinDur:0} y {MaxDur:0} s.");
        bool Inside(JsonNode x) => x is JsonObject && J.D(x["t"], -1) >= 0 && J.D(x["t"], -1) < dur;
        var lib = Renderer.Library();

        JsonNode? Pos(JsonNode? o, string key) => o?[key] is JsonValue ? JsonValue.Create(J.Num(o[key], .5, 0, 1)) : null;
        var notes = new JsonArray();
        foreach (var n in J.Items(raw["commentary"]?["notes"]).OrderBy(n => J.D(n["t"])))
        {
            double t = J.Num(n["t"], 0, 0, dur), d = J.Num(n["duration"], 3, 0, dur - t);
            var text = CleanText(n["text"], langs);
            if (d >= 0.5 && text.Any(kv => J.S(kv.Value) != "")) notes.Add(new JsonObject { ["t"] = t, ["duration"] = d, ["text"] = text, ["x"] = Pos(n,"x"), ["y"] = Pos(n,"y") });
        }
        var audio = J.Obj(raw["audio"]);
        var sfx = new JsonArray();
        foreach (var x in J.Items(audio["sfx"]).Take(40).Where(Inside).OrderBy(x => J.D(x["t"])))
            if (lib.TryGetValue(J.S(x["id"]), out var e) && J.S(e["kind"]) == "sfx")
                sfx.Add(new JsonObject { ["t"] = J.Num(x["t"], 0, 0, dur), ["id"] = J.S(x["id"]), ["gain"] = J.Num(x["gain"], 1, 0, 3) });
        var effects = new JsonArray();
        foreach (var x in J.Items(raw["effects"]).Take(40).Where(Inside).OrderBy(x => J.D(x["t"])))
        {
            if (Studio.Effect(J.S(x["type"], "punch_in")) is not { } spec) continue;
            double t = J.Num(x["t"], 0, 0, dur), min = J.D(spec["min"]);
            var effect = new JsonObject
            {
                ["t"] = t, ["type"] = J.S(spec["id"]), ["intensity"] = J.Num(x["intensity"], 1, 0.2, 2),
                ["duration"] = J.Num(x["duration"], J.D(spec["d"]), min, Math.Min(J.D(spec["max"]), Math.Max(min, dur - t))),
            };
            if (J.S(spec["id"]) == "face_focus")
            {
                if (Ai.CreativeEdits.RegionError(x["region"]) is { } problem) throw new EditException(problem);
                effect["region"] = J.Clone(x["region"]!);
                effect["reason"] = J.S(x["reason"]);
                if (x["frame_index"] != null) effect["frame_index"] = J.Clone(x["frame_index"]!);
            }
            effects.Add(effect);
        }
        var faceEffects = effects.Where(e => J.S(e!["type"]) == "face_focus").ToList();
        if (faceEffects.Count > 3) throw new EditException("Usa como máximo tres ampliaciones de caras por clip.");
        for (int i = 1; i < faceEffects.Count; i++)
            if (J.D(faceEffects[i]!["t"]) < J.D(faceEffects[i - 1]!["t"]) + J.D(faceEffects[i - 1]!["duration"]) - .001)
                throw new EditException("Las ampliaciones de caras no deben superponerse en el tiempo.");
        var texts = new JsonArray();
        foreach (var x in J.Items(raw["texts"]).Take(30).Where(Inside).OrderBy(x => J.D(x["t"])))
        {
            double t = J.Num(x["t"], 0, 0, dur), d = J.Num(x["duration"], 3, 0, dur - t);
            var text = CleanText(x["text"], langs, keepLines: true);
            if (d < 0.2 || text.All(kv => J.S(kv.Value) == "")) continue;
            foreach (var k in HexFields) if (x[k] != null && !Hex(J.S(x[k]))) throw new EditException($"Edición no válida: color «{J.S(x[k])}».");
            if (J.S(x["box"]) != "" && !Hex(J.S(x["box"]))) throw new EditException($"Edición no válida: color «{J.S(x["box"])}».");
            texts.Add(new JsonObject
            {
                ["t"] = t, ["duration"] = d, ["text"] = text, ["x"] = J.Num(x["x"], 0.5, 0, 1), ["y"] = J.Num(x["y"], 0.3, 0, 1),
                ["font"] = Studio.Fonts.ContainsKey(J.S(x["font"])) ? J.S(x["font"]) : "anton", ["size"] = (int)J.Num(x["size"], 80, 16, 300),
                ["color"] = J.S(x["color"], "#FFFFFF"), ["stroke"] = J.S(x["stroke"], "#000000"), ["stroke_w"] = J.Num(x["stroke_w"], 6, 0, 24),
                ["box"] = J.S(x["box"]), ["box_opacity"] = J.Num(x["box_opacity"], 1, 0.1, 1), ["opacity"] = J.Num(x["opacity"], 1, 0.1, 1),
                ["glow"] = J.Num(x["glow"], 0, 0, 12), ["upper"] = J.B(x["upper"]), ["rotate"] = J.Num(x["rotate"], 0, -45, 45),
                ["anim_in"] = OneOf(x["anim_in"], "fade", "none", "fade", "pop", "zoom", "slide_up", "slide_down", "slide_left", "slide_right"),
                ["anim_out"] = OneOf(x["anim_out"], "fade", "none", "fade", "shrink"),
            });
        }
        var col = J.Obj(raw["color"]);
        var color = new JsonObject
        {
            ["look"] = J.Items(Studio.Catalog["looks"]).Any(l => J.S(l["id"]) == J.S(col["look"])) ? J.S(col["look"]) : "none",
            ["look_amount"] = J.Num(col["look_amount"], 1, 0, 1),
        };
        foreach (var k in new[] { "brightness", "contrast", "saturation", "temperature", "tint", "fade", "vignette" })
            color[k] = J.Num(col[k], 0, k is "fade" or "vignette" ? 0 : -1, 1);
        var tr = J.Obj(raw["transitions"]);
        var pg = J.Obj(raw["progress"]);
        var caps = J.Obj(raw["captions"]);
        var hook = J.Obj(raw["hook"]);
        var music = J.S(audio["music_id"]);
        var style = J.S(caps["style"], J.S(baseEdl["captions"]?["style"], "bold"));
        var edl = new JsonObject
        {
            ["clip_id"] = J.S(clip["id"]),
            ["source"] = new JsonObject { ["id"] = J.S(src["id"]), ["start"] = Math.Round(start, 3), ["end"] = Math.Round(end, 3) },
            ["source_language"] = J.S(baseEdl["source_language"]), ["languages"] = new JsonArray(langs.Select(l => (JsonNode)l).ToArray()),
            ["layout"] = OneOf(raw["layout"], J.S(baseEdl["layout"]), "gameplay_blur_bg", "center_crop"), ["crop_x"] = J.Num(raw["crop_x"], 0.5, 0, 1),
            ["crop_y"] = J.Num(raw["crop_y"], J.D(baseEdl["crop_y"], .5), 0, 1),
            ["media"] = EditorMedia.Clean(store, src, raw["media"], dur),
            ["editor_mode"] = OneOf(raw["editor_mode"], "clip", "clip", "long"),
            ["safe_area"] = SafeArea.Clean(raw["safe_area"] ?? baseEdl["safe_area"]),
            ["tracking_enabled"] = J.B(raw["tracking_enabled"], true),
            ["framing"] = baseEdl["framing"] == null ? null : J.Clone(baseEdl["framing"]!),
            ["hook"] = new JsonObject { ["x"] = Pos(hook,"x"), ["y"] = Pos(hook,"y"), ["text"] = CleanText(hook["text"], langs), ["duration"] = J.Num(hook["duration"], 2, 0, Math.Min(dur, 8)) },
            ["commentary"] = new JsonObject { ["mode"] = "onscreen", ["notes"] = notes },
            ["captions"] = new JsonObject
            {
                ["style"] = Studio.Catalog["caption_styles"]![style] != null ? style : "bold",
                ["highlight_keywords"] = baseEdl["captions"]?["highlight_keywords"] is { } hk ? J.Clone(hk) : new JsonArray(),
                ["x"] = Pos(caps,"x"), ["y"] = caps["y"] is JsonValue ? J.Num(caps["y"], 0.7, 0.05, 0.95) : null, ["scale"] = J.Num(caps["scale"], 1, 0.5, 1.6),
            },
            ["audio"] = new JsonObject
            {
                ["normalize_lufs"] = J.Num(audio["normalize_lufs"], -14, -24, -8),
                ["music_id"] = lib.TryGetValue(music, out var me) && J.S(me["kind"]) == "music" ? music : null,
                ["duck_music"] = J.B(audio["duck_music"], true), ["sfx"] = sfx,
                ["voice_gain"] = J.Num(audio["voice_gain"], 1, 0, 3), ["music_gain"] = J.Num(audio["music_gain"], 1, 0, 3), ["voice_enhance"] = J.B(audio["voice_enhance"]),
            },
            ["effects"] = effects, ["color"] = color, ["texts"] = texts,
            ["editorial"] = baseEdl["editorial"] == null ? null : J.Clone(baseEdl["editorial"]!),
            ["transitions"] = new JsonObject
            {
                ["intro"] = OneOf(tr["intro"], "none", "none", "fade", "flash"), ["intro_d"] = J.Num(tr["intro_d"], 0.4, 0.1, 2),
                ["outro"] = OneOf(tr["outro"], "none", "none", "fade"), ["outro_d"] = J.Num(tr["outro_d"], 0.4, 0.1, 2),
            },
            ["progress"] = new JsonObject
            {
                ["on"] = J.B(pg["on"]), ["color"] = Hex(J.S(pg["color"])) ? J.S(pg["color"]) : "#FFE500",
                ["position"] = OneOf(pg["position"], "top", "top", "bottom"), ["height"] = (int)J.Num(pg["height"], 12, 4, 40),
            },
            ["risk"] = J.Clone(baseEdl["risk"]!), ["revision"] = (int)J.D(baseEdl["revision"]),
        };

        var tracks = new JsonObject();
        foreach (var lang in langs)
        {
            var tRaw = J.Obj(payload["tracks"]?[lang]);
            var cues = new JsonArray();
            double floor = 0;
            foreach (var c in J.Items(tRaw["cues"]).OrderBy(c => J.D(c["start"])))
            {
                double c0 = Math.Max(J.Num(c["start"], 0, 0, dur), floor), c1 = J.Num(c["end"], 0, 0, dur);
                var texts2 = J.Items(c["words"]).Select(w => (w, t: string.Join(' ', J.S(w["w"]).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))).Where(p => p.t != "").ToList();
                if (c1 - c0 < 0.08 || texts2.Count == 0) continue;
                var words = new JsonArray();
                double prevStart = c0;
                for (int i = 0; i < texts2.Count; i++)
                {
                    var (w, text) = texts2[i];
                    // Word times stay inside the cue and in order; the first word starts with the cue.
                    double w0 = i == 0 ? c0 : J.Num(w["start"], c0 + (c1 - c0) * i / texts2.Count, prevStart, c1);
                    double w1 = J.Num(w["end"], c0 + (c1 - c0) * (i + 1) / texts2.Count, w0, c1);
                    words.Add(new JsonObject { ["w"] = text.Length > 60 ? text[..60] : text, ["start"] = Math.Round(w0, 3), ["end"] = Math.Round(w1, 3), ["hl"] = J.B(w["hl"]) });
                    prevStart = w0;
                }
                cues.Add(new JsonObject { ["start"] = Math.Round(c0, 3), ["end"] = c1, ["words"] = words });
                floor = c1;
            }
            tracks[lang] = new JsonObject { ["lang"] = lang, ["style"] = J.S(edl["captions"]!["style"]), ["word_timed"] = J.B(tRaw["word_timed"], lang == J.S(edl["source_language"])), ["cues"] = cues };
        }
        return (edl, tracks);
    }

    public JsonObject SaveDraft(string clipId, JsonNode payload)
    {
        var (clip, src) = Load(clipId);
        var (edl, tracks) = Sanitize(clip, src, payload);
        var savedAt = Store.Now();
        store.Update("clips", clipId, ("draft", new JsonObject { ["edl"] = edl, ["tracks"] = tracks, ["saved_at"] = savedAt }));
        return new JsonObject { ["saved_at"] = savedAt, ["duration"] = Math.Round(Renderer.Duration(edl), 3) };
    }

    public void DiscardDraft(string clipId)
    {
        Load(clipId);
        store.Update("clips", clipId, ("draft", null));
    }

    // ---------- render ----------
    public JsonObject RenderDraft(string clipId, Action<string> log)
    {
        var (clip, src) = Load(clipId);
        var draft = clip["draft"] as JsonObject ?? throw new EditException("No hay cambios que renderizar.");
        JsonNode edl = draft["edl"]!, tracks = draft["tracks"]!;
        using var safeScope = SafeArea.Use(edl);
        var settings = store.Settings;
        var langs = J.Items(edl["languages"]).Select(n => J.S(n)).ToList();
        string work = Paths.Dir("clips", clipId, "work");
        // Keep existing packages where they are when editing; the preference controls new exports.
        string pkg = J.S(clip["package_dir"]);
        if (pkg == "") pkg = Path.Combine(store.OutputRoot, clipId);
        Directory.CreateDirectory(pkg);
        log($"Render {clipId} · {Renderer.Duration(edl):0.0} s · {string.Join(", ", langs)} · {settings.ResolvedEncoder}{(Ff.Gpu != "" ? " · " + Ff.Gpu : "")}");
        var checks = new JsonArray();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        foreach (var lang in langs)
        {
            log($"  [Render] {lang}");
            var r = Renderer.RenderClip(edl, src, tracks[lang], lang, work, settings, log);
            var dir = Directory.CreateDirectory(Path.Combine(pkg, lang)).FullName;
            File.Copy(r.Path, Path.Combine(dir, "clip.mp4"), true);
            File.Copy(Path.Combine(work, $"{lang}.srt"), Path.Combine(dir, "clip.srt"), true);
            log($"    {r.Width}×{r.Height} · {r.Duration:0.0} s · {r.Encoder} · {r.Seconds:0.0} s de render ({r.Duration / Math.Max(0.1, r.Seconds):0.0}× tiempo real)");
            checks.Add(new JsonObject { ["name"] = $"render_{lang}", ["status"] = r.Width == Ass.W && r.Height == Ass.H ? "pass" : "fail", ["detail"] = $"{r.Width}×{r.Height}, {r.Duration:0.0} s, {r.Encoder}" });
            checks.Add(new JsonObject
            {
                ["name"] = $"loudness_{lang}", ["status"] = !r.Normalized ? "warn" : Math.Abs((r.Lufs ?? -99) - J.D(edl["audio"]?["normalize_lufs"], -14)) <= 1 ? "pass" : "warn",
                ["detail"] = r.Normalized ? $"{r.Lufs:0.0} LUFS, pico {r.TruePeak:0.0} dBTP" : "sin audio de programa: no se normalizó",
            });
        }
        double dur = Renderer.Duration(edl);
        if (J.S(edl["editor_mode"]) != "long" && dur > RecommendedMax) checks.Add(new JsonObject { ["name"] = "duration", ["status"] = "warn", ["detail"] = $"dura {dur:0.0} s; los Shorts de más de {RecommendedMax:0} s suelen retener peor" });
        var review = new JsonObject { ["status"] = "needs_human", ["checks"] = checks, ["fixable"] = false, ["feedback"] = "Render terminado. Míralo antes de aprobarlo.", ["reviewer"] = "manual" };
        bool subjectKnown = edl["framing"]?["subject_region"] != null && Framing.SubjectFitsSafe(edl, src);
        checks.Add(new JsonObject { ["name"] = "platform_safe_area", ["status"] = J.S(edl["layout"]) == "center_crop" && !subjectKnown ? "warn" : "pass",
            ["detail"] = J.S(edl["layout"]) == "center_crop" && !subjectKnown ? "Recorte vertical elegido por el usuario. No se ha verificado todo el sujeto dentro de la zona: revisa caras y acción." : "Zona común Shorts/Reels aplicada a textos, animaciones y recuadros. El video completo aprovecha el lienzo; revisa sus detalles bajo la interfaz de la plataforma." });
        edl["safe_area"] = SafeArea.Json();
        edl["fit_profile"] = Framing.FitProfile;
        store.Update("clips", clipId, ("edl", edl), ("tracks", tracks), ("draft", null), ("status", "needs_human"), ("package_dir", pkg), ("review", review),
                     ("rendered_at", Store.Now()), ("decided_by", null), ("decision_note", null));
        WritePackage(clipId);
        log($"  Listo en {clock.Elapsed.TotalSeconds:0} s · {pkg}");
        return new JsonObject { ["clip_id"] = clipId, ["status"] = "needs_human", ["dir"] = pkg };
    }

    public void WritePackage(string clipId)
    {
        var clip = store.Get("clips", clipId) ?? throw new KeyNotFoundException(clipId);
        var pkg = J.S(clip["package_dir"]);
        if (!Directory.Exists(pkg)) return;
        void Write(string name, JsonNode? data) => File.WriteAllText(Path.Combine(pkg, name), data?.ToJsonString(J.Pretty) ?? "null");
        Write("edl.json", clip["edl"]);
        Write("safe_area.json", clip["edl"]?["safe_area"] ?? new JsonObject { ["enforced"] = false,
            ["note"] = "Paquete anterior a la protección de zonas. Vuelve a renderizarlo para aplicar el perfil actual." });
        Write("review.json", clip["review"]);
        var library = Renderer.Library();
        var usedSounds = J.Items(clip["edl"]?["audio"]?["sfx"]).Select(s => J.S(s["id"]))
            .Append(J.S(clip["edl"]?["audio"]?["music_id"])).Where(id => library.ContainsKey(id)).Distinct();
        var licenses = new JsonArray(usedSounds.Select(id =>
        {
            var entry = library[id];
            var record = new JsonObject { ["id"] = id };
            foreach (var key in new[] { "license", "license_url", "author", "source_url", "download_url", "changes", "file_sha256" })
                record[key] = entry[key] == null ? null : J.Clone(entry[key]!);
            return (JsonNode)record;
        }).ToArray());
        Write("sound_licenses.json", licenses);
        var files = new JsonObject();
        foreach (var lang in J.Items(clip["edl"]?["languages"]).Select(n => J.S(n)))
        {
            var dir = Path.Combine(pkg, lang);
            Directory.CreateDirectory(dir);
            var srt = Path.Combine(dir, "clip.srt");
            if (File.Exists(srt)) File.Copy(srt, Path.Combine(dir, "captions.srt"), true);
            if (clip["metadata"]?[lang] is { } meta) Write(Path.Combine(lang, "metadata.json"), meta);
            files[lang] = new JsonObject { ["video"] = Path.Combine(dir, "clip.mp4"), ["srt"] = Path.Combine(dir, "captions.srt"), ["metadata"] = Path.Combine(dir, "metadata.json") };
        }
        var manifest = new JsonObject { ["clip_id"] = clipId, ["files"] = files, ["sound_licenses"] = Path.Combine(pkg, "sound_licenses.json") };
        foreach (var key in new[] { "job_id", "parent_clip_id", "status", "created_at", "decided_by", "decided_at", "decision_note" })
            manifest[key] = clip[key] == null ? null : J.Clone(clip[key]!);
        Write("package.json", manifest);
    }

    // ---------- media ----------
    static string Tmp(string path) => Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}.{Environment.CurrentManagedThreadId}.tmp{Path.GetExtension(path)}");

    /// <summary>One JPEG of the source, cached. Times are rounded to half a second so tiles are reused.</summary>
    public string Frame(JsonObject src, double t, int height)
    {
        height = FrameHeights.MinBy(h => Math.Abs(h - height));
        t = Math.Max(0, Math.Min(J.D(src["duration"]) - 0.1, Math.Round(t * 2) / 2));
        var file = Path.Combine(Paths.Dir("cache", "frames", J.S(src["id"])), $"{height}_{(int)(t * 10):0000000}.jpg");
        if (!File.Exists(file))
        {
            var tmp = Tmp(file);
            Ff.RunLimited(["-ss", J.F(t, "0.000"), "-i", J.S(src["path"]), "-frames:v", "1", "-vf", $"scale=-2:{height}", "-q:v", "6", tmp]);
            File.Move(tmp, file, true);
        }
        return file;
    }

    public string? Thumb(JsonObject clip, string? lang)
    {
        var pkg = J.S(clip["package_dir"]);
        var langs = J.Items(clip["edl"]?["languages"]).Select(n => J.S(n)).ToList();
        if (pkg == "" || langs.Count == 0) return null;
        var video = Path.Combine(pkg, lang != null && langs.Contains(lang) ? lang : langs[0], "clip.mp4");
        if (!File.Exists(video)) return null;
        var file = Path.Combine(Paths.Dir("cache", "thumbs"), $"{J.S(clip["id"])}_{File.GetLastWriteTimeUtc(video).Ticks}.jpg");
        if (!File.Exists(file))
        {
            var tmp = Tmp(file);
            Ff.RunLimited(["-ss", "0.6", "-i", video, "-frames:v", "1", "-vf", "scale=360:-2", "-q:v", "5", tmp]);
            File.Move(tmp, file, true);
        }
        return file;
    }

    /// <summary>Peak level per 20 ms between a and b, 0..1, for the timeline's audio track.</summary>
    public JsonObject Waveform(JsonObject src, double a, double b)
    {
        int rate = Math.Clamp((int)Math.Ceiling(8000000 / Math.Max(1,b-a)), 100, 8000);
        int hop = Math.Max(rate/50, (int)Math.Ceiling(rate*(b-a)/8000));
        double perSecond = (double)rate/hop;
        var peaks = new JsonArray();
        if (J.B(src["has_audio"]) && b > a)
        {
            byte[] pcm = [];
            var (code, _, _) = Ff.Exec("ffmpeg", ["-hide_banner", "-nostdin", "-v", "error", "-ss", J.F(a, "0.000"), "-t", J.F(b - a, "0.000"), "-i", J.S(src["path"]),
                                                  "-vn", "-ac", "1", "-ar", rate.ToString(), "-f", "s16le", "-"], binaryStdout: true, onStdout: d => pcm = d);
            int n = pcm.Length / 2 / hop;
            if (code == 0 && n > 0)
            {
                var raw = new double[n];
                double top = 0;
                for (int i = 0; i < n; i++)
                {
                    int peak = 0;
                    for (int j = 0; j < hop; j++) peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(pcm, (i * hop + j) * 2)));
                    raw[i] = peak / 32768.0;
                    top = Math.Max(top, raw[i]);
                }
                if (top <= 0) top = 1;
                foreach (var v in raw) peaks.Add(Math.Round(Math.Sqrt(v / top), 3));
            }
        }
        return new JsonObject { ["a"] = a, ["per_second"] = perSecond, ["peaks"] = peaks };
    }

    /// <summary>A small H.264 copy of the editing window, for sources WebView2 cannot play or seek well.</summary>
    public string Proxy(string clipId, JsonObject src, double a, double b)
    {
        var file = Path.Combine(Paths.Dir("cache", "proxy"), $"{clipId}_{(int)(a * 10)}_{(int)(b * 10)}.mp4");
        lock (ProxyLock)
        {
            if (!File.Exists(file))
            {
                var tmp = file + ".tmp.mp4";
                // Always H.264 (WebView2 plays it everywhere), on the fastest encoder that answers; the CPU if the card refuses.
                var settings = store.Settings;
                var preferred = settings.ResolvedEncoder;
                var h264 = preferred.StartsWith("h264_") ? preferred : "libx264";
                List<string> Cmd(string enc) => [.. Hw.DeviceInit(settings.Device, enc), "-ss", J.F(a, "0.000"), "-t", J.F(b - a, "0.000"), "-i", J.S(src["path"]), "-vf", "scale=-2:540",
                    .. Hw.EncoderArgs(enc, 28, fast: true), .. Hw.DeviceOutput(settings.Device, enc), "-g", "15", "-c:a", "aac", "-b:a", "96k", "-movflags", "+faststart", tmp];
                try { Ff.Run(Cmd(h264)); }
                catch (FfmpegException) when (h264 != "libx264") { Ff.Run(Cmd("libx264")); }
                File.Move(tmp, file, true);
            }
        }
        return file;
    }
}
