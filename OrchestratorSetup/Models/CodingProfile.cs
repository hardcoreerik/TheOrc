// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
namespace OrchestratorSetup.Models;

/// <summary>
/// Represents one of the 8 coding discipline profiles the user can select.
/// Displayed as a card on the Profile picker page.
/// </summary>
public class CodingProfile
{
    public string Id          { get; init; } = "";
    public string Name        { get; init; } = "";
    public string Emoji       { get; init; } = "";
    public string Description { get; init; } = "";
    public string AgentMdFile { get; init; } = ""; // filename in Setup/Profiles/

    /// <summary>All 8 built-in profiles in display order.</summary>
    public static readonly IReadOnlyList<CodingProfile> All = new[]
    {
        new CodingProfile
        {
            Id          = "web",
            Name        = "Web / Full-Stack",
            Emoji       = "🌐",
            Description = "TypeScript · React · Node.js · REST & GraphQL · SQL/NoSQL",
            AgentMdFile = "web-fullstack.agent.md",
        },
        new CodingProfile
        {
            Id          = "systems",
            Name        = "Systems / Embedded",
            Emoji       = "⚙️",
            Description = "C · C++ · Rust · RTOS · bare-metal MCUs · BSP drivers",
            AgentMdFile = "systems-embedded.agent.md",
        },
        new CodingProfile
        {
            Id          = "data",
            Name        = "Data / AI / ML",
            Emoji       = "📊",
            Description = "Python · PyTorch · scikit-learn · Pandas/Polars · MLflow",
            AgentMdFile = "data-ai-ml.agent.md",
        },
        new CodingProfile
        {
            Id          = "security",
            Name        = "Security / Pentest",
            Emoji       = "🔐",
            Description = "Recon · OWASP · Metasploit · Impacket · RF/Flipper tooling",
            AgentMdFile = "security-pentest.agent.md",
        },
        new CodingProfile
        {
            Id          = "uiux",
            Name        = "UI / UX Development",
            Emoji       = "🎨",
            Description = "Design tokens · WCAG 2.2 · React · Tailwind · Animation",
            AgentMdFile = "ui-ux.agent.md",
        },
        new CodingProfile
        {
            Id          = "game",
            Name        = "Game Development",
            Emoji       = "🎮",
            Description = "Unity C# · Unreal C++ · HLSL/GLSL · Physics · Game AI",
            AgentMdFile = "game-dev.agent.md",
        },
        new CodingProfile
        {
            Id          = "mobile",
            Name        = "Android / Apple Mobile",
            Emoji       = "📱",
            Description = "Kotlin + Jetpack Compose · Swift + SwiftUI · React Native",
            AgentMdFile = "mobile.agent.md",
        },
        new CodingProfile
        {
            Id          = "finance",
            Name        = "Finance / FinTech",
            Emoji       = "📈",
            Description = "Trading systems · Risk · Crypto · Decimal-correct math · Audit",
            AgentMdFile = "finance.agent.md",
        },
    };
}
