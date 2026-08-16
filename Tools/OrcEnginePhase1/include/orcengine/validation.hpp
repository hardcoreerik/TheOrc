// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
#pragma once

#include <stdexcept>
#include <string>
#include <unordered_map>
#include <vector>

#include "orcengine/forward.hpp"
#include "orcengine/model.hpp"

namespace orcengine {

class ValidationError : public std::runtime_error {
public:
    explicit ValidationError(const std::string& message)
        : std::runtime_error("OrcEngine validation error: " + message) {}
};

struct ExpectedRequirement {
    std::string name;
    std::vector<int64_t> dims;
};

void validate_model_config(const ModelConfig& config);
void validate_model(const Model& model, const std::vector<int64_t>& token_ids);

std::vector<ExpectedRequirement> required_forward_expectations(
    const ModelConfig& config, int64_t sequence_length);

void validate_forward_expectations(
    const ModelConfig& config,
    int64_t sequence_length,
    const std::unordered_map<std::string, ActivationBuffer>& expected);

}  // namespace orcengine
