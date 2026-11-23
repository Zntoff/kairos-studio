using System.Text;
using System.Net.Http;
using System.Text.Json.Nodes;
using ClipperStudio.Engine;

namespace ClipperStudio.Ai;

public partial class LlmClient
{
    public record ToolTurn(JsonObject Message, string Model, long Input, long Output);

    // Canonical conversation uses Chat Completions roles; the Anthropic adapter
    // preserves tool-use IDs and groups the matching results into one user turn.
    public static JsonArray AnthropicToolMessages(JsonArray messages)
    {
        var result=new JsonArray();
        foreach(var message in J.Items(messages))
        {
            string role=J.S(message["role"]);
            if(role=="tool")
            {
                if(result.LastOrDefault() is not JsonObject last || J.S(last["role"])!="user" || last["content"] is not JsonArray)
                    result.Add(new JsonObject{["role"]="user",["content"]=new JsonArray()});
                result.Last()!["content"]!.AsArray().Add(new JsonObject{["type"]="tool_result",["tool_use_id"]=J.S(message["tool_call_id"]),["content"]=J.S(message["content"])});
            }
            else if(role=="assistant")
            {
                var content=new JsonArray();
                if(J.S(message["content"]) is {Length:>0} text)content.Add(new JsonObject{["type"]="text",["text"]=text});
                foreach(var call in J.Items(message["tool_calls"]))
                    content.Add(new JsonObject{["type"]="tool_use",["id"]=J.S(call["id"]),["name"]=J.S(call["function"]?["name"]),
                        ["input"]=ParseJson(J.S(call["function"]?["arguments"]))??new JsonObject()});
                result.Add(new JsonObject{["role"]="assistant",["content"]=content});
            }
            else result.Add(new JsonObject{["role"]="user",["content"]=J.S(message["content"])});
        }
        return result;
    }

    public async Task<ToolTurn> ToolTurnAsync(string tier,string system,JsonArray messages,JsonArray definitions,int maxTokens=2000)
    {
        var c=config;string model=ModelFor(tier);
        if(AiSettings.Missing(c) is {} missing)throw new LlmException(missing);
        JsonNode response;
        if(c.Kind=="anthropic")
        {
            var body=new JsonObject{["model"]=model,["max_tokens"]=maxTokens,["system"]=system,["messages"]=AnthropicToolMessages(messages),
                ["tools"]=new JsonArray(J.Items(definitions).Select(t=>(JsonNode)new JsonObject{["name"]=J.S(t["name"]),["description"]=J.S(t["description"]),["input_schema"]=t["inputSchema"]!.DeepClone()}).ToArray()),
                ["tool_choice"]=new JsonObject{["type"]="auto"}};
            string payload=body.ToJsonString();response=null!;
            for(int attempt=0;attempt<4;attempt++)
            {
                using var request=new HttpRequestMessage(HttpMethod.Post,c.BaseUrl+"/v1/messages"){Content=new StringContent(payload,Encoding.UTF8,"application/json")};
                request.Headers.Add("x-api-key",c.Key);request.Headers.Add("anthropic-version","2023-06-01");
                HttpResponseMessage reply;
                try{reply=await Http.SendAsync(request);}
                catch(Exception e) when(e is HttpRequestException or TaskCanceledException)
                {if(attempt==3)throw new LlmException("No se pudo conectar con el proveedor de IA.");await Task.Delay(1000*(attempt+1));continue;}
                using(reply)
                {
                    string text=await reply.Content.ReadAsStringAsync();int status=(int)reply.StatusCode;
                    if(status is 429 or 500 or 502 or 503 or 529 && attempt<3){await Task.Delay(TimeSpan.FromSeconds(Math.Min(20,2<<attempt)));continue;}
                    if(!reply.IsSuccessStatusCode)throw Friendly(c,new HttpFailure(status,text),model);
                    try{response=JsonNode.Parse(text)!;}catch(System.Text.Json.JsonException){throw new LlmException("El proveedor devolvió una respuesta de herramientas inválida.");}
                }
                break;
            }
            if(response==null)throw new LlmException("El proveedor no completó la llamada de herramientas.");
            long input=(long)J.D(response["usage"]?["input_tokens"]),output=(long)J.D(response["usage"]?["output_tokens"]);
            string used=J.S(response["model"],model);ai.AddUsage("video_editor",used,input,output,c.Id);
            if(J.S(response["stop_reason"])=="max_tokens")throw new LlmException("La respuesta de herramientas se cortó; no se aplicaron cambios.");
            var calls=new JsonArray();var texts=new List<string>();
            foreach(var block in J.Items(response["content"]))
            {
                if(J.S(block["type"])=="text")texts.Add(J.S(block["text"]));
                if(J.S(block["type"])=="tool_use")calls.Add(new JsonObject{["id"]=J.S(block["id"]),["type"]="function",["function"]=new JsonObject{["name"]=J.S(block["name"]),["arguments"]=block["input"]?.ToJsonString()??"{}"}});
            }
            return new ToolTurn(new JsonObject{["role"]="assistant",["content"]=string.Join('\n',texts),["tool_calls"]=calls},used,input,output);
        }
        string tokensField=c.MaxTokensField;
        JsonObject Body()=>new(){["model"]=model,[tokensField]=maxTokens,
            ["messages"]=new JsonArray(new[]{(JsonNode)new JsonObject{["role"]="system",["content"]=system}}.Concat(J.Items(messages).Select(J.Clone)).ToArray()),
            ["tools"]=new JsonArray(J.Items(definitions).Select(t=>(JsonNode)new JsonObject{["type"]="function",["function"]=new JsonObject{["name"]=J.S(t["name"]),["description"]=J.S(t["description"]),["parameters"]=Portable(t["inputSchema"]!.AsObject(),c.Dialect)}}).ToArray()),["tool_choice"]="auto"};
        var (ok,failure)=await PostAsync(c,Body().ToJsonString());
        if(ok==null && failure!.Status==400 && tokensField=="max_tokens" && failure.Text.Contains("max_completion_tokens"))
        {tokensField="max_completion_tokens";(ok,failure)=await PostAsync(c,Body().ToJsonString());}
        if(ok==null)
        {
            if(failure!.Status is 400 or 422 && (failure.Text.Contains("tools",StringComparison.OrdinalIgnoreCase)||failure.Text.Contains("function",StringComparison.OrdinalIgnoreCase)))
                throw new LlmException("El modelo rechazó las herramientas de edición. Elige un modelo compatible con llamadas a funciones.");
            throw Friendly(c,failure!,model);
        }
        response=ok;string usedModel=J.S(response["model"],model);
        long tin=(long)J.D(response["usage"]?["prompt_tokens"]),tout=(long)J.D(response["usage"]?["completion_tokens"]);
        ai.AddUsage("video_editor",usedModel,tin,tout,c.Id);
        if(J.S(response["choices"]?[0]?["finish_reason"])=="length")throw new LlmException("La respuesta de herramientas se cortó; no se aplicaron cambios.");
        var message=response["choices"]?[0]?["message"] as JsonObject ?? throw new LlmException("El proveedor no devolvió un mensaje de herramientas.");
        return new ToolTurn(new JsonObject{["role"]="assistant",["content"]=J.S(message["content"]),["tool_calls"]=message["tool_calls"]?.DeepClone()??new JsonArray()},usedModel,tin,tout);
    }
}
