# LLM Prompt Enhancer for SwarmUI

This extension uses a language model to rewrite your prompt into a richer one. It works with LM Studio, Ollama, OpenRouter, or any other server with an OpenAI-compatible API.\
**It keeps your wildcards and other prompt syntax working!**

- Recommended operation is **a new prompt for each generation** (enable in groups on the left)
- It only edits your prompt if you want it to (✨ button, reversible)

Background: I wasn't happy with existing enhancers, which do not understand SwarmUI's prompt syntax (especially wildcards) and directly edit the prompt, so: this.\
I use a lot of wildcard prompts and then do multiple gens, the prompt rewrite takes ~1s per gen here.\

YMMV (greatly), because I generate on a PC with a faster card, and have the LLM loaded on a PC with a slower card.\
I use the [Dolphin Mistral Venice Edition](https://huggingface.co/dphn/Dolphin-Mistral-24B-Venice-Edition), which does any kind of prompt including NSFW ones.

Be aware that running both SwarmUI and an LLM on one card can get dicey. Offload the LLM to RAM if you can (a - different - fast PC with lots of RAM is probably better for the LLM than the one you're generating on).

And yes, it's written by Claude, i couldn't have. I've been testing/using it and am quite happy with the LLM rewrites and the resulting images, so maybe someone else finds it useful, too.

## Install by hand:

1. In `SwarmUI/src/Extensions/`, run `git clone https://github.com/snottyone/SwarmUI-LLMPromptEnhancer`.\
SwarmUI's ## Install

1. In `SwarmUI/src/Extensions/`, run `git clone https://github.com/snottyone/SwarmUI-LLMPromptEnhancer` - with `git clone`
2. Restart SwarmUI with its normal launcher. It compiles the extension on start.
3. Open the **LLM Prompt Enhancer** tab, pick the provider, check the URL, click **Load models**, pick a model and click **Save**.
   Use **Try it** to check the answer. check updates the extension.
2. Restart SwarmUI with its normal launcher. It compiles the extension on start.
3. Open the **LLM Prompt Enhancer** tab, pick the provider, check the URL, click **Load models**, pick a model and click **Save**.
   Use **Try it** to check the answer.

| Provider | Usual URL | Notes |
|---|---|---|
| LM Studio | `http://localhost:1234/v1` | Start LM Studio's local server and load a model. |
| Ollama | `http://localhost:11434/v1` | Uses Ollama's OpenAI-compatible endpoint. Pull a model first. |
| OpenRouter | `https://openrouter.ai/api/v1` | Cloud models from many makers (GPT, Claude, Gemini, Llama, DeepSeek ...) with one API key. **Paid models cost money per request**; models ending in `:free` don't. |
| Other | your URL | Any `/v1/chat/completions` server: local ones (vLLM, llama.cpp server, text-generation-webui ...) or a cloud provider's own OpenAI-compatible API with its key, such as `https://api.openai.com/v1`. |

With a cloud model, your prompts are sent to that provider. In *Every image* mode that is one request per image.

The URL is resolved **on the machine running SwarmUI**, not in your browser. If the LLM runs on a different machine, use that machine's address.

## Use

- **LLM Prompt Enhancer** parameter group (switch it on, like any group): enhances the prompt automatically while generating. **Your prompt box is never changed**; only the prompt sent for each image is.
  - **Mode**:
    - *Every image* (default): for each image, SwarmUI fills in the wildcards first, then the model rewrites that finished prompt. The model sees the actual picks (say, the location) and writes around them, so nothing clashes. This is one LLM call per image.
    - *Once per Generate*: one LLM call per click. Wildcards stay in place, so every image still gets its own picks. The model doesn't know those picks, so it is told to write nothing about the parts a wildcard decides.
  - **Creativity**: the model's temperature.
  - **Instruction**: an optional extra wish for this generation, such as "moody film still". The ✨ button uses these values too.
- **✨ button** under the **+** next to the prompt box: rewrites the prompt in the box so you can read and edit it before generating. Like *Once per Generate*, it can't know the wildcard picks and leaves those parts alone. **Shift+click** puts the previous prompt back.
- The image metadata keeps your original prompt (`original_prompt`) next to the enhanced one.

## How wildcards and syntax are kept

Before the prompt goes to the model, every piece of syntax is replaced by a placeholder like `[[T1]]`. This covers:
- `<wildcard:name>` / `<wc:...>`, `<random:a|b>`, `<lora:...>`, `<embed:...>`, `<preset:...>`, `<var:...>` and other `<...>` tags, nested ones included
- Dynamic-Prompts style `__name__` and `{a|b}`

The model is told what each placeholder stands for. For a wildcard, it gets the number of values and six examples spread over the whole file (in SwarmUI's wildcards folder), so a file sorted in blocks (indoor first, then outdoor) doesn't make it assume one kind. The examples can be switched off in the settings. The model is told it doesn't know which value will be picked, and must write nothing about what a wildcard decides. For a location wildcard, that means no room, walls, sky or weather. The model's answer gets the original syntax back. A placeholder the model dropped is appended at the end, and a doubled one is removed.

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
