// Library on disk (one JSON file) and the single-lane job runner.
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClipperStudio.Engine;

/// <summary>Sources, clips and settings in %LOCALAPPDATA%\ClipperStudio\library.json. Every access goes
/// through the lock, and callers get copies, so a render thread never sees a half-written clip.</summary>
public partial class Store
{
    public const string DefaultProject = "project_default";
    readonly object _lock = new();
    readonly string _file = Path.Combine(Paths.Dir(), "library.json");
    readonly JsonObject _db;

    public Store()
    {
        JsonObject? db = null;
        try { if (File.Exists(_file)) db = JsonNode.Parse(File.ReadAllText(_file)) as JsonObject; } catch (JsonException) { File.Copy(_file, _file + ".dañado", true); }
        _db = db ?? [];
        bool changed = false;
        foreach (var k in new[] { "sources", "clips", "jobs", "projects", "resources", "video_projects" }) if (_db[k] is not JsonArray) { _db[k] = new JsonArray(); changed = true; }
        if (_db["settings"] is not JsonObject) _db["settings"] = new JsonObject();
        if (!Arr("projects").Any(p => J.S(p!["id"]) == DefaultProject))
        { Arr("projects").Add(new JsonObject { ["id"] = DefaultProject, ["name"] = "General", ["description"] = "Biblioteca inicial", ["created_at"] = Now() }); changed = true; }
        foreach (var kind in new[] { "sources", "jobs", "clips" })
            foreach (var item in Arr(kind).OfType<JsonObject>())
                if (item["project_id"] == null) { item["project_id"] = InheritProject(kind, item); changed = true; }
        if (changed) Save();
    }

    void Save()
    {
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, _db.ToJsonString(J.Pretty));
        File.Move(tmp, _file, true);
    }

    JsonArray Arr(string k) => (JsonArray)_db[k]!;
    public static string Now() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss");

    public List<JsonObject> List(string kind, string? projectId = null)
    {
        lock (_lock) return Arr(kind).Where(x => projectId == null || J.S(x!["project_id"]) == projectId).Select(x => (JsonObject)J.Clone(x!)).ToList();
    }

    public JsonObject? Get(string kind, string id)
    {
        lock (_lock) return Arr(kind).FirstOrDefault(x => J.S(x!["id"]) == id) is { } n ? (JsonObject)J.Clone(n) : null;
    }

    public void Add(string kind, JsonObject item)
    {
        lock (_lock)
        {
            var record = (JsonObject)J.Clone(item);
            if (kind is "sources" or "jobs" or "clips" && record["project_id"] == null) record["project_id"] = InheritProject(kind, record);
            Arr(kind).Insert(0, record); Save();
        }
    }

    string InheritProject(string kind, JsonObject item)
    {
        if (kind == "sources") return DefaultProject;
        string table = kind == "jobs" ? "sources" : "jobs", id = J.S(item[kind == "jobs" ? "source_id" : "job_id"]);
        var parent = Arr(table).FirstOrDefault(x => J.S(x!["id"]) == id);
        if (parent == null && kind == "clips") parent = Arr("sources").FirstOrDefault(x => J.S(x!["id"]) == J.S(item["edl"]?["source"]?["id"]));
        return J.S(parent?["project_id"], DefaultProject);
    }

    public void MoveSource(string sourceId, string projectId)
    {
        lock (_lock)
        {
            if (Get("projects", projectId) == null || Get("sources", sourceId) == null) throw new KeyNotFoundException();
            var jobs = Arr("jobs").OfType<JsonObject>().Where(j => J.S(j["source_id"]) == sourceId).ToList();
            if (jobs.Any(j => J.S(j["status"]) == "running")) throw new EditException("Espera a que termine el trabajo de este video para moverlo.");
            var ids = jobs.Select(j => J.S(j["id"])).ToHashSet();
            foreach (var job in jobs) job["project_id"] = projectId;
            foreach (var clip in Arr("clips").OfType<JsonObject>().Where(c => ids.Contains(J.S(c["job_id"])) || J.S(c["edl"]?["source"]?["id"]) == sourceId)) clip["project_id"] = projectId;
            Arr("sources").OfType<JsonObject>().Single(s => J.S(s["id"]) == sourceId)["project_id"] = projectId;
            Save();
        }
    }

    public JsonObject? Source(string id)
    {
        var src = Get("sources", id);
        if (src == null || J.D(src["width"]) > 0 && J.D(src["height"]) > 0 || !File.Exists(J.S(src["path"]))) return src;
        var info = Ff.Probe(J.S(src["path"]));
        Update("sources", id, info.Select(kv => (kv.Key, kv.Value)).ToArray());
        return Get("sources", id);
    }

    public string OutputRoot => Settings.OutputDirectory ?? Path.Combine(Paths.Data, "out");

    /// <summary>Set fields of one item. A null value stores JSON null.</summary>
    public void Update(string kind, string id, params (string key, JsonNode? value)[] fields)
    {
        lock (_lock)
        {
            if (Arr(kind).FirstOrDefault(x => J.S(x!["id"]) == id) is not JsonObject item) throw new KeyNotFoundException(id);
            foreach (var (key, value) in fields) item[key] = value == null ? null : J.Clone(value);
            Save();
        }
    }

    public RenderSettings Settings
    {
        get { lock (_lock) return _db["settings"].Deserialize<RenderSettings>() ?? new(); }
        set { lock (_lock) { _db["settings"] = JsonSerializer.SerializeToNode(value); Save(); } }
    }
}

public class RunInfo
{
    public string? ProjectId { get; init; }
    public long Sequence { get; init; }
    public string? JobId { get; init; }
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..10];
    public string Label { get; init; } = "";
    public string Status { get; set; } = "queued";
    public List<string> Log { get; } = [];
    public string? Error { get; set; }
    public string CreatedAt { get; } = Store.Now();

    public JsonObject Json(bool full)
    {
        lock (Log)
        {
            var o = new JsonObject { ["id"] = Id, ["label"] = Label, ["status"] = Status, ["error"] = Error, ["job_id"] = JobId, ["project_id"] = ProjectId, ["created_at"] = CreatedAt };
            o["log"] = new JsonArray((full ? Log : Log.TakeLast(6)).Select(l => (JsonNode)l).ToArray());
            return o;
        }
    }
}

/// <summary>Downloads and renders run one at a time, in order: two encodes at once would only fight for the GPU.</summary>
public class Runs
{
    long _sequence;
    readonly BlockingCollection<(RunInfo run, Action<Action<string>> work)> _queue = [];
    readonly ConcurrentDictionary<string, RunInfo> _all = new();

    public Runs()
    {
        new Thread(() =>
        {
            foreach (var (run, work) in _queue.GetConsumingEnumerable())
            {
                run.Status = "running";
                try { work(line => { lock (run.Log) run.Log.Add(line); }); run.Status = "done"; }
                catch (Exception e) { run.Error = e.Message.Length > 1500 ? e.Message[^1500..] : e.Message; run.Status = "failed"; }
            }
        }) { IsBackground = true, Name = "runs" }.Start();
    }

    public string Start(string label, Action<Action<string>> work, string? jobId = null, string? projectId = null)
    {
        var run = new RunInfo { Label = label, JobId = jobId, ProjectId = projectId, Sequence = Interlocked.Increment(ref _sequence) };
        _all[run.Id] = run;
        _queue.Add((run, work));
        return run.Id;
    }

    public RunInfo? Get(string id) => _all.GetValueOrDefault(id);
    public IEnumerable<RunInfo> All => _all.Values.OrderByDescending(r => r.Sequence);
    public bool Busy => _all.Values.Any(r => r.Status is "queued" or "running");
}
