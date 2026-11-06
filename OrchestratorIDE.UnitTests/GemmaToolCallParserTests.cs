// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Core;

namespace OrchestratorIDE.UnitTests;

[TestFixture]
public class GemmaToolCallParserTests
{
    [Test]
    public void Bare_SingleArg()
    {
        var calls = ToolCallTextParser.Parse("call:list_files{path:textstats}");
        Assert.That(calls, Has.Count.EqualTo(1));
        Assert.That(calls[0].Name, Is.EqualTo("list_files"));
        Assert.That(calls[0].Arguments["path"], Is.EqualTo("textstats"));
        Assert.That(calls[0].IsTextFormat, Is.True);
    }

    [Test]
    public void Bare_MultiArg_ContentKeepsCommasBracesAndNewlines()
    {
        var text = "call:write_file{content:def f(a, b):\n    return {'x': a, 'y': b}\n,path:textstats/stats.py,reason:Write module}";
        var calls = ToolCallTextParser.Parse(text);
        Assert.That(calls, Has.Count.EqualTo(1));
        Assert.That(calls[0].Arguments["content"], Is.EqualTo("def f(a, b):\n    return {'x': a, 'y': b}"));
        Assert.That(calls[0].Arguments["path"], Is.EqualTo("textstats/stats.py"));
        Assert.That(calls[0].Arguments["reason"], Is.EqualTo("Write module"));
    }

    [Test]
    public void Quoted_Delimiters_AreExact()
    {
        var q = "<|\"|>";
        var text = $"<|tool_call>call:write_file{{content:{q}x = {{1: 2}}, path:fake{q},path:{q}a.py{q}}}<tool_call|>";
        var calls = ToolCallTextParser.Parse(text);
        Assert.That(calls, Has.Count.EqualTo(1));
        Assert.That(calls[0].Arguments["content"], Is.EqualTo("x = {1: 2}, path:fake"));
        Assert.That(calls[0].Arguments["path"], Is.EqualTo("a.py"));
    }

    [Test]
    public void MultipleCalls_ParsedInOrder()
    {
        var text = "call:list_files{path:.}\ncall:read_file{path:a.py}\ncall:run_shell{command:python3 a.py,reason:Run it}";
        var calls = ToolCallTextParser.Parse(text);
        Assert.That(calls.Select(c => c.Name), Is.EqualTo(new[] { "list_files", "read_file", "run_shell" }));
        Assert.That(calls[2].Arguments["command"], Is.EqualTo("python3 a.py"));
    }

    [Test]
    public void JsonInsideContent_DoesNotCreatePhantomCall()
    {
        var text = "call:write_file{content:{\"name\":\"x\",\"args\":{}},path:t.json}";
        var calls = ToolCallTextParser.Parse(text);
        Assert.That(calls, Has.Count.EqualTo(1));
        Assert.That(calls[0].Name, Is.EqualTo("write_file"));
    }

    [Test]
    public void PlainJsonCalls_StillWork()
    {
        var calls = ToolCallTextParser.Parse("{\"name\":\"read_file\",\"arguments\":{\"path\":\"a.py\"}}");
        Assert.That(calls, Has.Count.EqualTo(1));
        Assert.That(calls[0].Arguments["path"], Is.EqualTo("a.py"));
    }

    [Test]
    public void ProseWithCallWord_NoCalls()
    {
        Assert.That(ToolCallTextParser.Parse("Please call: me later {ok}"), Is.Empty);
    }
}
