// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace OrchestratorIDE.Services.CodeGraph;

/// <summary>
/// Computes per-method complexity metrics using Roslyn.
/// - Cyclomatic + loop nesting via ControlFlowGraph.Create
/// - Cognitive via structured syntax walk (nesting-aware)
/// - linear_scan_in_loop via detection of scan calls (Contains/Where/Find/IndexOf/...) inside loop bodies
/// - is_recursive and transitive_loop_depth handled at graph level (CALLS edges) in GraphRepository
/// </summary>
public static class ComplexityAnalyzer
{
    public readonly record struct Metrics(
        int Cyclomatic,
        int Cognitive,
        int LoopDepth,
        int LinearScanInLoop);

    /// <summary>
    /// Analyze a method (or local function) declaration using its semantic model.
    /// Safe to call on any syntax; returns a default (1,0,0,0) when no body or unsupported.
    /// </summary>
    public static Metrics Analyze(SemanticModel model, SyntaxNode declaration)
    {
        if (model == null || declaration == null)
            return new Metrics(1, 0, 0, 0);

        ControlFlowGraph? cfg = null;
        try
        {
            // Preferred: body operation when available
            if (model.GetOperation(declaration) is IMethodBodyOperation methodBody)
            {
                cfg = ControlFlowGraph.Create(methodBody);
            }
            else
            {
                // Fallback: pass the declaration node itself
                cfg = ControlFlowGraph.Create(declaration, model);
            }
        }
        catch
        {
            // CFG not supported for this construct (e.g. extern, abstract, certain expressions) — fallback below
        }

        int cyclomatic = (cfg != null)
            ? ComputeCyclomatic(cfg)
            : ComputeCyclomaticFallback(declaration);

        var (cognitive, loopDepth, linScan) = ComputeCognitiveAndLoops(declaration);

        // Clamp to reasonable ranges (defensive)
        cyclomatic = Math.Max(1, Math.Min(cyclomatic, 1000));
        cognitive = Math.Max(0, Math.Min(cognitive, 1000));
        loopDepth = Math.Max(0, Math.Min(loopDepth, 100));
        linScan = (linScan != 0) ? 1 : 0;

        return new Metrics(cyclomatic, cognitive, loopDepth, linScan);
    }

    private static int ComputeCyclomatic(ControlFlowGraph cfg)
    {
        if (cfg.Blocks.IsDefaultOrEmpty)
            return 1;

        int n = 0;
        int e = 0;
        foreach (var block in cfg.Blocks)
        {
            if (!block.IsReachable) continue;
            n++;

            if (block.FallThroughSuccessor?.Destination != null)
                e++;

            var cond = block.ConditionalSuccessor;
            if (cond?.Destination != null)
            {
                // Avoid double-counting the same successor
                if (!ReferenceEquals(cond.Destination, block.FallThroughSuccessor?.Destination))
                    e++;
            }
        }

        // McCabe: E - N + 2 (connected function graph)
        int c = e - n + 2;
        return Math.Max(1, c);
    }

    private static int ComputeCyclomaticFallback(SyntaxNode root)
    {
        int decisions = 0;
        foreach (var n in root.DescendantNodes())
        {
            switch (n.Kind())
            {
                case SyntaxKind.IfStatement:
                case SyntaxKind.ForStatement:
                case SyntaxKind.ForEachStatement:
                case SyntaxKind.ForEachVariableStatement:
                case SyntaxKind.WhileStatement:
                case SyntaxKind.DoStatement:
                case SyntaxKind.SwitchStatement:
                case SyntaxKind.CatchClause:
                case SyntaxKind.ConditionalExpression:
                    decisions++;
                    break;
                case SyntaxKind.LogicalAndExpression:
                case SyntaxKind.LogicalOrExpression:
                    decisions++;
                    break;
            }
        }
        return 1 + decisions;
    }

    private static (int cognitive, int loopDepth, int lin) ComputeCognitiveAndLoops(SyntaxNode root)
    {
        var w = new CognitiveWalker();
        w.Visit(root);
        return (w.Cognitive, w.MaxLoopDepth, w.LinearScanInLoop ? 1 : 0);
    }

    private sealed class CognitiveWalker : CSharpSyntaxWalker
    {
        public int Cognitive { get; private set; }
        public int MaxLoopDepth { get; private set; }
        public bool LinearScanInLoop { get; private set; }

        private int _nest;
        private int _loop;

        private static readonly HashSet<string> ScanNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Find", "FindIndex", "FindLast", "FindLastIndex",
            "Contains", "IndexOf", "LastIndexOf",
            "Where", "Select", "First", "FirstOrDefault", "Single", "SingleOrDefault",
            "Last", "LastOrDefault", "Any", "All", "Count", "LongCount",
            "ElementAt", "ElementAtOrDefault", "Min", "Max", "Average", "Sum", "Aggregate",
            "OrderBy", "OrderByDescending", "ThenBy", "Skip", "Take"
        };

        public override void VisitIfStatement(IfStatementSyntax node)
        {
            Cognitive += 1 + _nest;
            _nest++;
            base.VisitIfStatement(node);
            _nest--;
        }

        public override void VisitForStatement(ForStatementSyntax node)
        {
            EnterLoop();
            base.VisitForStatement(node);
            ExitLoop();
        }

        public override void VisitForEachStatement(ForEachStatementSyntax node)
        {
            EnterLoop();
            base.VisitForEachStatement(node);
            ExitLoop();
        }

        public override void VisitForEachVariableStatement(ForEachVariableStatementSyntax node)
        {
            EnterLoop();
            base.VisitForEachVariableStatement(node);
            ExitLoop();
        }

        public override void VisitWhileStatement(WhileStatementSyntax node)
        {
            EnterLoop();
            base.VisitWhileStatement(node);
            ExitLoop();
        }

        public override void VisitDoStatement(DoStatementSyntax node)
        {
            EnterLoop();
            base.VisitDoStatement(node);
            ExitLoop();
        }

        private void EnterLoop()
        {
            Cognitive += 1 + _nest;
            _nest++;
            _loop++;
            if (_loop > MaxLoopDepth) MaxLoopDepth = _loop;
        }

        private void ExitLoop()
        {
            _nest--;
            _loop--;
        }

        public override void VisitSwitchStatement(SwitchStatementSyntax node)
        {
            Cognitive += 1 + _nest;
            _nest++;
            base.VisitSwitchStatement(node);
            _nest--;
        }

        public override void VisitCatchClause(CatchClauseSyntax node)
        {
            Cognitive += 1 + _nest;
            _nest++;
            base.VisitCatchClause(node);
            _nest--;
        }

        public override void VisitConditionalExpression(ConditionalExpressionSyntax node)
        {
            Cognitive += 1 + _nest;
            base.VisitConditionalExpression(node);
        }

        public override void VisitBinaryExpression(BinaryExpressionSyntax node)
        {
            if (node.Kind() is SyntaxKind.LogicalAndExpression or SyntaxKind.LogicalOrExpression)
            {
                Cognitive += 1;
            }
            base.VisitBinaryExpression(node);
        }

        public override void VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            if (_loop > 0 && !LinearScanInLoop)
            {
                string? name = null;
                var expr = node.Expression;
                if (expr is MemberAccessExpressionSyntax ma && ma.Name is IdentifierNameSyntax id)
                    name = id.Identifier.ValueText;
                else if (expr is IdentifierNameSyntax simple)
                    name = simple.Identifier.ValueText;
                else if (expr is MemberBindingExpressionSyntax mb && mb.Name is IdentifierNameSyntax bid)
                    name = bid.Identifier.ValueText;

                if (name != null && ScanNames.Contains(name))
                    LinearScanInLoop = true;
            }
            base.VisitInvocationExpression(node);
        }
    }
}
