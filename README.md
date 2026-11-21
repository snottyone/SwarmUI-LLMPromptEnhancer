# LLM Prompt Enhancer for SwarmUI

Has a language model rewrite your prompt into a richer one. It works with **LM Studio**, **Ollama**, **OpenRouter**, or any other server with an OpenAI-compatible API. It keeps your **wildcards** and other prompt syntax working.

## Install

1. Copy (or `git clone`) this folder into `SwarmUI/src/Extensions/` (for example `SwarmUI/src/Extensions/SwarmUI-LLMPromptEnhancer/`).
2. Restart SwarmUI with its normal launcher. It compiles the extension on start.
3. Open the **LLM Prompt Enhancer** tab, pick the provider, check the URL, click **Load models**, pick a model and click **Save**.
   Use **Try it** to check the answer.

| Provider | Usual URL | Notes |
|---|---|---|
| LM Studio | `http://localhost:1234/v1` | Start LM Studio's local server and load a model. |
| Ollama | `http://localhost:11434/v1` | Uses Ollama's OpenAI-compatible endpoint. Pull a model first. |
| OpenRouter | `https://openrouter.ai/api/v1` | Needs an API key. **Paid models cost money per request.** |
| Other | your URL | Any `/v1/chat/completions` server (vLLM, llama.cpp server, text-generation-webui ...). |

The URL is resolved **on the machine running SwarmUI**, not in your browser. If the LLM runs on a different machine, use that machine's address.

## Use

- **✨ button** under the **+** next to the prompt box: rewrites the prompt in the box so you can read and edit it before generating. **Shift+click** puts the previous prompt back.
- **LLM Prompt Enhancer** parameter group (switch it on, like any group): enhances the prompt automatically while generating.
  - **Mode**:
    - *Once per Generate*: one LLM call per click. Wildcards stay in place, so every image still gets its own picks.
    - *Every image*: one LLM call per image, after the wildcards are filled in, so the model sees the actual picks. This is slower.
  - **Creativity**: the model's temperature.
  - **Instruction**: an optional extra wish for this generation, such as "moody film still". The ✨ button uses these values too.
- The image metadata keeps your original prompt (`original_prompt`) next to the enhanced one.

## How wildcards and syntax are kept

Before the prompt goes to the model, every piece of syntax is replaced by a placeholder like `[[T1]]`. This covers:
- `<wildcard:name>` / `<wc:...>`, `<random:a|b>`, `<lora:...>`, `<embed:...>`, `<preset:...>`, `<var:...>` and other `<...>` tags, nested ones included
- Dynamic-Prompts style `__name__` and `{a|b}`

The model is told what each placeholder stands for. For a wildcard, it gets a few lines from that wildcard file (in SwarmUI's wildcards folder). This can be switched off in the settings. The model's answer gets the original syntax back. A placeholder the model dropped is appended at the end, and a doubled one is removed.

Text from the first section tag on (`<segment:...>`, `<region:...>`, `<object:...>`, `<extend:...>`, `<base>`, `<refiner>`, `<video>`, ...) is not sent to the model and stays exactly as written.

The negative prompt is never changed.

## Settings

The settings are stored server-wide in `Data/LLMPromptEnhancer.json`. They include the provider, URL, model, API key, instruction, max answer length, timeout, what to do when the LLM fails during generation (stop with the error, or generate with the unchanged prompt), and wildcard examples.

- API keys are never sent to the browser.
- Changing the settings needs the permission `llm_prompt_enhancer_settings` (admins by default), because the server will connect to whatever URL is entered.
- Using the enhancer needs `llm_prompt_enhancer_use` (all users by default).

## Network connections

The extension connects only to the LLM server you configure. It does this when you click ✨, **Load models** or **Try it**, and when you generate with the group switched on. Nothing is sent anywhere else.

## Notes

- *Thinking* models (with `<think>` blocks) work: the reasoning is stripped. They need a larger **Max answer length**.
- A local LLM shares VRAM with the image model. On a small GPU, use a small LLM or a remote server.
- If the LLM hangs, generation waits until the **Timeout** runs out.

## Uninstall

Delete the folder from `src/Extensions/` and restart SwarmUI. You can also delete `Data/LLMPromptEnhancer.json`.

## License

MIT, see `LICENSE`.
