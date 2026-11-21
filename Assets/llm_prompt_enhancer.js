/** LLM Prompt Enhancer: the enhance button next to the prompt box, and the settings tab. */
class LLMPromptEnhancerUI {

    constructor() {
        /** The settings as last loaded from the server. */
        this.settings = null;
        /** Whether this user may change the settings. */
        this.canEdit = false;
        /** Provider shown in the settings form. */
        this.shownProvider = null;
        /** The prompt before the last enhancement, for undo. */
        this.undoText = null;
        /** Whether a request from the button is running. */
        this.busy = false;
        /** The button element. */
        this.button = null;
        /** Whether Save should remove the shown provider's stored key. */
        this.clearKeyPending = false;
    }

    /** Adds the enhance button next to the prompt box's '+' button. */
    addButton() {
        if (document.getElementById('llmpe_enhance_button')) {
            return;
        }
        let plusWrapper = document.querySelector('.alt-text-add-button-wrapper');
        if (!plusWrapper) {
            return;
        }
        let wrapper = createSpan(null, 'llmpe-button-wrapper');
        this.button = createSpan('llmpe_enhance_button', 'basic-button llmpe-button', '&#x2728;');
        this.button.title = 'Enhance the prompt with an LLM (wildcards are kept).\nShift+click: undo the last enhancement.';
        this.button.addEventListener('click', (e) => {
            if (e.shiftKey) {
                this.undo();
            }
            else {
                this.enhancePromptBox();
            }
        });
        wrapper.appendChild(this.button);
        plusWrapper.after(wrapper);
    }

    /** Reads the generation group's creativity and instruction, if those controls exist. */
    currentOptions() {
        let temperature = 0.7;
        let instruction = '';
        let tempElem = document.getElementById('input_llmenhancecreativity');
        if (tempElem && tempElem.value !== '') {
            temperature = parseFloat(tempElem.value);
        }
        let instrElem = document.getElementById('input_llmenhanceinstruction');
        if (instrElem) {
            instruction = instrElem.value;
        }
        return { temperature: isNaN(temperature) ? 0.7 : temperature, instruction: instruction };
    }

    /** Sends the prompt box's text to the LLM and puts the answer in its place. */
    enhancePromptBox() {
        if (this.busy) {
            return;
        }
        let box = getRequiredElementById('alt_prompt_textbox');
        let original = box.value;
        if (original.trim() == '') {
            showError('LLM Prompt Enhancer: the prompt is empty.');
            return;
        }
        let options = this.currentOptions();
        this.setBusy(true);
        genericRequest('LLMPromptEnhancerEnhance', { prompt: original, instruction: options.instruction, temperature: options.temperature }, data => {
            this.setBusy(false);
            if (box.value != original) {
                showError('LLM Prompt Enhancer: the prompt was changed while the LLM was working - the answer was not inserted.');
                return;
            }
            this.undoText = original;
            box.value = data.prompt;
            triggerChangeFor(box);
            this.button.title = 'Enhance the prompt with an LLM (wildcards are kept).\nShift+click: undo the last enhancement (available now).';
        }, 0, e => {
            this.setBusy(false, true);
            showError(e);
        });
    }

    /** Puts the prompt from before the last enhancement back. */
    undo() {
        if (this.undoText == null) {
            showError('LLM Prompt Enhancer: nothing to undo.');
            return;
        }
        let box = getRequiredElementById('alt_prompt_textbox');
        box.value = this.undoText;
        this.undoText = null;
        triggerChangeFor(box);
        this.button.title = 'Enhance the prompt with an LLM (wildcards are kept).\nShift+click: undo the last enhancement.';
    }

    /** Shows the button as working / failed / normal. */
    setBusy(busy, failed = false) {
        this.busy = busy;
        if (!this.button) {
            return;
        }
        this.button.classList.toggle('llmpe-busy', busy);
        this.button.classList.toggle('llmpe-failed', failed);
        if (failed) {
            setTimeout(() => this.button.classList.remove('llmpe-failed'), 4000);
        }
    }

    /** Loads the settings into the settings tab. */
    loadSettings() {
        if (!document.getElementById('llmpe_provider')) {
            return;
        }
        genericRequest('LLMPromptEnhancerGetSettings', {}, data => {
            this.settings = data.settings;
            this.canEdit = data.can_edit;
            this.fillForm();
        }, 0, e => {
            console.log(`LLM Prompt Enhancer: could not load settings: ${e}`);
        });
    }

    /** Fills the settings form from this.settings. */
    fillForm() {
        let s = this.settings;
        let select = getRequiredElementById('llmpe_provider');
        select.innerHTML = '';
        for (let id of Object.keys(s.providers)) {
            let option = document.createElement('option');
            option.value = id;
            option.textContent = s.providers[id].name;
            select.appendChild(option);
        }
        select.value = s.provider;
        this.shownProvider = s.provider;
        this.showProvider();
        getRequiredElementById('llmpe_system').value = s.system_prompt;
        getRequiredElementById('llmpe_max_tokens').value = s.max_tokens;
        getRequiredElementById('llmpe_timeout').value = s.timeout_seconds;
        getRequiredElementById('llmpe_on_error').value = s.on_error;
        getRequiredElementById('llmpe_wc_examples').checked = s.wildcard_examples;
        getRequiredElementById('llmpe_readonly_note').style.display = this.canEdit ? 'none' : 'block';
        let tab = document.querySelector('.llmpe-settings');
        for (let elem of tab.querySelectorAll('input, select, textarea, button')) {
            if (elem.closest('.llmpe-row') && !['llmpe_test_in', 'llmpe_test_run'].includes(elem.id)) {
                elem.disabled = !this.canEdit;
            }
        }
    }

    /** Shows the selected provider's URL / model / key state. */
    showProvider() {
        this.clearKeyPending = false;
        let p = this.settings.providers[this.shownProvider];
        getRequiredElementById('llmpe_url').value = p.url;
        getRequiredElementById('llmpe_model').value = p.model;
        getRequiredElementById('llmpe_key').value = '';
        getRequiredElementById('llmpe_key').placeholder = p.has_key ? '(a key is stored - type to replace it)' : (this.shownProvider == 'openrouter' ? 'required for OpenRouter' : 'usually not needed');
        getRequiredElementById('llmpe_key_state').textContent = p.has_key ? 'key stored' : '';
        getRequiredElementById('llmpe_model_pick').innerHTML = '';
        getRequiredElementById('llmpe_model_pick_row').style.display = 'none';
        getRequiredElementById('llmpe_models_state').textContent = '';
    }

    /** Keeps the edits of the shown provider in the working copy (so switching back and forth keeps them). */
    rememberProviderEdits() {
        if (!this.shownProvider) {
            return;
        }
        let p = this.settings.providers[this.shownProvider];
        p.url = getRequiredElementById('llmpe_url').value.trim();
        p.model = getRequiredElementById('llmpe_model').value.trim();
        p.pending_key = getRequiredElementById('llmpe_key').value.trim();
    }

    /** Asks the server which models the shown provider has. */
    loadModels() {
        let state = getRequiredElementById('llmpe_models_state');
        state.textContent = 'asking...';
        genericRequest('LLMPromptEnhancerListModels', {
            provider: this.shownProvider,
            url: getRequiredElementById('llmpe_url').value.trim(),
            api_key: getRequiredElementById('llmpe_key').value.trim()
        }, data => {
            let pick = getRequiredElementById('llmpe_model_pick');
            let modelBox = getRequiredElementById('llmpe_model');
            let current = modelBox.value.trim();
            pick.innerHTML = '';
            let first = document.createElement('option');
            first.value = '';
            first.textContent = current == '' || data.models.includes(current) ? '(choose a model)' : `(keep '${current}' - not on the server)`;
            pick.appendChild(first);
            for (let model of data.models) {
                let option = document.createElement('option');
                option.value = model;
                option.textContent = model;
                pick.appendChild(option);
            }
            pick.value = data.models.includes(current) ? current : '';
            getRequiredElementById('llmpe_model_pick_row').style.display = data.models.length == 0 ? 'none' : '';
            state.textContent = data.models.length == 0 ? 'the server has no models loaded' : `${data.models.length} models - pick one below, then Save`;
            if (current == '' && data.models.length == 1) {
                modelBox.value = data.models[0];
                pick.value = data.models[0];
            }
        }, 0, e => {
            state.textContent = e;
        });
    }

    /** Saves the form. */
    save() {
        let state = getRequiredElementById('llmpe_save_state');
        let key = getRequiredElementById('llmpe_key').value.trim();
        let input = {
            provider: this.shownProvider,
            url: getRequiredElementById('llmpe_url').value.trim(),
            model: getRequiredElementById('llmpe_model').value.trim(),
            system_prompt: getRequiredElementById('llmpe_system').value,
            max_tokens: parseInt(getRequiredElementById('llmpe_max_tokens').value) || 400,
            timeout_seconds: parseInt(getRequiredElementById('llmpe_timeout').value) || 120,
            on_error: getRequiredElementById('llmpe_on_error').value,
            wildcard_examples: getRequiredElementById('llmpe_wc_examples').checked
        };
        if (key != '') {
            input.api_key = key;
        }
        else if (this.clearKeyPending) {
            input.clear_api_key = true;
        }
        state.textContent = 'saving...';
        genericRequest('LLMPromptEnhancerSaveSettings', input, data => {
            this.clearKeyPending = false;
            this.settings = data.settings;
            this.fillForm();
            state.textContent = `Saved - using ${this.settings.providers[this.settings.provider].name}.`;
        }, 0, e => {
            state.textContent = e;
        });
    }

    /** Marks the shown provider's stored key for removal at the next Save. */
    clearKey() {
        getRequiredElementById('llmpe_key').value = '';
        this.clearKeyPending = true;
        getRequiredElementById('llmpe_key_state').textContent = 'the key will be removed when you Save';
    }

    /** Runs the 'Try it' box. */
    test() {
        let input = getRequiredElementById('llmpe_test_in').value;
        let out = getRequiredElementById('llmpe_test_out');
        if (input.trim() == '') {
            out.textContent = 'Type a prompt first.';
            return;
        }
        out.textContent = 'asking the model (uses the SAVED settings)...';
        let options = this.currentOptions();
        let started = Date.now();
        genericRequest('LLMPromptEnhancerEnhance', { prompt: input, instruction: options.instruction, temperature: options.temperature }, data => {
            out.textContent = `${data.prompt}\n\n(${((Date.now() - started) / 1000).toFixed(1)} s)`;
        }, 0, e => {
            out.textContent = e;
        });
    }

    /** Hooks up the settings tab's controls. */
    initSettingsTab() {
        if (!document.getElementById('llmpe_provider')) {
            return;
        }
        getRequiredElementById('llmpe_provider').addEventListener('change', () => {
            this.rememberProviderEdits();
            this.shownProvider = getRequiredElementById('llmpe_provider').value;
            this.showProvider();
            let pending = this.settings.providers[this.shownProvider].pending_key;
            if (pending) {
                getRequiredElementById('llmpe_key').value = pending;
            }
        });
        getRequiredElementById('llmpe_url_default').addEventListener('click', () => {
            getRequiredElementById('llmpe_url').value = this.settings.providers[this.shownProvider].default_url;
        });
        getRequiredElementById('llmpe_system_default').addEventListener('click', () => {
            getRequiredElementById('llmpe_system').value = this.settings.default_system_prompt;
        });
        getRequiredElementById('llmpe_load_models').addEventListener('click', () => this.loadModels());
        getRequiredElementById('llmpe_model_pick').addEventListener('change', () => {
            let picked = getRequiredElementById('llmpe_model_pick').value;
            if (picked != '') {
                getRequiredElementById('llmpe_model').value = picked;
            }
        });
        getRequiredElementById('llmpe_save').addEventListener('click', () => this.save());
        getRequiredElementById('llmpe_key_clear').addEventListener('click', () => this.clearKey());
        getRequiredElementById('llmpe_test_run').addEventListener('click', () => this.test());
        let tabLink = document.getElementById('maintab_llmpromptenhancer');
        if (tabLink) {
            tabLink.addEventListener('click', () => this.loadSettings());
        }
    }
}

let llmPromptEnhancer = new LLMPromptEnhancerUI();
llmPromptEnhancer.addButton();
llmPromptEnhancer.initSettingsTab();
sessionReadyCallbacks.push(() => llmPromptEnhancer.loadSettings());
