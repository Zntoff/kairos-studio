// The subagents: analysis, editing, captions and metadata. Ports of clipper/agents/*.py with the same
// prompts (prompts\*.md), the same contract checks and the same conversion from word indexes to seconds.
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClipperStudio.Engine;

namespace ClipperStudio.Ai;

public static class Limits
{
    public const double ClipMin = 20, ClipMax = 90, TargetLufs = -14;
    public const int MaxClips = 12, HookMax = 60, NoteMax = 120, MinCommentaryWords = 20, TitleMax = 100, DescriptionMax = 2200, HashtagsMax = 5;
    public const double LeadIn = 0.15, Tail = 0.30;
}

public static class Schemas
{
    const string Styles = "\"bold\",\"bounce\",\"clean\",\"impact\",\"comic\",\"neon\",\"pixel\",\"soft\"";
    const string Localized = "{\"type\":\"object\",\"additionalProperties\":{\"type\":\"string\"},\"description\":\"Object keyed by language code with the text in that language, e.g. {\\\"en\\\": \\\"...\\\", \\\"es\\\": \\\"...\\\"}. Include every requested language.\"}";
    static JsonObject P(string s) => (JsonObject)JsonNode.Parse(s)!;

    public static readonly JsonObject Analysis = P("""
    {"type":"object","properties":{"candidates":{"type":"array","items":{"type":"object","properties":{
      "start_segment":{"type":"integer","minimum":0},"end_segment":{"type":"integer","minimum":0},"score":{"type":"number","minimum":0,"maximum":100},
      "category":{"type":"string","maxLength":40},"reason":{"type":"string","maxLength":400},"hook_idea":{"type":"string","maxLength":160}},
      "required":["start_segment","end_segment","score","category","reason"]}}},"required":["candidates"]}
    """);

    public static readonly JsonObject Editing = P("{\"type\":\"object\",\"properties\":{" +
        "\"start_word\":{\"type\":\"integer\",\"minimum\":0},\"end_word\":{\"type\":\"integer\",\"minimum\":0}," +
        "\"layout\":{\"type\":\"string\",\"enum\":[\"gameplay_blur_bg\",\"center_crop\"]},\"crop_x\":{\"type\":\"number\",\"minimum\":0,\"maximum\":1}," +
        "\"hook\":{\"type\":\"object\",\"properties\":{\"text\":" + Localized + ",\"duration\":{\"type\":\"number\",\"minimum\":1,\"maximum\":3}},\"required\":[\"text\",\"duration\"]}," +
        "\"commentary\":{\"type\":\"object\",\"properties\":{\"mode\":{\"type\":\"string\",\"enum\":[\"onscreen\",\"voiceover\"]},\"notes\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{" +
        "\"at_word\":{\"type\":\"integer\",\"minimum\":0},\"duration\":{\"type\":\"number\",\"minimum\":1.5,\"maximum\":6},\"text\":" + Localized + "},\"required\":[\"at_word\",\"duration\",\"text\"]}}},\"required\":[\"notes\"]}," +
        "\"captions\":{\"type\":\"object\",\"properties\":{\"style\":{\"type\":\"string\",\"enum\":[" + Styles + "]},\"highlight_keywords\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"maxItems\":12}},\"required\":[\"style\"]}," +
        "\"audio\":{\"type\":\"object\",\"properties\":{\"sfx\":{\"type\":\"array\",\"maxItems\":8,\"items\":{\"type\":\"object\",\"properties\":{\"at_word\":{\"type\":\"integer\",\"minimum\":0},\"id\":{\"type\":\"string\"}},\"required\":[\"at_word\",\"id\"]}}," +
        "\"music_id\":{\"type\":[\"string\",\"null\"]},\"duck_music\":{\"type\":\"boolean\"}}}," +
        "\"effects\":{\"type\":\"array\",\"maxItems\":6,\"items\":{\"type\":\"object\",\"properties\":{\"at_word\":{\"type\":\"integer\",\"minimum\":0},\"type\":{\"type\":\"string\",\"enum\":[\"punch_in\",\"zoom_slow\",\"shake\",\"flash\",\"glitch\",\"blur\",\"bw\"]},\"duration\":{\"type\":\"number\",\"minimum\":0.2,\"maximum\":4}},\"required\":[\"at_word\",\"type\",\"duration\"]}}," +
        "\"risk\":{\"type\":\"object\",\"properties\":{\"spoiler\":{\"type\":\"boolean\"},\"notes\":{\"type\":\"string\"}}}}," +
        "\"required\":[\"start_word\",\"end_word\",\"layout\",\"hook\",\"commentary\",\"captions\"]}");

    public static readonly JsonObject Captions = P("{\"type\":\"object\",\"properties\":{\"phrases\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{" +
        "\"id\":{\"type\":\"integer\"},\"text\":{\"type\":\"string\"},\"highlight\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}," +
        "\"translations\":{\"type\":\"object\",\"additionalProperties\":{\"type\":\"string\"},\"description\":\"Object keyed by target language code, e.g. {\\\"es\\\": \\\"...\\\"}.\"}," +
        "\"translation_highlight\":{\"type\":\"object\",\"additionalProperties\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},\"description\":\"Per target language, the words of that translation to highlight.\"}}," +
        "\"required\":[\"id\",\"text\"]}}},\"required\":[\"phrases\"]}");

    static readonly string Platform = "{\"type\":\"object\",\"properties\":{\"title\":{\"type\":\"string\"},\"description\":{\"type\":\"string\"},\"hashtags\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}},\"required\":[\"description\"]}";
    public static readonly JsonObject Metadata = P("{\"type\":\"object\",\"properties\":{\"per_language\":{\"type\":\"object\",\"description\":\"Object keyed by language code, e.g. {\\\"en\\\": {...}, \\\"es\\\": {...}}.\"," +
        "\"additionalProperties\":{\"type\":\"object\",\"properties\":{\"youtube\":" + Platform + ",\"tiktok\":" + Platform + ",\"instagram\":" + Platform + "},\"required\":[\"youtube\",\"tiktok\",\"instagram\"]}}},\"required\":[\"per_language\"]}");
}

/// <summary>Captions helpers: phrases, word-by-word cues, gap closing. Port of clipper/media/captions.py.</summary>
public static class CaptionKit
{
    static readonly Regex PunctBreak = new(@"[,.;:!?…]+[""')\]]*$"), SentenceEnd = new(@"[.!?…]+[""')\]]*$");

    public static string Norm(string token) => Regex.Replace(token.ToLowerInvariant(), @"[^\w']+", "");

    public static List<Word> ClipWords(JsonNode transcript, double start, double end)
    {
        var o = new List<Word>();
        foreach (var seg in J.Items(transcript["segments"]))
            foreach (var w in J.Items(seg["words"]))
            {
                double s = J.D(w["start"]), e = J.D(w["end"]), mid = (s + e) / 2;
                if (J.S(w["w"]) != "" && mid >= start && mid <= end)
                    o.Add(new Word(J.S(w["w"]), Math.Round(Math.Max(0, s - start), 3), Math.Round(Math.Min(end, e) - start, 3)));
            }
        return o;
    }

    public static List<List<Word>> BuildPhrases(List<Word> words, int maxWords = 9)
    {
        var phrases = new List<List<Word>>();
        var cur = new List<Word>();
        for (int i = 0; i < words.Count; i++)
        {
            cur.Add(words[i]);
            bool last = i + 1 >= words.Count;
            double pause = last ? 0 : words[i + 1].Start - words[i].End;
            if (last || SentenceEnd.IsMatch(words[i].W) || pause > 0.6 || cur.Count >= maxWords || (cur.Count >= 5 && PunctBreak.IsMatch(words[i].W))) { phrases.Add(cur); cur = []; }
        }
        return phrases;
    }

    static List<List<int>> Chunk(List<string> tokens, int maxWords = 3, int maxChars = 16)
    {
        var groups = new List<List<int>>();
        var cur = new List<int>();
        int chars = 0;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (cur.Count > 0 && (cur.Count >= maxWords || chars + tokens[i].Length + 1 > maxChars)) { groups.Add(cur); cur = []; chars = 0; }
            cur.Add(i); chars += tokens[i].Length + 1;
            if (SentenceEnd.IsMatch(tokens[i]) || (cur.Count >= 2 && PunctBreak.IsMatch(tokens[i]))) { groups.Add(cur); cur = []; chars = 0; }
        }
        if (cur.Count > 0) groups.Add(cur);
        return groups;
    }

    static JsonObject Cue(IEnumerable<(string w, double s, double e, bool hl)> words)
    {
        var list = words.ToList();
        return new JsonObject
        {
            ["start"] = list[0].s, ["end"] = list[^1].e,
            ["words"] = new JsonArray(list.Select(x => (JsonNode)new JsonObject { ["w"] = x.w, ["start"] = x.s, ["end"] = x.e, ["hl"] = x.hl }).ToArray()),
        };
    }

    /// <summary>Cues for the spoken language: each word keeps its real timing. Corrected spellings are used only when they map one-to-one.</summary>
    public static List<JsonObject> TimedCues(List<Word> phrase, List<string>? tokens, HashSet<string> highlights)
    {
        var texts = tokens != null && tokens.Count == phrase.Count ? tokens : phrase.Select(w => w.W).ToList();
        var groups = new List<List<int>>();
        foreach (var group in Chunk(texts))
        {
            var current = new List<int>();
            foreach (int i in group)
            {
                if (current.Count > 0 && phrase[i].Start - phrase[current[^1]].End > .25) { groups.Add(current); current = []; }
                current.Add(i);
            }
            if (current.Count > 0) groups.Add(current);
        }
        return groups.Select(g => Cue(g.Select(i => (texts[i], phrase[i].Start, phrase[i].End, highlights.Contains(Norm(texts[i])))))).ToList();
    }

    /// <summary>Cues for a translated phrase: its words share the phrase's time span in proportion to length.</summary>
    public static List<JsonObject> SpreadCues(string text, double start, double end, HashSet<string> highlights)
    {
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (tokens.Count == 0 || end <= start) return [];
        double total = tokens.Sum(t => t.Length + 1), t0 = start;
        var times = new List<(double a, double b)>();
        foreach (var tok in tokens) { double d = (end - start) * (tok.Length + 1) / total; times.Add((Math.Round(t0, 3), Math.Round(t0 + d, 3))); t0 += d; }
        return Chunk(tokens).Select(g => Cue(g.Select(i => (tokens[i], times[i].a, times[i].b, highlights.Contains(Norm(tokens[i])))))).ToList();
    }

    /// <summary>Keep each cue on screen briefly after its last word, never past the next cue or the clip end.</summary>
    public static List<JsonObject> CloseGaps(List<JsonObject> cues, double clipDuration, double hold = 0.3)
    {
        cues = cues.OrderBy(c => J.D(c["start"])).ToList();
        for (int i = 0; i < cues.Count; i++)
        {
            double limit = i + 1 < cues.Count ? J.D(cues[i + 1]["start"]) : clipDuration;
            double lastEnd = J.D(J.Items(cues[i]["words"]).Last()["end"]);
            cues[i]["end"] = Math.Round(Math.Max(lastEnd, Math.Min(lastEnd + hold, limit)), 3);
        }
        return cues;
    }
}

public class Agents(LlmClient llm, Action<string> log)
{
    public record Step(string Agent, string? ClipId, StepMeta Meta);

    static string Norm(string t) => Regex.Replace(t.ToLowerInvariant(), @"[\W_]+", " ").Trim();
    static JsonObject Obj(JsonNode? n) => J.Obj(n);

    // ---------- analysis ----------
    public static JsonObject AnalysisSchema(int segmentCount)
    {
        var schema = (JsonObject)J.Clone(Schemas.Analysis);
        var properties = schema["properties"]!["candidates"]!["items"]!["properties"]!;
        foreach (var key in new[] { "start_segment", "end_segment" })
        {
            properties[key]!["maximum"] = segmentCount - 1;
            properties[key]!["description"] = $"Zero-based segment INDEX, between 0 and {segmentCount - 1}. Never a time in seconds.";
        }
        return schema;
    }

    /// <summary>Keep valid independent proposals. Never reinterpret seconds as indexes or clamp a bad range.</summary>
    public static string? ValidateAnalysis(JsonObject proposal, List<JsonNode> segments, Action<string> log)
    {
        var candidates = J.Items(proposal["candidates"]).ToList();
        if (candidates.Count == 0) return "No candidates. Propose at least one using the listed segment indexes.";
        var accepted = new List<(JsonNode candidate, int start, int end)>();
        var errors = new List<string>();
        foreach (var (candidate, index) in candidates.Select((c, i) => (c, i)).OrderByDescending(p => p.c is JsonObject ? J.D(p.c["score"]) : -1))
        {
            if (candidate is not JsonObject) { errors.Add($"candidates[{index}]: must be an object."); continue; }
            double a = J.D(candidate["start_segment"], -1), b = J.D(candidate["end_segment"], -1);
            string? error = null;
            if (a < 0 || a != Math.Truncate(a) || b != Math.Truncate(b) || a > b || b >= segments.Count)
                error = $"segment range {a}..{b} is invalid: use integer INDEXES 0..{segments.Count - 1}, NOT seconds";
            else
            {
                double duration = J.D(segments[(int)b]["end"]) - J.D(segments[(int)a]["start"]);
                if (duration < Limits.ClipMin || duration > Limits.ClipMax)
                    error = $"lasts {duration:0.0}s; must last {Limits.ClipMin:0}-{Limits.ClipMax:0}s";
                else if (accepted.Any(c => a <= c.end && b >= c.start)) error = "overlaps a higher-scoring valid candidate";
            }
            if (error == null && (J.S(candidate["category"]).Trim() == "" || J.S(candidate["reason"]).Trim() == "" ||
                J.D(candidate["score"], -1) is < 0 or > 100)) error = "missing or invalid score, category or reason";
            if (error != null) errors.Add($"candidates[{index}]: {error}.");
            else if (accepted.Count < Limits.MaxClips) accepted.Add((candidate, (int)a, (int)b));
        }
        if (accepted.Count == 0) return string.Join("\n", errors.Take(12)) + "\nSubmit at least one valid candidate; copy indexes from the segment_id fields.";
        if (errors.Count > 0)
        {
            log($"    analysis: se conservan {accepted.Count} propuestas válidas y se omiten {errors.Count} no válidas.");
            foreach (var error in errors) log("      " + error);
        }
        proposal["candidates"] = new JsonArray(accepted.Select(c => J.Clone(c.candidate)).ToArray());
        return null;
    }

    public async Task<(List<JsonObject> candidates, StepMeta meta)> AnalyzeAsync(JsonNode source, JsonNode transcript, List<double> energy, List<double> cuts, string? modelOverride = null)
    {
        var segs = J.Items(transcript["segments"]).ToList();
        if (segs.Count == 0) throw new LlmException("El video no tiene contenido que analizar.");
        double duration = J.D(source["duration"]);
        int target = Math.Max(1, Math.Min(Limits.MaxClips, (int)(duration / (Limits.ClipMin * 2))));
        bool speech = segs.Any(s => J.S(s["text"]) != "");
        var lines = segs.Select((s, index) =>
        {
            var (mean, peak, c) = Signals.Features(s, energy, cuts);
            return $"segment_id={index} | time_seconds={J.F(J.D(s["start"]), "0.0")}..{J.F(J.D(s["end"]), "0.0")} | energy {J.F(mean, "0.00")}/{J.F(peak, "0.00")} | cuts {c} | {(J.S(s["text"]) is { Length: > 0 } t ? t : "(no speech)")}";
        });
        var user = "# Video\n" +
            $"Duration: {duration:0}s. Spoken language: {J.S(transcript["language"])}. Speech present: {(speech ? "yes" : "no")}.\n\n" +
            "# Constraints\n" +
            $"- Propose up to {target} candidates, best first. Fewer is fine if the video does not have that many good moments.\n" +
            $"- A candidate spans start_segment..end_segment inclusive and must last between {Limits.ClipMin:0} and {Limits.ClipMax:0} seconds.\n" +
            $"- There are exactly {segs.Count} segments. start_segment and end_segment MUST be integer segment_id values from 0 to {segs.Count - 1}. The last index is {segs.Count - 1}, even though the video lasts {duration:0} seconds. Never put time_seconds values in these fields.\n" +
            "- Candidates must not overlap.\n\n" +
            "# Segments\nFormat: segment_id=INDEX | time_seconds=START..END | energy mean/peak (0-1) | hard cuts | text\n" + string.Join("\n", lines);

        var (data, meta) = await llm.DecideAsync("analysis", "fast", LlmClient.Prompt("analysis"), user, AnalysisSchema(segs.Count),
            o => ValidateAnalysis(o, segs, log), log, maxTokens: 6000, modelOverride: modelOverride);
        var ranked = J.Items(data["candidates"]).OrderByDescending(c => J.D(c["score"])).ToList();
        var result = ranked.Select((c, i) =>
        {
            int a = (int)J.D(c["start_segment"]), b = (int)J.D(c["end_segment"]);
            return new JsonObject
            {
                ["id"] = $"cand{i:00}", ["start_segment"] = a, ["end_segment"] = b, ["start"] = J.D(segs[a]["start"]), ["end"] = J.D(segs[b]["end"]),
                ["score"] = J.D(c["score"]), ["category"] = J.S(c["category"]), ["reason"] = J.S(c["reason"]), ["hook_idea"] = J.S(c["hook_idea"]),
            };
        }).ToList();
        return (result, meta);
    }

    // ---------- editing ----------
    public class EditInput
    {
        public required string ClipId { get; init; }
        public required JsonNode Source { get; init; }
        public required JsonNode Candidate { get; init; }
        public required List<Word> Words { get; init; }
        public required (int a, int b) Core { get; init; }
        public required string SourceLanguage { get; init; }
        public required List<string> Languages { get; init; }
        public required List<string> RecentHooks { get; init; }
        public required Dictionary<string, JsonNode> Library { get; init; }
        public List<string> Frames { get; init; } = [];
        public string LayoutChoice { get; init; } = "auto";
        public string CaptionChoice { get; init; } = "auto";
    }

    public static EditInput PrepareEdit(string clipId, JsonNode source, JsonNode candidate, JsonNode transcript, List<string> languages, List<string> recentHooks,
        string layoutChoice, string captionChoice, List<string> frames)
    {
        var segs = J.Items(transcript["segments"]).ToList();
        int a = Math.Max(0, (int)J.D(candidate["start_segment"]) - 1), b = Math.Min(segs.Count - 1, (int)J.D(candidate["end_segment"]) + 1);
        var words = new List<Word>();
        int cs = 0, ce = 0;
        for (int i = a; i <= b; i++)
        {
            if (i == (int)J.D(candidate["start_segment"])) cs = words.Count;
            words.AddRange(J.Items(segs[i]["words"]).Select(w => new Word(J.S(w["w"]), J.D(w["start"]), J.D(w["end"]))));
            if (i == (int)J.D(candidate["end_segment"])) ce = words.Count - 1;
        }
        return new EditInput
        {
            ClipId = clipId, Source = source, Candidate = candidate, Words = words, Core = (cs, ce), SourceLanguage = J.S(transcript["language"]), Languages = languages,
            RecentHooks = recentHooks, Library = Renderer.Library(), Frames = frames, LayoutChoice = layoutChoice, CaptionChoice = captionChoice,
        };
    }

    static (double start, double end) ClipBounds(EditInput inp, int sw, int ew)
    {
        var w = inp.Words;
        double start = w[sw].Start - Limits.LeadIn;
        double floor = sw > 0 ? w[sw - 1].End + 0.02 : 0;
        start = floor <= w[sw].Start ? Math.Max(0, Math.Max(floor, start)) : Math.Max(0, start);
        double end = w[ew].End + Limits.Tail;
        if (ew + 1 < w.Count) end = Math.Min(end, Math.Max(w[ew].End, w[ew + 1].Start - 0.02));
        end = Math.Min(end, J.D(inp.Source["duration"]));
        return (Math.Round(start, 3), Math.Round(end, 3));
    }

    public async Task<(JsonObject edl, StepMeta meta)> EditAsync(EditInput inp, string tier, string? modelOverride)
    {
        var c = inp.Candidate;
        var wl = new List<string>();
        for (int i = 0; i < inp.Words.Count; i += 10)
            wl.Add(string.Join("  ", Enumerable.Range(i, Math.Min(10, inp.Words.Count - i)).Select(k => $"{k}:{(inp.Words[k].W == "" ? "·" : inp.Words[k].W)}@{J.F(inp.Words[k].Start, "0.0")}")));
        var sfx = inp.Library.Values.Select(e => $"- {J.S(e["id"])} ({J.S(e["kind"])}, {J.D(e["duration"]):0.0}s): {J.S(e["description"])}");
        var fixedChoices = new List<string>();
        if (inp.LayoutChoice != "auto") fixedChoices.Add($"- The owner fixed layout = \"{inp.LayoutChoice}\" for this job. Use it.");
        if (inp.CaptionChoice != "auto") fixedChoices.Add($"- The owner fixed captions.style = \"{inp.CaptionChoice}\" for this job. Use it.");
        var user = string.Join("\n", (string[])
        [
            "# Source",
            $"{J.D(inp.Source["width"])}x{J.D(inp.Source["height"])}, spoken language: {inp.SourceLanguage}." + (inp.Frames.Count > 0 ? $" {inp.Frames.Count} frames from this moment are attached, in order." : ""),
            "\n# Candidate picked by the Analysis subagent",
            $"Category: {J.S(c["category"])}. Why: {J.S(c["reason"])} Hook idea: {(J.S(c["hook_idea"]) is { Length: > 0 } h ? h : "(none)")}",
            $"It covers words {inp.Core.a}..{inp.Core.b}. You may trim it, or extend it into the context words around it.",
            "\n# Words (index:word@second-in-source; '·' marks a second with no speech)",
            string.Join("\n", wl),
            "\n# Requirements",
            $"- Final clip length: {Limits.ClipMin * 0.6:0}-{Limits.ClipMax:0} seconds.",
            $"- Write every text field in each of these languages: {string.Join(", ", inp.Languages)}.",
            $"- Hook: at most {Limits.HookMax} characters. Each commentary note: at most {Limits.NoteMax} characters.",
            $"- Commentary: at least one note and {Limits.MinCommentaryWords}+ words in total per language. Notes start after the hook ends and do not overlap.",
            "- commentary.mode must be \"onscreen\" (voice-over is not enabled yet).",
            .. fixedChoices,
            "- layout: \"gameplay_blur_bg\" keeps the whole 16:9 picture over a blurred background; \"center_crop\" fills the frame and crops the sides (crop_x 0=left, 1=right).",
            "\n# Sound library (use only these ids; music_id may be null)",
            sfx.Any() ? string.Join("\n", sfx) : "(empty)",
            "\n# Hooks used recently on this channel (do not repeat or closely imitate)",
            inp.RecentHooks.Count > 0 ? string.Join("\n", inp.RecentHooks.Select(h => "- " + h)) : "(none yet)",
            "\n# Visual references (original source image coordinates)",
            string.Join("\n", inp.Frames.Select((_, i) => $"frame_index={i}, source_time_seconds={J.F(J.D(c["start"]) + (J.D(c["end"]) - J.D(c["start"])) * (i + .5) / inp.Frames.Count)}")),
        ]);

        string? Check(JsonObject d)
        {
            int n = inp.Words.Count, sw = (int)J.D(d["start_word"], -1), ew = (int)J.D(d["end_word"], -1);
            if (ew >= n || sw < 0 || sw >= ew) return $"start_word/end_word {sw}..{ew} invalid (valid indexes: 0..{n - 1}, start < end).";
            var (s0, e0) = ClipBounds(inp, sw, ew);
            double dur = e0 - s0, lo = Limits.ClipMin * 0.6;
            if (dur < lo || dur > Limits.ClipMax) return $"Clip would last {dur:0.0}s; it must last {lo:0}-{Limits.ClipMax:0}s. Change start_word/end_word.";
            if (J.S(d["commentary"]?["mode"], "onscreen") != "onscreen") return "commentary.mode must be \"onscreen\".";
            var notes = J.Items(d["commentary"]?["notes"]).ToList();
            if (notes.Count == 0) return "commentary.notes is empty. Add at least one note with original commentary.";
            string? NeedLangs(string label, JsonNode? text, int limit)
            {
                foreach (var lang in inp.Languages)
                {
                    var v = J.S(text?[lang]).Trim();
                    if (v == "") return $"{label}: missing text for language '{lang}'.";
                    if (v.Length > limit) return $"{label}[{lang}] has {v.Length} characters; the limit is {limit}.";
                }
                return null;
            }
            if (NeedLangs("hook.text", d["hook"]?["text"], Limits.HookMax) is { } e1) return e1;
            for (int i = 0; i < notes.Count; i++)
            {
                if (NeedLangs($"commentary.notes[{i}].text", notes[i]["text"], Limits.NoteMax) is { } e2) return e2;
                int at = (int)J.D(notes[i]["at_word"], -1);
                if (at < sw || at > ew) return $"commentary.notes[{i}].at_word={at} is outside the clip ({sw}..{ew}).";
            }
            foreach (var lang in inp.Languages)
            {
                int total = notes.Sum(nn => J.S(nn["text"]?[lang]).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);
                if (total < Limits.MinCommentaryWords) return $"Commentary in '{lang}' has {total} words; write at least {Limits.MinCommentaryWords} of original commentary.";
                var hook = Norm(J.S(d["hook"]?["text"]?[lang]));
                if (inp.RecentHooks.Any(h => Norm(h) == hook)) return $"hook.text[{lang}] repeats a recent hook. Write a different one.";
            }
            var sfxList = J.Items(d["audio"]?["sfx"]).ToList();
            for (int i = 0; i < sfxList.Count; i++)
            {
                var id = J.S(sfxList[i]["id"]);
                if (!inp.Library.TryGetValue(id, out var le) || J.S(le["kind"]) != "sfx") return $"audio.sfx[{i}].id='{id}' is not a sound effect in the library ({string.Join(", ", inp.Library.Where(kv => J.S(kv.Value["kind"]) == "sfx").Select(kv => kv.Key))}).";
                int at = (int)J.D(sfxList[i]["at_word"], -1);
                if (at < sw || at > ew) return $"audio.sfx[{i}].at_word={at} is outside the clip.";
            }
            var music = J.S(d["audio"]?["music_id"]);
            if (music != "" && (!inp.Library.TryGetValue(music, out var me) || J.S(me["kind"]) != "music")) return $"audio.music_id='{music}' is not a music track in the library. Use null if there is none.";
            var fxList = J.Items(d["effects"]).ToList();
            for (int i = 0; i < fxList.Count; i++)
            {
                int at = (int)J.D(fxList[i]["at_word"], -1);
                if (at < sw || at > ew) return $"effects[{i}].at_word={at} is outside the clip.";
            }
            if (CreativeEdits.Validate(inp, d, sw, ew) is { } creativeError) return creativeError;
            var edl = ToEdl(inp, d);
            foreach (var note in J.Items(edl["commentary"]!["notes"]))
            {
                if (J.D(note["t"]) < J.D(edl["hook"]!["duration"])) return $"A commentary note starts at {J.D(note["t"]):0.0}s, before the hook ends ({J.D(edl["hook"]!["duration"]):0.0}s). Move its at_word later.";
                if (J.D(note["duration"]) < 1.5) return "Commentary notes are too close together or too close to the end: each needs 1.5s on screen. Space out their at_word values.";
            }
            return null;
        }
        var images = inp.Frames;
        var (data, meta) = await llm.DecideAsync("editing", tier, LlmClient.Prompt("editing") + "\n\n" + LlmClient.Prompt("editing_desktop"), user, CreativeEdits.Schema(), Check, log, images, 6000, modelOverride);
        return (ToEdl(inp, data), meta);
    }

    /// <summary>Word indexes -> seconds, with the owner's fixed layout and caption style applied.</summary>
    public static JsonObject ToEdl(EditInput inp, JsonObject d)
    {
        int sw = (int)J.D(d["start_word"]), ew = (int)J.D(d["end_word"]);
        var (start, end) = ClipBounds(inp, sw, ew);
        double dur = end - start;
        double T(int i) => Math.Round(Math.Min(Math.Max(0, inp.Words[Math.Min(i, inp.Words.Count - 1)].Start - start), Math.Max(0, dur - 0.1)), 3);
        var raw = J.Items(d["commentary"]?["notes"]).Select(n => (t: T((int)J.D(n["at_word"])), n)).OrderBy(x => x.t).ToList();
        var notes = new JsonArray();
        for (int i = 0; i < raw.Count; i++)
        {
            double limit = i + 1 < raw.Count ? raw[i + 1].t - 0.2 : dur - 0.1;
            notes.Add(new JsonObject { ["t"] = raw[i].t, ["duration"] = Math.Round(Math.Max(0, Math.Min(J.D(raw[i].n["duration"], 3), limit - raw[i].t)), 3), ["text"] = J.Clone(raw[i].n["text"]!) });
        }
        var effects = new JsonArray(J.Items(d["effects"]).Select(fx => (t: T((int)J.D(fx["at_word"])), fx)).OrderBy(x => x.t)
            .Select(x => (JsonNode)new JsonObject { ["t"] = x.t, ["type"] = J.S(x.fx["type"], "punch_in"), ["duration"] = J.Num(x.fx["duration"], 0.8, 0.2, 4), ["intensity"] = 1.0 }).ToArray());
        var sfx = new JsonArray(J.Items(d["audio"]?["sfx"]).Select(s => (t: T((int)J.D(s["at_word"])), s)).OrderBy(x => x.t)
            .Select(x => (JsonNode)new JsonObject { ["t"] = x.t, ["id"] = J.S(x.s["id"]), ["gain"] = 1.0 }).ToArray());
        var music = J.S(d["audio"]?["music_id"]);
        var style = J.S(d["captions"]?["style"], "bold");
        var edl = EditorSvc.NewEdl(inp.ClipId, (JsonObject)inp.Source, start, end, inp.SourceLanguage);
        edl["languages"] = new JsonArray(inp.Languages.Select(l => (JsonNode)l).ToArray());
        edl["layout"] = inp.LayoutChoice != "auto" ? inp.LayoutChoice : J.S(d["layout"], "gameplay_blur_bg");
        edl["crop_x"] = J.Num(d["crop_x"], 0.5, 0, 1);
        edl["hook"] = new JsonObject { ["text"] = J.Clone(d["hook"]!["text"]!), ["duration"] = Math.Min(J.D(d["hook"]!["duration"], 2), Math.Max(1, dur / 4)) };
        edl["commentary"] = new JsonObject { ["mode"] = "onscreen", ["notes"] = notes };
        edl["captions"] = new JsonObject
        {
            ["style"] = inp.CaptionChoice != "auto" ? inp.CaptionChoice : style,
            ["highlight_keywords"] = new JsonArray(J.Items(d["captions"]?["highlight_keywords"]).Select(k => (JsonNode)J.S(k)).ToArray()), ["y"] = null, ["scale"] = 1.0,
        };
        edl["audio"] = new JsonObject
        {
            ["normalize_lufs"] = Limits.TargetLufs, ["music_id"] = music == "" ? null : music, ["duck_music"] = J.B(d["audio"]?["duck_music"], true), ["sfx"] = sfx,
            ["voice_gain"] = 1.0, ["music_gain"] = 1.0, ["voice_enhance"] = false,
        };
        edl["effects"] = effects;
        if (d["editorial"] != null) CreativeEdits.Apply(edl, d, T);
        Framing.Apply(inp, d, edl);
        edl["risk"] = new JsonObject { ["spoiler"] = J.B(d["risk"]?["spoiler"]), ["rights_ok"] = true, ["notes"] = J.S(d["risk"]?["notes"]) };
        return edl;
    }

    // ---------- captions ----------
    public async Task<(JsonObject tracks, StepMeta? meta)> CaptionsAsync(JsonObject edl, JsonNode transcript, string tier, string? modelOverride)
    {
        double s0 = J.D(edl["source"]!["start"]), s1 = J.D(edl["source"]!["end"]), dur = s1 - s0;
        var langs = J.Items(edl["languages"]).Select(n => J.S(n)).ToList();
        var src = J.S(edl["source_language"]);
        var style = J.S(edl["captions"]!["style"]);
        var words = CaptionKit.ClipWords(transcript, s0, s1);
        var phrases = CaptionKit.BuildPhrases(words);
        var tracks = new JsonObject();
        if (phrases.Count == 0)
        {
            foreach (var l in langs) tracks[l] = new JsonObject { ["lang"] = l, ["style"] = style, ["word_timed"] = false, ["cues"] = new JsonArray() };
            return (tracks, null);
        }
        var targets = langs.Where(l => l != src).ToList();
        var keywords = J.Items(edl["captions"]!["highlight_keywords"]).Select(k => J.S(k)).ToList();
        var hookText = J.S(edl["hook"]!["text"]![src]) is { Length: > 0 } ht ? ht : J.S((edl["hook"]!["text"] as JsonObject)!.First().Value);
        var user = $"# Clip\nSpoken language: {src}. Hook: {hookText}\n" +
            $"Keywords the editor wants highlighted: {(keywords.Count > 0 ? string.Join(", ", keywords) : "(none)")}\n\n" +
            $"# Translate into\n{(targets.Count > 0 ? string.Join(", ", targets) : "(no translation needed: leave translations empty)")}\n\n" +
            "# Phrases (automatic transcript; may contain recognition errors)\n" + string.Join("\n", phrases.Select((p, i) => $"[{i}] {string.Join(' ', p.Select(w => w.W))}"));

        string? Check(JsonObject o)
        {
            var ids = J.Items(o["phrases"]).Select(p => (int)J.D(p["id"], -1)).OrderBy(x => x).ToList();
            if (!ids.SequenceEqual(Enumerable.Range(0, phrases.Count))) return $"phrases must contain each id from 0 to {phrases.Count - 1} exactly once; got [{string.Join(", ", ids)}].";
            foreach (var p in J.Items(o["phrases"]))
            {
                if (J.S(p["text"]).Trim() == "") return $"phrases[{J.D(p["id"])}].text is empty.";
                foreach (var l in targets) if (J.S(p["translations"]?[l]).Trim() == "") return $"phrases[{J.D(p["id"])}].translations is missing language '{l}'.";
            }
            return null;
        }
        var (data, meta) = await llm.DecideAsync("captions", tier, LlmClient.Prompt("captions"), user, Schemas.Captions, Check, log, maxTokens: 8000, modelOverride: modelOverride);
        var byId = J.Items(data["phrases"]).ToDictionary(p => (int)J.D(p["id"]));
        var kw = keywords.Select(CaptionKit.Norm).ToHashSet();
        foreach (var lang in langs)
        {
            var cues = new List<JsonObject>();
            bool timed = lang == src;
            for (int i = 0; i < phrases.Count; i++)
            {
                var p = byId[i];
                var hl = kw.Union(J.Items(p["highlight"]).Select(h => CaptionKit.Norm(J.S(h)))).ToHashSet();
                var tr = J.S(p["translations"]?[lang]).Trim();
                if (timed || tr == "") cues.AddRange(CaptionKit.TimedCues(phrases[i], J.S(p["text"]).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList(), hl));
                else cues.AddRange(CaptionKit.SpreadCues(tr, phrases[i][0].Start, phrases[i][^1].End, J.Items(p["translation_highlight"]?[lang]).Select(h => CaptionKit.Norm(J.S(h))).ToHashSet()));
            }
            tracks[lang] = new JsonObject
            {
                ["lang"] = lang, ["style"] = style, ["word_timed"] = timed,
                ["cues"] = new JsonArray(CaptionKit.CloseGaps(cues, dur).Select(c => (JsonNode)c).ToArray()),
            };
        }
        return (tracks, meta);
    }

    // ---------- metadata ----------
    static readonly string[] Platforms = ["youtube", "tiktok", "instagram"];
    static readonly Dictionary<string, string> CreditLabel = new() { ["en"] = "Credit", ["es"] = "Créditos" };

    public static List<string> SpoilerTerms()
    {
        var f = Path.Combine(Paths.App, "config", "spoilers.txt");
        return File.Exists(f) ? File.ReadAllLines(f).Select(l => l.Trim().ToLowerInvariant()).Where(l => l != "" && !l.StartsWith('#')).ToList() : [];
    }

    public static List<string> FindTerms(string text, List<string> terms) =>
        terms.Where(t => Regex.IsMatch(text.ToLowerInvariant(), $@"(?<!\w){Regex.Escape(t)}(?!\w)")).ToList();

    public async Task<(JsonObject meta, StepMeta m)> MetadataAsync(JsonObject edl, JsonNode transcript, string? credit, string tier, string? modelOverride)
    {
        double s0 = J.D(edl["source"]!["start"]), s1 = J.D(edl["source"]!["end"]);
        var langs = J.Items(edl["languages"]).Select(n => J.S(n)).ToList();
        var spoken = string.Join(' ', CaptionKit.ClipWords(transcript, s0, s1).Select(w => w.W));
        var terms = SpoilerTerms();
        int reserve = credit != null ? credit.Length + 20 : 0;
        var notes = string.Join("\n", J.Items(edl["commentary"]!["notes"]).Select(n => $"- {string.Join(" / ", ((JsonObject)n["text"]!).Select(kv => J.S(kv.Value)))}"));
        var user = $"# Clip ({s1 - s0:0}s)\nHook: {string.Join(" / ", ((JsonObject)edl["hook"]!["text"]!).Select(kv => J.S(kv.Value)))}\nCommentary notes:\n{notes}\n\n" +
            $"What is said in the clip ({J.S(edl["source_language"])}):\n{(spoken == "" ? "(no speech)" : spoken)}\n\n# Write for languages\n{string.Join(", ", langs)}\n\n# Limits\n" +
            $"- youtube.title: required, at most {Limits.TitleMax} characters. tiktok.title and instagram.title: leave empty.\n" +
            $"- description: at most {Limits.DescriptionMax - reserve} characters on every platform. Do not put hashtags inside it.\n- hashtags: at most {Limits.HashtagsMax} per platform.\n" +
            (credit != null ? "- A credit line for the original creator is appended automatically. Do not write one.\n" : "");

        string? Check(JsonObject o)
        {
            foreach (var lang in langs)
            {
                var lm = o["per_language"]?[lang];
                if (lm == null) return $"per_language is missing '{lang}'.";
                var title = J.S(lm["youtube"]?["title"]);
                if (title.Trim() == "") return $"per_language.{lang}.youtube.title is empty.";
                if (title.Length > Limits.TitleMax) return $"per_language.{lang}.youtube.title has {title.Length} characters; the limit is {Limits.TitleMax}.";
                foreach (var p in Platforms)
                {
                    var pm = lm[p];
                    var desc = J.S(pm?["description"]);
                    if (desc.Trim() == "") return $"per_language.{lang}.{p}.description is empty.";
                    if (desc.Length > Limits.DescriptionMax - reserve) return $"per_language.{lang}.{p}.description has {desc.Length} characters; the limit is {Limits.DescriptionMax - reserve}.";
                    var hits = FindTerms($"{J.S(pm?["title"])} {desc} {string.Join(' ', J.Items(pm?["hashtags"]).Select(h => J.S(h)))}", terms);
                    if (hits.Count > 0) return $"per_language.{lang}.{p} contains spoiler-list terms [{string.Join(", ", hits)}]. Rewrite without revealing them.";
                }
            }
            return null;
        }
        var (data, meta) = await llm.DecideAsync("metadata", tier, LlmClient.Prompt("metadata"), user, Schemas.Metadata, Check, log, maxTokens: 4000, modelOverride: modelOverride);
        // Deterministic clean-up: hashtag format and the credit line are not left to the model.
        foreach (var lang in langs)
            foreach (var p in Platforms)
            {
                var pm = (JsonObject)data["per_language"]![lang]![p]!;
                var tags = new List<string>();
                foreach (var tag in J.Items(pm["hashtags"]).Select(h => "#" + Regex.Replace(J.S(h).TrimStart('#'), @"[^\w]", "")))
                    if (tag.Length > 1 && !tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) tags.Add(tag);
                pm["hashtags"] = new JsonArray(tags.Take(Limits.HashtagsMax).Select(t => (JsonNode)t).ToArray());
                pm["title"] = J.S(pm["title"]);
                if (credit != null) pm["description"] = $"{J.S(pm["description"]).TrimEnd()}\n\n{CreditLabel.GetValueOrDefault(lang, "Credit")}: {credit}";
            }
        return ((JsonObject)data, meta);
    }
}
