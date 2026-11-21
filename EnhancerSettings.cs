using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Core;
using SwarmUI.Utils;
using System.IO;

namespace LLMPromptEnhancerExtension;

/// <summary>Connection settings for one LLM provider.</summary>
public class ProviderSettings
{
    /// <summary>Base URL of the OpenAI-compatible API, eg 'http://localhost:1234/v1'.</summary>
    public string URL = "";

    /// <summary>Model ID to send requests to.</summary>
    public string Model = "";

    /// <summary>API key, if the provider needs one (OpenRouter does). Never sent to the browser.</summary>
    public string APIKey = "";
}

/// <summary>Server-wide settings of the extension, stored as JSON in the SwarmUI Data folder.</summary>
public class EnhancerSettings
{
    /// <summary>Known providers and their default base URLs.</summary>
    public static readonly Dictionary<string, string> DefaultURLs = new()
    {
        ["lmstudio"] = "http://localhost:1234/v1",
        ["ollama"] = "http://localhost:11434/v1",
        ["openrouter"] = "https://openrouter.ai/api/v1",
        ["custom"] = "http://localhost:8000/v1"
    };

    /// <summary>Display names of the providers.</summary>
    public static readonly Dictionary<string, string> ProviderNames = new()
    {
        ["lmstudio"] = "LM Studio",
        ["ollama"] = "Ollama",
        ["openrouter"] = "OpenRouter",
        ["custom"] = "Other OpenAI-compatible server"
    };

    /// <summary>The default instruction given to the language model.</summary>
    public const string DefaultSystemPrompt = "You rewrite prompts for an AI image generator. Turn the user's prompt into one rich, vivid prompt: describe the subject, the setting, lighting, mood, composition and visual style. Keep everything the user asked for and do not change its meaning. Answer with the finished prompt only - no explanation, no title, no quotes, no lists.";

    /// <summary>Which provider is in use: lmstudio, ollama, openrouter, custom.</summary>
    public string Provider = "lmstudio";

    /// <summary>Per-provider connection settings.</summary>
    public Dictionary<string, ProviderSettings> Providers = [];

    /// <summary>Instruction given to the language model.</summary>
    public string SystemPrompt = DefaultSystemPrompt;

    /// <summary>Maximum length of the answer, in tokens.</summary>
    public int MaxTokens = 400;

    /// <summary>Seconds to wait for an answer.</summary>
    public int TimeoutSeconds = 120;

    /// <summary>During generation, when the LLM fails: 'stop' (the image fails with the reason) or 'original' (generate with the unchanged prompt).</summary>
    public string OnError = "stop";

    /// <summary>Whether wildcard placeholders get a few example values from the wildcard file, so the model knows what they stand for.</summary>
    public bool WildcardExamples = true;

    /// <summary>Gets the settings of the current provider (created with its default URL if missing).</summary>
    public ProviderSettings Current
    {
        get
        {
            if (!Providers.TryGetValue(Provider, out ProviderSettings ps))
            {
                ps = new() { URL = DefaultURLs.GetValueOrDefault(Provider, DefaultURLs["custom"]) };
                Providers[Provider] = ps;
            }
            return ps;
        }
    }

    /// <summary>Lock for loading/saving.</summary>
    public static readonly object FileLock = new();

    /// <summary>Path of the settings file.</summary>
    public static string FilePath => Utilities.CombinePathWithAbsolute(Program.DataDir, "LLMPromptEnhancer.json");

    /// <summary>Cached instance, replaced on save.</summary>
    private static volatile EnhancerSettings Cached = null;

    /// <summary>Returns the current settings (loaded once, then cached).</summary>
    public static EnhancerSettings Get()
    {
        EnhancerSettings cached = Cached;
        if (cached is not null)
        {
            return cached;
        }
        lock (FileLock)
        {
            if (Cached is not null)
            {
                return Cached;
            }
            EnhancerSettings loaded = new();
            try
            {
                if (File.Exists(FilePath))
                {
                    loaded = JsonConvert.DeserializeObject<EnhancerSettings>(File.ReadAllText(FilePath)) ?? new();
                }
            }
            catch (Exception ex)
            {
                Logs.Error($"LLM Prompt Enhancer: could not read {FilePath}, using defaults: {ex.Message}");
                loaded = new();
            }
            loaded.Normalize();
            Cached = loaded;
            return loaded;
        }
    }

    /// <summary>Fills missing values and keeps values in sane ranges.</summary>
    public void Normalize()
    {
        Providers ??= [];
        if (!DefaultURLs.ContainsKey(Provider ?? ""))
        {
            Provider = "lmstudio";
        }
        foreach ((string id, string url) in DefaultURLs)
        {
            if (!Providers.TryGetValue(id, out ProviderSettings ps) || ps is null)
            {
                ps = new();
                Providers[id] = ps;
            }
            ps.URL = string.IsNullOrWhiteSpace(ps.URL) ? url : ps.URL.Trim().TrimEnd('/');
            ps.Model = (ps.Model ?? "").Trim();
            ps.APIKey = (ps.APIKey ?? "").Trim();
        }
        if (string.IsNullOrWhiteSpace(SystemPrompt))
        {
            SystemPrompt = DefaultSystemPrompt;
        }
        MaxTokens = Math.Clamp(MaxTokens, 16, 32000);
        TimeoutSeconds = Math.Clamp(TimeoutSeconds, 5, 3600);
        if (OnError != "original")
        {
            OnError = "stop";
        }
    }

    /// <summary>Saves the settings and makes them the current ones.</summary>
    public void Save()
    {
        Normalize();
        lock (FileLock)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(FilePath));
            Directory.CreateDirectory(dir);
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(this, Formatting.Indented));
            File.Move(temp, FilePath, true);
            Cached = this;
        }
    }

    /// <summary>Returns the settings for the browser, with API keys replaced by whether one is set.</summary>
    public JObject ToClientJson()
    {
        JObject providers = [];
        foreach ((string id, ProviderSettings ps) in Providers)
        {
            providers[id] = new JObject()
            {
                ["name"] = ProviderNames.GetValueOrDefault(id, id),
                ["url"] = ps.URL,
                ["default_url"] = DefaultURLs.GetValueOrDefault(id, ""),
                ["model"] = ps.Model,
                ["has_key"] = !string.IsNullOrEmpty(ps.APIKey)
            };
        }
        return new JObject()
        {
            ["provider"] = Provider,
            ["providers"] = providers,
            ["system_prompt"] = SystemPrompt,
            ["default_system_prompt"] = DefaultSystemPrompt,
            ["max_tokens"] = MaxTokens,
            ["timeout_seconds"] = TimeoutSeconds,
            ["on_error"] = OnError,
            ["wildcard_examples"] = WildcardExamples
        };
    }

    /// <summary>Returns a deep copy, so a change can be prepared without touching the current settings.</summary>
    public EnhancerSettings Copy()
    {
        return JsonConvert.DeserializeObject<EnhancerSettings>(JsonConvert.SerializeObject(this));
    }
}
