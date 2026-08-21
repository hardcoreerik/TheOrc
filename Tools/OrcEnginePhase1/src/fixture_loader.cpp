// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
#include "orcengine/fixture_loader.hpp"

#include <fstream>
#include <sstream>
#include <stdexcept>
#include <unordered_set>

#include "orcengine/validation.hpp"

namespace orcengine {

namespace {

struct Reader {
    std::ifstream in;
    explicit Reader(const std::string& path) : in(path) {
        if (!in) throw std::runtime_error("fixture_loader: cannot open " + path);
    }

    std::string next_token() {
        std::string tok;
        if (!(in >> tok)) throw std::runtime_error("fixture_loader: unexpected end of file");
        return tok;
    }

    int64_t next_int() { return std::stoll(next_token()); }
    float next_float() { return std::stof(next_token()); }

    // Reads "NAME ndims d0 d1 ..." header, then a line of ndims-product float values.
    void read_tensor_body(std::vector<int64_t>& dims, std::vector<float>& data) {
        int64_t ndims = next_int();
        if (ndims <= 0 || ndims > 8) {
            throw ValidationError("tensor rank must be in [1, 8]");
        }
        dims.resize(static_cast<size_t>(ndims));
        for (int64_t i = 0; i < ndims; ++i) {
            dims[static_cast<size_t>(i)] = next_int();
        }
        int64_t count = TensorShape(dims).element_count();
        data.resize(static_cast<size_t>(count));
        for (int64_t i = 0; i < count; ++i) data[static_cast<size_t>(i)] = next_float();
    }

    void read_tensor_body_int(std::vector<int64_t>& dims, std::vector<int64_t>& data) {
        int64_t ndims = next_int();
        if (ndims <= 0 || ndims > 8) {
            throw ValidationError("tensor rank must be in [1, 8]");
        }
        dims.resize(static_cast<size_t>(ndims));
        for (int64_t i = 0; i < ndims; ++i) {
            dims[static_cast<size_t>(i)] = next_int();
        }
        int64_t count = TensorShape(dims).element_count();
        data.resize(static_cast<size_t>(count));
        for (int64_t i = 0; i < count; ++i) data[static_cast<size_t>(i)] = next_int();
    }
};

ResidentView make_view(std::vector<int64_t> dims, std::vector<float> data) {
    return ResidentView(TensorShape(std::move(dims)), std::move(data));
}

}  // namespace

LoadedFixture load_fixture(const std::string& path) {
    Reader r(path);
    LoadedFixture out;
    ModelConfig cfg;

    std::string tag = r.next_token();
    if (tag != "CONFIG") throw std::runtime_error("fixture_loader: expected CONFIG, got " + tag);
    cfg.vocab = r.next_int();
    cfg.hidden = r.next_int();
    cfg.intermediate = r.next_int();
    cfg.n_layers = r.next_int();
    cfg.n_q_heads = r.next_int();
    cfg.n_kv_heads = r.next_int();
    cfg.head_dim = r.next_int();
    cfg.max_positions = r.next_int();
    cfg.rmsnorm_epsilon = r.next_float();
    cfg.rope_theta = r.next_float();
    validate_model_config(cfg);

    tag = r.next_token();
    if (tag != "TIED") throw std::runtime_error("fixture_loader: expected TIED");
    bool tied = r.next_int() != 0;

    tag = r.next_token();
    if (tag != "SEQ") throw std::runtime_error("fixture_loader: expected SEQ");
    int64_t seq = r.next_int();
    if (seq <= 0) throw ValidationError("input sequence must not be empty");
    if (seq > cfg.max_positions) {
        throw ValidationError("input sequence length exceeds max_positions");
    }

    tag = r.next_token();
    if (tag != "TOKENS") throw std::runtime_error("fixture_loader: expected TOKENS");
    out.token_ids.resize(static_cast<size_t>(seq));
    for (int64_t i = 0; i < seq; ++i) out.token_ids[static_cast<size_t>(i)] = r.next_int();

    out.model.manifest.config = cfg;
    out.model.manifest.tied_embeddings = tied;
    out.model.layers.resize(static_cast<size_t>(cfg.n_layers));

    int64_t current_layer = -1;
    std::unordered_set<std::string> loaded_tensors;
    while (r.in.peek() != EOF) {
        std::string marker;
        if (!(r.in >> marker)) break;

        if (marker == "LAYER") {
            current_layer = r.next_int();
            if (current_layer < 0 || current_layer >= cfg.n_layers) {
                throw ValidationError("layer index is outside configured layer count");
            }
            continue;
        }
        if (marker == "TENSOR") {
            std::string name = r.next_token();
            std::vector<int64_t> dims;
            std::vector<float> data;
            r.read_tensor_body(dims, data);
            const std::string tensor_key = current_layer < 0
                ? name
                : "layer" + std::to_string(current_layer) + "." + name;
            if (!loaded_tensors.insert(tensor_key).second) {
                throw ValidationError("duplicate tensor '" + tensor_key + "'");
            }
            out.model.manifest.tensors.emplace_back(tensor_key, TensorShape(dims));

            if (current_layer < 0) {
                if (name == "token_embedding") out.model.token_embedding = make_view(dims, data);
                else if (name == "lm_head") out.model.lm_head = make_view(dims, data);
                else if (name == "final_norm_weight") out.model.final_norm_weight = make_view(dims, data);
                else throw std::runtime_error("fixture_loader: unknown top-level tensor " + name);
            } else {
                LayerWeights& lw = out.model.layers[static_cast<size_t>(current_layer)];
                if (name == "attn_norm_weight") lw.attn_norm_weight = make_view(dims, data);
                else if (name == "w_q") lw.w_q = make_view(dims, data);
                else if (name == "w_k") lw.w_k = make_view(dims, data);
                else if (name == "w_v") lw.w_v = make_view(dims, data);
                else if (name == "w_o") lw.w_o = make_view(dims, data);
                else if (name == "ffn_norm_weight") lw.ffn_norm_weight = make_view(dims, data);
                else if (name == "w_gate") lw.w_gate = make_view(dims, data);
                else if (name == "w_up") lw.w_up = make_view(dims, data);
                else if (name == "w_down") lw.w_down = make_view(dims, data);
                else throw std::runtime_error("fixture_loader: unknown layer tensor " + name);
            }
            continue;
        }
        if (marker == "EXPECT") {
            std::string name = r.next_token();
            std::vector<int64_t> dims;
            std::vector<float> data;
            r.read_tensor_body(dims, data);
            if (!out.expected.emplace(name, ActivationBuffer{dims, data}).second) {
                throw ValidationError("duplicate expectation '" + name + "'");
            }
            continue;
        }
        if (marker == "EXPECT_INT") {
            std::string name = r.next_token();
            std::vector<int64_t> dims;
            std::vector<int64_t> idata;
            r.read_tensor_body_int(dims, idata);
            std::vector<float> data;
            data.reserve(idata.size());
            for (int64_t v : idata) data.push_back(static_cast<float>(v));
            if (!out.expected.emplace(name, ActivationBuffer{dims, data}).second) {
                throw ValidationError("duplicate expectation '" + name + "'");
            }
            continue;
        }
        throw std::runtime_error("fixture_loader: unknown marker '" + marker + "'");
    }

    if (out.model.manifest.tied_embeddings != !out.model.lm_head.has_value()) {
        throw std::runtime_error("fixture_loader: TIED flag disagrees with presence of lm_head tensor");
    }

    validate_model(out.model, out.token_ids);

    return out;
}

}  // namespace orcengine
