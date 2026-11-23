using ClipperStudio.Engine;
using Microsoft.AspNetCore.Builder;
using System.Text.Json.Nodes;

namespace ClipperStudio.Server;

public partial class Api
{
    void MapDeletion(WebApplication app)
    {
        app.MapDelete("/api/library/{kind}/{id}",(string kind,string id)=>
        {
            if(kind is not ("projects" or "sources" or "clips" or "resources" or "video_projects"))return Err(404,"Unknown library item");
            lock(_jobLock)
            {
                var item=_store.Get(kind,id);if(item==null)return Err(404,"Item not found");
                string project=kind=="projects"?id:J.S(item["project_id"]);
                if(_runs.All.Any(r=>(r.Status is "queued" or "running")&&RunInProject(r,project)))return Err(409,"Wait for the project's active jobs to finish before deleting.");
                return Guard(()=>Json(_store.DeleteLibrary(kind,id)));
            }
        });
    }
}
