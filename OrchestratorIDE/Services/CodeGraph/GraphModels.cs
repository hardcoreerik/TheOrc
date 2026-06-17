// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace OrchestratorIDE.Services.CodeGraph;

/// <summary>
/// A node in the code knowledge graph (C# types, methods, routes, files).
/// Produced by RoslynIndexer (step 2+). Persisted by GraphRepository.
/// </summary>
public sealed record CodeNode(
    int? Id,
    string Project,
    string Label,                 // "Function" | "Method" | "Class" | "Interface" | "Route" | "File"
    string Name,
    string QualifiedName,
    string FilePath,
    int LineStart,
    int LineEnd,
    int? Cyclomatic,
    int? Cognitive,
    int? LoopDepth,
    int? TransitiveLoopDepth,
    int? LinearScanInLoop,
    bool IsRecursive = false,
    int Degree = 0
);

/// <summary>
/// Directed edge between two nodes. Edge types are structural (CALLS primary for v1).
/// </summary>
public sealed record CodeEdge(
    int? Id,
    string Project,
    int SrcId,
    int DstId,
    string EdgeType               // "CALLS" | "IMPLEMENTS" | "IMPORTS" | "ROUTES_TO" | ...
);

/// <summary>
/// Persistent Architecture Decision Record (agent memory). Updated via graph_adr tool.
/// Fields: title/decision/status per step 4. Status: proposed | accepted | deprecated | superseded.
/// </summary>
public sealed record AdrRecord(
    int? Id,
    string Project,
    string Title,
    string Decision,
    string Status,
    string? Body,
    string CreatedAt
);
