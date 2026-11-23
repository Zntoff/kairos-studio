using System.Text.Json.Nodes;

namespace ClipperStudio.Engine;

public record FacePlacement(int X, int Y, int Width, int Height, int InnerWidth, int InnerHeight);

public static class Framing
{
    // Coordinates are normalized crop offsets, interpolated between timestamped visual references.
    public static string CropExpression(JsonNode edl, string axis)
    {
        var points = J.Items(edl["framing"]?["track"]).OrderBy(p => J.D(p["source_time"])).ToList();
        if (!J.B(edl["tracking_enabled"], true) || points.Count < 2) return J.F(J.D(edl["crop_" + axis], .5));
        double start = J.D(edl["source"]?["start"]);
        string expression = J.F(J.D(points[0][axis], .5));
        for (int i = 1; i < points.Count; i++)
        {
            double a = J.D(points[i-1]["source_time"])-start, b = J.D(points[i]["source_time"])-start;
            if (b <= a) continue;
            double from = J.D(points[i-1][axis], .5), to = J.D(points[i][axis], .5);
            expression += $"+({J.F(to-from)})*clip((t-({J.F(a)}))/{J.F(b-a)},0,1)";
        }
        return expression;
    }
    public const string FitProfile = "canvas_contain_v2";
    public static bool NeedsRender(JsonNode clip) => clip["package_dir"] != null &&
        (J.S(clip["edl"]?["safe_area"]?["profile"]) != SafeArea.Profile ||
         J.S(clip["edl"]?["layout"]) == "gameplay_blur_bg" && J.S(clip["edl"]?["fit_profile"]) != FitProfile);
    public static bool SubjectFitsSafe(JsonNode edl, JsonNode source)
    {
        using var safeScope = SafeArea.Use(edl);
        var region = edl["framing"]?["subject_region"];
        if (region == null) return false;
        double sw = J.D(source["width"]), sh = J.D(source["height"]), scale = Math.Max(Ass.W / sw, Ass.H / sh);
        double left = J.D(region["x"]) * sw * scale - J.D(edl["crop_x"], .5) * (sw * scale - Ass.W);
        double top = J.D(region["y"]) * sh * scale - J.D(edl["crop_y"], .5) * (sh * scale - Ass.H);
        double right = left + J.D(region["w"]) * sw * scale, bottom = top + J.D(region["h"]) * sh * scale;
        double Sum(string type) => Math.Min(2, J.Items(edl["effects"]).Where(e => J.S(e["type"]) == type).Sum(e => J.D(e["intensity"], 1)));
        double zoom = 1 + J.D(Studio.Fx["zoom"]) * Sum("punch_in") + J.D(Studio.Fx["slow"]) * Sum("zoom_slow") + J.D(Studio.Fx["shake_zoom"]) * Sum("shake");
        double shake = J.D(Studio.Fx["shake_px"]) * Sum("shake");
        double a = Ass.W / 2.0, b = Ass.H / 2.0;
        return a + (left - a) * zoom - shake >= SafeArea.Left && a + (right - a) * zoom + shake <= SafeArea.Right &&
            b + (top - b) * zoom - shake >= SafeArea.Top && b + (bottom - b) * zoom + shake <= SafeArea.Bottom;
    }
    // Fit the complete selected region: never force a rectangle into a square and lose headroom.
    public static FacePlacement Face(JsonNode effect, JsonNode source)
    {
        var r = effect["region"]!;
        double w = J.D(r["w"]) * J.D(source["width"]), h = J.D(r["h"]) * J.D(source["height"]);
        if (w <= 0 || h <= 0) throw new EditException("La región de la cara no tiene dimensiones válidas.");
        double scale = Math.Min(360 / w, 200 / h);
        int iw = Math.Max(2, (int)(w * scale) / 2 * 2), ih = Math.Max(2, (int)(h * scale) / 2 * 2);
        return new(SafeArea.Right - iw - 16, SafeArea.Top, iw + 16, ih + 16, iw, ih);
    }

    public static void Apply(Ai.Agents.EditInput input, JsonObject decision, JsonObject edl)
    {
        var plan = J.Obj(decision["framing"]);
        var region = plan["subject_region"];
        bool auto = input.LayoutChoice == "auto";
        if (!auto) edl["layout"] = input.LayoutChoice;
        double sw = J.D(input.Source["width"]), sh = J.D(input.Source["height"]);
        double scale = Math.Max(Ass.W / sw, Ass.H / sh);
        double visibleW = Ass.W / scale / sw, visibleH = Ass.H / scale / sh;
        double scaledW = sw * scale, scaledH = sh * scale;
        bool fits = region != null && J.D(region["w"]) * scaledW <= SafeArea.Width && J.D(region["h"]) * scaledH <= SafeArea.Height;
        if (auto)
            edl["layout"] = J.S(plan["mode"]) == "subject" && fits ? "center_crop" : "gameplay_blur_bg";
        if (auto && J.S(edl["layout"]) == "center_crop" && region == null) edl["layout"] = "gameplay_blur_bg";
        edl["crop_y"] = .5;
        if (J.S(edl["layout"]) == "center_crop" && region != null)
        {
            double cx = J.D(region["x"]) + J.D(region["w"]) / 2, cy = J.D(region["y"]) + J.D(region["h"]) / 2;
            edl["crop_x"] = visibleW >= .99999 ? .5 : Math.Clamp((cx * scaledW - SafeArea.CenterX) / (scaledW - Ass.W), 0, 1);
            edl["crop_y"] = visibleH >= .99999 ? .5 : Math.Clamp((cy * scaledH - (SafeArea.Top + SafeArea.Bottom) / 2.0) / (scaledH - Ass.H), 0, 1);
            edl["framing"] = J.Clone(plan);
            fits = fits && SubjectFitsSafe(edl, input.Source);
            // Automatic choice may prefer fit; an explicit crop must remain a crop.
            if (auto && !fits) edl["layout"] = "gameplay_blur_bg";
        }
        var stored = (JsonObject)J.Clone(plan);
        var track = new JsonArray();
        foreach (var point in J.Items(plan["subject_track"]).OrderBy(p => J.D(p["frame_index"])).Take(32))
        {
            int index = (int)J.D(point["frame_index"], -1);
            var r = point["region"];
            if (index < 0 || index >= input.Frames.Count || Ai.CreativeEdits.RegionError(r) != null) continue;
            double time = J.D(input.Candidate["start"]) + (J.D(input.Candidate["end"])-J.D(input.Candidate["start"]))*(index+.5)/input.Frames.Count;
            double cx = J.D(r!["x"]) + J.D(r["w"])/2, cy = J.D(r["y"]) + J.D(r["h"])/2;
            track.Add(new JsonObject { ["source_time"] = time,
                ["x"] = visibleW >= .99999 ? .5 : Math.Clamp((cx*scaledW-SafeArea.CenterX)/(scaledW-Ass.W),0,1),
                ["y"] = visibleH >= .99999 ? .5 : Math.Clamp((cy*scaledH-(SafeArea.Top+SafeArea.Bottom)/2.0)/(scaledH-Ass.H),0,1) });
        }
        stored["track"] = track;
        edl["tracking_enabled"] = true;
        stored["note"] = J.S(edl["layout"]) == "center_crop" && !fits
            ? "No se pudo verificar el sujeto principal dentro de la zona segura del recorte vertical. Revisa el encuadre."
            : !fits ? "El video completo aprovecha el lienzo, conservando sus proporciones. Los textos y las reacciones ampliadas permanecen en la zona segura." : "Encuadre centrado en la zona segura, dejando margen para las interfaces de Shorts y Reels.";
        edl["safe_area"] = SafeArea.Json();
        edl["framing"] = stored;
    }
}
