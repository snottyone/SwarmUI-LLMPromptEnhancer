using SwarmUI.Utils;
using System.Text.RegularExpressions;

namespace LLMPromptEnhancerExtension;

/// <summary>Takes a prompt apart so a language model can rewrite the prose without breaking the prompt syntax:
/// every wildcard / random / lora / embed / other tag becomes a placeholder like [[T1]], the parts after a section tag
/// (segment, region, base, refiner, video ...) stay out of the rewrite, and afterwards everything is put back.</summary>
public class ProtectedPrompt
{
    /// <summary>Tags that start a new prompt section. Everything from the first one on is kept as it is.</summary>
    public static readonly HashSet<string> SectionTags = ["segment", "object", "region", "extend", "base", "refiner", "video", "videoswap", "pixeldecoder"];

    /// <summary>The main section, with placeholders instead of tags.</summary>
    public string Text;

    /// <summary>The original text of each placeholder (index 0 = [[T1]]).</summary>
    public List<string> Tokens = [];

    /// <summary>What each placeholder stands for, told to the model.</summary>
    public List<string> Hints = [];

    /// <summary>Everything from the first section tag on, kept verbatim.</summary>
    public string Tail = "";

    /// <summary>Matches a placeholder in the model's answer, tolerant of spaces and single brackets.</summary>
    public static readonly Regex PlaceholderRegex = new(@"\[\[?\s*T(\d+)\s*\]\]?", RegexOptions.Compiled);

    /// <summary>Dynamic-Prompts style wildcard: __name__.</summary>
    public static readonly Regex DunderWildcard = new(@"^__[A-Za-z0-9_\-/\\. *]+?__", RegexOptions.Compiled);

    /// <summary>Reads a wildcard file's first few options as examples, or null.</summary>
    public static Func<string, string> WildcardExampleSource = DefaultWildcardExamples;

    /// <summary>Takes a prompt apart.</summary>
    /// <param name="prompt">The prompt as typed (or as filled in by SwarmUI).</param>
    /// <param name="wildcardExamples">Whether to show the model a few values of each wildcard file.</param>
    public static ProtectedPrompt Protect(string prompt, bool wildcardExamples)
    {
        ProtectedPrompt result = new();
        StringBuilder text = new();
        prompt ??= "";
        int i = 0;
        while (i < prompt.Length)
        {
            char c = prompt[i];
            if (c == '<')
            {
                int end = FindClose(prompt, i, '<', '>');
                if (end > i)
                {
                    string tag = prompt[i..(end + 1)];
                    string name = TagName(tag);
                    if (SectionTags.Contains(name))
                    {
                        result.Tail = prompt[i..];
                        break;
                    }
                    text.Append(result.Add(tag, HintForTag(name, tag, wildcardExamples)));
                    i = end + 1;
                    continue;
                }
            }
            else if (c == '\0')
            {
                // Already processed embedding marker: \0swarmembed:name\0end
                int end = prompt.IndexOf("\0end", i + 1, StringComparison.Ordinal);
                if (end > i)
                {
                    end += 4;
                    text.Append(result.Add(prompt[i..end], "an embedding (technical - keep it)"));
                    i = end;
                    continue;
                }
            }
            else if (c == '{')
            {
                int end = FindClose(prompt, i, '{', '}');
                if (end > i)
                {
                    string block = prompt[i..(end + 1)];
                    if (block.Contains('|'))
                    {
                        text.Append(result.Add(block, $"one of: {string.Join(" / ", SplitTop(block[1..^1], '|').Select(s => s.Trim()))}"));
                        i = end + 1;
                        continue;
                    }
                }
            }
            else if (c == '_' && i + 1 < prompt.Length && prompt[i + 1] == '_')
            {
                Match m = DunderWildcard.Match(prompt[i..]);
                if (m.Success)
                {
                    string name = m.Value[2..^2];
                    text.Append(result.Add(m.Value, WildcardHint(name, wildcardExamples)));
                    i += m.Length;
                    continue;
                }
            }
            text.Append(c);
            i++;
        }
        result.Text = text.ToString();
        return result;
    }

    /// <summary>Whether the main section has words to rewrite (not only tags and punctuation).</summary>
    public bool HasProse => Regex.IsMatch(PlaceholderRegex.Replace(Text, " "), @"\p{L}");

    /// <summary>Adds a placeholder and returns its text.</summary>
    public string Add(string original, string hint)
    {
        Tokens.Add(original);
        Hints.Add(hint);
        return $"[[T{Tokens.Count}]]";
    }

    /// <summary>The placeholder rules appended to the instruction, or "" if there are no placeholders.</summary>
    public string Instructions()
    {
        if (Tokens.Count == 0)
        {
            return "";
        }
        StringBuilder sb = new();
        sb.Append("\n\nThe prompt contains placeholders like [[T1]]. Each one is replaced later by the value given below (a random pick, or technical syntax). ");
        sb.Append("Copy every placeholder into your answer exactly as written (double square brackets), each exactly once, where it fits in the sentence. ");
        sb.Append("You do NOT know which value a random placeholder will get - the examples are very different from each other and any of them can be picked. ");
        sb.Append("A random placeholder fully decides its part of the picture: write nothing at all about that part yourself, and nothing that assumes one of its values. ");
        sb.Append("For example, if a placeholder is a location or setting, write no setting details anywhere (no room, walls, furniture, objects around, landscape, sky, weather, time of day, indoor or outdoor); ");
        sb.Append("if it is clothing, write no clothing details; if it is a person or a pose, write no details of that. ");
        sb.Append("Enrich only the parts the user wrote out in words, and keep each placeholder as a self-contained phrase.\nPlaceholders:");
        for (int i = 0; i < Tokens.Count; i++)
        {
            sb.Append($"\n[[T{i + 1}]] = {Hints[i]}");
        }
        return sb.ToString();
    }

    /// <summary>Puts the original tags back into the model's answer. Placeholders the model dropped are appended, duplicates removed.</summary>
    public string Restore(string answer)
    {
        HashSet<int> used = [];
        string restored = PlaceholderRegex.Replace(answer ?? "", m =>
        {
            int index = int.Parse(m.Groups[1].Value) - 1;
            if (index < 0 || index >= Tokens.Count || !used.Add(index))
            {
                return "";
            }
            return Tokens[index];
        });
        restored = Regex.Replace(restored, @"[ \t]{2,}", " ").Replace(" ,", ",");
        restored = Regex.Replace(restored, @",(\s*,)+", ",").Trim().Trim(',').Trim();
        for (int i = 0; i < Tokens.Count; i++)
        {
            if (!used.Contains(i))
            {
                restored = restored.Length == 0 ? Tokens[i] : $"{restored.TrimEnd(',', ' ')}, {Tokens[i]}";
            }
        }
        if (!string.IsNullOrEmpty(Tail))
        {
            restored = restored.Length == 0 ? Tail : $"{restored} {Tail}";
        }
        return restored;
    }

    /// <summary>Finds the index of the bracket closing the one at 'start', or -1.</summary>
    public static int FindClose(string s, int start, char open, char close)
    {
        int depth = 0;
        for (int i = start; i < s.Length; i++)
        {
            if (s[i] == open)
            {
                depth++;
            }
            else if (s[i] == close)
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }
        return -1;
    }

    /// <summary>Splits on a separator outside of nested brackets.</summary>
    public static List<string> SplitTop(string s, char sep)
    {
        List<string> parts = [];
        int depth = 0;
        int last = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '<' || c == '{' || c == '(' || c == '[')
            {
                depth++;
            }
            else if (c == '>' || c == '}' || c == ')' || c == ']')
            {
                depth--;
            }
            else if (c == sep && depth == 0)
            {
                parts.Add(s[last..i]);
                last = i + 1;
            }
        }
        parts.Add(s[last..]);
        return parts;
    }

    /// <summary>The lowercase name of a tag like '&lt;wildcard[1,2]:name&gt;' ('wildcard').</summary>
    public static string TagName(string tag)
    {
        string inner = tag[1..^1];
        int stop = inner.IndexOfAny([':', '[', '>', '<', ' ']);
        string name = stop < 0 ? inner : inner[..stop];
        return name.Trim().ToLowerInvariant();
    }

    /// <summary>The data part of a tag (after the first ':'), or "".</summary>
    public static string TagData(string tag)
    {
        string inner = tag[1..^1];
        int colon = inner.IndexOf(':');
        return colon < 0 ? "" : inner[(colon + 1)..];
    }

    /// <summary>Describes a tag for the model.</summary>
    public static string HintForTag(string name, string tag, bool wildcardExamples)
    {
        string data = TagData(tag);
        switch (name)
        {
            case "wildcard":
            case "wc":
                return WildcardHint(data.Split(',')[0].Trim(), wildcardExamples);
            case "random":
            case "r":
                {
                    List<string> opts = SplitTop(data, '|');
                    if (opts.Count == 1)
                    {
                        opts = SplitTop(data, ',');
                    }
                    return $"one random pick of: {string.Join(" / ", opts.Select(o => o.Trim()).Where(o => o.Length > 0).Take(12))}";
                }
            case "alternate":
            case "alt":
            case "fromto":
                return "a value that changes during generation (keep it)";
            case "lora":
                return "a LoRA (technical - keep it)";
            case "embed":
            case "embedding":
                return "an embedding (technical - keep it)";
            case "preset":
            case "p":
            case "param":
            case "setvar":
            case "var":
            case "setmacro":
            case "macro":
            case "trigger":
            case "comment":
            case "break":
            case "repeat":
            case "weight":
                return "special syntax (technical - keep it)";
            default:
                return "special syntax (keep it)";
        }
    }

    /// <summary>Describes a wildcard for the model.</summary>
    public static string WildcardHint(string name, bool wildcardExamples)
    {
        string hint = $"a random pick from the wildcard list '{name}'";
        if (wildcardExamples)
        {
            string examples = WildcardExampleSource?.Invoke(name);
            if (!string.IsNullOrWhiteSpace(examples))
            {
                hint += $" ({examples})";
            }
        }
        return hint;
    }

    /// <summary>A few options of a SwarmUI wildcard file, or null if it doesn't exist.</summary>
    public static string DefaultWildcardExamples(string name)
    {
        try
        {
            string card = WildcardsHelper.ListFiles.FirstOrDefault(f => f.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? WildcardsHelper.ListFiles.FirstOrDefault(f => f.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase));
            if (card is null)
            {
                return null;
            }
            WildcardsHelper.Wildcard wildcard = WildcardsHelper.GetWildcard(card);
            if (wildcard?.Options is null || wildcard.Options.Length == 0)
            {
                return null;
            }
            // Spread over the whole file: files are often sorted in blocks (all indoor first, then outdoor ...),
            // and the first lines alone made the model assume the first block's kind (owner: 'The room, <wildcard:Locations/All-In-Out>').
            List<string> usable = [.. wildcard.Options.Select(o => o.Trim()).Where(o => o.Length > 0 && o.Length < 120)];
            if (usable.Count == 0)
            {
                return null;
            }
            const int maxExamples = 6;
            List<string> picks = [];
            if (usable.Count <= maxExamples)
            {
                picks = usable;
            }
            else
            {
                for (int i = 0; i < maxExamples; i++)
                {
                    picks.Add(usable[(int)((long)i * (usable.Count - 1) / (maxExamples - 1))]);
                }
            }
            return $"{usable.Count} different values, eg {string.Join(" / ", picks.Distinct())}";
        }
        catch (Exception)
        {
            return null;
        }
    }
}
