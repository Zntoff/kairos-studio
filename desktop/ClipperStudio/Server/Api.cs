// Local HTTP API on 127.0.0.1, with the same routes and JSON shapes as clipper/web/app.py, so the
// interface (clipper/web/static) runs unchanged inside the app's WebView2.
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClipperStudio.Engine;
using ClipperStudio.Ai;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace ClipperStudio.Server;

public partial class Api
{
    static readonly string[] Rights = ["own", "written_permission", "official_press", "cc_licensed"];
    static readonly Regex AllowedHost = new(@"(^|\.)(youtube\.com|youtu\.be|twitch\.tv|kick\.com)$", RegexOptions.IgnoreCase);
    static readonly Regex SafeId = new("^[A-Za-z0-9_-]+$");
    readonly Store _store = new();
    readonly Runs _runs = new();
    readonly EditorSvc _editor;
    readonly AiSettings _ai = new();
    readonly Pipeline _pipeline;
    WebApplication? _app;

    public Api()
    {
        _editor = new EditorSvc(_store);
        _pipeline = new Pipeline(_store, _ai, _editor);
        _pipeline.MarkInterrupted();
        _ = Task.Run(() => { _ = Hw.Profile; _ = Hw.DeviceList(); });
    }
    public string Url { get; private set; } = "";
    public Func<string,Task<string?>>? SelectOutputFolder { get; set; }

    IResult Json(JsonNode? node, int status = 200) => Results.Text(Redact(node?.ToJsonString() ?? "null"), "application/json", System.Text.Encoding.UTF8, status);
    IResult Err(int status, string message) => Json(new JsonObject { ["detail"] = message }, status);
    static JsonArray Arr(IEnumerable<JsonNode> items) => new(items.ToArray());

    JsonObject ProviderJson()
    {
        var c = _ai.Conf();
        return new JsonObject { ["id"] = c.Id, ["name"] = c.Name, ["missing"] = AiSettings.Missing(c) };
    }

    /// <summary>Run an engine call and turn its errors into the HTTP answers the interface expects.</summary>
    IResult Guard(Func<IResult> fn)
    {
        try { return fn(); }
        catch (KeyNotFoundException) { return Err(404, "No existe."); }
        catch (EditException e) { return Err(400, e.Message); }
        catch (FfmpegException e) { return Err(500, e.Message.Length > 300 ? e.Message[..300] : e.Message); }
    }

    static JsonObject Public(JsonObject clip)
    {
        clip["needs_safe_render"] = Framing.NeedsRender(clip);
        clip["has_draft"] = clip["draft"] != null;
        clip.Remove("draft");
        return clip;
    }

    JsonObject Register(string path, string rights, string evidence, string credit, string language, string projectId = Store.DefaultProject)
    {
        if (!HasProject(projectId)) throw new EditException("No existe el proyecto.");
        var info = Ff.Probe(path);
        var existing = _store.List("sources", projectId).FirstOrDefault(s => string.Equals(J.S(s["path"]), path, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing;
        var src = new JsonObject
        {
            ["id"] = "src_" + Guid.NewGuid().ToString("N")[..10], ["path"] = path, ["rights_status"] = rights, ["evidence"] = evidence,
            ["credit"] = credit == "" ? null : credit, ["language"] = language == "" ? null : language,
            ["duration"] = J.D(info["duration"]), ["width"] = (int)J.D(info["width"]), ["height"] = (int)J.D(info["height"]), ["fps"] = J.D(info["fps"]),
            ["has_audio"] = J.B(info["has_audio"]), ["created_at"] = Store.Now(),
            ["project_id"] = projectId,
        };
        _store.Add("sources", src);
        return src;
    }

    static string? CheckRights(string rights, string evidence) =>
        !Rights.Contains(rights) ? $"Estado de derechos no válido. Valores: {string.Join(", ", Rights)}"
        : evidence.Trim().Length < 8 ? "Describe la evidencia de los derechos (al menos 8 caracteres)." : null;

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Environment.EnvironmentName = "Production";
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls("http://127.0.0.1:0");                       // this computer only, on a free port
        builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = null);
        builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = long.MaxValue);
        var app = _app = builder.Build();
        var version = typeof(Api).Assembly.GetName().Version?.ToString() ?? "0";

        Secure(app);
        MapDeletion(app);
        app.MapGet("/healthz",()=>Results.Ok(new {ok=true}));
        var staticTypes = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
        staticTypes.Mappings[".vtt"] = "text/vtt";
        app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(Paths.Web), RequestPath = "/static", ContentTypeProvider = staticTypes });
        app.MapGet("/", () => Results.Text(File.ReadAllText(Path.Combine(Paths.Web, "index.html")).Replace("{{v}}", version + "-" + File.GetLastWriteTimeUtc(Path.Combine(Paths.Web, "editor.js")).Ticks).Replace("{{csrf}}", _csrf), "text/html"));

        app.MapGet("/api/config", () =>
        {
            var s = _store.Settings;
            return Json(new JsonObject
            {
                ["rights"] = Arr(Rights.Select(r => (JsonNode)r)), ["languages"] = new JsonArray("es", "en"), ["llm"] = "anthropic", ["api_key_set"] = _ai.MissingActive == null,
                ["provider"] = ProviderJson(), ["version"] = typeof(Api).Assembly.GetName().Version?.ToString(3),
                ["models"] = new JsonObject(AiSettings.Tiers.Select(t => KeyValuePair.Create(t, (JsonNode?)_ai.Conf().Models[t]))), ["tiers"] = Pipeline.TierList(_ai),
                ["desktop"] = true, ["ai"] = true, ["claude_review"] = false, ["ffmpeg"] = Ff.Available,
                ["gpu"] = Hw.Json(), ["devices"] = Hw.DeviceList(),
                ["render"] = new JsonObject { ["encoder"] = s.Encoder, ["device"] = s.Device, ["resolved_encoder"] = s.ResolvedEncoder, ["fps"] = s.Fps, ["quality"] = s.Quality, ["gpu_decode"] = s.GpuDecode },
                ["data_dir"] = Paths.Data,
                ["output_dir"] = _store.OutputRoot, ["setup_completed"] = s.SetupCompleted, ["transcription_mode"] = s.TranscriptionMode,
            });
        });
        app.MapPut("/api/settings", async (HttpRequest req) =>
        {
            var b = await JsonNode.ParseAsync(req.Body) ?? new JsonObject();
            var s = _store.Settings;
            if (J.S(b["encoder"]) == "auto" || Hw.Known.Contains(J.S(b["encoder"]))) s.Encoder = J.S(b["encoder"]);
            if (b["device"] != null)
            {
                var device = J.S(b["device"]);
                if (device != "auto" && device != "cpu" && !Hw.Adapters().Any(a => !a.Software && device == $"dxgi:{a.Index}")) return Err(400, "Dispositivo no válido.");
                if (device.StartsWith("dxgi:") && Hw.DeviceEncoder(device) == "libx264") return Err(400, "Esta GPU no superó la prueba de codificación. Actualiza su controlador o elige CPU.");
                s.Device = device;
            }
            if (J.D(b["fps"]) is 30 or 60) s.Fps = (int)J.D(b["fps"]);
            if (b["quality"] != null) s.Quality = (int)J.Num(b["quality"], 19, 14, 30);
            if (b["gpu_decode"] != null) s.GpuDecode = J.B(b["gpu_decode"]);
            _store.Settings = s;
            return Json(new JsonObject { ["ok"] = true });
        });
        MapSetup(app);
        MapProjects(app);
        MapVideoProjects(app);
        MapResources(app);

        // ---------- AI provider connection, usage and tools ----------
        string? Provider(string? pid) => string.IsNullOrEmpty(pid) ? _ai.ActiveId : AiSettings.Meta(pid) != null ? pid : null;
        JsonObject WithModels(string? warning, List<string>? models)
        {
            var st = _ai.State();
            st["warning"] = warning;
            st["models"] = models == null ? null : new JsonArray(models.Select(m => (JsonNode)m).ToArray());
            return st;
        }
        app.MapGet("/api/settings/ai", () => Json(_ai.State()));
        app.MapGet("/api/settings/ai/providers", () => Json(J.Clone(AiSettings.Catalog)));
        app.MapPut("/api/settings/ai/provider", async (HttpRequest req) =>
        {
            var b = await JsonNode.ParseAsync(req.Body) ?? new JsonObject();
            if (Provider(J.S(b["provider"])) is not { } pid || J.S(b["provider"]) == "") return Err(400, "Proveedor desconocido.");
            var meta = AiSettings.Meta(pid)!;
            string? url = b["base_url"] is JsonValue ? J.S(b["base_url"]).Trim() : null;
            if (url is { Length: > 0 } && !Regex.IsMatch(url, "^https?://")) return Err(400, "La dirección del servidor debe empezar por http:// o https://.");
            if (url is { Length: > 0 } && !J.B(meta["editable_url"]) && url.TrimEnd('/') != J.S(meta["base_url"])) return Err(400, $"La dirección de {J.S(meta["name"])} es fija.");
            var models = b["models"] as JsonObject;
            if (models != null && models.Any(kv => J.S(kv.Value).Length > 200)) return Err(400, "Nombre de modelo demasiado largo.");
            var prices = b["prices"] as JsonObject;
            if (prices != null && prices.Any(kv => kv.Value is not JsonArray { Count: 2 } a || a.Any(x => J.D(x, -1) is < 0 or > 10_000)))
                return Err(400, "Precios no válidos: usa [entrada, salida] entre 0 y 10.000 USD por millón de tokens.");
            _ai.SetProvider(pid, url, models, prices, b["activate"] == null || J.B(b["activate"]));
            return Json(_ai.State());
        });
        app.MapGet("/api/settings/ai/models", async (string? provider) =>
        {
            if (Provider(provider) is not { } pid) return Err(400, "Proveedor desconocido.");
            var c = _ai.Conf(pid);
            if (c.NeedsKey && c.Key == null) return Err(400, $"Guarda primero la API key de {c.Name}.");
            try { return Json(new JsonObject { ["models"] = new JsonArray((await _ai.ListModelsAsync(pid)).Select(m => (JsonNode)m).ToArray()) }); }
            catch (KeyRejectedException e) { return Err(400, e.Message); }
        });
        app.MapPut("/api/settings/ai/key", async (HttpRequest req) =>
        {
            var b = await JsonNode.ParseAsync(req.Body) ?? new JsonObject();
            if (Provider(J.S(b["provider"])) is not { } pid) return Err(400, "Proveedor desconocido.");
            try { var (warning, models) = await _ai.SetKeyAsync(J.S(b["key"]), pid); return Json(WithModels(warning, models)); }
            catch (KeyRejectedException e) { return Err(400, e.Message); }
        });
        app.MapPost("/api/settings/ai/test", async (string? provider) =>
        {
            if (Provider(provider) is not { } pid) return Err(400, "Proveedor desconocido.");
            try { var (warning, models) = await _ai.TestAsync(pid); return Json(WithModels(warning, models)); }
            catch (KeyRejectedException e) { return Err(400, e.Message); }
        });
        app.MapDelete("/api/settings/ai/key", (string? provider) =>
        {
            if (Provider(provider) is not { } pid) return Err(400, "Proveedor desconocido.");
            _ai.RemoveKey(pid);
            return Json(_ai.State());
        });
        app.MapPut("/api/settings/ai/budget", async (HttpRequest req) =>
        {
            var b = await JsonNode.ParseAsync(req.Body) ?? new JsonObject();
            double? usd = b["usd"] is JsonValue ? J.D(b["usd"]) : null;
            if (usd is < 0 or > 1_000_000) return Err(400, "El crédito debe estar entre 0 y 1.000.000 USD.");
            _ai.SetBudget(usd);
            return Json(_ai.State());
        });
        app.MapGet("/api/usage", (int? days) => Json(_ai.Summarize(Math.Max(1, Math.Min(days ?? 30, 365)))));
        app.MapGet("/api/tools", () => Json(Tools.Status()));
        app.MapPost("/api/tools/install", () => Json(new JsonObject { ["run_id"] = _runs.Start("Instalar FFmpeg y yt-dlp", log => Tools.InstallAsync(log).GetAwaiter().GetResult()) }));
        app.MapPost("/api/tools/whisper/install", () => Json(new JsonObject { ["run_id"] = _runs.Start("Instalar transcripción", log => Tools.InstallWhisperAsync(log).GetAwaiter().GetResult()) }));

        // ---------- sources ----------
        app.MapGet("/api/sources",(string? project_id) => Guard(() => Json(Arr(_store.List("sources", project_id).Select(s => { s = _store.Source(J.S(s["id"]))!; s.Remove("words"); return (JsonNode)s; })))));
        app.MapGet("/api/inputs", () => Json(new JsonArray()));
        app.MapPost("/api/sources/upload", async (HttpRequest req) =>
        {
            var form = await req.ReadFormAsync();
            string projectId = form["project_id"].ToString() is { Length: > 0 } p ? p : Store.DefaultProject;
            if (!HasProject(projectId)) return Err(404, "No existe el proyecto.");
            string rights = form["rights"].ToString(), evidence = form["evidence"].ToString();
            if (CheckRights(rights, evidence) is { } problem) return Err(400, problem);
            if (form.Files.GetFile("file") is not { Length: > 0 } file) return Err(400, "Elige un archivo de video.");
            var name = Regex.Replace(Path.GetFileName(file.FileName), @"[^\w.\- ]", "_");
            var path = Path.Combine(Paths.Dir("projects", projectId, "videos"), $"{Guid.NewGuid():N}_{name}");
            await using (var fs = File.Create(path)) await file.CopyToAsync(fs);
            try { return Json(Register(path, rights, evidence.Trim(), form["credit"].ToString().Trim(), form["language"].ToString(), projectId)); }
            catch (FfmpegException e) { File.Delete(path); return Err(400, "No se pudo leer el video: " + e.Message.Split('\n')[0]); }
        });
        // Desktop only: register a file where it already is, without copying it.
        app.MapPost("/api/sources/from-path", async (HttpRequest req) =>
        {
            var b = await JsonNode.ParseAsync(req.Body) ?? new JsonObject();
            string path = J.S(b["path"]), rights = J.S(b["rights"]), evidence = J.S(b["evidence"]);
            if (CheckRights(rights, evidence) is { } problem) return Err(400, problem);
            if (!File.Exists(path)) return Err(400, "No se encuentra ese archivo.");
            return Guard(() => Json(Register(path, rights, evidence.Trim(), J.S(b["credit"]).Trim(), J.S(b["language"]), J.S(b["project_id"], Store.DefaultProject))));
        });
        app.MapPost("/api/sources/from-url", async (HttpRequest req) =>
        {
            var form = await req.ReadFormAsync();
            string projectId = form["project_id"].ToString() is { Length: > 0 } p ? p : Store.DefaultProject;
            if (!HasProject(projectId)) return Err(404, "No existe el proyecto.");
            string url = form["url"].ToString().Trim(), rights = form["rights"].ToString(), evidence = form["evidence"].ToString();
            if (CheckRights(rights, evidence) is { } problem) return Err(400, problem);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !AllowedHost.IsMatch(uri.Host))
                return Err(400, "Solo se aceptan enlaces https de YouTube, Twitch o Kick.");
            string credit = form["credit"].ToString().Trim(), language = form["language"].ToString();
            var run = _runs.Start($"Descargar {uri.Host}", log =>
            {
                log($"Descargando {url}");
                var dir = Paths.Dir("projects", projectId, "videos");
                var (code, stdout, err) = Ff.Exec("yt-dlp", ["--no-playlist", "-S", "vcodec:h264,res:1080,acodec:aac", "--merge-output-format", "mp4",
                    "--ffmpeg-location", Path.GetDirectoryName(Paths.Tool("ffmpeg")) ?? ".", "-o", Path.Combine(dir, "%(id)s.%(ext)s"), "--print", "after_move:filepath", "--no-progress", url]);
                var path = stdout.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l != "") ?? "";
                if (code != 0 || !File.Exists(path)) throw new FfmpegException("yt-dlp no pudo descargar el video: " + Ff.Tail(err.Trim(), 600));
                var src = Register(path, rights, evidence.Trim(), credit, language, projectId);
                log($"Registrado como {J.S(src["id"])} · {J.D(src["duration"]):0} s · {src["width"]}×{src["height"]}");
            }, projectId: projectId);
            return Json(new JsonObject { ["run_id"] = run });
        });
        app.MapGet("/api/sources/{id}/video", (string id) => _store.Get("sources", id) is { } s && File.Exists(J.S(s["path"]))
            ? Results.File(J.S(s["path"]), "video/mp4", enableRangeProcessing: true) : Err(404, "Video no encontrado."));
        app.MapGet("/api/sources/{id}/frame", (string id, double? t, int? h) => Guard(() =>
        {
            var src = _store.Get("sources", id) ?? throw new KeyNotFoundException(id);
            return Results.File(_editor.Frame(src, t ?? 0, h ?? 72), "image/jpeg");
        }));
        app.MapPost("/api/sources/{id}/clip", async (string id, HttpRequest req) =>
        {
            var b = await JsonNode.ParseAsync(req.Body) ?? new JsonObject();
            return Guard(() => Json(new JsonObject { ["id"] = _editor.CreateClip(id, J.D(b["start"]), J.D(b["duration"], 45)) }));
        });

        // ---------- runs, jobs, clips ----------
        app.MapGet("/api/runs", (string? project_id) => Json(Arr(_runs.All.Where(r => RunInProject(r, project_id)).Select(r => (JsonNode)r.Json(false)))));
        app.MapGet("/api/runs/{id}", (string id) => _runs.Get(id) is { } r ? Json(r.Json(true)) : Err(404, "No existe."));
        MapJobs(app);
        app.MapGet("/api/clips", (string? project_id) => Json(Arr(_store.List("clips", project_id).Select(c => (JsonNode)Public(c)))));
        app.MapGet("/api/queue", (string? project_id) => Json(Arr(_store.List("clips", project_id).Where(c => J.S(c["status"]) is "needs_human" or "pending_approval" && c["package_dir"] != null).Select(c => (JsonNode)Public(c)))));
        app.MapGet("/api/clips/{id}", (string id) => _store.Get("clips", id) is { } c ? Json(Public(c)) : Err(404, "No existe."));
        app.MapGet("/api/clips/{id}/file/{lang}/{kind}", (string id, string lang, string kind, bool? download) =>
        {
            var file = kind switch { "video" => "clip.mp4", "srt" => "clip.srt", "metadata" => "metadata.json", _ => "" };
            if (_store.Get("clips", id) is not { } c || file == "" || !SafeId.IsMatch(lang)) return Err(404, "No existe.");
            var path = Path.Combine(J.S(c["package_dir"]), lang, file);
            if (!File.Exists(path)) return Err(404, "Archivo no disponible.");
            return Results.File(path, kind == "video" ? "video/mp4" : "text/plain", download == true ? $"{id}_{lang}{Path.GetExtension(file)}" : null, enableRangeProcessing: true);
        });
        app.MapGet("/api/clips/{id}/thumb", (string id, string? lang) => Guard(() =>
        {
            var c = _store.Get("clips", id) ?? throw new KeyNotFoundException(id);
            return _editor.Thumb(c, lang) is { } p ? Results.File(p, "image/jpeg") : Err(404, "Sin miniatura.");
        }));
        foreach (var (verb, status) in new[] { ("approve", "approved"), ("reject", "rejected_by_human") })
            app.MapPost($"/api/clips/{{id}}/{verb}", async (string id, HttpRequest req) =>
            {
                var b = await JsonNode.ParseAsync(req.Body) ?? new JsonObject();
                if (_store.Get("clips", id) is not { } c) return Err(404, "No existe.");
                if (c["package_dir"] == null) return Err(400, "Renderiza el clip antes de decidir.");
                _store.Update("clips", id, ("status", status), ("decided_by", J.S(b["by"])), ("decision_note", J.S(b["note"])), ("decided_at", Store.Now()));
                _editor.WritePackage(id);
                return Json(new JsonObject { ["status"] = status });
            });
        app.MapPost("/api/clips/{id}/reveal", (string id) =>
        {
            if (_store.Get("clips", id) is not { } c || !Directory.Exists(J.S(c["package_dir"]))) return Err(404, "Este clip todavía no tiene archivos.");
            System.Diagnostics.Process.Start("explorer.exe", $"\"{J.S(c["package_dir"])}\"");
            return Json(new JsonObject { ["ok"] = true });
        });

        // ---------- editor ----------
        app.MapGet("/api/clips/{id}/editor", (string id) => Guard(() => Json(_editor.State(id))));
        app.MapPut("/api/clips/{id}/draft", async (string id, HttpRequest req) =>
        {
            var payload = await JsonNode.ParseAsync(req.Body) ?? new JsonObject();
            return Guard(() => Json(_editor.SaveDraft(id, payload)));
        });
        app.MapDelete("/api/clips/{id}/draft", (string id) => Guard(() => { _editor.DiscardDraft(id); return Json(new JsonObject { ["ok"] = true }); }));
        app.MapPost("/api/clips/{id}/render", async (string id, HttpRequest req) =>
        {
            JsonNode? payload = null;
            try { if (req.ContentLength is > 2) payload = await JsonNode.ParseAsync(req.Body); } catch (System.Text.Json.JsonException) { }
            return Guard(() =>
            {
                if (payload is JsonObject) _editor.SaveDraft(id, payload);
                var (clip, _) = _editor.Load(id);
                if (clip["draft"] == null) throw new EditException("No hay cambios que renderizar.");
                if (!Ff.Available) throw new EditException("Falta FFmpeg. Ejecuta desktop\\setup.ps1 para descargarlo.");
                return Json(new JsonObject { ["run_id"] = _runs.Start($"Render {id}", log => _editor.RenderDraft(id, log), projectId: J.S(clip["project_id"], Store.DefaultProject)) });
            });
        });
        app.MapGet("/api/clips/{id}/waveform", (string id, double a, double b) => Guard(() =>
        {
            var (_, src) = _editor.Load(id);
            a = Math.Max(0, a); b = Math.Min(J.D(src["duration"]), Math.Min(b, a + EditorSvc.MaxDur));
            return Json(_editor.Waveform(src, a, b));
        }));
        app.MapPost("/api/clips/{id}/proxy", (string id, double a, double b) => Guard(() =>
        {
            var (_, src) = _editor.Load(id);
            a = Math.Max(0, a); b = Math.Min(J.D(src["duration"]), Math.Min(b, a + 600));
            var file = _editor.Proxy(id, src, a, b);
            return Json(new JsonObject { ["url"] = "/api/proxy/" + Path.GetFileName(file), ["offset"] = a });
        }));
        app.MapGet("/api/proxy/{name}", (string name) => SafeId.IsMatch(Path.GetFileNameWithoutExtension(name)) && File.Exists(Path.Combine(Paths.Dir("cache", "proxy"), name))
            ? Results.File(Path.Combine(Paths.Dir("cache", "proxy"), name), "video/mp4", enableRangeProcessing: true) : Err(404, "No existe."));
        app.MapGet("/api/sfx/{id}", (string id) => Renderer.Library().TryGetValue(id, out var e)
            ? Results.File(Path.Combine(Paths.Sfx, J.S(e["file"])), "audio/wav", enableRangeProcessing: true) : Err(404, "Sonido no disponible."));
        app.MapGet("/api/fonts/{id}", (string id) => Studio.Fonts.TryGetValue(id, out var f) ? Results.File(f.Path, "font/otf") : Err(404, "Fuente no disponible."));
        app.MapGet("/api/studio/vignette.png", () => Results.File(Path.Combine(Paths.StudioDir, "vignette.png"), "image/png"));

        await app.StartAsync();
        Url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    public Task StopAsync() => _app?.StopAsync() ?? Task.CompletedTask;
}
