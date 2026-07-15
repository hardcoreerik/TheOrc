// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Core.Runtime;

namespace OrchestratorIDE.UnitTests;

[TestFixture]
public sealed class LLamaSharpRuntimeThinkingSuppressionTests
{
    // Trimmed excerpt of Qwen3.5-9B-Q8_0's actual tokenizer.chat_template (extracted
    // directly from the GGUF), keeping only the trailing add_generation_prompt block that
    // matters here -- the full template is ~7.8KB and irrelevant to this specific check.
    private const string Qwen35TemplateTail =
        "{%- if add_generation_prompt %}\n" +
        "    {{- '<|im_start|>assistant\\n' }}\n" +
        "    {%- if enable_thinking is defined and enable_thinking is true %}\n" +
        "        {{- '<think>\\n' }}\n" +
        "    {%- else %}\n" +
        "        {{- '<think>\\n\\n</think>\\n\\n' }}\n" +
        "    {%- endif %}\n" +
        "{%- endif %}";

    private const string PlainChatMlTemplate =
        "{% for message in messages %}{{'<|im_start|>' + message['role'] + '\\n' + " +
        "message['content'] + '<|im_end|>' + '\\n'}}{% endfor %}" +
        "{% if add_generation_prompt %}{{ '<|im_start|>assistant\\n' }}{% endif %}";

    // Mentions the enable_thinking variable name (e.g. a template with unrelated custom
    // logic reusing that name) but does NOT emit Qwen's specific empty-seed shape -- must
    // NOT be treated as supporting suppression, since appending Qwen's literal text would
    // be wrong for whatever this template actually does with the variable.
    private const string EnableThinkingNameOnlyNoSeedTemplate =
        "{%- if enable_thinking is defined and enable_thinking is true %}" +
        "{{- '<reasoning-mode-on/>' }}" +
        "{%- endif %}" +
        "{% if add_generation_prompt %}{{ '<|im_start|>assistant\\n' }}{% endif %}";

    [Test]
    public void SupportsThinkingSuppression_True_For_Qwen35Style_Template()
    {
        Assert.That(LLamaSharpRuntime.SupportsThinkingSuppression(Qwen35TemplateTail), Is.True);
    }

    [Test]
    public void SupportsThinkingSuppression_False_When_EnableThinking_Present_But_Seed_Shape_Differs()
    {
        // Regression coverage for the dual-marker tightening (Grok review, PR #58):
        // the variable name alone is not sufficient.
        Assert.That(LLamaSharpRuntime.SupportsThinkingSuppression(EnableThinkingNameOnlyNoSeedTemplate), Is.False);
    }

    [Test]
    public void SupportsThinkingSuppression_False_For_Plain_ChatMl_Template()
    {
        Assert.That(LLamaSharpRuntime.SupportsThinkingSuppression(PlainChatMlTemplate), Is.False);
    }

    [Test]
    public void SupportsThinkingSuppression_False_For_Null()
    {
        Assert.That(LLamaSharpRuntime.SupportsThinkingSuppression(null), Is.False);
    }

    [Test]
    public void SupportsThinkingSuppression_False_For_Empty_String()
    {
        Assert.That(LLamaSharpRuntime.SupportsThinkingSuppression(""), Is.False);
    }

    [Test]
    public void ApplyThinkingSuppression_Appends_Empty_Think_Block_After_Assistant_Preamble()
    {
        var rendered = "<|im_start|>system\nYou are helpful.<|im_end|>\n" +
                       "<|im_start|>user\nHi<|im_end|>\n" +
                       "<|im_start|>assistant\n";

        var result = LLamaSharpRuntime.ApplyThinkingSuppression(rendered);

        Assert.That(result, Is.EqualTo(rendered + "<think>\n\n</think>\n\n"));
    }

    [Test]
    public void ApplyThinkingSuppression_NoOp_When_Prompt_Does_Not_End_In_Newline()
    {
        // Defends against appending onto a shape that isn't the expected
        // "...<|im_start|>assistant\n" tail -- e.g. a template variant that doesn't
        // trail with a newline. Malformed input should pass through unchanged rather
        // than silently corrupt the prompt.
        const string rendered = "<|im_start|>assistant";

        var result = LLamaSharpRuntime.ApplyThinkingSuppression(rendered);

        Assert.That(result, Is.EqualTo(rendered));
    }

    [Test]
    public void ApplyThinkingSuppression_Is_Idempotent_Does_Not_Double_Append()
    {
        // Guards against a double call (or a future LLamaSharp version that DOES evaluate
        // the template's own <think> seed) producing two back-to-back think blocks.
        var once = LLamaSharpRuntime.ApplyThinkingSuppression("<|im_start|>assistant\n");
        var twice = LLamaSharpRuntime.ApplyThinkingSuppression(once);

        Assert.That(twice, Is.EqualTo(once));
    }

    [Test]
    public void ApplyThinkingSuppression_NoOp_When_Prompt_Already_Has_A_Think_Marker()
    {
        // Covers the case where a future/different LLamaTemplate render already emitted
        // <think>\n itself (the model's OWN opt-in seed) -- must not append a second block.
        const string rendered = "<|im_start|>assistant\n<think>\n";

        var result = LLamaSharpRuntime.ApplyThinkingSuppression(rendered);

        Assert.That(result, Is.EqualTo(rendered));
    }

    [Test]
    public void ApplyThinkingSuppression_Still_Applies_When_Think_Text_Appears_Earlier_In_History()
    {
        // Regression coverage (Grok review, PR #58): the idempotency guard must only look
        // at the prompt's TAIL, not scan the whole prompt -- otherwise any earlier message
        // (conversation history, a document being analyzed) that happens to mention the
        // literal text "<think>" for unrelated reasons would false-positive and silently
        // skip suppression for the entire call, re-enabling full reasoning mode.
        var rendered = "<|im_start|>user\nPlease explain what a <think> tag is used for.<|im_end|>\n" +
                       "<|im_start|>assistant\n";

        var result = LLamaSharpRuntime.ApplyThinkingSuppression(rendered);

        Assert.That(result, Is.EqualTo(rendered + "<think>\n\n</think>\n\n"));
    }
}
