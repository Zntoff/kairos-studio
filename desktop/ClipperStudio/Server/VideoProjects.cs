using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using ClipperStudio.Ai;
using ClipperStudio.Engine;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace ClipperStudio.Server;

public partial class Api
{
    readonly ConcurrentDictionary<string,Lazy<Task<JsonObject>>> _videoRequests=new();
    void MapVideoProjects(WebApplication app)
    {
        var videos=new VideoProjects(_store);
        JsonObject PublicVideo(JsonObject record)
        {
            record.Remove("export_path");
            return record;
        }
        app.MapGet("/api/projects/{project}/videos",(string project)=>Guard(()=>
        {
            if(!HasProject(project))throw new KeyNotFoundException(project);
            return Json(new JsonArray(_store.List("video_projects",project).Select(v=>(JsonNode)PublicVideo(v)).ToArray()));
        }));
        app.MapPost("/api/projects/{project}/videos",async(string project,HttpRequest req)=>
        {
            var body=await JsonNode.ParseAsync(req.Body)??new JsonObject();
            return Guard(()=>Json(PublicVideo(videos.Create(project,body))));
        });
        app.MapGet("/api/video-projects/{id}",(string id)=>Guard(()=>
        {
            var record=videos.Get(id);record["assets"]=videos.Assets(J.S(record["project_id"]));record["studio"]=VideoStudio.Catalog(record["settings"]!);
            return Json(PublicVideo(record));
        }));
        app.MapPut("/api/video-projects/{id}",async(string id,HttpRequest req)=>
        {
            var body=await JsonNode.ParseAsync(req.Body)??new JsonObject();
            return Guard(()=>Json(PublicVideo(videos.Save(id,body))));
        });
        app.MapPost("/api/video-projects/{id}/render",(string id)=>Guard(()=>
        {
            var snapshot=videos.Get(id);
            if(!VideoStudio.HasContent(snapshot["document"]!))throw new EditException("Add media before exporting the video.");
            string run=_runs.Start("Export "+J.S(snapshot["name"]),log=>videos.Render(snapshot,log),projectId:J.S(snapshot["project_id"]));
            return Json(new JsonObject{["run_id"]=run,["revision"]=J.D(snapshot["revision"])});
        }));
        app.MapGet("/api/video-projects/{id}/export",(string id)=>Guard(()=>
        {
            var record=videos.Get(id);string path=J.S(record["export_path"]);
            if(!File.Exists(path))throw new KeyNotFoundException(id);
            string ext=Path.GetExtension(path);
            return Results.File(path,ext==".mov"?"video/quicktime":"video/mp4","Kairos-"+id+ext,enableRangeProcessing:true);
        }));
        app.MapGet("/api/video-projects/{id}/ai/tools",(string id)=>Guard(()=>
        {
            videos.Get(id);
            return Json(new JsonObject{["contract"]=EditorTools.Version,["tools"]=EditorTools.Definitions()});
        }));
        app.MapPost("/api/video-projects/{id}/ai",async(string id,HttpRequest req)=>
        {
            var body=await JsonNode.ParseAsync(req.Body)??new JsonObject();
            try
            {
                videos.Get(id);
                if(AiSettings.Missing(_ai.Conf()) is string missing)return Err(400,missing);
                if(!Guid.TryParse(J.S(body["request_id"]),out _))return Err(400,"Invalid AI request ID.");
                // Retries of the same request reuse the paid result rather than billing again.
                string key=id+":"+J.S(body["request_id"]);
                if(_videoRequests.Count>=200)
                    foreach(var old in _videoRequests.Where(x=>x.Value.IsValueCreated && x.Value.Value.IsCompleted).Take(100))_videoRequests.TryRemove(old.Key,out _);
                var task=_videoRequests.GetOrAdd(key,_=>new Lazy<Task<JsonObject>>(()=>VideoAssistant.Propose(videos,_ai,id,body)));
                return Json(await task.Value);
            }
            catch(Exception e) when(e is EditException or LlmException or KeyNotFoundException)
            {return Err(e is KeyNotFoundException?404:400,e.Message);}
        });
    }
}
