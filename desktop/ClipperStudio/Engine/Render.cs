// Renderer: executes an EDL with FFmpeg. Port of clipper/media/render.py and audio.py, with the
// video encoded (and decoded) on whatever graphics hardware answers: NVIDIA, AMD, Intel or integrated
// graphics, with the CPU as the last resort (Gpu.cs decides).
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ClipperStudio.Engine;

public class RenderSettings
{
    public string? OutputDirectory { get; set; }
    public bool SetupCompleted { get; set; }
    public string TranscriptionMode { get; set; } = "fast";
    public string Encoder { get; set; } = "auto";      // auto | one of Hw.Known (NVENC, AMF, Quick Sync, Media Foundation, libx264)
    public int Fps { get; set; } = 30;                  // 30 | 60
    public int Quality { get; set; } = 19;              // constant quality: lower is better (15-28)
    public bool GpuDecode { get; set; } = true;
    public string Device { get; set; } = "auto";
    public double TruePeak { get; set; } = -1.0;

    /// <summary>The chosen encoder if it works on this PC (settings can come from another machine), else the best one found.</summary>
    public string ResolvedEncoder => Device == "cpu" ? "libx264" : Device.StartsWith("dxgi:") ? Hw.DeviceEncoder(Device) : Encoder != "auto" && Hw.Known.Contains(Encoder) && Hw.Works(Encoder) ? Encoder : Ff.Encoder;

    public List<string> EncoderArgs() => Hw.EncoderArgs(ResolvedEncoder, Quality);
}

public record RenderReport(string Lang, string Path, int Width, int Height, double Duration, double? Lufs, double? TruePeak, bool Normalized, string Encoder, double Seconds);

public static class Renderer
{
    const string Fmt = "aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo";
    static string N(double v, string f = "0.000") => J.F(v, f);

    // ---------- picture ----------
    /// <summary>Camera motion as zoompan expressions (zoom, dx, dy); null without punch-in, slow zoom or shake.</summary>
    public static (string z, string dx, string dy)? Motion(JsonNode edl)
    {
        var fx = Studio.Fx;
        double ramp = J.D(fx["ramp"]);
        List<string> punch = [], slow = [], shake = [];
        foreach (var e in J.Items(edl["effects"]))
        {
            double a = J.D(e["t"]), d = J.D(e["duration"]), b = a + d;
            string k = N(J.D(e["intensity"], 1), "0.00");
            switch (J.S(e["type"]))
            {
                case "punch_in": punch.Add($"{k}*clip((it-{N(a)})/{N(ramp, "0.###")},0,1)*clip(({N(b)}-it)/{N(ramp, "0.###")},0,1)"); break;
                case "zoom_slow": slow.Add($"{k}*between(it,{N(a)},{N(b)})*(it-{N(a)})/{N(d)}"); break;
                case "shake": shake.Add($"{k}*between(it,{N(a)},{N(b)})"); break;
            }
        }
        if (punch.Count + slow.Count + shake.Count == 0) return null;
        var z = new List<string> { "1" };
        if (punch.Count > 0) z.Add($"{N(J.D(fx["zoom"]), "0.###")}*min(2,{string.Join('+', punch)})");
        if (slow.Count > 0) z.Add($"{N(J.D(fx["slow"]), "0.###")}*min(2,{string.Join('+', slow)})");
        string dx = "0", dy = "0";
        if (shake.Count > 0)
        {
            var env = $"min(2,{string.Join('+', shake)})";
            z.Add($"{N(J.D(fx["shake_zoom"]), "0.###")}*{env}");
            double w = 2 * Math.PI * J.D(fx["shake_hz"]);
            dx = $"{N(J.D(fx["shake_px"]), "0.###")}*{env}*sin({N(w)}*it)";
            dy = $"{N(J.D(fx["shake_px"]), "0.###")}*{env}*cos({N(w * 1.3)}*it)";
        }
        return (string.Join('+', z), dx, dy);
    }

    static string Zoompan((string z, string dx, string dy) m, int w, int h, int fps) =>
        $"zoompan=z='{m.z}':x='iw/2-(iw/zoom/2)+({m.dx})*(iw/zoom)/ow':y='ih/2-(ih/zoom/2)+({m.dy})*(ih/zoom)/oh':d=1:s={w}x{h}:fps={fps}";

    public static List<string> TimedFilters(JsonNode edl)
    {
        var fx = Studio.Fx;
        var o = new List<string>();
        foreach (var e in J.Items(edl["effects"]))
        {
            double a = J.D(e["t"]), k = J.D(e["intensity"], 1);
            var on = $"enable='between(t,{N(a)},{N(a + J.D(e["duration"]))})'";
            switch (J.S(e["type"]))
            {
                case "blur": o.Add($"gblur=sigma={N(J.D(fx["blur_sigma"]) * k, "0.0")}:{on}"); break;
                case "bw": o.Add($"hue=s=0:{on}"); break;
                case "glitch": int n = Math.Max(2, (int)Math.Round(J.D(fx["glitch_px"]) * k)); o.Add($"rgbashift=rh={-n}:bh={n}:{on}"); break;
            }
        }
        return o;
    }

    public static string VideoGraph(JsonNode edl, JsonNode src, int fps, string assPath, int? vignetteInput, string? mediaGraph = null)
    {
        using var safeScope = SafeArea.Use(edl);
        int W = Ass.W, H = Ass.H;
        var g = Ass.Geom(edl, (int)J.D(src["width"]), (int)J.D(src["height"]));
        var m = Motion(edl);
        var graph = new List<string>();
        var faces = J.Items(edl["effects"]).Where(e => J.S(e["type"]) == "face_focus").Take(3).ToList();
        string input = "[0:v]";
        if (faces.Count > 0)
        {
            graph.Add($"[0:v]split={faces.Count + 1}[main]" + string.Concat(Enumerable.Range(0, faces.Count).Select(i => $"[faceSrc{i}]")));
            input = "[main]";
        }
        if (J.S(edl["layout"]) == "gameplay_blur_bg")
        {
            var fg = m is { } mm ? $"scale={g.FgW * 2}:{g.FgH * 2}:flags=bicubic,{Zoompan(mm, g.FgW, g.FgH, fps)}" : $"scale={g.FgW}:{g.FgH}:flags=lanczos";
            graph.Add($"{input}fps={fps},setsar=1,split=2[bgs][fgs]");
            graph.Add($"[bgs]scale={W / 4}:{H / 4}:force_original_aspect_ratio=increase,crop={W / 4}:{H / 4},gblur=sigma=9,scale={W}:{H}:flags=bilinear,eq=brightness=-0.16:saturation=0.85[bg]");
            graph.Add($"[fgs]{fg}[fg]");
            graph.Add($"[bg][fg]overlay={g.FgX}:{g.FgY}[v0]");
        }
        else
        {
            var crop = $"scale={W}:{H}:force_original_aspect_ratio=increase,crop={W}:{H}:x='(iw-{W})*({Framing.CropExpression(edl, "x")})':y='(ih-{H})*({Framing.CropExpression(edl, "y")})'";
            if (m is { } mm) crop += $",scale={W * 3 / 2}:{H * 3 / 2}:flags=bicubic,{Zoompan(mm, W, H, fps)}";
            graph.Add($"{input}fps={fps},setsar=1,{crop}[v0]");
        }
        string picture = "v0";
        for (int i = 0; i < faces.Count; i++)
        {
            var face = faces[i];
            if (Ai.CreativeEdits.RegionError(face["region"]) is { } problem) throw new EditException(problem);
            var r = face["region"]!;
            int sw = (int)J.D(src["width"]), sh = (int)J.D(src["height"]);
            int cw = Math.Clamp((int)(J.D(r["w"]) * sw) / 2 * 2, 2, sw / 2 * 2);
            int ch = Math.Clamp((int)(J.D(r["h"]) * sh) / 2 * 2, 2, sh / 2 * 2);
            int x = Math.Clamp((int)(J.D(r["x"]) * sw) / 2 * 2, 0, sw - cw);
            int y = Math.Clamp((int)(J.D(r["y"]) * sh) / 2 * 2, 0, sh - ch);
            double a = J.D(face["t"]), b = Math.Min(Duration(edl), a + J.D(face["duration"]));
            var box = Framing.Face(face, src);
            graph.Add($"[faceSrc{i}]fps={fps},setsar=1,crop={cw}:{ch}:{x}:{y},scale={box.InnerWidth}:{box.InnerHeight},format=rgba,pad={box.Width}:{box.Height}:8:8:color=0xFFE500,fade=t=in:st={N(a)}:d=0.12:alpha=1,fade=t=out:st={N(Math.Max(a, b - .12))}:d=0.12:alpha=1[face{i}]");
            graph.Add($"[{picture}][face{i}]overlay=x='{box.X}-80*(1-clip((t-{N(a)})/0.18,0,1))':y={box.Y}:enable='between(t,{N(a)},{N(b)})':eof_action=pass[focus{i}]");
            picture = $"focus{i}";
        }
        if (mediaGraph != null) { graph.Add(mediaGraph.Replace("[v0]", $"[{picture}]")); picture = "mediaPicture"; }
        var grade = Studio.ColorFilter(edl["color"]);
        List<string> pre = grade != null ? [grade] : [];
        var tail = TimedFilters(edl);
        tail.Add($"ass='{Ff.FilterPath(assPath)}':fontsdir='{Ff.FilterPath(Paths.Fonts)}'");
        double dur = Duration(edl);
        var tr = edl["transitions"];
        if (J.S(tr?["intro"]) == "fade") tail.Add($"fade=t=in:st=0:d={N(Math.Min(J.D(tr!["intro_d"], 0.4), dur))}");
        if (J.S(tr?["outro"]) == "fade") { double d = Math.Min(J.D(tr!["outro_d"], 0.4), dur); tail.Add($"fade=t=out:st={N(dur - d)}:d={N(d)}"); }
        tail.Add("format=yuv420p");
        double vig = Studio.ColorParams(edl["color"])["vignette"];
        if (vig > 0 && vignetteInput is int vi)
        {
            graph.Add($"[{picture}]{(pre.Count > 0 ? string.Join(',', pre) : "null")}[vc]");
            graph.Add($"[{vi}:v]scale={W}:{H},format=rgba,colorchannelmixer=aa={N(vig)}[vg]");
            graph.Add("[vc][vg]overlay=shortest=1[v1]");
            graph.Add($"[v1]{string.Join(',', tail)}[v]");
        }
        else graph.Add($"[{picture}]{string.Join(',', pre.Concat(tail))}[v]");
        return string.Join(';', graph);
    }

    public static double Duration(JsonNode edl) => J.D(edl["source"]!["end"]) - J.D(edl["source"]!["start"]);

    // ---------- sound ----------
    /// <summary>Only sounds listed in library.json, each with a licence, can be used in a clip.</summary>
    public static Dictionary<string, JsonNode> Library()
    {
        var index = Path.Combine(Paths.Sfx, "library.json");
        var map = new Dictionary<string, JsonNode>();
        if (!File.Exists(index)) return map;
        foreach (var e in J.Items(JsonNode.Parse(File.ReadAllText(index))))
            if (J.S(e["license"]) != "" && File.Exists(Path.Combine(Paths.Sfx, J.S(e["file"])))) map[J.S(e["id"])] = e;
        return map;
    }

    public static (List<string> inputs, string graph, string label) MixGraph(JsonNode edl, bool hasAudio, int firstExtraInput, string voiceInput = "[0:a]")
    {
        var lib = Library();
        double dur = Duration(edl);
        var au = J.Obj(edl["audio"]);
        List<string> inputs = [], graph = [], layers = [];
        int k = firstExtraInput;
        if (hasAudio)
        {
            var enhance = J.B(au["voice_enhance"]) ? "highpass=f=85,acompressor=threshold=-20dB:ratio=3:attack=8:release=160:makeup=2," : "";
            graph.Add($"{voiceInput}{Fmt},{enhance}volume={N(J.D(au["voice_gain"], 1))},afade=t=in:d=0.04,afade=t=out:st={N(Math.Max(0, dur - 0.08))}:d=0.08[voice]");
        }
        else
        {
            inputs.AddRange(["-f", "lavfi", "-t", N(dur), "-i", "anullsrc=r=48000:cl=stereo"]);
            graph.Add($"[{k}:a]{Fmt}[voice]");
            k++;
        }
        int i = 0;
        foreach (var s in J.Items(au["sfx"]))
        {
            if (!lib.TryGetValue(J.S(s["id"]), out var entry) || J.S(entry["kind"]) != "sfx") { i++; continue; }
            inputs.AddRange(["-i", Path.Combine(Paths.Sfx, J.S(entry["file"]))]);
            graph.Add($"[{k}:a]{Fmt},volume={N(J.D(entry["gain"], 0.6) * J.D(s["gain"], 1))},adelay={(int)(J.D(s["t"]) * 1000)}:all=1[sfx{i}]");
            layers.Add($"[sfx{i}]");
            k++; i++;
        }
        var voice = "[voice]";
        if (lib.TryGetValue(J.S(au["music_id"]), out var music) && J.S(music["kind"]) == "music")
        {
            inputs.AddRange(["-stream_loop", "-1", "-i", Path.Combine(Paths.Sfx, J.S(music["file"]))]);
            graph.Add($"[{k}:a]{Fmt},volume={N(J.D(music["gain"], 0.22) * J.D(au["music_gain"], 1))}[mraw]");
            if (J.B(au["duck_music"], true) && hasAudio)
            {
                // The voice drives a compressor on the music: music dips while someone speaks.
                graph.Add("[voice]asplit=2[voicem][vkey]");
                graph.Add("[mraw][vkey]sidechaincompress=threshold=0.02:ratio=8:attack=100:release=800[music]");
                voice = "[voicem]";
            }
            else graph.Add("[mraw]anull[music]");
            layers.Add("[music]");
        }
        var tr = edl["transitions"];
        double d = Math.Min(J.D(tr?["outro_d"], 0.4), dur);
        var end = J.S(tr?["outro"]) == "fade" ? $",afade=t=out:st={N(dur - d)}:d={N(d)}" : "";
        graph.Add(layers.Count > 0
            ? $"{voice}{string.Concat(layers)}amix=inputs={layers.Count + 1}:normalize=0:duration=first:dropout_transition=0{end}[aout]"
            : $"{voice}anull{end}[aout]");
        return (inputs, string.Join(';', graph), "[aout]");
    }

    /// <summary>EBU R128 measurement via loudnorm's analysis pass. null when the audio is silent.</summary>
    public static Dictionary<string, double>? Measure(string path, double target, double tp, double? start = null, double? duration = null)
    {
        var args = new List<string>();
        if (start is double s) args.AddRange(["-ss", N(s)]);
        if (duration is double d) args.AddRange(["-t", N(d)]);
        args.AddRange(["-i", path, "-vn", "-af", $"loudnorm=I={J.F(target)}:TP={J.F(tp)}:LRA=11:print_format=json", "-f", "null", "-"]);
        var log = Ff.Run(args);
        var m = Regex.Match(log, "\\{[^{}]*\"input_i\"[^{}]*\\}", RegexOptions.Singleline);
        if (!m.Success) return null;
        try
        {
            var raw = JsonNode.Parse(m.Value)!;
            var vals = new Dictionary<string, double>();
            foreach (var key in new[] { "input_i", "input_tp", "input_lra", "input_thresh", "target_offset" })
            {
                var v = double.Parse(J.S(raw[key]), J.Inv);
                if (!double.IsFinite(v)) return null;
                vals[key] = v;
            }
            return vals["input_i"] < -69 ? null : vals;
        }
        catch (Exception e) when (e is FormatException or JsonException) { return null; }
    }

    // ---------- one language ----------
    public static RenderReport RenderClip(JsonNode edl, JsonNode src, JsonNode? track, string lang, string outDir, RenderSettings s, Action<string> log)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Directory.CreateDirectory(outDir);
        string ass = Path.Combine(outDir, $"{lang}.ass"), srt = Path.Combine(outDir, $"{lang}.srt");
        int sw = (int)J.D(src["width"]), sh = (int)J.D(src["height"]);
        File.WriteAllText(ass, Ass.Build(edl, track, lang, sw, sh));
        File.WriteAllText(srt, Ass.BuildSrt(track));

        var extra = new List<string>();
        int? vignetteInput = null;
        var vignette = Path.Combine(Paths.StudioDir, "vignette.png");
        if (Studio.ColorParams(edl["color"])["vignette"] > 0 && File.Exists(vignette)) { extra.AddRange(["-loop", "1", "-i", vignette]); vignetteInput = 1; }
        bool hasAudio = J.B(src["has_audio"]);
        var (audioInputs, aGraph, aOut) = MixGraph(edl, hasAudio, vignetteInput == null ? 1 : 2);
        extra.AddRange(audioInputs);
        var mediaFilters = new List<string>();
        int mediaStart = 1 + extra.Count(x => x == "-i");
        var media = EditorMedia.Compose(edl, "v0", aOut, mediaStart, mediaFilters, s.Fps);
        extra.AddRange(media.inputs);
        // Insert visual media after the base picture, before captions and grading.
        var visualFilters = mediaFilters.Where(x => !x.Contains("mediaA") && !x.Contains("mediaMix")).ToList();
        var soundFilters = mediaFilters.Where(x => x.Contains("mediaA") || x.Contains("mediaMix")).ToList();
        string? mediaGraph = visualFilters.Count == 0 ? null : string.Join(';', visualFilters) + $";[{media.video}]null[mediaPicture]";
        if (soundFilters.Count > 0) { aGraph += ";" + string.Join(';', soundFilters); aOut = media.audio; }
        double start = J.D(edl["source"]!["start"]), dur = Duration(edl);
        string srcPath = J.S(src["path"]), temp = Path.Combine(outDir, $"{lang}.premix.mkv"), final = Path.Combine(outDir, $"{lang}.mp4");
        var encoder = s.ResolvedEncoder;

        // Pass A: picture, text and the audio mix, with audio kept as float so nothing clips yet.
        List<string> PassA(List<string> decode, List<string> enc) =>
        [
            .. decode,
            "-ss", N(start), "-t", N(dur), "-i", srcPath, .. extra,
            "-filter_complex", $"{VideoGraph(edl, src, s.Fps, ass, vignetteInput, mediaGraph)};{aGraph}",
            "-map", "[v]", "-map", aOut, "-t", N(dur), "-r", s.Fps.ToString(), .. enc, "-c:a", "pcm_f32le", temp,
        ];
        // A card can refuse a session (driver, VRAM, too many encodes, a codec its decoder lacks): try the same
        // encoder with CPU decoding, then the other cards that answered (integrated graphics included), then the CPU.
        var attempts = new List<(string enc, List<string> decode)>();
        foreach (var enc in s.Device.StartsWith("dxgi:") ? new[] { encoder, "libx264" }.Distinct() : Hw.Chain(encoder))
        {
            var decode = s.GpuDecode && !s.Device.StartsWith("dxgi:") ? Hw.DecodeArgs(enc) : [];
            if (decode.Count > 0) attempts.Add((enc, decode));
            attempts.Add((enc, []));
        }
        for (int i = 0; ; i++)
        {
            var (enc, decode) = attempts[i];
            try { Ff.Run([.. Hw.DeviceInit(s.Device, enc), .. PassA(s.Device.StartsWith("dxgi:") ? [] : decode, [.. Hw.EncoderArgs(enc, s.Quality), .. Hw.DeviceOutput(s.Device, enc)])]); encoder = enc; break; }
            catch (FfmpegException e) when (i + 1 < attempts.Count)
            {
                var next = attempts[i + 1];
                log($"    {Hw.Label(enc)}{(decode.Count > 0 ? " con decodificación por GPU" : "")} no pudo con este clip; se reintenta con {Hw.Label(next.enc)}{(next.decode.Count > 0 ? "" : next.enc == enc ? " decodificando en la CPU" : "")}. ({e.Message.Split('\n').LastOrDefault(l => l.Trim() != "")?.Trim()})");
            }
        }

        // Pass B: two-pass loudness normalisation, only when the clip has real programme audio.
        double target = J.D(edl["audio"]?["normalize_lufs"], -14), tp = s.TruePeak;
        List<string> encode = ["-c:v", "copy", "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-movflags", "+faststart", final];
        var measured = Measure(temp, target, tp);
        bool normalized = measured != null && measured["input_i"] > -45;
        if (!normalized) measured = null;
        if (measured != null)
        {
            var ln = $"loudnorm=I={J.F(target)}:TP={J.F(tp)}:LRA=11:measured_I={J.F(measured["input_i"])}:measured_TP={J.F(measured["input_tp"])}" +
                     $":measured_LRA={J.F(measured["input_lra"])}:measured_thresh={J.F(measured["input_thresh"])}:offset={J.F(measured["target_offset"])}:linear=true,aresample=48000";
            Ff.Run(["-i", temp, "-af", ln, .. encode]);
            var check = Measure(final, target, tp);
            if (check == null || Math.Abs(check["input_i"] - target) > 0.7)
                Ff.Run(["-i", temp, "-af", $"loudnorm=I={J.F(target)}:TP={J.F(tp)}:LRA=11,aresample=48000", .. encode]);
        }
        else
        {
            normalized = false;
            Ff.Run(["-i", temp, "-af", "alimiter=limit=0.89:level=disabled", .. encode]);
        }
        File.Delete(temp);

        var info = Ff.Probe(final);
        var after = Measure(final, target, tp);
        return new(lang, final, (int)J.D(info["width"]), (int)J.D(info["height"]), J.D(info["duration"]),
                   after?["input_i"], after?["input_tp"], normalized, encoder, Math.Round(clock.Elapsed.TotalSeconds, 1));
    }
}
