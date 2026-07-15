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

    [Test]
    public void SupportsThinkingSuppression_True_For_Qwen35Style_Template()
    {
        Assert.That(LLamaSharpRuntime.SupportsThinkingSuppression(Qwen35TemplateTail), Is.True);
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
    public void ApplyThinkingSuppression_Idempotent_Guard_Does_Not_Apply_Twice_Manually()
    {
        // Not a code guarantee (the runtime only calls this once per render via the
        // cached _templateSupportsThinkingSuppression flag) -- documents that calling it
        // twice WOULD double-append, so any future caller must not do that.
        var once = LLamaSharpRuntime.ApplyThinkingSuppression("<|im_start|>assistant\n");
        var twice = LLamaSharpRuntime.ApplyThinkingSuppression(once);

        Assert.That(twice, Is.Not.EqualTo(once));
        Assert.That(twice, Does.Contain("<think>\n\n</think>\n\n<think>\n\n</think>\n\n"));
    }
}
