using Newtonsoft.Json.Linq;
using SwarmUI.Utils;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace LLMPromptEnhancerExtension;

/// <summary>Talks to an OpenAI-compatible chat API (LM Studio, Ollama's /v1, OpenRouter, others).</summary>
public static class LLMClient
{
    /// <summary>Shared HTTP client; timeouts are per request.</summary>
    public static readonly HttpClient Client = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Removes reasoning blocks some local models put before the answer.</summary>
    public static readonly Regex ThinkRegex = new(@"<(think|thinking|reasoning)>.*?</\1>", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    /// <summary>Builds a request with the provider's headers.</summary>
    public static HttpRequestMessage MakeRequest(HttpMethod method, ProviderSettings ps, string path)
    {
        HttpRequestMessage request = new(method, $"{ps.URL.TrimEnd('/')}/{path}");
        if (!string.IsNullOrEmpty(ps.APIKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ps.APIKey);
        }
        // OpenRouter asks apps to name themselves (optional); generic, no user data.
        request.Headers.TryAddWithoutValidation("X-Title", "SwarmUI LLM Prompt Enhancer");
        return request;
    }

    /// <summary>Reads an error message out of a failed response.</summary>
    public static string ErrorText(int status, string body)
    {
        string message = body ?? "";
        try
        {
            JObject json = JObject.Parse(body);
            JToken err = json["error"];
            message = err is JObject eo ? (eo["message"]?.ToString() ?? eo.ToString()) : (err?.ToString() ?? body);
        }
        catch (Exception)
        {
        }
        if (message.Length > 300)
        {
            message = message[..300] + "...";
        }
        return $"HTTP {status}: {message}";
    }

    /// <summary>Lists the model IDs the server offers.</summary>
    public static async Task<List<string>> ListModels(ProviderSettings ps, int timeoutSeconds)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(Math.Min(timeoutSeconds, 30)));
        using HttpRequestMessage request = MakeRequest(HttpMethod.Get, ps, "models");
        using HttpResponseMessage response = await Client.SendAsync(request, cts.Token);
        string body = await response.Content.ReadAsStringAsync(cts.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ErrorText((int)response.StatusCode, body));
        }
        JObject json = JObject.Parse(body);
        List<string> models = [];
        if (json["data"] is JArray data)
        {
            foreach (JToken m in data)
            {
                string id = m["id"]?.ToString();
                if (!string.IsNullOrWhiteSpace(id))
                {
                    models.Add(id);
                }
            }
        }
        models.Sort(StringComparer.OrdinalIgnoreCase);
        return models;
    }

    /// <summary>Size, parameter count, quant and vision per model ID ('13.5 GB · 24B · Q4_K_S', '... · vision'), from the server's own model list:
    /// LM Studio's /api/v1/models or Ollama's /api/tags. Empty when the server has neither (OpenRouter, others) - never throws.</summary>
    public static async Task<Dictionary<string, string>> ModelLabels(ProviderSettings ps)
    {
        Dictionary<string, string> labels = new(StringComparer.OrdinalIgnoreCase);
        string root = Regex.Replace(ps.URL.TrimEnd('/'), "/v1$", "", RegexOptions.IgnoreCase);
        foreach (string path in new[] { "api/v1/models", "api/tags" })
        {
            try
            {
                using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
                using HttpRequestMessage request = new(HttpMethod.Get, $"{root}/{path}");
                if (!string.IsNullOrEmpty(ps.APIKey))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ps.APIKey);
                }
                using HttpResponseMessage response = await Client.SendAsync(request, cts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }
                JObject json = JObject.Parse(await response.Content.ReadAsStringAsync(cts.Token));
                if (json["models"] is not JArray list)
                {
                    continue;
                }
                foreach (JToken m in list)
                {
                    // LM Studio: key, size_bytes, params_string, quantization.name - Ollama: name, size, details.parameter_size / quantization_level
                    string id = (m["key"] ?? m["name"] ?? m["model"])?.ToString();
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        continue;
                    }
                    long bytes = (long?)(m["size_bytes"] ?? m["size"]) ?? 0;
                    JToken quantToken = m["quantization"];
                    string quant = (quantToken is JObject qo ? qo["name"]?.ToString() : quantToken?.ToString()) ?? m["details"]?["quantization_level"]?.ToString();
                    string parameters = (m["params_string"] ?? m["details"]?["parameter_size"])?.ToString();
                    List<string> parts = [];
                    if (bytes > 0)
                    {
                        parts.Add($"{bytes / 1e9:0.0} GB");
                    }
                    if (!string.IsNullOrWhiteSpace(parameters))
                    {
                        parts.Add(parameters);
                    }
                    if (!string.IsNullOrWhiteSpace(quant))
                    {
                        parts.Add(quant);
                    }
                    // LM Studio: capabilities.vision (Ollama's list doesn't say)
                    if (m["capabilities"] is JObject caps && caps["vision"]?.Type == JTokenType.Boolean && (bool)caps["vision"])
                    {
                        parts.Add("vision");
                    }
                    if (parts.Count > 0)
                    {
                        labels[id] = string.Join(" · ", parts);
                    }
                }
                if (labels.Count > 0)
                {
                    return labels;
                }
            }
            catch (Exception)
            {
                // Not this kind of server; the plain names are shown.
            }
        }
        return labels;
    }

    /// <summary>Sends one chat request and returns the cleaned answer text.</summary>
    public static Task<string> Complete(EnhancerSettings settings, string system, string user, double temperature, CancellationToken cancel)
    {
        return Complete(settings, system, [user], temperature, cancel);
    }

    /// <summary>Sends one chat request and returns the cleaned answer text.</summary>
    /// <param name="turns">The conversation after the instruction: user, assistant, user, ... (always an odd count, ending with a user turn).</param>
    public static async Task<string> Complete(EnhancerSettings settings, string system, IList<string> turns, double temperature, CancellationToken cancel)
    {
        ProviderSettings ps = settings.Current;
        if (string.IsNullOrWhiteSpace(ps.Model))
        {
            throw new InvalidOperationException($"no model chosen for {EnhancerSettings.ProviderNames.GetValueOrDefault(settings.Provider, settings.Provider)} - set one on the 'LLM Prompt Enhancer' tab");
        }
        JArray messages = [new JObject() { ["role"] = "system", ["content"] = system }];
        for (int t = 0; t < turns.Count; t++)
        {
            messages.Add(new JObject() { ["role"] = t % 2 == 0 ? "user" : "assistant", ["content"] = turns[t] });
        }
        JObject payload = new()
        {
            ["model"] = ps.Model,
            ["messages"] = messages,
            ["temperature"] = temperature,
            ["max_tokens"] = settings.MaxTokens,
            ["stream"] = false
        };
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        cts.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        using HttpRequestMessage request = MakeRequest(HttpMethod.Post, ps, "chat/completions");
        request.Content = new StringContent(payload.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try
        {
            response = await Client.SendAsync(request, cts.Token);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new InvalidOperationException($"no answer from {ps.URL} within {settings.TimeoutSeconds} seconds");
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"cannot reach {ps.URL} ({ex.Message})");
        }
        using (response)
        {
            string body = await response.Content.ReadAsStringAsync(cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(ErrorText((int)response.StatusCode, body));
            }
            JObject json = JObject.Parse(body);
            if (json["error"] is not null && json["choices"] is null)
            {
                throw new InvalidOperationException(ErrorText(200, body));
            }
            string content = json["choices"]?[0]?["message"]?["content"]?.ToString() ?? "";
            string cleaned = Clean(content);
            if (cleaned.Length == 0)
            {
                string reason = json["choices"]?[0]?["finish_reason"]?.ToString();
                throw new InvalidOperationException(reason == "length"
                    ? $"the model used up all {settings.MaxTokens} tokens without an answer (a thinking model? raise 'Max answer length')"
                    : "the model gave an empty answer");
            }
            return cleaned;
        }
    }

    /// <summary>Strips reasoning blocks, code fences, quotes and "Here is ..." intros from an answer.</summary>
    public static string Clean(string content)
    {
        string text = ThinkRegex.Replace(content ?? "", "");
        // An unclosed think block (cut off answer) - take what comes after a closing tag, if any.
        int closeThink = text.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (closeThink >= 0)
        {
            text = text[(closeThink + "</think>".Length)..];
        }
        else if (text.TrimStart().StartsWith("<think>", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }
        text = text.Trim();
        text = Regex.Replace(text, @"^```[a-zA-Z]*\s*", "");
        text = Regex.Replace(text, @"\s*```$", "");
        text = Regex.Replace(text, @"^(here(?:'s| is)[^:\n]*|(?:enhanced|improved|rewritten|final)?\s*prompt)\s*:\s*", "", RegexOptions.IgnoreCase).Trim();
        if (text.Length > 1 && ((text[0] == '"' && text[^1] == '"') || (text[0] == '“' && text[^1] == '”') || (text[0] == '\'' && text[^1] == '\'')))
        {
            text = text[1..^1].Trim();
        }
        // Prompts are one paragraph; models sometimes add blank lines.
        text = Regex.Replace(text, @"\s*\n\s*\n\s*", " ");
        return text.Trim();
    }
}
