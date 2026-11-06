// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using NUnit.Framework;
using OrchestratorIDE.Agents;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// Swarm fixes from a full run on Apple Silicon (Gemma-4-26B-A4B as Boss and Worker): the Boss's
/// plan looped until the token limit and the retry never ran; Gemma 4's native control tokens
/// leaked into a Tester note; the Tester verified a main.py that was never written.
/// </summary>
[TestFixture]
public sealed class SwarmQualityTests
{
    // ── Boss repetition loop ──────────────────────────────────────────────────────────

    // How the real plan degenerated (2026-11-05): valid JSON, then one phrase on repeat.
    private static StringBuilder LoopingPlan()
    {
        var sb = new StringBuilder("""
            {
              "plan": "Implement a Python module for text statistics and a corresponding test suite using pytest.",
              "tasks": [
                {
                  "role": "RESEARCHER",
                  "priority": 1,
                  "title": "Research Python standard library for text processing",
                  "description": "Investigate the Python standard library modules relevant for text analysis (re, collections.Counter) to ensure efficient and-to-use functions for implementing the ability to implement the ability to
            """);
        for (var i = 0; i < 40; i++) sb.Append(" implement the ability to");
        return sb;
    }

    private static StringBuilder GoodPlan() => new("""
        {
          "plan": "Add a text statistics module and its tests.",
          "tasks": [
            { "role": "CODER", "priority": 1, "title": "Write textstats/stats.py", "description": "Implement word_count(text) using a regex over word characters, returning an int. Empty text returns 0." },
            { "role": "CODER", "priority": 1, "title": "Write average_word_length", "description": "Implement average_word_length(text) in textstats/stats.py, returning a float, 0.0 for no words." },
            { "role": "CODER", "priority": 2, "title": "Write top_words", "description": "Implement top_words(text, n) with collections.Counter, case-insensitive, returning (word, count) pairs." },
            { "role": "CODER", "priority": 2, "title": "Write tests/test_stats.py", "description": "pytest tests for all three functions: empty input, punctuation, case, ties, n <= 0." },
            { "role": "TESTER", "priority": 3, "title": "Run the tests", "description": "Run python3 -m pytest -q and report STATUS: PASS or STATUS: FAIL with the exact error text." }
          ]
        }
        """);

    [Test]
    public void A_phrase_on_repeat_is_detected_as_a_loop() =>
        Assert.That(SwarmSession.IsRepetitionLoop(LoopingPlan()), Is.True);

    [Test]
    public void A_normal_plan_with_repeated_keys_is_not_a_loop() =>
        Assert.That(SwarmSession.IsRepetitionLoop(GoodPlan()), Is.False);

    [TestCase('=')]
    [TestCase('-')]
    public void A_long_separator_line_is_not_a_loop(char c) =>
        Assert.That(SwarmSession.IsRepetitionLoop(new StringBuilder("intro\n" + new string(c, 500))), Is.False);

    [Test]
    public void A_few_repeated_code_lines_are_not_a_loop()
    {
        var code = new StringBuilder("def f():\n");
        for (var i = 0; i < 6; i++) code.Append("    total += 1\n");
        code.Append("    return total\n");
        Assert.That(SwarmSession.IsRepetitionLoop(code), Is.False);
    }

    [Test]
    public void An_unparseable_plan_counts_as_underplanned_however_long_it_is()
    {
        var fallback = SwarmSession.FallbackTask(LoopingPlan().ToString());
        Assert.That(fallback[0].Description!.Length, Is.GreaterThan(30), "the old length check alone missed it");
        Assert.That(SwarmSession.IsFallbackPlan(fallback), Is.True);
        Assert.That(SwarmSession.IsBossUnderPlanned(fallback), Is.True);
    }

    [Test]
    public void A_real_multi_task_plan_is_not_underplanned()
    {
        var plan = new List<SwarmTask>
        {
            new() { Role = SwarmWorkerRole.Coder, Title = "Write textstats/stats.py", Description = "Implement word_count, average_word_length and top_words." },
            new() { Role = SwarmWorkerRole.Coder, Title = "Write tests/test_stats.py", Description = "pytest tests for all three functions and their edge cases." },
        };
        Assert.That(SwarmSession.IsBossUnderPlanned(plan), Is.False);
        Assert.That(SwarmSession.IsFallbackPlan(plan), Is.False);
    }

    // ── Gemma 4 control tokens ────────────────────────────────────────────────────────

    [Test]
    public void Gemma4_native_thinking_marker_is_removed_from_a_tester_note() =>
        // Verbatim shape of the leaked Tester note.
        Assert.That(SwarmSession.StripGemma4Artifacts("ERRORS: none | NOTES: All files present and syntax checks pass.<|channel>thought | ERRORS: none"),
            Is.EqualTo("ERRORS: none | NOTES: All files present and syntax checks pass. | ERRORS: none"));

    [TestCase("<|channel>thought\nThe user wants a plan.<channel|>{\"tasks\": []}", "{\"tasks\": []}")]
    [TestCase("<|channel|>thought\nreasoning<channel|>answer", "answer")]
    [TestCase("<|turn>model\nhello<turn|>", "model\nhello")]
    [TestCase("<|tool_call|>{}", "{}")]
    public void Thinking_blocks_and_control_tokens_are_removed_in_both_spellings(string raw, string expected) =>
        Assert.That(SwarmSession.StripGemma4Artifacts(raw), Is.EqualTo(expected));

    [TestCase("if (a < b && c > d) return x | y;")]
    [TestCase("List<int> xs = new();")]
    public void Ordinary_code_is_left_alone(string code) =>
        Assert.That(SwarmSession.StripGemma4Artifacts(code), Is.EqualTo(code));

    // ── Tester target ─────────────────────────────────────────────────────────────────

    [Test]
    public void The_tester_checks_the_files_that_were_written_not_a_guessed_main_py()
    {
        var (entry, cmd) = SwarmSession.TesterTarget("Python", ["text_stats.py", "test_text_stats.py", "final_report.md"], "python3");
        Assert.That(entry, Is.EqualTo("text_stats.py"));
        Assert.That(cmd, Is.EqualTo("python3 -m py_compile \"text_stats.py\" \"test_text_stats.py\""));
    }

    [Test]
    public void The_usual_entry_point_wins_when_it_exists_and_is_the_fallback_when_nothing_was_written()
    {
        Assert.That(SwarmSession.TesterTarget("Python", ["utils.py", "main.py"], "python").EntryPoint, Is.EqualTo("main.py"));
        Assert.That(SwarmSession.TesterTarget("Python", [], "python").SyntaxCommand, Is.EqualTo("python -m py_compile \"main.py\""));
        Assert.That(SwarmSession.TesterTarget("JavaScript", ["app.js", "app.test.js"], "python3"),
            Is.EqualTo(("app.js", "node --check \"app.js\"")));
    }
}
