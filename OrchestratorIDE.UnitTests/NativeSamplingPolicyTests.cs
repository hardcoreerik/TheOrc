// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Core.Runtime;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// Pure-logic coverage for ToolCallGrammarBuilder/NativeSamplingPolicy -- no native LLamaSharp
/// backend or GGUF model required, since GBNF string construction and the null-tools/empty-tools
/// fallback paths are ordinary C#. Genuine end-to-end proof that a loaded native model actually
/// obeys this grammar (can generate a live tool name, cannot generate a fabricated one) needs a
/// real GGUF + native backend; no existing test in this suite loads one (RuntimeOrchestratorTests
/// and friends all use synthetic/temp-file fixtures, never LoadModelAsync against a real model),
/// so that class of test is intentionally NOT added here -- see the OrcEngine steering task's
/// final report for why this is deferred rather than silently skipped.
/// </summary>
[TestFixture]
public sealed class NativeSamplingPolicyTests
{
    private static object Tool(string name) => new
    {
        type = "function",
        function = new { name, parameters = new { type = "object", properties = new { } } },
    };

    [Test]
    public void Build_Grammar_Contains_Every_Live_Tool_Name()
    {
        var tools = new List<object> { Tool("read_file"), Tool("write_file") };

        var gbnf = ToolCallGrammarBuilder.Build(tools);

        Assert.That(gbnf, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(gbnf, Does.Contain("\\\"read_file\\\""));
            Assert.That(gbnf, Does.Contain("\\\"write_file\\\""));
        });
    }

    [Test]
    public void Build_Grammar_Does_Not_Contain_A_NonExistent_Tool_Name()
    {
        // The grammar's tool-name alternation must be a closed set -- a name that was never in
        // the live tool list must not appear anywhere in the compiled grammar, since that's the
        // only thing standing between the model and fabricating a call to it.
        var tools = new List<object> { Tool("read_file") };

        var gbnf = ToolCallGrammarBuilder.Build(tools);

        Assert.That(gbnf, Is.Not.Null);
        Assert.That(gbnf, Does.Not.Contain("delete_everything_now"));
    }

    [Test]
    public void Build_FreeText_Branch_Forbids_Open_Brace_Anywhere()
    {
        // CodeRabbit-caught regression class (see ToolCallGrammarBuilder's own docs): an earlier
        // version only forbade a LEADING brace, so a free-text reply could still embed a
        // `{...}` blob later in the text that ToolCallTextParser would then parse as a real tool
        // call, completely bypassing the name restriction. free-text must forbid '{' everywhere,
        // not just at position 0.
        var tools = new List<object> { Tool("read_file") };

        var gbnf = ToolCallGrammarBuilder.Build(tools);

        Assert.That(gbnf, Is.Not.Null);
        Assert.That(gbnf, Does.Contain("free-text   ::= [^{\\x00]*"),
            "free-text must exclude '{' from its entire character class, not just forbid a leading brace");
    }

    [Test]
    public void Build_Returns_Null_When_No_Tool_Names_Extractable()
    {
        // An empty alternation would make every tool call unreachable -- worse than no grammar
        // at all. Build() must signal "give up, use unconstrained decoding" via null, not emit
        // a grammar no tool call could ever satisfy.
        var malformed = new List<object> { new { not_a_recognized_shape = true } };

        var gbnf = ToolCallGrammarBuilder.Build(malformed);

        Assert.That(gbnf, Is.Null);
    }

    [Test]
    public void Build_Escapes_Quotes_And_Backslashes_In_Tool_Names()
    {
        var tools = new List<object> { Tool("weird\"name\\here") };

        var gbnf = ToolCallGrammarBuilder.Build(tools);

        Assert.That(gbnf, Is.Not.Null);
        Assert.That(gbnf, Does.Contain("weird\\\"name\\\\here"));
    }

    [Test]
    public void TryBuildToolGrammar_Returns_Null_For_Empty_Tool_List()
    {
        Assert.That(NativeSamplingPolicy.TryBuildToolGrammar([]), Is.Null);
    }

    [Test]
    public void TryBuildToolGrammar_Returns_Null_For_Null_Tool_List()
    {
        Assert.That(NativeSamplingPolicy.TryBuildToolGrammar(null), Is.Null);
    }

    [Test]
    public void BuildPipeline_Does_Not_Throw_With_No_Tools()
    {
        // No-tools behavior must remain normal unconstrained conversation -- constructing the
        // pipeline with an empty/null tool list must not throw, and its Grammar must be null.
        using var pipeline = NativeSamplingPolicy.BuildPipeline(temperature: 0.7, topP: null, tools: null);

        Assert.That(pipeline.Grammar, Is.Null);
    }
}
