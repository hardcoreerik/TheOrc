// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
namespace OrchestratorIDE.UI.ViewModels;

public sealed record CitationViewModel(
    int Index,
    string SegmentId,
    string DocumentId,
    string HeadingPath,
    int CharStart,
    int CharEnd,
    string Quote,
    string VerificationLabel)   // Supported | PartiallySupported | Interpretive | CitationMismatch | Unverifiable
{
    public bool IsVerified =>
        VerificationLabel is "Supported" or "PartiallySupported";

    public bool IsInterpretive =>
        VerificationLabel == "Interpretive";

    public bool IsMismatch =>
        VerificationLabel is "CitationMismatch" or "Unverifiable";
}
