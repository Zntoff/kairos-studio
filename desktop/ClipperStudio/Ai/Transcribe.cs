// Transcript and signals. whisper.cpp (GPU through CUDA when the NVIDIA build is installed) replaces
// faster-whisper; the transcript has the same shape as clipper/schemas.py: segments with words.
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClipperStudio.Engine;

namespace ClipperStudio.Ai;

public record Word(string W, double Start, double End);

public static class Whisper
{
    public const string ModelFile = "ggml-small-q5_1.bin";
    public static string ModelPath => Path.Combine(Paths.Dir("models"), ModelFile);

    public static string? Cli
    {
        get
        {
            var root = Path.Combine(Tools.InstallDir, "whisper");
            if (!Directory.Exists(root)) return null;
            return Directory.EnumerateFiles(root, "*.exe", SearchOption.AllDirectories)
                .FirstOrDefault(f => Path.GetFileName(f).Equals("whisper-cli.exe", StringComparison.OrdinalIgnoreCase))
                ?? Directory.EnumerateFiles(root, "main.exe", SearchOption.AllDirectories).FirstOrDefault();
        }
    }

    public static bool Ready => Cli != null && File.Exists(ModelPath);

    static string? CliDir => Cli is { } c ? Path.GetDirectoryName(c) : null;
    static bool Has(string pattern) => CliDir is { } d && Directory.EnumerateFiles(d, pattern).Any();

    /// <summary>Whether the installed build runs on the NVIDIA GPU (CUDA build).</summary>
    public static bool Gpu => Has("ggml-cuda*.dll") || Has("cublas*.dll");
    public static string Backend => Cli == null ? "sin instalar" : Gpu ? "GPU NVIDIA (CUDA)" : Has("*openblas*.dll") ? "CPU optimizada (OpenBLAS)" : "CPU";

    /// <summary>Run the installed build on one second of silence: catches missing DLLs and unsupported CPUs or GPUs.</summary>
    public static bool Check()
    {
        if (Cli is not { } cli || !File.Exists(ModelPath)) return false;
        var wav = Path.Combine(Path.GetTempPath(), "clipper-whisper-check.wav");
        using (var w = new BinaryWriter(File.Create(wav)))   // 16 kHz mono 16-bit PCM, 1 s of zeros
        {
            w.Write("RIFF"u8); w.Write(36 + 32000); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(16000); w.Write(32000); w.Write((short)2); w.Write((short)16); w.Write("data"u8); w.Write(32000); w.Write(new byte[32000]);
        }
        try
        {
            var psi = new ProcessStartInfo(cli) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(cli)! };
            foreach (var a in new[] { "-m", ModelPath, "-f", wav, "-l", "en", "-nt", "-t", "2" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEndAsync(); p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(180_000)) { try { p.Kill(true); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    public static void ExtractAudio(string video, string wav) =>
        Ff.Run(["-i", video, "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", wav]);

    /// <summary>Words with times, grouped into segments that end at a sentence end, a pause or a length limit.</summary>
    public static JsonObject Transcribe(string wav, string? language, double duration, Action<string> log, bool fast = true, bool cpuOnly = false)
    {
        var cli = Cli ?? throw new LlmException("Falta el motor de transcripción. Instálalo en Ajustes → Herramientas.");
        if (!File.Exists(ModelPath)) throw new LlmException("Falta el modelo de transcripción. Instálalo en Ajustes → Herramientas.");
        var outBase = Path.Combine(Path.GetDirectoryName(wav)!, "whisper_out");
        var json = outBase + ".json";
        File.Delete(json);
        var clock = Stopwatch.StartNew();
        log(fast ? "    modo rápido (una alternativa por palabra)" : "    modo preciso (varias alternativas por palabra)");
        log($"    motor: {(cpuOnly ? "CPU (seleccionada en Ajustes)" : Backend)}");
        var tail = new StringBuilder();
        int Run(bool noGpu)
        {
            var psi = new ProcessStartInfo(cli) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(cli)! };
            foreach (var a in new[] { "-m", ModelPath, "-f", wav, "-l", string.IsNullOrEmpty(language) ? "auto" : language, "-ojf", "-of", outBase, "-ml", fast ? "1" : "0", "-sow", "-pp", "-t", Math.Clamp(Environment.ProcessorCount / 2, 1, 8).ToString(), "-bs", fast ? "1" : "5", "-bo", fast ? "1" : "5" })
                psi.ArgumentList.Add(a);
            if (!fast) { psi.ArgumentList.Add("-dtw"); psi.ArgumentList.Add("small"); }
            if (noGpu) psi.ArgumentList.Add("-ng");
            using var p = Process.Start(psi)!;
            int lastPct = -10;
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (tail) { tail.AppendLine(e.Data); if (tail.Length > 4000) tail.Remove(0, tail.Length - 4000); }
                var m = Regex.Match(e.Data, @"progress\s*=\s*(\d+)%");
                if (m.Success && int.Parse(m.Groups[1].Value) >= lastPct + 10) { lastPct = int.Parse(m.Groups[1].Value); log($"    transcribiendo… {lastPct}%"); }
            };
            p.OutputDataReceived += (_, _) => { };
            p.BeginErrorReadLine(); p.BeginOutputReadLine();
            p.WaitForExit();
            return p.ExitCode;
        }
        // A CUDA build whose GPU is busy or out of memory still runs on the CPU with -ng.
        if ((Run(cpuOnly) != 0 || !File.Exists(json)) && Gpu && !cpuOnly)
        {
            log("    la GPU no pudo transcribir; se repite en la CPU");
            tail.Clear();
            if (Run(true) != 0) File.Delete(json);
        }
        if (!File.Exists(json)) throw new LlmException("La transcripción falló: " + Ff.Tail(tail.ToString().Trim(), 500));
        log($"    transcripción terminada en {clock.Elapsed.TotalSeconds:0} s ({duration / Math.Max(1, clock.Elapsed.TotalSeconds):0.0}× tiempo real)");

        var doc = JsonNode.Parse(File.ReadAllText(json))!;
        var words = ParseWords(doc, duration);
        var lang = J.S(doc["result"]?["language"]);
        if (lang == "") lang = language ?? "und";
        return words.Count == 0 ? Silent(duration, lang) : Build(lang, duration, words);
    }

    public static List<Word> ParseWords(JsonNode doc, double duration)
    {
        var words = new List<Word>();
        foreach (var t in J.Items(doc["transcription"]))
        {
            // Full JSON contains acoustic token offsets. Segment boundaries are
            // not word boundaries, especially with multibyte Spanish BPE tokens.
            var tokens = J.Items(t["tokens"]).Where(x => !J.S(x["text"]).StartsWith("[_"))
                .Where(x => x["offsets"] is JsonObject && J.D(x["offsets"]?["from"], -1) >= 0).ToList();
            foreach (var token in tokens.Count > 0 ? tokens : new List<JsonNode> { t })
            {
                string text = J.S(token["text"]);
                if (string.IsNullOrWhiteSpace(text) || Regex.IsMatch(text.Trim(), @"^\[.*\]$|^\(.*\)$")) continue;
                double s = Math.Clamp(J.D(token["offsets"]?["from"]) / 1000, 0, duration);
                double e = Math.Clamp(J.D(token["offsets"]?["to"]) / 1000, s, duration);
                // DTW is measured in 10 ms ticks, unlike JSON offsets (ms).
                // It follows the acoustic attention; offsets can still be a
                // heuristic distribution over a whole sentence, including silence.
                double anchor = J.D(token["t_dtw"], -1);
                if (anchor >= 0) { s = Math.Clamp(anchor / 100 - .04, 0, duration); e = Math.Min(duration, s + .18); }
                if (e <= s) continue;
                // DTW may assign the same acoustic frame to adjacent short
                // words. Keep both spellings together rather than dropping the
                // first word when its interval would otherwise become empty.
                if (anchor >= 0 && words.Count > 0 && s <= words[^1].Start + .005)
                {
                    words[^1] = words[^1] with { W = words[^1].W + text, End = Math.Max(words[^1].End, e) };
                    continue;
                }
                if (!char.IsWhiteSpace(text[0]) && words.Count > 0)
                    words[^1] = words[^1] with { W = words[^1].W + text, End = Math.Max(words[^1].End, e) };
                else
                {
                    if (words.Count > 0 && words[^1].End > s) words[^1] = words[^1] with { End = Math.Max(words[^1].Start, s) };
                    words.Add(new Word(text.Trim(), s, e));
                }
            }
        }
        return words.Where(w => w.End > w.Start).ToList();
    }

    public static JsonObject Build(string language, double duration, List<Word> words)
    {
        var segments = new JsonArray();
        var cur = new List<Word>();
        void Flush()
        {
            if (cur.Count == 0) return;
            segments.Add(new JsonObject
            {
                ["id"] = segments.Count, ["start"] = Math.Round(cur[0].Start, 3), ["end"] = Math.Round(cur[^1].End, 3), ["text"] = string.Join(' ', cur.Select(w => w.W)),
                ["words"] = new JsonArray(cur.Select(w => (JsonNode)new JsonObject { ["w"] = w.W, ["start"] = Math.Round(w.Start, 3), ["end"] = Math.Round(w.End, 3) }).ToArray()),
            });
            cur = [];
        }
        for (int i = 0; i < words.Count; i++)
        {
            cur.Add(words[i]);
            double pause = i + 1 < words.Count ? words[i + 1].Start - words[i].End : 0;
            if (i + 1 == words.Count || Regex.IsMatch(words[i].W, @"[.!?…]+[""')\]]*$") || pause > 0.8 || cur.Count >= 28) Flush();
        }
        return new JsonObject { ["language"] = language, ["duration"] = duration, ["segments"] = segments };
    }

    /// <summary>A silent video still gets one empty segment per 10 s, so the analysis has something to cut on.</summary>
    public static JsonObject Silent(double duration, string language)
    {
        var segs = new JsonArray();
        for (double t = 0; t < duration - 0.5; t += 5)
        {
            var end = Math.Min(duration, t + 5);
            var marks = new JsonArray();
            for (double m = t; m < end - 0.25; m += 1)
                marks.Add(new JsonObject { ["w"] = "", ["start"] = m, ["end"] = Math.Min(end, m + 1) });
            segs.Add(new JsonObject { ["id"] = segs.Count, ["start"] = t, ["end"] = end, ["text"] = "", ["words"] = marks });
        }
        return new JsonObject { ["language"] = language, ["duration"] = duration, ["segments"] = segs };
    }
}

/// <summary>Cheap signals that hint where something happens: loudness over time and hard visual cuts.</summary>
public static class Signals
{
    public const double Hop = 0.5;

    public static List<double> Energy(string wav)
    {
        var bytes = File.ReadAllBytes(wav);
        int data = 44;
        for (int i = 12; i + 8 < bytes.Length; )   // walk the RIFF chunks to the "data" one
        {
            int size = BitConverter.ToInt32(bytes, i + 4);
            if (bytes[i] == 'd' && bytes[i + 1] == 'a' && bytes[i + 2] == 't' && bytes[i + 3] == 'a') { data = i + 8; break; }
            i += 8 + size + (size & 1);
        }
        int hop = (int)(16000 * Hop), n = (bytes.Length - data) / 2 / hop;
        var db = new double[n];
        for (int f = 0; f < n; f++)
        {
            double sum = 0;
            for (int j = 0; j < hop; j++) { double v = BitConverter.ToInt16(bytes, data + (f * hop + j) * 2) / 32768.0; sum += v * v; }
            db[f] = 20 * Math.Log10(Math.Sqrt(sum / hop) + 1e-6);
        }
        if (n == 0) return [];
        double top = db.Max();
        return top < -70 ? Enumerable.Repeat(0.0, n).ToList() : db.Select(x => Math.Round(Math.Clamp((x - (top - 40)) / 40, 0, 1), 3)).ToList();
    }

    /// <summary>Hard cuts, found on keyframes of a small copy (fast: encoders usually put a keyframe at a cut).</summary>
    public static List<double> SceneCuts(string video)
    {
        try
        {
            var log = Ff.Run(["-skip_frame", "nokey", "-i", video, "-an", "-fps_mode", "vfr", "-vf", "scale=320:-2,select='gt(scene,0.5)',showinfo", "-f", "null", "-"]);
            return Regex.Matches(log, @"pts_time:([0-9.]+)").Select(m => Math.Round(double.Parse(m.Groups[1].Value, J.Inv), 2)).ToList();
        }
        catch (FfmpegException) { return []; }
    }

    public static (double mean, double peak, int cuts) Features(JsonNode seg, List<double> energy, List<double> cuts)
    {
        double s = J.D(seg["start"]), e = J.D(seg["end"]);
        int a = (int)(s / Hop), b = Math.Max(a + 1, (int)Math.Ceiling(e / Hop));
        var w = energy.Skip(a).Take(Math.Max(0, b - a)).ToList();
        return (w.Count > 0 ? Math.Round(w.Average(), 2) : 0, w.Count > 0 ? Math.Round(w.Max(), 2) : 0, cuts.Count(c => c >= s && c < e));
    }
}
