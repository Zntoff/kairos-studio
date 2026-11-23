// The two-phase flow of the Docker app, on Windows: (1) analyse a video with Haiku and stop at the
// moments it proposes, (2) generate only the moments the person picked, with the model, layout,
// caption style and languages they chose. Port of clipper/agents/orchestrator.py.
using System.Text.Json.Nodes;
using ClipperStudio.Engine;

namespace ClipperStudio.Ai;

public class Pipeline(Store store, AiSettings ai, EditorSvc editor)
{

    /// <summary>The three tiers of the active provider, with its model names and the prices the person entered.</summary>
    public static JsonArray TierList(AiSettings ai)
    {
        var c = ai.Conf();
        var notes = new Dictionary<string, string> { ["fast"] = "Rápido y barato", ["standard"] = "Equilibrado", ["superior"] = "Máxima calidad" };
        string Price(string t) => c.Prices.TryGetValue(t, out var p) ? $"${p[0].ToString("0.###", J.Inv)} / ${p[1].ToString("0.###", J.Inv)}" : "precio no configurado";
        return new JsonArray(AiSettings.Tiers.Select(t => (JsonNode)new JsonObject
            { ["id"] = t, ["name"] = c.TierNames[t], ["model"] = c.Models[t], ["price"] = Price(t), ["note"] = notes[t] }).ToArray());
    }

    public static readonly string[] LayoutChoices = ["auto", "gameplay_blur_bg", "center_crop"];
    public static readonly string[] CaptionChoices = ["auto", "bold", "bounce", "clean", "impact", "comic", "neon", "pixel", "soft"];

    /// <summary>Jobs left "running" by a closed app cannot still be running: say so, so they can be resumed.</summary>
    public void MarkInterrupted()
    {
        foreach (var j in store.List("jobs").Where(j => J.S(j["status"]) == "running")) store.Update("jobs", J.S(j["id"]), ("status", "interrupted"));
    }

    public string CreateJob(string sourceId, JsonObject options)
    {
        var id = "job_" + Guid.NewGuid().ToString("N")[..10];
        store.Add("jobs", new JsonObject
        {
            ["id"] = id, ["source_id"] = sourceId, ["status"] = "running", ["options"] = options, ["summary"] = null, ["created_at"] = Store.Now(),
            ["analysis"] = null, ["steps"] = new JsonArray(),
        });
        return id;
    }

    void AddStep(string jobId, string agent, string? clipId, StepMeta m)
    {
        var job = store.Get("jobs", jobId)!;
        var steps = (JsonArray)(job["steps"] ?? new JsonArray());
        steps.Add(new JsonObject
        {
            ["agent"] = agent, ["clip_id"] = clipId, ["status"] = "done", ["attempts"] = m.Attempts, ["model"] = m.Model, ["seconds"] = m.Seconds,
            ["usage"] = new JsonObject { ["input_tokens"] = m.Input, ["output_tokens"] = m.Output },
        });
        store.Update("jobs", jobId, ("steps", steps));
    }

    // ---------- phase 1: find the moments ----------
    public async Task AnalyzeAsync(string jobId, Action<string> log, ProviderConf? provider = null)
    {
        var _llm = new LlmClient(ai, provider);
        try
        {
            var job = store.Get("jobs", jobId) ?? throw new KeyNotFoundException(jobId);
            var src = store.Source(J.S(job["source_id"])) ?? throw new LlmException("La fuente ya no está registrada.");
            if (!File.Exists(J.S(src["path"]))) throw new LlmException("No se encuentra el archivo del video.");
            if (!Ff.Available) throw new LlmException("Falta FFmpeg. Instálalo en Ajustes → Herramientas.");
            if (_llm.Missing is { } missing) throw new LlmException(missing);
            var dir = Paths.Dir("jobs", jobId);
            double duration = J.D(src["duration"]);
            bool hasAudio = J.B(src["has_audio"]);
            log($"Analizando {J.S(src["id"])} · {duration / 60:0.0} min");

            var transcriptPath = J.S(src["transcript_path"]);
            var wav = Path.Combine(dir, "audio_16k.wav");
            if (hasAudio) { log("  [Audio] extrayendo"); Whisper.ExtractAudio(J.S(src["path"]), wav); }
            JsonObject transcript;
            if (transcriptPath != "" && File.Exists(transcriptPath)) { transcript = (JsonObject)JsonNode.Parse(File.ReadAllText(transcriptPath))!; log("  [Transcripción] ya existía, se reutiliza"); }
            else
            {
                var lang = J.S(src["language"]);
                if (hasAudio)
                {
                    log("  [Transcripción] whisper.cpp local");
                    var preferences = store.Settings;
                    transcript = Whisper.Transcribe(wav, lang == "" ? null : lang, duration, log, preferences.TranscriptionMode == "fast", preferences.Device == "cpu" || preferences.Device.StartsWith("dxgi:") && Hw.Family(preferences.ResolvedEncoder) != "nvenc");
                }
                else transcript = Whisper.Silent(duration, lang == "" ? "und" : lang);
                transcriptPath = Path.Combine(Paths.Dir("transcripts"), J.S(src["id"]) + ".json");
                File.WriteAllText(transcriptPath, transcript.ToJsonString());
                store.Update("sources", J.S(src["id"]), ("transcript_path", transcriptPath), ("language", J.S(src["language"]) == "" ? J.S(transcript["language"]) : J.S(src["language"])));
            }
            if (J.D(transcript["duration"]) <= 0) transcript["duration"] = duration;
            store.Update("sources", J.S(src["id"]), ("words", new JsonArray(J.Items(transcript["segments"])
                .SelectMany(s => J.Items(s["words"])).Select(J.Clone).ToArray())));

            log("  [Señales] energía y cortes de escena");
            var energy = hasAudio && File.Exists(wav) ? Signals.Energy(wav) : [];
            var cuts = Signals.SceneCuts(J.S(src["path"]));

            var opts = J.Obj(job["options"]);
            var model = _llm.ModelFor(J.S(opts["analysis_model"], "fast"));
            log($"  [Análisis] {model}");
            var agents = new Agents(_llm, log);
            var (cands, meta) = await agents.AnalyzeAsync(src, transcript, energy, cuts, model);
            AddStep(jobId, "analysis", null, meta);
            store.Update("jobs", jobId, ("analysis", new JsonObject { ["transcript_path"] = transcriptPath, ["candidates"] = new JsonArray(cands.Select(c => (JsonNode)c).ToArray()) }),
                         ("status", "awaiting_selection"), ("summary", new JsonObject { ["candidates"] = cands.Count }));
            log($"  {cands.Count} momentos sugeridos. Elígelos en «Elegir momentos»; no se generó nada todavía.");
        }
        catch (Exception e)
        {
            store.Update("jobs", jobId, ("status", "failed"), ("summary", new JsonObject { ["error"] = e.Message }));
            throw;
        }
    }

    // ---------- phase 2: generate what was picked ----------
    public async Task GenerateAsync(string jobId, List<string> picks, string tier, string layout, string captionStyle, List<string> languages, Action<string> log, ProviderConf? provider = null)
    {
        var _llm = new LlmClient(ai, provider);
        var job = store.Get("jobs", jobId) ?? throw new KeyNotFoundException(jobId);
        store.Update("jobs", jobId, ("status", "running"));
        var agents = new Agents(_llm, log);
        int made = 0, failed = 0;
        log($"Generando {picks.Count} clip(s) · modelo {_llm.ModelFor(tier)} · idiomas {string.Join(", ", languages)}");
        try
        {
            var analysis = J.Obj(job["analysis"]);
            var src = store.Source(J.S(job["source_id"])) ?? throw new LlmException("La fuente ya no está registrada.");
            var transcript = JsonNode.Parse(File.ReadAllText(J.S(analysis["transcript_path"])))!;
            var cands = J.Items(analysis["candidates"]).ToList();
            for (int idx = 0; idx < cands.Count; idx++)
            {
                if (!picks.Contains(J.S(cands[idx]["id"]))) continue;
                var clipId = $"{jobId.Replace("job_", "")}_c{idx:00}";
                if (store.Get("clips", clipId) is { } existing && J.S(existing["status"]) is "needs_human" or "pending_approval" or "approved" or "rejected_by_human" && existing["package_dir"] != null) { log($"  {clipId}: ya estaba generado"); continue; }
                log($"  [{clipId}] {J.D(cands[idx]["start"]):0}–{J.D(cands[idx]["end"]):0} s · {J.S(cands[idx]["category"])}");
                try
                {
                    await MakeClipAsync(agents, jobId, clipId, idx, src, cands[idx], transcript, tier, layout, captionStyle, languages, log);
                    made++;
                }
                catch (Exception e) when (e is LlmException or FfmpegException or EditException or ContractException)
                {
                    if (e.Message.Contains("API key") || e.Message.Contains("crédito") || e.Message.Contains("limitó")) throw;   // not this clip's fault: stop and say so
                    failed++;
                    log($"    ✗ {e.Message.Split('\n')[0]}");
                    if (store.Get("clips", clipId) == null)
                        store.Add("clips", new JsonObject { ["id"] = clipId, ["job_id"] = jobId, ["idx"] = idx, ["status"] = "agent_failed", ["edl"] = null, ["package_dir"] = null, ["decision_note"] = Ff.Tail(e.Message, 300), ["created_at"] = Store.Now() });
                    else store.Update("clips", clipId, ("status", "error"), ("decision_note", Ff.Tail(e.Message, 300)));
                }
            }
            store.Update("jobs", jobId, ("status", made > 0 || failed == 0 ? "done" : "failed"), ("summary", new JsonObject { ["clips"] = made, ["failed"] = failed }));
            log($"Terminado: {made} clip(s) listo(s){(failed > 0 ? $", {failed} con error" : "")}. Míralos en la cola de aprobación.");
        }
        catch (Exception e)
        {
            store.Update("jobs", jobId, ("status", "failed"), ("summary", new JsonObject { ["error"] = e.Message }));
            throw;
        }
    }

    public async Task CreativeVersionAsync(string originalId, string newId, string tier, Action<string> log, string layoutChoice = "auto", ProviderConf? provider = null)
    {
        var _llm = new LlmClient(ai, provider);
        var original = store.Get("clips", originalId) ?? throw new KeyNotFoundException(originalId);
        var job = store.Get("jobs", J.S(original["job_id"])) ?? throw new LlmException("Este clip no tiene un análisis guardado para reeditar.");
        var candidates = J.Items(job["analysis"]?["candidates"]).ToList();
        int index = (int)J.D(original["idx"], -1);
        if (index < 0 || index >= candidates.Count) throw new LlmException("No se encontró el momento original del clip.");
        var transcript = JsonNode.Parse(File.ReadAllText(J.S(job["analysis"]?["transcript_path"])))!;
        var src = store.Source(J.S(job["source_id"])) ?? throw new LlmException("No se encontró la fuente.");
        var opts = job["options"]!;
        var langs = J.Items(original["edl"]?["languages"]).Select(l => J.S(l)).ToList();
        try
        {
            log($"Nueva versión creativa de {originalId}. Se conserva el clip anterior.");
            await MakeClipAsync(new Agents(_llm, log), J.S(job["id"]), newId, index, src, candidates[index], transcript,
                tier, layoutChoice, J.S(opts["caption_style"], "auto"), langs, log);
            store.Update("clips", newId, ("parent_clip_id", originalId));
            editor.WritePackage(newId);
        }
        catch (Exception e)
        {
            if (store.Get("clips", newId) != null) store.Update("clips", newId, ("status", "error"), ("decision_note", Ff.Tail(e.Message, 300)), ("parent_clip_id", originalId));
            throw;
        }
    }

    async Task MakeClipAsync(Agents agents, string jobId, string clipId, int idx, JsonObject src, JsonNode cand, JsonNode transcript,
        string tier, string layout, string captionStyle, List<string> languages, Action<string> log)
    {
        var recent = store.List("clips", J.S(src["project_id"], Store.DefaultProject)).Where(c => c["edl"] != null && J.S(c["id"]) != clipId)
            .SelectMany(c => ((JsonObject)c["edl"]!["hook"]!["text"]!).Select(kv => J.S(kv.Value))).Where(h => h != "").TakeLast(12).ToList();
        var frames = new List<string>();
        var fdir = Paths.Dir("jobs", jobId, "frames", clipId);
        double a = J.D(cand["start"]), b = J.D(cand["end"]);
        const int frameCount = 12;
        for (int i = 0; i < frameCount; i++)
        {
            var f = Path.Combine(fdir, $"src_{i:00}.jpg");
            Ff.RunLimited(["-ss", J.F(a + (b - a) * (i + 0.5) / frameCount, "0.000"), "-i", J.S(src["path"]), "-frames:v", "1", "-vf", "scale=720:-2", "-q:v", "4", f]);
            frames.Add(f);
        }
        log("    [Edición]");
        var inp = Agents.PrepareEdit(clipId, src, cand, transcript, languages, recent, layout, captionStyle, frames);
        var (edl, m1) = await agents.EditAsync(inp, tier, null);
        log($"    [Plan] {J.S(edl["editorial"]?["original_angle"])}");
        log($"    {J.Items(edl["effects"]).Count()} efectos visuales · {J.Items(edl["audio"]?["sfx"]).Count()} sonidos · {J.Items(edl["texts"]).Count()} textos animados");
        AddStep(jobId, "editing", clipId, m1);
        log("    [Subtítulos y traducción]");
        var captionTranscript = transcript;
        if (J.B(src["has_audio"], true) && Whisper.Ready)
        {
            log("    [Sincronización] alineación precisa sobre la voz del recorte");
            double origin = Math.Max(0, J.D(edl["source"]?["start"]) - .4);
            double end = Math.Min(J.D(src["duration"]), J.D(edl["source"]?["end"]) + .4);
            string wav = Path.Combine(fdir, "alignment.wav");
            Ff.Run(["-ss", J.F(origin), "-i", J.S(src["path"]), "-t", J.F(end-origin), "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", wav]);
            var prefs = store.Settings;
            captionTranscript = Whisper.Transcribe(wav, J.S(transcript["language"]), end-origin, log, fast:false,
                cpuOnly:prefs.Device == "cpu" || prefs.Device.StartsWith("dxgi:") && Hw.Family(prefs.ResolvedEncoder) != "nvenc");
            foreach (var seg in J.Items(captionTranscript["segments"]))
            {
                seg["start"] = J.D(seg["start"]) + origin; seg["end"] = J.D(seg["end"]) + origin;
                foreach (var w in J.Items(seg["words"])) { w["start"] = J.D(w["start"]) + origin; w["end"] = J.D(w["end"]) + origin; }
            }
        }
        var (tracks, m2) = await agents.CaptionsAsync(edl, captionTranscript, tier, null);
        if (m2 != null) AddStep(jobId, "captions", clipId, m2);
        log("    [Títulos y metadata]");
        var (meta, m3) = await agents.MetadataAsync(edl, transcript, J.S(src["credit"]) is { Length: > 0 } c ? c : null, tier, null);
        AddStep(jobId, "metadata", clipId, m3);

        var draft = new JsonObject { ["edl"] = edl, ["tracks"] = tracks, ["saved_at"] = Store.Now() };
        var row = new JsonObject
        {
            ["id"] = clipId, ["job_id"] = jobId, ["idx"] = idx, ["status"] = "needs_human", ["edl"] = J.Clone(edl), ["tracks"] = J.Clone(tracks), ["draft"] = draft,
            ["metadata"] = J.Clone(meta["per_language"]!), ["package_dir"] = null, ["review"] = null, ["created_at"] = Store.Now(),
        };
        if (store.Get("clips", clipId) == null) store.Add("clips", row);
        else store.Update("clips", clipId, row.Select(kv => (kv.Key, kv.Value)).ToArray());
        log("    [Render]");
        editor.RenderDraft(clipId, log);

        // The Claude reviewer is not part of this edition: the deterministic guards stay, and every clip waits for a person.
        var spoken = string.Join(' ', CaptionKit.ClipWords(transcript, J.D(edl["source"]!["start"]), J.D(edl["source"]!["end"])).Select(w => w.W));
        var hits = Agents.FindTerms(spoken + " " + SpokenAndWritten(edl, meta), Agents.SpoilerTerms());
        var clip = store.Get("clips", clipId)!;
        var review = (JsonObject)clip["review"]!;
        var checks = (JsonArray)review["checks"]!;
        checks.Add(new JsonObject { ["name"] = "spoiler_terms", ["status"] = hits.Count > 0 ? "fail" : "pass", ["detail"] = hits.Count > 0 ? "la lista de spoilers aparece en el texto: " + string.Join(", ", hits) : "ningún término de la lista de spoilers" });
        if (J.B(edl["risk"]?["spoiler"])) checks.Add(new JsonObject { ["name"] = "model_spoiler_flag", ["status"] = "fail", ["detail"] = "el subagente marcó posible spoiler: " + J.S(edl["risk"]!["notes"]) });
        checks.Add(new JsonObject { ["name"] = "claude_review", ["status"] = "warn", ["detail"] = "La revisión de Claude (spoilers y valor propio) no forma parte de esta edición: mira el clip antes de aprobarlo." });
        review["feedback"] = hits.Count > 0 || J.B(edl["risk"]?["spoiler"]) ? "Posible spoiler: revísalo con cuidado antes de aprobar." : "Míralo antes de aprobarlo.";
        store.Update("clips", clipId, ("review", review));
        editor.WritePackage(clipId);
    }

    static string SpokenAndWritten(JsonObject edl, JsonObject meta)
    {
        var parts = new List<string>();
        parts.AddRange(((JsonObject)edl["hook"]!["text"]!).Select(kv => J.S(kv.Value)));
        foreach (var n in J.Items(edl["commentary"]!["notes"])) parts.AddRange(((JsonObject)n["text"]!).Select(kv => J.S(kv.Value)));
        parts.Add(meta.ToJsonString());
        return string.Join(' ', parts);
    }
}
