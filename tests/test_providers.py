"""Multi-provider support: the OpenAI-compatible client against a simulated server, the schema adapter,
per-provider settings, prices, and the web routes that configure them. No real API is called."""
import json

import httpx
import pytest


def test_anthropic_models_discovery_uses_the_provider_catalog(monkeypatch):
    def get(url, *, headers, timeout):
        assert url == "https://api.anthropic.com/v1/models"
        assert headers == {"x-api-key": "fake-key", "anthropic-version": "2023-06-01"}
        return httpx.Response(200, json={"data": [{"id": "claude-test-b"}, {"id": "claude-test-a"}]})
    monkeypatch.setattr(httpx, "get", get)
    assert ai.list_models("https://api.anthropic.com", "fake-key", "Anthropic", "anthropic") == ["claude-test-a", "claude-test-b"]

from clipper import ai_settings as ai
from clipper.llm import LLMError, OpenAICompatLLM, make_llm, parse_json_answer, portable_schema
from clipper.schemas import EditDecision
from test_web import client, upload  # noqa: F401  (shared fixtures)


def fake_server(answers, seen):
    """An OpenAI-compatible /chat/completions that replies with the queued answers in order."""
    def handler(request: httpx.Request):
        body = json.loads(request.content)
        seen.append({"url": str(request.url), "auth": request.headers.get("authorization"), "body": body})
        status, payload = answers.pop(0) if len(answers) > 1 else answers[0]
        return httpx.Response(status, json=payload) if isinstance(payload, dict) else httpx.Response(status, text=payload)
    return httpx.MockTransport(handler)


def tool_reply(args: dict, model="m-1"):
    return 200, {"model": model, "usage": {"prompt_tokens": 120, "completion_tokens": 30},
                 "choices": [{"finish_reason": "tool_calls", "message": {"tool_calls": [
                     {"type": "function", "function": {"name": "submit_editing", "arguments": json.dumps(args)}}]}}]}


def client_for(answers, seen, usage=None, **kw):
    return OpenAICompatLLM(base_url="https://api.example.com/v1/", api_key="k-123", name="Ejemplo",
                           on_usage=usage, transport=fake_server(answers, seen), **kw)


def test_openai_compatible_forced_tool_call_with_images(tmp_path):
    img = tmp_path / "f.jpg"
    img.write_bytes(b"\xff\xd8jpeg")
    seen, usage = [], []
    llm = client_for([tool_reply({"start_word": 1})], seen, usage.append)
    res = llm.complete(agent="editing", model="m-1", system="Sys", user="Hola", contract=EditDecision, images=[img])
    assert res.data == {"start_word": 1} and res.model == "m-1" and res.usage == {"input_tokens": 120, "output_tokens": 30}
    sent = seen[0]
    assert sent["url"] == "https://api.example.com/v1/chat/completions" and sent["auth"] == "Bearer k-123"
    b = sent["body"]
    assert b["tool_choice"] == {"type": "function", "function": {"name": "submit_editing"}}
    assert b["messages"][1]["content"][1]["image_url"]["url"].startswith("data:image/jpeg;base64,")
    params = b["tools"][0]["function"]["parameters"]
    assert "$defs" not in params and "$ref" not in json.dumps(params)                     # self-contained schema
    assert usage == [{"agent": "editing", "model": "m-1", "input": 120, "output": 30}]       # billed calls are recorded


def test_fallbacks_for_servers_that_differ(tmp_path):
    img = tmp_path / "f.jpg"
    img.write_bytes(b"x")
    seen, logs = [], []
    answers = [(400, "This model does not support image_url content"),
               (400, "tool_choice object is not supported"),
               (200, {"model": "m", "choices": [{"finish_reason": "stop", "message": {"content": "```json\n{\"start_word\": 3}\n```"}}]})]
    llm = client_for(answers, seen, log=logs.append)
    res = llm.complete(agent="editing", model="m", system="S", user="U", contract=EditDecision, images=[img])
    assert res.data == {"start_word": 3}                         # JSON written as text is still used
    assert isinstance(seen[1]["body"]["messages"][1]["content"], str)   # retried without the frames
    assert seen[2]["body"]["tool_choice"] == "auto"                     # and without the forced call
    assert any("no acepta imágenes" in m for m in logs)
    # The model is remembered: the next call goes straight to the settings that worked.
    llm.complete(agent="editing", model="m", system="S", user="U", contract=EditDecision, images=[img])
    assert isinstance(seen[3]["body"]["messages"][1]["content"], str) and seen[3]["body"]["tool_choice"] == "auto"


@pytest.mark.parametrize("status,text,expected", [
    (401, "invalid api key", "rechazó la API key"),
    (402, "Insufficient credits", "saldo"),
    (404, "The model `x` does not exist", "no encuentra el modelo"),
])
def test_errors_are_explained(status, text, expected):
    llm = client_for([(status, text)], [])
    with pytest.raises(LLMError, match=expected):
        llm.complete(agent="editing", model="x", system="S", user="U", contract=EditDecision)


def test_cut_answer_and_missing_tool_are_retryable_errors():
    llm = client_for([(200, {"choices": [{"finish_reason": "length", "message": {"content": "{\"a\": "}}]})], [])
    with pytest.raises(LLMError, match="max_tokens"):
        llm.complete(agent="editing", model="x", system="S", user="U", contract=EditDecision)
    llm = client_for([(200, {"choices": [{"finish_reason": "stop", "message": {"content": "Lo siento"}}]})], [])
    with pytest.raises(LLMError, match="no incluyó"):
        llm.complete(agent="editing", model="x", system="S", user="U", contract=EditDecision)


def test_portable_schema_for_gemini():
    schema = portable_schema(EditDecision.model_json_schema(), "gemini")
    text = json.dumps(schema)
    assert "$ref" not in text and "additionalProperties" not in text and '"title"' not in text and '"default"' not in text
    assert '"type": "null"' not in text                                   # Optional fields become nullable
    assert parse_json_answer('Aquí está: {"a": 1}') == {"a": 1} and parse_json_answer("nada") is None


def test_models_without_tool_support_use_validated_json():
    seen=[]
    reply=(200,{'model':'local-text','choices':[{'finish_reason':'stop','message':{'content':'{"start_word":4}'}}]})
    llm=client_for([(400,'model does not support tools'),reply],seen)
    assert llm.complete(agent='editing',model='local-text',system='S',user='U',contract=EditDecision).data=={'start_word':4}
    assert 'tools' not in seen[-1]['body'] and 'tool_choice' not in seen[-1]['body']
    assert 'schema' in seen[-1]['body']['messages'][0]['content']
    llm.complete(agent='editing',model='local-text',system='S',user='U',contract=EditDecision)
    assert 'tools' not in seen[-1]['body']


def test_usage_prices_and_groups_are_scoped_by_provider(settings):
    for pid,price in [('openai',[1,2]),('openrouter',[3,4])]:
        ai.set_provider(settings.data_dir,pid,models=dict.fromkeys(ai.TIERS,'same-model'),prices={'fast':price})
        ai.Ledger(settings.data_dir,pid).add({'model':'same-model','input':1_000_000,'output':1_000_000})
    usage=ai.summarize(settings,7)
    assert usage['totals']['cost']==10
    assert len(usage['by_model'])==2
    assert {row['provider']:row['cost'] for row in usage['by_model']}=={'openai':3,'openrouter':7}


def test_provider_settings_models_and_prices(settings, monkeypatch):
    d = settings.data_dir
    for env in ("OPENAI_API_KEY", "GEMINI_API_KEY", "ANTHROPIC_API_KEY"):
        monkeypatch.delenv(env, raising=False)
    assert ai.active_id(d) == "anthropic"
    ai.set_provider(d, "openai", activate=True)
    ai.apply(settings)
    assert settings.ai_kind == "openai" and settings.ai_missing and "API key de OpenAI" in settings.ai_missing
    ai.set_key(d, "openai", "sk-proj-abcdefghijklmnop")
    ai.set_provider(d, "openai", models={"fast": "small-1", "standard": "big-1", "superior": "big-1"},
                    prices={"fast": [0.2, 0.8], "standard": [3, 12]})
    ai.apply(settings)
    assert settings.ai_missing is None and settings.model_for("fast") == "small-1" and settings.api_key.startswith("sk-proj")
    assert settings.ai_max_tokens_field == "max_completion_tokens"
    llm = make_llm("anthropic", settings.api_key, settings=settings)
    assert isinstance(llm, OpenAICompatLLM) and llm.base_url == "https://api.openai.com/v1"
    # Prices come from what the person entered; a model without a price is counted but not billed.
    assert ai.price_for(settings, "small-1") == (0.2, 0.8) and ai.price_for(settings, "local-llama") is None
    assert ai.price_for(settings, "claude-opus-5-5") == (4.0, 20.0)
    led = ai.Ledger(d, "openai")
    led.add({"agent": "analysis", "model": "small-1", "input": 1_000_000, "output": 1_000_000})
    led.add({"agent": "editing", "model": "local-llama", "input": 10, "output": 10})
    u = ai.summarize(settings, 7)
    assert abs(u["totals"]["cost"] - 1.0) < 1e-9 and u["totals"]["unpriced_calls"] == 1
    assert u["by_model"][0]["provider"] == "openai" and u["prices"]["fast"]["model"] == "small-1"
    # The Anthropic key keeps living where older versions read it.
    ai.set_key(d, "anthropic", "sk-ant-" + "y" * 40)
    assert ai.load(d)["api_key"].startswith("sk-ant-") and ai.stored_key_for(d, "anthropic").startswith("sk-ant-")


def test_web_routes_configure_a_provider(client, demo, tmp_path, monkeypatch):
    for env in ("OPENAI_API_KEY", "GEMINI_API_KEY", "OLLAMA_API_KEY"):
        monkeypatch.delenv(env, raising=False)
    monkeypatch.setattr(ai, "list_models", lambda base, key, name="", dialect="": ["gemini-a", "gemini-b"])
    st = client.get("/api/settings/ai").json()
    ids = [p["id"] for p in st["providers"]]
    assert st["provider"] == "anthropic" and {"openai", "gemini", "openrouter", "ollama", "custom"} <= set(ids)

    assert client.put("/api/settings/ai/key", json={"key": "has space", "provider": "gemini"}).status_code == 400
    test_credential = "-".join(("AIza", "test", "key", "123"))
    r = client.put("/api/settings/ai/key", json={"key": test_credential, "provider": "gemini"}).json()
    assert r["models"] == ["gemini-a", "gemini-b"] and r["provider"] == "anthropic"           # saving a key does not switch
    assert client.get("/api/settings/ai/models?provider=gemini").json()["models"] == ["gemini-a", "gemini-b"]

    st = client.put("/api/settings/ai/provider", json={"provider": "gemini", "models": {"fast": "gemini-a", "standard": "gemini-b", "superior": "gemini-b"},
                                                         "prices": {"fast": [0.1, 0.4]}}).json()
    assert st["provider"] == "gemini" and st["ready"] and test_credential not in json.dumps(st) and "test-key" not in json.dumps(st)
    cfg = client.get("/api/config").json()
    assert cfg["api_key_set"] and cfg["provider"]["id"] == "gemini"
    assert [t["model"] for t in cfg["tiers"]] == ["gemini-a", "gemini-b", "gemini-b"] and cfg["tiers"][0]["name"] == "Rápido"
    assert cfg["tiers"][0]["price"] == "$0.1 / $0.4" and cfg["tiers"][1]["price"] == "precio no configurado"

    # Local servers: no key needed, but the address must be a URL and fixed addresses stay fixed.
    assert client.put("/api/settings/ai/provider", json={"provider": "ollama", "base_url": "ftp://x"}).status_code == 400
    assert client.put("/api/settings/ai/provider", json={"provider": "openai", "base_url": "https://evil.example"}).status_code == 400
    st = client.put("/api/settings/ai/provider", json={"provider": "ollama", "base_url": "http://localhost:11434/v1/"}).json()
    assert not st["ready"] and "modelos" in st["missing"]
    sid = upload(client, demo).json()["id"]
    r = client.post("/api/jobs", json={"source_id": sid, "analysis_model": "fast"})   # an incomplete provider cannot start a job
    assert r.status_code == 400 and "modelos" in r.json()["detail"]
    assert client.put("/api/settings/ai/provider", json={"provider": "nadie"}).status_code == 400


def test_full_job_through_an_openai_compatible_provider(make_ctx, scripted, settings, source, demo):
    """The whole pipeline (analysis, editing, captions, metadata, review, render, package) with every
    subagent answering through Chat Completions tool calls instead of Anthropic's API."""
    from clipper.agents.orchestrator import Orchestrator
    from conftest import read_json

    script = scripted(("en",)).script   # the same canned answers the Anthropic-path tests use
    calls = []

    def handler(request: httpx.Request):
        body = json.loads(request.content)
        agent = body["tools"][0]["function"]["name"].removeprefix("submit_")
        calls.append(agent)
        user = body["messages"][1]["content"]
        user = user if isinstance(user, str) else user[0]["text"]
        answer = script[agent][0]
        data = answer(user) if callable(answer) else answer
        return httpx.Response(200, json={"model": "local-model", "usage": {"prompt_tokens": 10, "completion_tokens": 5}, "choices": [
            {"finish_reason": "tool_calls", "message": {"tool_calls": [{"type": "function", "function": {
                "name": f"submit_{agent}", "arguments": json.dumps(data)}}]}}]})

    llm = OpenAICompatLLM(base_url="http://local/v1", api_key=None, name="Local", transport=httpx.MockTransport(handler))
    summary = Orchestrator(make_ctx(llm)).run(source.id, transcript=str(demo[1]), max_clips=1)
    assert summary["clips"] == {"pending_approval": 1}
    assert {"analysis", "editing", "captions", "metadata", "reviewer"} <= set(calls)
    pkg = summary["packages"][0]["dir"]
    assert read_json(f"{pkg}/en/metadata.json")["youtube"]["title"] == "He cleared the whole bridge"
