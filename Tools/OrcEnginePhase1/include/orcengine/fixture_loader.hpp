// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Loads the flat text fixture format written by
// Tools/OrcEnginePhase0/oracle/export_cpp_phase1_fixture.py -- weights,
// token ids, and every EXPECT tap the Python oracle captured. Plain
// std::ifstream whitespace-token parsing, on purpose: the fixture format is
// fully self-controlled (both producer and consumer live in this repo), so
// a general JSON dependency buys nothing here (see
// docs/OrcEngine/PHASE1_IMPLEMENTATION.md, "Why no JSON library").
#pragma once

#include <string>
#include <unordered_map>
#include <vector>

#include "orcengine/forward.hpp"
#include "orcengine/model.hpp"

namespace orcengine {

struct LoadedFixture {
    Model model;
    std::vector<int64_t> token_ids;
    std::unordered_map<std::string, ActivationBuffer> expected;  // includes float and int taps (int taps use dims only, data as float)
};

LoadedFixture load_fixture(const std::string& path);

}  // namespace orcengine
