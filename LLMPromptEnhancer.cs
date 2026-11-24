using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using SwarmUI.WebAPI;

namespace LLMPromptEnhancerExtension;

/// <summary>Sends the prompt to a language model (LM Studio, Ollama, OpenRouter or any OpenAI-compatible server) to have it enhanced,
/// either from a button next to the prompt box or automatically during generation - keeping SwarmUI wildcards and other prompt syntax intact.</summary>
public class LLMPromptEnhancer : Extension
{
    /// <summary>Parameter group of the automatic enhancement.</summary>
    public static T2IParamGroup EnhanceGroup;

    /// <summary>When to enhance: once per Generate (before wildcards) or for every image (after wildcards).</summary>
    public static T2IRegisteredParam<string> EnhanceMode;

    /// <summary>Temperature for the model. Also the group's activation parameter.</summary>
    public static T2IRegisteredParam<double> EnhanceCreativity;

    /// <summary>Extra instruction for this generation.</summary>
    public static T2IRegisteredParam<string> EnhanceInstruction;

    /// <summary>Permission group.</summary>
    public static PermInfoGroup PermGroup = new("LLM Prompt Enhancer", "Permissions of the LLM Prompt Enhancer extension.");

    /// <summary>Use the enhance button.</summary>
    public static PermInfo PermEnhance;

    /// <summary>Change the connection settings (makes the server call any URL).</summary>
    public static PermInfo PermSettings;

    /// <summary>Enhancements of 'once per Generate' mode, shared by the images of one request.</summary>
    public static ConcurrentDictionary<string, (long Time, Lazy<Task<Enhancement>> Result)> OnceCache = new();

    /// <summary>An enhanced prompt, plus a note when the enhancer had to put dropped placeholders back (null if not).</summary>
    public record Enhancement(string Prompt, string Note);

    /// <inheritdoc/>
    public override void OnInit()
    {
        ScriptFiles.Add("Assets/llm_prompt_enhancer.js");
        StyleSheetFiles.Add("Assets/llm_prompt_enhancer.css");
        PermEnhance = Permissions.Register(new("llm_prompt_enhancer_use", "Use LLM Prompt Enhancer", "Allows enhancing prompts with the configured language model.", PermissionDefault.USER, PermGroup));
        PermSettings = Permissions.Register(new("llm_prompt_enhancer_settings", "Edit LLM Prompt Enhancer Settings", "Allows changing which LLM server, model and API key the enhancer uses. The server will connect to whatever URL is entered.", PermissionDefault.ADMINS, PermGroup, PermSafetyLevel.RISKY));
        EnhanceGroup = new("LLM Prompt Enhancer", Toggles: true, Open: false, Description: "Has a language model rewrite the prompt before generating. Server, model and instruction are set on the 'LLM Prompt Enhancer' tab.");
        EnhanceMode = T2IParamTypes.Register<string>(new("LLM Enhance Mode",
            "'Every image': each image's prompt is enhanced after its wildcards are filled in - one LLM call per image, the model sees the actual picks and writes around them.\n'Once per Generate': the prompt is enhanced once, with wildcards and other tags kept in place, so every image still gets its own picks - but the model doesn't know them and leaves those parts alone.\nThe prompt box is never changed.",
            "each", GetValues: _ => ["each///Every image (after wildcards are filled in)", "once///Once per Generate (wildcards still vary)"], Group: EnhanceGroup, OrderPriority: 1));
        EnhanceCreativity = T2IParamTypes.Register<double>(new("LLM Enhance Creativity",
            "The model's temperature: low keeps close to your words, high invents more.",
            "0.7", Min: 0, Max: 2, Step: 0.05, Group: EnhanceGroup, OrderPriority: 2));
        EnhanceInstruction = T2IParamTypes.Register<string>(new("LLM Enhance Instruction",
            "Optional extra instruction for this generation, eg 'make it a moody film still' or 'keep it short'. Added to the instruction set on the 'LLM Prompt Enhancer' tab.",
            "", Group: EnhanceGroup, OrderPriority: 3, ViewType: ParamViewType.BIG));
        T2IParamInput.LateSpecialParameterHandlers.Insert(0, EnhanceBeforeWildcards);
        T2IParamInput.LateSpecialParameterHandlers.Add(EnhanceAfterWildcards);
        API.RegisterAPICall(LLMPromptEnhancerEnhance, true, PermEnhance);
        API.RegisterAPICall(LLMPromptEnhancerGetSettings, false, PermEnhance);
        API.RegisterAPICall(LLMPromptEnhancerSaveSettings, true, PermSettings);
        API.RegisterAPICall(LLMPromptEnhancerListModels, false, PermSettings);
    }

    /// <summary>Enhances a prompt: protects its syntax, asks the model, puts the syntax back.</summary>
    /// <param name="prompt">The prompt.</param>
    /// <param name="instruction">Optional extra instruction.</param>
    /// <param name="temperature">Model temperature.</param>
    /// <param name="cancel">Cancellation.</param>
    public static async Task<Enhancement> EnhanceAsync(string prompt, string instruction, double temperature, CancellationToken cancel)
    {
        EnhancerSettings settings = EnhancerSettings.Get();
        ProtectedPrompt protectedPrompt = ProtectedPrompt.Protect(prompt, settings.WildcardExamples);
        if (!protectedPrompt.HasProse)
        {
            return new(prompt, null);
        }
        string system = settings.SystemPrompt + protectedPrompt.Instructions();
        if (!string.IsNullOrWhiteSpace(instruction))
        {
            system += $"\n\nAdditional instruction: {instruction.Trim()}";
        }
        string user = protectedPrompt.Text.Trim();
        string answer = await LLMClient.Complete(settings, system, user, temperature, cancel);
        List<string> notes = [];
        // A dropped random pick whose name is in the answer ('Carla' for "Carla is a nude [[T1]]") is put back right there by Restore -
        // asking the model again moved one to the wrong woman in a test. Only placeholders with no such name make it ask once more.
        List<int> missing = [.. protectedPrompt.Missing(answer).Where(i => !protectedPrompt.CanAnchor(answer, i))];
        if (missing.Count > 0)
        {
            string names = string.Join(", ", missing.Select(i => $"[[T{i + 1}]]"));
            string again = $"You left out {names}. Write the same text again with each of them put back exactly once, where it belongs: a person's placeholder right after that person's name or first mention. Change nothing else.";
            try
            {
                string second = await LLMClient.Complete(settings, system, [user, answer, again], temperature, cancel);
                List<int> stillMissing = protectedPrompt.Missing(second);
                // Take the second answer only if it really has more of them and didn't shrink to a summary.
                if (stillMissing.Count < missing.Count && second.Length >= answer.Length * 0.7)
                {
                    answer = second;
                    notes.Add($"model left out {names}, asked again: {(stillMissing.Count == 0 ? "all put back" : $"{missing.Count - stillMissing.Count} of {missing.Count} put back")}");
                }
                else
                {
                    notes.Add($"model left out {names}, asking again didn't help");
                }
            }
            catch (Exception ex)
            {
                notes.Add($"model left out {names}, asking again failed: {ex.Message}");
            }
        }
        string result = protectedPrompt.Restore(answer, notes);
        return new(result, notes.Count == 0 ? null : string.Join("; ", notes));
    }

    /// <summary>Waits for an enhancement inside generation. On failure either stops the image with a readable message or keeps the prompt, per settings.
    /// A note about placeholders the enhancer had to put back goes into the image's metadata as 'llm_placeholders_fixed'.</summary>
    public static string WaitForEnhancement(Task<Enhancement> task, T2IParamInput input, string fallback)
    {
        try
        {
            Enhancement result = task.GetAwaiter().GetResult();
            if (!string.IsNullOrEmpty(result.Note))
            {
                input.ExtraMeta["llm_placeholders_fixed"] = result.Note;
            }
            return result.Prompt;
        }
        catch (Exception ex)
        {
            string message = ex is InvalidOperationException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            if (EnhancerSettings.Get().OnError == "original")
            {
                Logs.Warning($"LLM Prompt Enhancer: {message} - generating with the unchanged prompt.");
                input.ExtraMeta["llm_enhance_error"] = message;
                return fallback;
            }
            throw new SwarmReadableErrorException($"LLM Prompt Enhancer: {message}");
        }
    }

    /// <summary>Late parameter handler, runs before SwarmUI fills in wildcards: 'once per Generate' mode.</summary>
    public static void EnhanceBeforeWildcards(T2IParamInput input)
    {
        if (!input.TryGet(EnhanceCreativity, out double temperature) || input.Get(EnhanceMode, "each") != "once")
        {
            return;
        }
        string raw = input.Get(T2IParamTypes.Prompt) ?? "";
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }
        string instruction = input.Get(EnhanceInstruction, "") ?? "";
        Task<Enhancement> task;
        if (input.UserRequestId == 0)
        {
            task = EnhanceAsync(raw, instruction, temperature, CancellationToken.None);
        }
        else
        {
            long now = Environment.TickCount64;
            if (OnceCache.Count > 50)
            {
                foreach ((string oldKey, (long time, _)) in OnceCache)
                {
                    if (now - time > 30 * 60 * 1000)
                    {
                        OnceCache.TryRemove(oldKey, out _);
                    }
                }
            }
            string key = $"{input.SourceSession?.User?.UserID}\n{input.UserRequestId}\n{temperature}\n{instruction}\n{raw}";
            task = OnceCache.GetOrAdd(key, _ => (now, new Lazy<Task<Enhancement>>(() => EnhanceAsync(raw, instruction, temperature, CancellationToken.None)))).Result.Value;
        }
        string enhanced = WaitForEnhancement(task, input, raw);
        if (enhanced != raw)
        {
            input.ExtraMeta.TryAdd("original_prompt", raw);
            input.ExtraMeta["llm_enhanced_prompt"] = enhanced;
            input.Set(T2IParamTypes.Prompt, enhanced);
        }
    }

    /// <summary>Late parameter handler, runs after SwarmUI filled in wildcards: 'every image' mode.</summary>
    public static void EnhanceAfterWildcards(T2IParamInput input)
    {
        if (!input.TryGet(EnhanceCreativity, out double temperature) || input.Get(EnhanceMode, "each") != "each")
        {
            return;
        }
        string filled = input.Get(T2IParamTypes.Prompt) ?? "";
        if (string.IsNullOrWhiteSpace(filled))
        {
            return;
        }
        string instruction = input.Get(EnhanceInstruction, "") ?? "";
        string enhanced = WaitForEnhancement(EnhanceAsync(filled, instruction, temperature, CancellationToken.None), input, filled);
        if (enhanced != filled)
        {
            input.ExtraMeta.TryAdd("original_prompt", filled);
            input.ExtraMeta["llm_prompt_before_enhance"] = filled;
            input.Set(T2IParamTypes.Prompt, enhanced);
        }
    }

    /// <summary>API route: enhance a prompt (used by the button next to the prompt box).</summary>
    [API.APIDescription("Enhances a prompt with the configured language model. Wildcards and other prompt syntax are kept.", "\"prompt\": \"the enhanced prompt\"")]
    public static async Task<JObject> LLMPromptEnhancerEnhance(Session session,
        [API.APIParameter("The prompt to enhance.")] string prompt,
        [API.APIParameter("Optional extra instruction.")] string instruction = "",
        [API.APIParameter("Model temperature, 0 to 2.")] double temperature = 0.7)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return new JObject() { ["error"] = "The prompt is empty." };
        }
        try
        {
            Enhancement enhanced = await EnhanceAsync(prompt, instruction, Math.Clamp(temperature, 0, 2), Program.GlobalProgramCancel);
            JObject result = new() { ["success"] = true, ["prompt"] = enhanced.Prompt };
            if (!string.IsNullOrEmpty(enhanced.Note))
            {
                result["note"] = enhanced.Note;
            }
            return result;
        }
        catch (Exception ex)
        {
            string message = ex is InvalidOperationException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            Logs.Warning($"LLM Prompt Enhancer: {message}");
            return new JObject() { ["error"] = $"LLM Prompt Enhancer: {message}" };
        }
    }

    /// <summary>API route: read the settings (API keys are never returned).</summary>
    [API.APIDescription("Returns the enhancer settings. API keys are only reported as set or not.", "\"settings\": {...}, \"can_edit\": true")]
    public static async Task<JObject> LLMPromptEnhancerGetSettings(Session session)
    {
        return new JObject()
        {
            ["settings"] = EnhancerSettings.Get().ToClientJson(),
            ["can_edit"] = session.User.HasPermission(PermSettings)
        };
    }

    /// <summary>API route: save the settings.</summary>
    [API.APIDescription("Saves the enhancer settings. Send 'provider' plus any of: url, model, api_key (omit to keep, \"\" with clear_api_key=true to remove), system_prompt, max_tokens, timeout_seconds, on_error, wildcard_examples.", "\"success\": true, \"settings\": {...}")]
    public static async Task<JObject> LLMPromptEnhancerSaveSettings(Session session, JObject raw)
    {
        EnhancerSettings settings = EnhancerSettings.Get().Copy();
        string provider = raw.Value<string>("provider") ?? settings.Provider;
        if (!EnhancerSettings.DefaultURLs.ContainsKey(provider))
        {
            return new JObject() { ["error"] = $"Unknown provider '{provider}'." };
        }
        settings.Provider = provider;
        ProviderSettings ps = settings.Current;
        if (raw["url"] is not null)
        {
            string url = raw.Value<string>("url")?.Trim() ?? "";
            if (url.Length > 0 && !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return new JObject() { ["error"] = "The URL must start with http:// or https://" };
            }
            ps.URL = url;
        }
        if (raw["model"] is not null)
        {
            ps.Model = raw.Value<string>("model") ?? "";
        }
        if (raw.Value<bool?>("clear_api_key") == true)
        {
            ps.APIKey = "";
        }
        else if (!string.IsNullOrWhiteSpace(raw.Value<string>("api_key")))
        {
            ps.APIKey = raw.Value<string>("api_key").Trim();
        }
        if (raw["system_prompt"] is not null)
        {
            settings.SystemPrompt = raw.Value<string>("system_prompt") ?? "";
        }
        if (raw["max_tokens"] is not null)
        {
            settings.MaxTokens = raw.Value<int>("max_tokens");
        }
        if (raw["timeout_seconds"] is not null)
        {
            settings.TimeoutSeconds = raw.Value<int>("timeout_seconds");
        }
        if (raw["on_error"] is not null)
        {
            settings.OnError = raw.Value<string>("on_error");
        }
        if (raw["wildcard_examples"] is not null)
        {
            settings.WildcardExamples = raw.Value<bool>("wildcard_examples");
        }
        try
        {
            settings.Save();
        }
        catch (Exception ex)
        {
            return new JObject() { ["error"] = $"Could not save the settings: {ex.Message}" };
        }
        return new JObject() { ["success"] = true, ["settings"] = settings.ToClientJson() };
    }

    /// <summary>API route: list the models a server offers.</summary>
    [API.APIDescription("Lists the models of an OpenAI-compatible server. Uses the stored API key of the provider when none is given.", "\"models\": [\"id\", ...]")]
    public static async Task<JObject> LLMPromptEnhancerListModels(Session session,
        [API.APIParameter("Provider ID: lmstudio, ollama, openrouter, custom.")] string provider,
        [API.APIParameter("Base URL to ask; empty = the stored one.")] string url = "",
        [API.APIParameter("API key to use; empty = the stored one.")] string api_key = "")
    {
        EnhancerSettings settings = EnhancerSettings.Get();
        if (!settings.Providers.TryGetValue(provider ?? "", out ProviderSettings stored))
        {
            return new JObject() { ["error"] = $"Unknown provider '{provider}'." };
        }
        ProviderSettings ps = new()
        {
            URL = string.IsNullOrWhiteSpace(url) ? stored.URL : url.Trim().TrimEnd('/'),
            APIKey = string.IsNullOrWhiteSpace(api_key) ? stored.APIKey : api_key.Trim()
        };
        if (!ps.URL.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !ps.URL.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return new JObject() { ["error"] = "The URL must start with http:// or https://" };
        }
        try
        {
            List<string> models = await LLMClient.ListModels(ps, settings.TimeoutSeconds);
            return new JObject() { ["models"] = new JArray(models.ToArray()) };
        }
        catch (Exception ex)
        {
            string message = ex is InvalidOperationException ? ex.Message : ex.Message;
            return new JObject() { ["error"] = $"Could not list models at {ps.URL}: {message}" };
        }
    }
}
