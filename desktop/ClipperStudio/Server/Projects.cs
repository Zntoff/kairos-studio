using System.Text.Json.Nodes;
using ClipperStudio.Engine;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace ClipperStudio.Server;

public partial class Api
{
    bool HasProject(string id) => _store.Get("projects", id) != null;
    void MapProjects(WebApplication app)
    {
        app.MapGet("/api/projects", () => Json(Arr(_store.List("projects").Select(p =>
        {
            var id = J.S(p["id"]);
            p["source_count"] = _store.List("sources", id).Count;
            p["job_count"] = _store.List("jobs", id).Count;
            p["clip_count"] = _store.List("clips", id).Count;
            p["video_count"] = _store.List("video_projects", id).Count;
            p["resource_count"] = _store.List("resources", id).Count;
            return (JsonNode)p;
        }))));
        async Task<IResult> SaveProject(HttpRequest req, string? id)
        {
            var b = await JsonNode.ParseAsync(req.Body) ?? new JsonObject();
            var name = J.S(b["name"]).Trim(); var description = J.S(b["description"]).Trim();
            if (name.Length is < 1 or > 80 || description.Length > 600) return Err(400, "Usa un nombre de 1–80 caracteres y una descripción de hasta 600.");
            if (id == null)
            {
                id = "project_" + Guid.NewGuid().ToString("N")[..10];
                _store.Add("projects", new JsonObject { ["id"] = id, ["name"] = name, ["description"] = description, ["created_at"] = Store.Now() });
            }
            else
            {
                if (!HasProject(id)) return Err(404, "No existe el proyecto.");
                _store.Update("projects", id, ("name", name), ("description", description));
            }
            return Json(_store.Get("projects", id));
        }
        app.MapPost("/api/projects", (HttpRequest req) => SaveProject(req, null));
        app.MapPut("/api/projects/{id}", (string id, HttpRequest req) => SaveProject(req, id));
        app.MapPut("/api/sources/{id}/project", async (string id, HttpRequest req) =>
        {
            var b = await JsonNode.ParseAsync(req.Body) ?? new JsonObject();
            var project = J.S(_store.Get("sources", id)?["project_id"]);
            if (_runs.All.Any(r => (r.Status is "queued" or "running") && RunInProject(r, project))) return Err(409, "Espera a que terminen los trabajos activos del proyecto para mover el video.");
            return Guard(() => { _store.MoveSource(id, J.S(b["project_id"])); return Json(new JsonObject { ["ok"] = true }); });
        });
    }
    bool RunInProject(RunInfo r, string? projectId) => projectId == null ||
        (r.JobId != null ? J.S(_store.Get("jobs", r.JobId)?["project_id"], r.ProjectId ?? "") == projectId : r.ProjectId == null || r.ProjectId == projectId);
}
