// All on-screen text and shapes as one ASS file per language. Port of clipper/media/captions.py.
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Media;

namespace ClipperStudio.Engine;

public record Geometry(int FgH, int FgY, int CaptionY, int TopY, int SafeWidth, int TopSafe, int BottomSafe)
{
    public int FgW { get; init; } = 1080;
    public int FgX { get; init; }
    public JsonObject Json() => new()
    {
        ["fg_h"] = FgH, ["fg_y"] = FgY, ["caption_y"] = CaptionY, ["top_y"] = TopY,
        ["safe_width"] = SafeWidth, ["top_safe"] = TopSafe, ["bottom_safe"] = BottomSafe,
        ["fg_w"] = FgW, ["fg_x"] = FgX, ["center_x"] = SafeArea.CenterX,
    };
}

public static class Ass
{
    static readonly AsyncLocal<(int Width,int Height)?> Canvas = new();
    public static int W => Canvas.Value?.Width ?? 1080;
    public static int H => Canvas.Value?.Height ?? 1920;
    sealed class CanvasScope((int Width,int Height)? previous) : IDisposable
    { public void Dispose() => Canvas.Value=previous; }
    public static IDisposable UseCanvas(int width,int height)
    {
        var scope=new CanvasScope(Canvas.Value);Canvas.Value=(width,height);return scope;
    }
    public static int SafeTop => SafeArea.Top;
    public static int SafeBottom => SafeArea.BottomMargin;
    public static int SafeSide => SafeArea.Left;
    public const int TextMargin = 60, TextBoxPad = 14;
    const string White = "&H00FFFFFF", Yellow = "&H0000E5FF", NoteBox = "&H38181818";
    static readonly ConcurrentDictionary<string, GlyphTypeface> Faces = new();

    // ---------- measuring ----------
    /// <summary>Width in pixels of text set at an ASS font size (a line height), like libass draws it.</summary>
    public static double TextWidth(string text, string fontId, double assSize)
    {
        var font = Studio.Font(fontId);
        var face = Faces.GetOrAdd(font.Path, p => new GlyphTypeface(new Uri(p)));
        double em = assSize * Studio.FontScale(font.Path), w = 0;
        foreach (var rune in text.EnumerateRunes())
            if (face.CharacterToGlyphMap.TryGetValue(rune.Value, out var g) || face.CharacterToGlyphMap.TryGetValue('?', out g))
                w += face.AdvanceWidths[g] * em;
        return w;
    }

    static List<string> Greedy(string text, string fontId, double size, double max)
    {
        var lines = new List<string>();
        var cur = "";
        foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (TextWidth(word, fontId, size) > max)
            {
                if (cur != "") { lines.Add(cur); cur = ""; }
                foreach (var rune in word.EnumerateRunes())
                {
                    var part = cur + rune;
                    if (cur != "" && TextWidth(part, fontId, size) > max) { lines.Add(cur); cur = rune.ToString(); }
                    else cur = part;
                }
                continue;
            }
            var trial = (cur + " " + word).Trim();
            if (cur != "" && TextWidth(trial, fontId, size) > max) { lines.Add(cur); cur = word; }
            else cur = trial;
        }
        if (cur != "") lines.Add(cur);
        return lines;
    }

    /// <summary>Wrap to max width, then narrow the block as far as the line count allows.</summary>
    public static List<string> Wrap(string text, string fontId, double size, double max)
    {
        var lines = Greedy(text, fontId, size, max);
        if (lines.Count < 2) return lines;
        double lo = max * 0.5, hi = max;
        for (int i = 0; i < 8; i++)
        {
            double mid = (lo + hi) / 2;
            var trial = Greedy(text, fontId, size, mid);
            if (trial.Count <= lines.Count && trial.All(l => TextWidth(l, fontId, size) <= max)) { hi = mid; lines = trial; }
            else lo = mid;
        }
        return lines;
    }

    // ---------- layout ----------
    public static Geometry Geom(string layout, double? captionY, int srcW, int srcH)
    {
        if (srcW <= 0 || srcH <= 0) throw new EditException("Las dimensiones del video no son válidas. Vuelve a abrir el clip para repararlas desde el archivo original.");
        int topSafe = SafeTop, bottomSafe = H - SafeBottom, fgH = H, fgY = 0, fgW = W, fgX = 0, capY, topY;
        if (layout == "gameplay_blur_bg")
        {
            // Fit the source to the canvas. Platform margins constrain overlays, not the video size.
            double scale = Math.Min((double)W / srcW, (double)H / srcH);
            fgW = Math.Clamp((int)Math.Round(srcW * scale / 2) * 2, 2, W);
            fgH = Math.Clamp((int)Math.Round(srcH * scale / 2) * 2, 2, H);
            fgX = (W - fgW) / 2;
            fgY = (H - fgH) / 2;
            capY = bottomSafe - 140;
            topY = topSafe + 150;
        }
        else { capY = bottomSafe - 140; topY = topSafe + 150; }
        if (captionY is double y) capY = (int)Math.Round(y * H);
        capY = Math.Clamp(capY, topSafe + 140, bottomSafe - 140);
        return new(fgH, fgY, capY, topY, SafeArea.Width, topSafe, bottomSafe) { FgW = fgW, FgX = fgX };
    }

    public static Geometry Geom(JsonNode edl, int srcW, int srcH)
    {
        using var safeScope = SafeArea.Use(edl);
        var g = Geom(J.S(edl["layout"]), edl["captions"]?["y"] is JsonValue ? J.D(edl["captions"]!["y"]) : null, srcW, srcH);
        var source = new JsonObject { ["width"] = srcW, ["height"] = srcH };
        var faces = J.Items(edl["effects"]).Where(e => J.S(e["type"]) == "face_focus" && Ai.CreativeEdits.RegionError(e["region"]) == null);
        foreach (var face in faces)
        {
            var box = Framing.Face(face, source);
            g = g with { TopY = Math.Min(SafeArea.Bottom - 200, Math.Max(g.TopY, box.Y + box.Height + 150)) };
        }
        return g;
    }

    public static int CueFontSize(string cueText, JsonNode spec, double safeWidth, double scale)
    {
        var text = J.B(spec["upper"]) ? cueText.ToUpperInvariant() : cueText;
        int size = (int)Math.Round(J.D(spec["size"]) * scale);
        double width = TextWidth(text, J.S(spec["font"]), size) * 1.2 + 2 * J.D(spec["outline"]);
        int fitted = width <= safeWidth ? size : Math.Max(12, (int)(size * safeWidth / width));
        if (TextWidth(text, J.S(spec["font"]), fitted) * 1.2 + 2 * J.D(spec["outline"]) > safeWidth + 1)
            throw new EditException("El subtítulo es demasiado extenso para la zona segura. Divide esa frase en más bloques.");
        return fitted;
    }

    // ---------- ASS ----------
    static string Ts(double t)
    {
        long cs = (long)Math.Round(Math.Max(0, t) * 100);
        return $"{cs / 360000}:{cs / 6000 % 60:00}:{cs / 100 % 60:00}.{cs % 100:00}";
    }

    static string Esc(string text) => text.Replace("\\", "/").Replace("{", "(").Replace("}", ")").Replace("\n", " ").Trim();
    static string Rect(int w, int h) => $"\\p1}}m 0 0 l {w} 0 {w} {h} 0 {h}{{\\p0}}";
    static string Fmt(double v) => J.F(v, "0.###");

    public static string Build(JsonNode edl, JsonNode? track, string lang, int srcW, int srcH)
    {
        using var safeScope = SafeArea.Use(edl);
        var g = Geom(edl, srcW, srcH);
        int cx = SafeArea.CenterX;
        double dur = J.D(edl["source"]!["end"]) - J.D(edl["source"]!["start"]);
        var spec = Studio.Style(J.S(edl["captions"]?["style"], "bold"));
        var capFont = Studio.Font(J.S(spec["font"]));
        double capScale = J.D(edl["captions"]?["scale"], 1);
        string baseC = Studio.AssColor(J.S(spec["color"])), active = Studio.AssColor(J.S(spec["active"])), keyword = Studio.AssColor(J.S(spec["keyword"]));
        int glowN = (int)J.D(spec["glow"]);
        string glow = glowN > 0 ? $"\\blur{glowN}" : "";
        string inter = Studio.Font("caption").Family, interText = Studio.Font("text").Family;
        var sb = new StringBuilder();
        void L(string s) => sb.Append(s).Append('\n');
        L("[Script Info]"); L("ScriptType: v4.00+"); L($"PlayResX: {W}"); L($"PlayResY: {H}"); L("WrapStyle: 2"); L("ScaledBorderAndShadow: yes"); L("");
        L("[V4+ Styles]");
        L("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");
        L($"Style: Cap,{capFont.Family},{J.D(spec["size"])},{baseC},{baseC},{Studio.AssColor(J.S(spec["outline_color"]))},&H80000000,{(capFont.Bold ? -1 : 0)},0,0,0,100,100,0,0,1,{J.D(spec["outline"])},{(glowN > 0 ? 0 : 2)},5,20,20,20,1");
        L("Style: Txt,Inter,60,&H00FFFFFF,&H00FFFFFF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,0,0,5,0,0,0,1");
        L("Style: TxtB,Inter,60,&H00FFFFFF,&H00FFFFFF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,3,14,0,5,0,0,0,1");
        L("Style: Shape,Inter,20,&H00FFFFFF,&H00FFFFFF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,0,0,7,0,0,0,1");
        L($"Style: Hook,{inter},86,&H00000000,&H00000000,{Yellow},{Yellow},0,0,0,0,100,100,0,0,3,16,0,5,20,20,20,1");
        L($"Style: Note,{interText},46,{White},{White},{NoteBox},{NoteBox},-1,0,0,0,100,100,0,0,3,14,0,5,20,20,20,1");
        L(""); L("[Events]"); L("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");

        (int x, int y) Block(JsonNode? o, double width, double height, int fallbackY)
        {
            int hw = (int)Math.Min(SafeArea.Width/2.0, Math.Ceiling(width/2+4));
            int hh = (int)Math.Min(SafeArea.Height/2.0, Math.Ceiling(height/2+4));
            return (Math.Clamp((int)Math.Round(o?["x"] is JsonValue ? J.D(o["x"])*W : cx), SafeArea.Left+hw,SafeArea.Right-hw),
                Math.Clamp((int)Math.Round(o?["y"] is JsonValue ? J.D(o["y"])*H : fallbackY),SafeArea.Top+hh,SafeArea.Bottom-hh));
        }
        // Hook: big label at the top for the first seconds.
        var hook = Esc(J.S(edl["hook"]?["text"]?[lang])).ToUpperInvariant();
        double hookDur = J.D(edl["hook"]?["duration"]);
        if (hook != "" && hookDur > 0)
        {
            int size = 86;
            var lines = Wrap(hook, "caption", size, g.SafeWidth - 40);
            while (lines.Count > 3 && size > 56) { size -= 8; lines = Wrap(hook, "caption", size, g.SafeWidth - 40); }
            int half = (int)Math.Ceiling(lines.Count * size / 2.0 + 18);
            var (x,y) = Block(edl["hook"],lines.Max(l=>TextWidth(l,"caption",size))+32,half*2,g.TopY);
            L($"Dialogue: 2,{Ts(0)},{Ts(hookDur)},Hook,,0,0,0,,{{{SafeArea.Clip}\\pos({x},{y})\\fs{size}\\fad(80,160)}}" + string.Join("\\N", lines));
        }
        // Commentary notes, in a box at the top.
        foreach (var note in J.Items(edl["commentary"]?["notes"]))
        {
            var text = Esc(J.S(note["text"]?[lang]));
            double t = J.D(note["t"]), d = J.D(note["duration"]);
            if (text == "" || d <= 0) continue;
            var lines = Wrap(text, "text", 46, g.SafeWidth - 40);
            int half = (int)Math.Ceiling(lines.Count * 46 / 2.0 + 16);
            if (half * 2 >= SafeArea.Height) throw new EditException("La nota es demasiado extensa para la zona segura.");
            var (x,y) = Block(note,lines.Max(l=>TextWidth(l,"text",46))+28,half*2,g.TopY);
            L($"Dialogue: 1,{Ts(t)},{Ts(t + d)},Note,,0,0,0,,{{{SafeArea.Clip}\\pos({x},{y})\\fad(120,160)}}" + string.Join("\\N", lines));
        }
        // Captions: one event per spoken word, with that word highlighted.
        bool upper = J.B(spec["upper"]), pop = J.B(spec["pop"]);
        foreach (var cue in J.Items(track?["cues"]))
        {
            var words = J.Items(cue["words"]).ToList();
            if (words.Count == 0) continue;
            int size = CueFontSize(string.Join(' ', words.Select(w => J.S(w["w"]))), spec, g.SafeWidth, capScale);
            var (capX,capY) = Block(edl["captions"],TextWidth(string.Join(' ',words.Select(w=>J.S(w["w"]))),J.S(spec["font"]),size)*1.2+J.D(spec["outline"])*2,size*1.2,g.CaptionY);
            for (int k = 0; k < words.Count; k++)
            {
                double t0 = k == 0 ? J.D(cue["start"]) : J.D(words[k]["start"]);
                double t1 = k + 1 < words.Count ? J.D(words[k + 1]["start"]) : J.D(cue["end"]);
                if (t1 <= t0) continue;
                var parts = new List<string>();
                for (int j = 0; j < words.Count; j++)
                {
                    var word = Esc(upper ? J.S(words[j]["w"]).ToUpperInvariant() : J.S(words[j]["w"]));
                    if (j == k)
                    {
                        var p = pop ? "\\fscx86\\fscy86\\t(0,80,\\fscx116\\fscy116)\\t(80,170,\\fscx106\\fscy106)" : "\\fscx106\\fscy106";
                        parts.Add($"{{\\c{active}{p}}}{word}{{\\c{baseC}\\fscx100\\fscy100}}");
                    }
                    else if (J.B(words[j]["hl"])) parts.Add($"{{\\c{keyword}}}{word}{{\\c{baseC}}}");
                    else parts.Add(word);
                }
                L($"Dialogue: 0,{Ts(t0)},{Ts(t1)},Cap,,0,0,0,,{{{SafeArea.Clip}\\pos({capX},{capY})\\fs{size}{glow}}}" + string.Join(' ', parts));
            }
        }
        TextEvents(edl, lang, dur, L);
        ShapeEvents(edl, dur, L);
        return sb.ToString();
    }

    /// <summary>Free text layers, with their entrance and exit animations.</summary>
    static void TextEvents(JsonNode edl, string lang, double dur, Action<string> L)
    {
        var A = Studio.Catalog["anim"]!;
        foreach (var tx in J.Items(edl["texts"]))
        {
            double t0 = Math.Max(0, J.D(tx["t"])), t1 = Math.Min(dur, J.D(tx["t"]) + J.D(tx["duration"]));
            var f = Studio.Font(J.S(tx["font"], "anton"));
            var placement = SafeArea.PlaceText(tx, lang);
            int fs = placement.FontSize;
            var lines = placement.Lines.Select(Esc).ToList();
            if (lines.Count == 0 || t1 - t0 < 0.05) continue;
            int x = placement.X, y = placement.Y, ms = (int)Math.Round((t1 - t0) * 1000);
            double opacity = J.D(tx["opacity"], 1), rotate = J.D(tx["rotate"]), glow = J.D(tx["glow"]);
            string box = J.S(tx["box"]), animIn = J.S(tx["anim_in"], "fade"), animOut = J.S(tx["anim_out"], "fade");
            var tags = new StringBuilder($"{SafeArea.Clip}\\an5\\fn{f.Family}\\b{(f.Bold ? 1 : 0)}\\fs{fs}\\shad0\\c&H{Studio.Bgr(J.S(tx["color"], "#FFFFFF"))}&\\1a{Studio.Alpha(opacity)}");
            if (box != "") tags.Append($"\\bord{TextBoxPad}\\3c&H{Studio.Bgr(box)}&\\3a{Studio.Alpha(J.D(tx["box_opacity"], 1) * opacity)}");
            else
            {
                tags.Append($"\\bord{Fmt(J.D(tx["stroke_w"], 6))}\\3c&H{Studio.Bgr(J.S(tx["stroke"], "#000000"))}&\\3a{Studio.Alpha(opacity)}");
                if (glow > 0) tags.Append($"\\blur{Fmt(glow)}");
            }
            if (rotate != 0) tags.Append($"\\frz{Fmt(-rotate)}");
            (int sx, int sy)? start = animIn.StartsWith("slide_") ? (placement.StartX, placement.StartY) : null;
            tags.Append(start is { } s ? $"\\move({s.sx},{s.sy},{x},{y},0,{(int)(J.D(A["slide"]) * 1000)})" : $"\\pos({x},{y})");
            int fadeIn = animIn == "none" ? 0 : (int)((animIn == "pop" ? 0.08 : J.D(A["fade"])) * 1000);
            int fadeOut = animOut == "fade" ? (int)(J.D(A["fade"]) * 1000) : 0;
            if (fadeIn > 0 || fadeOut > 0) tags.Append($"\\fad({fadeIn},{fadeOut})");
            if (animIn == "pop")
            {
                int a = (int)(J.D(A["pop_a"]) * 1000), b = (int)(J.D(A["pop_b"]) * 1000);
                tags.Append($"\\fscx40\\fscy40\\t(0,{a},\\fscx112\\fscy112)\\t({a},{b},\\fscx100\\fscy100)");
            }
            else if (animIn == "zoom") tags.Append($"\\fscx160\\fscy160\\t(0,{(int)(J.D(A["zoom"]) * 1000)},\\fscx100\\fscy100)");
            if (animOut == "shrink") tags.Append($"\\t({Math.Max(0, ms - (int)(J.D(A["shrink"]) * 1000))},{ms},\\fscx0\\fscy0)");
            L($"Dialogue: 3,{Ts(t0)},{Ts(t1)},{(box != "" ? "TxtB" : "Txt")},,0,0,0,,{{{tags}}}" + string.Join("\\N", lines));
        }
    }

    /// <summary>Full-frame shapes drawn by libass: white flashes and the progress bar.</summary>
    static void ShapeEvents(JsonNode edl, double dur, Action<string> L)
    {
        double amount = J.D(Studio.Fx["flash"]);
        var flashes = J.Items(edl["effects"]).Where(e => J.S(e["type"]) == "flash").Select(e => (t: J.D(e["t"]), d: J.D(e["duration"]), k: J.D(e["intensity"], 1))).ToList();
        if (J.S(edl["transitions"]?["intro"]) == "flash") flashes.Add((0, J.D(edl["transitions"]!["intro_d"], 0.4), 1.2));
        foreach (var (t, d, k) in flashes)
            if (d > 0 && t < dur)
                L($"Dialogue: 9,{Ts(t)},{Ts(Math.Min(dur, t + d))},Shape,,0,0,0,,{{\\pos(0,0)\\c&HFFFFFF&\\alpha{Studio.Alpha(amount * k)}\\t(\\alpha&HFF&){Rect(W, H)}");
        var pg = edl["progress"];
        if (J.B(pg?["on"]))
        {
            int h = (int)J.D(pg!["height"], 12), y = J.S(pg["position"], "top") == "top" ? SafeArea.Top : SafeArea.Bottom - h;
            L($"Dialogue: 8,{Ts(0)},{Ts(dur)},Shape,,0,0,0,,{{\\pos({SafeArea.Left},{y})\\c&H{Studio.Bgr(J.S(pg["color"], "#FFE500"))}&\\clip({SafeArea.Left},{y},{SafeArea.Left},{y + h})\\t(\\clip({SafeArea.Left},{y},{SafeArea.Right},{y + h})){Rect(SafeArea.Width, h)}");
        }
    }

    // ---------- SRT ----------
    static string SrtTs(double t)
    {
        long ms = (long)Math.Round(Math.Max(0, t) * 1000);
        return $"{ms / 3600000:00}:{ms / 60000 % 60:00}:{ms / 1000 % 60:00},{ms % 1000:000}";
    }

    public static string BuildSrt(JsonNode? track)
    {
        var sb = new StringBuilder();
        int i = 1;
        foreach (var c in J.Items(track?["cues"]))
            sb.Append($"{i++}\n{SrtTs(J.D(c["start"]))} --> {SrtTs(J.D(c["end"]))}\n{string.Join(' ', J.Items(c["words"]).Select(w => J.S(w["w"])))}\n\n");
        return sb.ToString();
    }
}
