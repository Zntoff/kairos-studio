// AI client for the subagents. Port of clipper/llm.py and the retry loop of clipper/agents/base.py:
// each subagent answers by calling one tool whose input schema is its contract; answers that break a rule
// are sent back to the model with the error, up to two more times.
// Two wire formats cover every provider in config/providers.json: Anthropic's Messages API, and the OpenAI
// Chat Completions API (OpenAI, Gemini, OpenRouter, Mistral, DeepSeek, Groq, Ollama, LM Studio, vLLM).
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClipperStudio.Engine;

namespace ClipperStudio.Ai;

public class LlmException(string message) : Exception(message);

public class ContractException(string message) : Exception(message);

public record StepMeta(int Attempts, string Model, long Input, long Output, double Seconds);

public partial class LlmClient(Engine.AiSettings ai, ProviderConf? snapshot = null)
{
    readonly ProviderConf config = snapshot ?? ai.Conf(); // Freeze provider, credentials and models for this execution.
    public string? Missing => AiSettings.Missing(config);
    static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(6) };
    static readonly ConcurrentDictionary<string, bool> NoImages = new(), NoForcedTool = new(), NoTools = new();
    static readonly string[] GeminiDrop = ["additionalProperties", "default", "examples", "const", "$schema", "title"];

    /// <summary>Model of the active provider for a tier.</summary>
    public string ModelFor(string tier) => config.Models.GetValueOrDefault(tier) is { Length: > 0 } m ? m : config.Models["standard"];

    public record Reply(JsonObject Data, string Model, long Input, long Output);

    Task<Reply> CompleteAsync(string agent, string model, string system, string user, JsonObject schema, IEnumerable<string>? images, int maxTokens, Action<string> log)
    {
        var c = config;
        if (AiSettings.Missing(c) is { } missing) throw new LlmException(missing);
        return c.Kind == "anthropic" ? AnthropicAsync(c, agent, model, system, user, schema, images, maxTokens)
                                     : OpenAiAsync(c, agent, model, system, user, schema, images, maxTokens, log);
    }

    // ---------- Anthropic Messages API ----------
    async Task<Reply> AnthropicAsync(ProviderConf c, string agent, string model, string system, string user, JsonObject schema, IEnumerable<string>? images, int maxTokens)
    {
        var tool = $"submit_{agent}";
        var content = new JsonArray();
        foreach (var img in images ?? [])
            content.Add(new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = "image/jpeg", ["data"] = Convert.ToBase64String(await File.ReadAllBytesAsync(img)) },
            });
        content.Add(new JsonObject { ["type"] = "text", ["text"] = user });
        // Opus / Sonnet 5.5 reject a forced tool_choice, so the tool is requested in the prompt and the contract is checked afterwards.
        var body = new JsonObject
        {
            ["model"] = model, ["max_tokens"] = maxTokens,
            ["system"] = $"{system}\n\nDeliver your final answer by calling the tool `{tool}` exactly once, with the complete answer as its input. Do not answer in plain text.",
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = content }),
            ["tools"] = new JsonArray(new JsonObject { ["name"] = tool, ["description"] = "Submit your final answer. Call this exactly once.", ["input_schema"] = J.Clone(schema) }),
            ["tool_choice"] = new JsonObject { ["type"] = "auto" },
        };
        var payload = body.ToJsonString();
        for (int attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, c.BaseUrl + "/v1/messages") { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
            req.Headers.Add("x-api-key", c.Key);
            req.Headers.Add("anthropic-version", "2023-06-01");
            HttpResponseMessage res;
            try { res = await Http.SendAsync(req); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                if (attempt < 3) { await Task.Delay(TimeSpan.FromSeconds(2 << attempt)); continue; }
                throw new LlmException("No se pudo conectar con Anthropic. Revisa tu conexión a internet.");
            }
            var text = await res.Content.ReadAsStringAsync();
            int code = (int)res.StatusCode;
            if (code is 429 or 500 or 502 or 503 or 529 && attempt < 4)
            {
                await Task.Delay(res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Min(30, 3 << attempt)));
                continue;
            }
            if (!res.IsSuccessStatusCode)
                throw new LlmException(code switch
                {
                    401 => "Anthropic rechazó la API key. Revísala en Ajustes.",
                    403 => "La API key no tiene permiso para este modelo.",
                    429 => "Anthropic limitó las peticiones de tu cuenta. Espera un minuto y retoma el trabajo.",
                    400 when text.Contains("credit balance", StringComparison.OrdinalIgnoreCase) => "La cuenta de Anthropic se quedó sin crédito. Añade saldo en console.anthropic.com y retoma el trabajo.",
                    _ => $"Anthropic respondió {code}: {Engine.Ff.Tail(text, 300)}",
                });
            var msg = JsonNode.Parse(text)!;
            long input = (long)J.D(msg["usage"]?["input_tokens"]), output = (long)J.D(msg["usage"]?["output_tokens"]);
            var used = J.S(msg["model"], model);
            ai.AddUsage(agent, used, input, output, c.Id);   // recorded before any check: a rejected answer was still billed
            if (J.S(msg["stop_reason"]) == "max_tokens") throw new LlmException($"{agent}: la respuesta se cortó por max_tokens ({maxTokens}).");
            foreach (var block in J.Items(msg["content"]))
                if (J.S(block["type"]) == "tool_use" && J.S(block["name"]) == tool && block["input"] is JsonObject data)
                    return new Reply(data, used, input, output);
            throw new LlmException($"{agent}: la respuesta no incluyó la llamada a {tool}.");
        }
    }

    // ---------- OpenAI-compatible Chat Completions ----------
    record HttpFailure(int Status, string Text);

    async Task<Reply> OpenAiAsync(ProviderConf c, string agent, string model, string system, string user, JsonObject schema, IEnumerable<string>? images, int maxTokens, Action<string> log)
    {
        var tool = $"submit_{agent}";
        var scope = c.Id + "|" + c.BaseUrl + "|" + model;
        var frames = NoImages.ContainsKey(scope) ? [] : (images ?? []).ToList();
        var encoded = new List<string>();
        foreach (var img in frames) encoded.Add("data:image/jpeg;base64," + Convert.ToBase64String(await File.ReadAllBytesAsync(img)));
        var parameters = Portable(schema, c.Dialect);
        bool withImages = encoded.Count > 0, forced = !NoForcedTool.ContainsKey(scope), tools = !NoTools.ContainsKey(scope);
        var tokensField = c.MaxTokensField;

        string Body()
        {
            JsonNode content = withImages
                ? new JsonArray([new JsonObject { ["type"] = "text", ["text"] = user }, .. encoded.Select(u => (JsonNode)new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = u } })])
                : JsonValue.Create(user)!;
            var body = new JsonObject
            {
                ["model"] = model, [tokensField] = maxTokens,
                ["messages"] = new JsonArray(
                    new JsonObject { ["role"] = "system", ["content"] = $"{system}\n\nDeliver your final answer by calling the function `{tool}` exactly once, with the complete answer as its arguments. Do not answer in plain text." },
                    new JsonObject { ["role"] = "user", ["content"] = content }),
                ["tools"] = new JsonArray(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = tool, ["description"] = "Submit your final answer. Call this exactly once.", ["parameters"] = J.Clone(parameters) },
                }),
                ["tool_choice"] = forced ? new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = tool } } : JsonValue.Create("auto"),
            };
            if (!tools)
            {
                body.Remove("tools"); body.Remove("tool_choice");
                body["messages"]![0]!["content"] = system + "\nReturn exactly one JSON object matching this schema. No markdown or commentary.\n" + parameters.ToJsonString();
            }
            return body.ToJsonString();
        }

        JsonNode? resp = null;
        for (int round = 0; round < 6 && resp == null; round++)
        {
            var (ok, failure) = await PostAsync(c, Body());
            if (ok != null) { resp = ok; break; }
            var text = failure!.Text.ToLowerInvariant();
            if (failure.Status == 400 && withImages && new[] { "image", "vision", "multimodal", "content type", "image_url" }.Any(text.Contains))
            {
                NoImages[scope] = true; withImages = false;
                log($"    {model} no acepta imágenes: se continúa sin los fotogramas.");
                continue;
            }
            if (failure.Status == 400 && forced && text.Contains("tool_choice")) { NoForcedTool[scope] = true; forced = false; continue; }
            if (failure.Status is 400 or 422 && tools && (text.Contains("tools") || text.Contains("function calling")) &&
                (text.Contains("not support") || text.Contains("unsupported") || text.Contains("not allowed") || text.Contains("unknown")))
            {
                NoTools[scope] = true; tools = false;
                log($"    {model} no admite herramientas: se solicita JSON y se valida el contrato.");
                continue;
            }
            if (failure.Status == 400 && tokensField == "max_tokens" && text.Contains("max_completion_tokens")) { tokensField = "max_completion_tokens"; continue; }
            throw Friendly(c, failure, model);
        }
        if (resp == null) throw new LlmException($"{c.Name} rechazó la petición de {agent}.");

        var used = J.S(resp["model"], model);
        long input = (long)J.D(resp["usage"]?["prompt_tokens"]), output = (long)J.D(resp["usage"]?["completion_tokens"]);
        ai.AddUsage(agent, used, input, output, c.Id);   // recorded before any check: a rejected answer was still billed
        var choice = resp["choices"]?[0];
        if (J.S(choice?["finish_reason"]) == "length") throw new LlmException($"{agent}: la respuesta se cortó por max_tokens ({maxTokens}).");
        foreach (var call in J.Items(choice?["message"]?["tool_calls"]))
        {
            var fn = call["function"];
            var name = J.S(fn?["name"]);
            if (name != "" && name != tool) continue;
            var data = fn?["arguments"] is JsonObject o ? o : ParseJson(J.S(fn?["arguments"]));
            if (data != null) return new Reply(data, used, input, output);
        }
        if (J.S(choice?["finish_reason"]) == "length") throw new LlmException($"{agent}: la respuesta se cortó por max_tokens ({maxTokens}).");
        if (ParseJson(J.S(choice?["message"]?["content"])) is { } fromText) return new Reply(fromText, used, input, output);
        throw new LlmException($"{agent}: la respuesta no incluyó la llamada a {tool} (finish_reason={J.S(choice?["finish_reason"])}).");
    }

    static async Task<(JsonNode? ok, HttpFailure? failure)> PostAsync(ProviderConf c, string payload)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, c.BaseUrl + "/chat/completions") { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
            if (!string.IsNullOrEmpty(c.Key)) req.Headers.Add("Authorization", "Bearer " + c.Key);
            req.Headers.Add("X-Title", "Kairos Studio");
            HttpResponseMessage res;
            try { res = await Http.SendAsync(req); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                if (attempt < 3) { await Task.Delay(TimeSpan.FromSeconds(2 << attempt)); continue; }
                throw new LlmException($"No se pudo conectar con {c.Name}. Revisa tu conexión o la dirección del servidor.");
            }
            var text = await res.Content.ReadAsStringAsync();
            int code = (int)res.StatusCode;
            if (code is 429 or 500 or 502 or 503 or 504 or 529 && attempt < 4)
            {
                await Task.Delay(res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Min(30, 3 << attempt)));
                continue;
            }
            if (!res.IsSuccessStatusCode) return (null, new HttpFailure(code, text));
            try { return (JsonNode.Parse(text), null); }
            catch (System.Text.Json.JsonException) { throw new LlmException($"{c.Name} devolvió una respuesta que no es JSON."); }
        }
    }

    static LlmException Friendly(ProviderConf c, HttpFailure f, string model)
    {
        var t = f.Text.ToLowerInvariant();
        if (f.Status is 401 or 403) return new($"{c.Name} rechazó la API key o no da acceso a {model}. Revísala en Ajustes.");
        if (f.Status == 402 || t.Contains("insufficient") || t.Contains("quota") || t.Contains("credit")) return new($"La cuenta de {c.Name} no tiene saldo o cuota suficiente. Recárgala y retoma el trabajo.");
        if (f.Status == 404 || t.Contains("model") && (t.Contains("not found") || t.Contains("does not exist"))) return new($"{c.Name} no encuentra el modelo «{model}». Elige otro en Ajustes.");
        if (f.Status == 429) return new($"{c.Name} limitó las peticiones de tu cuenta. Espera un minuto y retoma el trabajo.");
        return new($"{c.Name} respondió {f.Status}: provider request failed");
    }

    /// <summary>A JSON object written as text (models that ignore the tool): fenced or bare.</summary>
    public static JsonObject? ParseJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Regex.Match(text, @"```(?:json)?\s*(\{.*\})\s*```", RegexOptions.Singleline);
        int first = text.IndexOf('{'), last = text.LastIndexOf('}');
        var candidate = m.Success ? m.Groups[1].Value : first >= 0 && last >= first ? text[first..(last + 1)] : "";
        try { return JsonNode.Parse(candidate) as JsonObject; } catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>The contract for OpenAI-compatible servers. Gemini rejects some JSON Schema keywords and type unions with null.</summary>
    public static JsonObject Portable(JsonObject schema, string dialect)
    {
        var copy = (JsonObject)J.Clone(schema);
        if (dialect != "gemini") return copy;
        void Walk(JsonNode? n)
        {
            if (n is JsonArray a) { foreach (var x in a) Walk(x); return; }
            if (n is not JsonObject o) return;
            foreach (var k in GeminiDrop) o.Remove(k);
            if (o["type"] is JsonArray types && types.Any(t => J.S(t) == "null"))
            {
                var rest = types.Where(t => J.S(t) != "null").Select(t => J.S(t)).ToList();
                o["type"] = rest.Count == 1 ? rest[0] : JsonValue.Create(rest.FirstOrDefault() ?? "string");
                o["nullable"] = true;
            }
            foreach (var kv in o.ToList()) Walk(kv.Value);
        }
        Walk(copy);
        return copy;
    }

    /// <summary>Ask a subagent and validate the answer; on a broken contract, send the error back (max_retries = 2).</summary>
    public async Task<(JsonObject data, StepMeta meta)> DecideAsync(string agent, string tier, string system, string user, JsonObject schema,
        Func<JsonObject, string?> check, Action<string> log, IEnumerable<string>? images = null, int maxTokens = 8000, string? modelOverride = null)
    {
        var model = modelOverride ?? ModelFor(tier);
        string feedback = "", last = "";
        long tin = 0, tout = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            Reply r;
            try { r = await CompleteAsync(agent, model, system, user + feedback, schema, images, maxTokens, log); }
            catch (LlmException e) when (e.Message.Contains("max_tokens") || e.Message.Contains("no incluyó"))
            {
                last = e.Message;
                feedback = $"\n\n# Your previous attempt failed\n{last}\nAnswer again, shorter if needed.";
                continue;
            }
            tin += r.Input; tout += r.Output;
            string? problem;
            try { problem = check(r.Data); } catch (Exception e) when (e is ContractException or InvalidCastException or NullReferenceException or FormatException or InvalidOperationException or KeyNotFoundException) { problem = e.Message; }
            if (problem == null) return (r.Data, new StepMeta(attempt, r.Model, tin, tout, Math.Round(clock.Elapsed.TotalSeconds, 1)));
            last = problem;
            log($"    {agent}: intento {attempt} no pasó el contrato: {Engine.Ff.Tail(problem, 200)}");
            feedback = "\n\n# Your previous answer was rejected by validation\n" +
                       $"Previous answer:\n{Engine.Ff.Tail(r.Data.ToJsonString(J.Pretty), 6000)}\n\nValidation error:\n{Engine.Ff.Tail(problem, 2000)}\n\nFix exactly what the error names and submit the full answer again.";
        }
        throw new LlmException($"{agent}: no pasó el contrato tras 3 intentos: {Engine.Ff.Tail(last, 500)}");
    }

    public static string Prompt(string agent)
    {
        var text = File.ReadAllText(Path.Combine(Engine.Paths.App, "prompts", agent + ".md"));
        return Regex.Replace(text, @"^<!--\s*version:[^>]*-->\s*", "");
    }
}
