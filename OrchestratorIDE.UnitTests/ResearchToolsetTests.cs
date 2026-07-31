// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Research;

namespace OrchestratorIDE.UnitTests;

[TestFixture]
public sealed class ResearchToolsetTests
{
    [Test]
    public void ParseReActCalls_acceptsInnerTagsWhenNativeTemplateDropsOuterWrapper()
    {
        var calls = ResearchToolset.ParseReActCalls(
            "<name>atlas_graph</name><args>{\"run_id\":\"run_0123456789ab\"}</args>");

        Assert.Multiple(() =>
        {
            Assert.That(calls, Has.Count.EqualTo(1));
            Assert.That(calls[0].Name, Is.EqualTo("atlas_graph"));
            Assert.That(calls[0].Args["run_id"]?.ToString(), Is.EqualTo("run_0123456789ab"));
        });
    }

    [Test]
    public void ParseReActCalls_rejectsMalformedStandaloneArguments()
    {
        var calls = ResearchToolset.ParseReActCalls(
            "<name>atlas_graph</name><args>run_0123456789ab</args>");

        Assert.That(calls, Is.Empty);
    }

    [Test]
    public void ParseReActCalls_doesNotDuplicateCompleteBlocks()
    {
        var calls = ResearchToolset.ParseReActCalls(
            "<tool_call><name>image_gallery</name><args>{\"limit\":3}</args></tool_call>");

        Assert.That(calls, Has.Count.EqualTo(1));
    }
}
