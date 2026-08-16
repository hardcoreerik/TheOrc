// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// ExecutionPlan: decides WHERE tensors live and how they get there --
// explicitly NOT OrcScheduler (TheOrc: should this workload run, on which
// role/node). See docs/OrcEngine/ARCHITECTURE.md, "ExecutionPlanner is not
// a second OrcScheduler".
//
// Phase 1's planner has exactly one strategy (ResidentCPU, everything
// already materialized by Model::Open) -- the type exists now, with a
// single trivial case, so Phase 6B's PagedCUDA/StaticHybrid strategies are
// additive, not a rewrite of every caller.
#pragma once

#include "orcengine/model.hpp"

namespace orcengine {

enum class ExecutionStrategy {
    ResidentCPU,  // Phase 1: the only strategy that exists.
};

class ExecutionPlan {
public:
    static ExecutionPlan Create(const Model& model) {
        (void)model;
        return ExecutionPlan{};
    }

    ExecutionStrategy strategy() const { return strategy_; }

private:
    ExecutionStrategy strategy_ = ExecutionStrategy::ResidentCPU;
};

}  // namespace orcengine
