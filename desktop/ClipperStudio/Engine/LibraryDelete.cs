using System.Text.Json.Nodes;

namespace ClipperStudio.Engine;

public partial class Store
{
    public JsonObject DeleteLibrary(string kind,string id)
    {
        lock(_lock)
        {
            var item=Get(kind,id)??throw new KeyNotFoundException();
            if(kind=="projects" && id==DefaultProject)throw new EditException("The default project cannot be deleted. You can delete its content.");
            bool project=kind=="projects";
            string pid=project?id:J.S(item["project_id"]);
            var sources=List("sources").Where(s=>project?J.S(s["project_id"])==pid:kind=="sources"&&J.S(s["id"])==id).ToList();
            var sourceIds=sources.Select(s=>J.S(s["id"])).ToHashSet();
            var jobs=List("jobs").Where(j=>sourceIds.Contains(J.S(j["source_id"])) || project&&J.S(j["project_id"])==pid).ToList();
            var jobIds=jobs.Select(j=>J.S(j["id"])).ToHashSet();
            var clips=List("clips").Where(c=>jobIds.Contains(J.S(c["job_id"])) || sourceIds.Contains(J.S(c["edl"]?["source"]?["id"])) || project&&J.S(c["project_id"])==pid || kind=="clips"&&J.S(c["id"])==id).ToList();
            var resources=List("resources").Where(r=>project?J.S(r["project_id"])==pid:kind=="resources"&&J.S(r["id"])==id).ToList();
            var clipIds=clips.Select(c=>J.S(c["id"])).ToHashSet();
            var videos=List("video_projects").Where(v=>project?J.S(v["project_id"])==pid:kind=="video_projects"&&J.S(v["id"])==id).ToList();
            var videoIds=videos.Select(v=>J.S(v["id"])).ToHashSet();
            var resourceIds=resources.Select(r=>J.S(r["id"])).ToHashSet();
            string trash=Paths.Dir("trash",Guid.NewGuid().ToString("N"));
            var paths=videos.Select(v=>Path.Combine(Paths.Data,"projects",J.S(v["project_id"]),"videos",J.S(v["id"]))).Concat(clips.Select(c=>J.S(c["package_dir"])))
                .Concat(sources.Select(s=>J.S(s["path"])))
                .Concat(resources.Select(r=>Path.Combine(Paths.Data,"projects",J.S(r["project_id"]),"resources",J.S(r["id"])+J.S(r["extension"]))))
                .Where(p=>p!="").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var roots=new[]{"out","jobs","downloads","uploads","projects","cache"}.Select(p=>Path.GetFullPath(Path.Combine(Paths.Data,p))).Append(Path.GetFullPath(OutputRoot)).ToList();
            var remaining=List("sources").Where(s=>!sourceIds.Contains(J.S(s["id"]))).Select(s=>J.S(s["path"]))
                .Concat(List("clips").Where(c=>!clipIds.Contains(J.S(c["id"]))).Select(c=>J.S(c["package_dir"]))).Where(p=>p!="").Select(Path.GetFullPath).ToList();
            bool Under(string path,string root)=>path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
            var moved=new List<(string Original,string Target)>();
            var snapshot=J.Obj(J.Clone(_db));
            try
            {
                foreach(var path in paths)
                {
                    var full=Path.GetFullPath(path);
                    if(!File.Exists(full)&&!Directory.Exists(full) || !roots.Any(root=>Under(full,root)))continue;
                    if(remaining.Any(p=>string.Equals(p,full,StringComparison.OrdinalIgnoreCase)||Under(p,full)))continue;
                    bool link=false;
                    for(string? current=full;current!=null;current=Path.GetDirectoryName(current))
                        if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0){link=true;break;}
                    if(link)continue;
                    string targetRoot=trash;
                    if(!string.Equals(Path.GetPathRoot(full),Path.GetPathRoot(trash),StringComparison.OrdinalIgnoreCase))
                    {
                        var owner=roots.Where(root=>Under(full,root)).OrderByDescending(root=>root.Length).First();
                        targetRoot=Path.Combine(owner,".kairos-trash",Path.GetFileName(trash));Directory.CreateDirectory(targetRoot);
                    }
                    string target=Path.Combine(targetRoot,moved.Count.ToString());
                    if(Directory.Exists(full))Directory.Move(full,target);else File.Move(full,target);
                    moved.Add((full,target));
                }
                var manifest=new JsonObject { ["kind"]=kind,["item"]=J.Clone(item),["sources"]=new JsonArray(sources.Select(s=>(JsonNode)J.Clone(s)).ToArray()),
                    ["jobs"]=new JsonArray(jobs.Select(j=>(JsonNode)J.Clone(j)).ToArray()),["clips"]=new JsonArray(clips.Select(c=>(JsonNode)J.Clone(c)).ToArray()),
                    ["video_projects"]=new JsonArray(videos.Select(v=>(JsonNode)J.Clone(v)).ToArray()),
                    ["resources"]=new JsonArray(resources.Select(r=>(JsonNode)J.Clone(r)).ToArray()),
                    ["files"]=new JsonArray(moved.Select(p=>(JsonNode)new JsonObject{["original"]=p.Original,["trash"]=p.Target}).ToArray())};
                File.WriteAllText(Path.Combine(trash,"manifest.json"),manifest.ToJsonString(J.Pretty));
                foreach(var (table,ids) in new[]{("sources",sourceIds),("jobs",jobIds),("clips",clipIds),("resources",resourceIds),("video_projects",videoIds),("projects",project?new HashSet<string>{id}:new HashSet<string>())})
                    foreach(var row in Arr(table).Where(r=>ids.Contains(J.S(r?["id"]))).ToList())Arr(table).Remove(row);
                Save();
            }
            catch
            {
                foreach(var field in snapshot.ToList())_db[field.Key]=field.Value==null?null:J.Clone(field.Value);
                foreach(var (original,target) in moved.AsEnumerable().Reverse())
                {Directory.CreateDirectory(Path.GetDirectoryName(original)!);if(Directory.Exists(target))Directory.Move(target,original);else if(File.Exists(target))File.Move(target,original);}
                throw;
            }
            return new JsonObject { ["ok"]=true,["recoverable"]=true,["removed"]=new JsonObject{["sources"]=sources.Count,["jobs"]=jobs.Count,["clips"]=clips.Count,["resources"]=resources.Count} };
        }
    }
}
