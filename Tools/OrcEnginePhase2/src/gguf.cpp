// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
#include "orcengine/gguf.hpp"

#include <algorithm>
#include <bit>
#include <chrono>
#include <cmath>
#include <fstream>
#include <limits>
#include <set>
#include <sstream>
#include <unordered_map>
#include <unordered_set>

#include "orcengine/validation.hpp"

namespace orcengine {
namespace {

uint64_t checked_add(uint64_t a, uint64_t b, const std::string& what) {
    if (a > std::numeric_limits<uint64_t>::max() - b) {
        throw GgufError(what + " overflows uint64");
    }
    return a + b;
}

uint64_t checked_mul(uint64_t a, uint64_t b, const std::string& what) {
    if (a != 0 && b > std::numeric_limits<uint64_t>::max() / a) {
        throw GgufError(what + " overflows uint64");
    }
    return a * b;
}

uint64_t align_up(uint64_t value, uint64_t alignment) {
    const uint64_t remainder = value % alignment;
    return remainder == 0 ? value : checked_add(value, alignment - remainder, "aligned offset");
}

bool valid_utf8(const std::string& value) {
    const auto* bytes = reinterpret_cast<const unsigned char*>(value.data());
    size_t i = 0;
    while (i < value.size()) {
        const unsigned char lead = bytes[i++];
        if (lead <= 0x7f) continue;
        size_t continuation = 0;
        uint32_t codepoint = 0;
        if ((lead & 0xe0) == 0xc0) {
            continuation = 1;
            codepoint = lead & 0x1f;
            if (codepoint < 2) return false;
        } else if ((lead & 0xf0) == 0xe0) {
            continuation = 2;
            codepoint = lead & 0x0f;
        } else if ((lead & 0xf8) == 0xf0) {
            continuation = 3;
            codepoint = lead & 0x07;
        } else {
            return false;
        }
        if (continuation > value.size() - i) return false;
        for (size_t n = 0; n < continuation; ++n) {
            const unsigned char next = bytes[i++];
            if ((next & 0xc0) != 0x80) return false;
            codepoint = (codepoint << 6) | (next & 0x3f);
        }
        if ((continuation == 2 && codepoint < 0x800) ||
            (continuation == 3 && codepoint < 0x10000) ||
            codepoint > 0x10ffff ||
            (codepoint >= 0xd800 && codepoint <= 0xdfff)) {
            return false;
        }
    }
    return true;
}

bool valid_metadata_key(const std::string& key) {
    if (key.empty() || key.front() == '.' || key.back() == '.') return false;
    bool previous_was_alphanumeric = false;
    for (char c : key) {
        if (c == '.') {
            if (!previous_was_alphanumeric) return false;
            previous_was_alphanumeric = false;
            continue;
        }
        const bool alphanumeric = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
        if (alphanumeric) {
            previous_was_alphanumeric = true;
        } else if (c == '_') {
            if (!previous_was_alphanumeric) return false;
            previous_was_alphanumeric = false;
        } else {
            return false;
        }
    }
    return previous_was_alphanumeric;
}

struct EncodingTraits {
    uint64_t block_elements;
    uint64_t block_bytes;
};

EncodingTraits encoding_traits(GgufTensorEncoding encoding) {
    switch (encoding) {
        case GgufTensorEncoding::F32: return {1, 4};
        case GgufTensorEncoding::F16: return {1, 2};
        case GgufTensorEncoding::Q4_0: return {32, 18};
        case GgufTensorEncoding::Q4_1: return {32, 20};
        case GgufTensorEncoding::Q5_0: return {32, 22};
        case GgufTensorEncoding::Q5_1: return {32, 24};
        case GgufTensorEncoding::Q8_0: return {32, 34};
        case GgufTensorEncoding::Q2_K: return {256, 84};
        case GgufTensorEncoding::Q3_K: return {256, 110};
        case GgufTensorEncoding::Q4_K: return {256, 144};
        case GgufTensorEncoding::Q5_K: return {256, 176};
        case GgufTensorEncoding::Q6_K: return {256, 210};
        case GgufTensorEncoding::Q8_K: return {256, 292};
    }
    throw GgufError("unsupported tensor encoding");
}

GgufTensorEncoding parse_encoding(uint32_t raw, const std::string& tensor_name) {
    switch (raw) {
        case 0: return GgufTensorEncoding::F32;
        case 1: return GgufTensorEncoding::F16;
        case 2: return GgufTensorEncoding::Q4_0;
        case 3: return GgufTensorEncoding::Q4_1;
        case 6: return GgufTensorEncoding::Q5_0;
        case 7: return GgufTensorEncoding::Q5_1;
        case 8: return GgufTensorEncoding::Q8_0;
        case 10: return GgufTensorEncoding::Q2_K;
        case 11: return GgufTensorEncoding::Q3_K;
        case 12: return GgufTensorEncoding::Q4_K;
        case 13: return GgufTensorEncoding::Q5_K;
        case 14: return GgufTensorEncoding::Q6_K;
        case 15: return GgufTensorEncoding::Q8_K;
        default:
            throw GgufError("unsupported tensor type " + std::to_string(raw) +
                            " for tensor '" + tensor_name + "'");
    }
}

class BinaryReader {
public:
    BinaryReader(const std::filesystem::path& path, uint64_t file_size,
                 const GgufLimits& limits)
        : stream_(path, std::ios::binary), file_size_(file_size), limits_(limits) {
        if (!stream_) throw GgufError("cannot open '" + path.string() + "'");
    }

    uint64_t position() const { return position_; }
    uint64_t remaining() const { return file_size_ - position_; }
    uint64_t allocation_bytes() const { return allocation_bytes_; }

    std::vector<uint8_t> bytes(size_t count, const std::string& what) {
        require_available(count, what);
        std::vector<uint8_t> out(count);
        if (count != 0) {
            stream_.read(reinterpret_cast<char*>(out.data()), static_cast<std::streamsize>(count));
            if (!stream_) throw GgufError("truncated " + what);
        }
        position_ += count;
        return out;
    }

    uint8_t u8(const std::string& what) { return bytes(1, what)[0]; }

    uint16_t u16(const std::string& what) {
        const auto b = bytes(2, what);
        return static_cast<uint16_t>(b[0]) |
               static_cast<uint16_t>(static_cast<uint16_t>(b[1]) << 8);
    }

    uint32_t u32(const std::string& what) {
        const auto b = bytes(4, what);
        return static_cast<uint32_t>(b[0]) |
               (static_cast<uint32_t>(b[1]) << 8) |
               (static_cast<uint32_t>(b[2]) << 16) |
               (static_cast<uint32_t>(b[3]) << 24);
    }

    uint64_t u64(const std::string& what) {
        const auto b = bytes(8, what);
        uint64_t value = 0;
        for (size_t i = 0; i < b.size(); ++i) value |= static_cast<uint64_t>(b[i]) << (8 * i);
        return value;
    }

    std::string string(uint64_t cap, const std::string& what) {
        const uint64_t length = u64(what + " length");
        if (length > cap) throw GgufError(what + " length exceeds configured cap");
        if (length > remaining()) throw GgufError(what + " extends past EOF");
        reserve_allocation(length, what);
        const auto raw = bytes(static_cast<size_t>(length), what);
        std::string value(raw.begin(), raw.end());
        if (!valid_utf8(value)) throw GgufError("invalid UTF-8 in " + what);
        return value;
    }

    GgufValue value(uint32_t raw_type, uint32_t depth) {
        if (raw_type > static_cast<uint32_t>(GgufValueType::Float64)) {
            throw GgufError("unsupported metadata value type " + std::to_string(raw_type));
        }
        const auto type = static_cast<GgufValueType>(raw_type);
        GgufValue result;
        result.type = type;
        switch (type) {
            case GgufValueType::UInt8: result.data = static_cast<uint64_t>(u8("uint8 metadata")); break;
            case GgufValueType::Int8:
                result.data = static_cast<int64_t>(std::bit_cast<int8_t>(u8("int8 metadata")));
                break;
            case GgufValueType::UInt16: result.data = static_cast<uint64_t>(u16("uint16 metadata")); break;
            case GgufValueType::Int16:
                result.data = static_cast<int64_t>(std::bit_cast<int16_t>(u16("int16 metadata")));
                break;
            case GgufValueType::UInt32: result.data = static_cast<uint64_t>(u32("uint32 metadata")); break;
            case GgufValueType::Int32:
                result.data = static_cast<int64_t>(std::bit_cast<int32_t>(u32("int32 metadata")));
                break;
            case GgufValueType::Float32: {
                const double value = static_cast<double>(std::bit_cast<float>(u32("float32 metadata")));
                if (!std::isfinite(value)) throw GgufError("non-finite float32 metadata value");
                result.data = value;
                break;
            }
            case GgufValueType::Bool: {
                const uint8_t value = u8("bool metadata");
                if (value > 1) throw GgufError("boolean metadata must be 0 or 1");
                result.data = value != 0;
                break;
            }
            case GgufValueType::String:
                result.data = string(limits_.max_string_bytes, "metadata string");
                break;
            case GgufValueType::Array: {
                if (depth >= limits_.max_array_depth) {
                    throw GgufError("metadata array nesting exceeds configured cap");
                }
                const uint32_t element_type = u32("metadata array element type");
                if (element_type > static_cast<uint32_t>(GgufValueType::Float64)) {
                    throw GgufError("unsupported metadata array element type " +
                                    std::to_string(element_type));
                }
                const uint64_t count = u64("metadata array length");
                if (count > limits_.max_array_elements) {
                    throw GgufError("metadata array length exceeds configured cap");
                }
                const uint64_t allocation = checked_mul(count, sizeof(GgufValue),
                                                        "metadata array allocation");
                reserve_allocation(allocation, "metadata array");
                std::vector<GgufValue> values;
                values.reserve(static_cast<size_t>(count));
                for (uint64_t i = 0; i < count; ++i) values.push_back(value(element_type, depth + 1));
                result.data = std::move(values);
                break;
            }
            case GgufValueType::UInt64: result.data = u64("uint64 metadata"); break;
            case GgufValueType::Int64:
                result.data = std::bit_cast<int64_t>(u64("int64 metadata"));
                break;
            case GgufValueType::Float64: {
                const double value = std::bit_cast<double>(u64("float64 metadata"));
                if (!std::isfinite(value)) throw GgufError("non-finite float64 metadata value");
                result.data = value;
                break;
            }
        }
        return result;
    }

private:
    void require_available(uint64_t count, const std::string& what) const {
        if (count > remaining()) throw GgufError("truncated " + what);
        if (count > static_cast<uint64_t>(std::numeric_limits<std::streamsize>::max())) {
            throw GgufError(what + " is too large for stream I/O");
        }
    }

    void reserve_allocation(uint64_t count, const std::string& what) {
        allocation_bytes_ = checked_add(allocation_bytes_, count,
                                        "cumulative metadata allocation");
        if (allocation_bytes_ > limits_.max_metadata_allocation) {
            throw GgufError(what + " exceeds cumulative metadata allocation cap");
        }
    }

    std::ifstream stream_;
    uint64_t file_size_ = 0;
    uint64_t position_ = 0;
    uint64_t allocation_bytes_ = 0;
    const GgufLimits& limits_;
};

int64_t checked_i64(uint64_t value, const std::string& what) {
    if (value > static_cast<uint64_t>(std::numeric_limits<int64_t>::max())) {
        throw GgufError(what + " exceeds int64 range");
    }
    return static_cast<int64_t>(value);
}

BackingEncoding backing_encoding(GgufTensorEncoding encoding) {
    switch (encoding) {
        case GgufTensorEncoding::F32: return BackingEncoding::F32Raw;
        case GgufTensorEncoding::F16: return BackingEncoding::F16Raw;
        case GgufTensorEncoding::Q4_0: return BackingEncoding::GgufQ4_0;
        case GgufTensorEncoding::Q4_1: return BackingEncoding::GgufQ4_1;
        case GgufTensorEncoding::Q5_0: return BackingEncoding::GgufQ5_0;
        case GgufTensorEncoding::Q5_1: return BackingEncoding::GgufQ5_1;
        case GgufTensorEncoding::Q8_0: return BackingEncoding::GgufQ8_0;
        case GgufTensorEncoding::Q2_K: return BackingEncoding::GgufQ2_K;
        case GgufTensorEncoding::Q3_K: return BackingEncoding::GgufQ3_K;
        case GgufTensorEncoding::Q4_K: return BackingEncoding::GgufQ4_K;
        case GgufTensorEncoding::Q5_K: return BackingEncoding::GgufQ5_K;
        case GgufTensorEncoding::Q6_K: return BackingEncoding::GgufQ6_K;
        case GgufTensorEncoding::Q8_K: return BackingEncoding::GgufQ8_K;
    }
    throw GgufError("indexed encoding has no Phase-2 BackingExtent representation");
}

const GgufTensorInfo& require_tensor(const std::unordered_map<std::string, const GgufTensorInfo*>& tensors,
                                     const std::string& name) {
    const auto it = tensors.find(name);
    if (it == tensors.end()) throw GgufError("required Llama tensor '" + name + "' is missing");
    return *it->second;
}

float half_to_float(uint16_t half) {
    const uint32_t sign = static_cast<uint32_t>(half & 0x8000U) << 16;
    uint32_t exponent = (half >> 10) & 0x1fU;
    uint32_t fraction = half & 0x03ffU;
    uint32_t bits = 0;
    if (exponent == 0) {
        if (fraction == 0) {
            bits = sign;
        } else {
            exponent = 127 - 15 + 1;
            while ((fraction & 0x0400U) == 0) {
                fraction <<= 1;
                --exponent;
            }
            fraction &= 0x03ffU;
            bits = sign | (exponent << 23) | (fraction << 13);
        }
    } else if (exponent == 0x1fU) {
        bits = sign | 0x7f800000U | (fraction << 13);
    } else {
        bits = sign | ((exponent + (127 - 15)) << 23) | (fraction << 13);
    }
    return std::bit_cast<float>(bits);
}

}  // namespace

GgufArtifact index_gguf(const std::filesystem::path& path, const GgufLimits& limits) {
    const auto started = std::chrono::steady_clock::now();
    std::error_code error;
    const uint64_t file_size = std::filesystem::file_size(path, error);
    if (error) throw GgufError("cannot stat '" + path.string() + "': " + error.message());
    if (file_size < 24) throw GgufError("file is too short for a GGUF header");
    if (file_size > limits.max_file_bytes) throw GgufError("file size exceeds configured cap");

    BinaryReader reader(path, file_size, limits);
    const auto magic = reader.bytes(4, "GGUF magic");
    if (magic != std::vector<uint8_t>{'G', 'G', 'U', 'F'}) throw GgufError("magic mismatch");
    const uint32_t version = reader.u32("GGUF version");
    if (version != 3) throw GgufError("unsupported GGUF version " + std::to_string(version));
    const uint64_t tensor_count = reader.u64("tensor count");
    const uint64_t metadata_count = reader.u64("metadata count");
    if (tensor_count > limits.max_tensors) throw GgufError("tensor count exceeds configured cap");
    if (metadata_count > limits.max_metadata_entries) {
        throw GgufError("metadata count exceeds configured cap");
    }

    GgufArtifact artifact;
    artifact.path = std::filesystem::absolute(path);
    artifact.version = version;
    artifact.telemetry.file_size = file_size;

    for (uint64_t i = 0; i < metadata_count; ++i) {
        const std::string key = reader.string(limits.max_key_bytes, "metadata key");
        if (!valid_metadata_key(key)) throw GgufError("invalid metadata key '" + key + "'");
        const uint32_t type = reader.u32("metadata type for '" + key + "'");
        GgufValue value = reader.value(type, 0);
        if (!artifact.metadata.emplace(key, std::move(value)).second) {
            throw GgufError("duplicate metadata key '" + key + "'");
        }
    }
    artifact.telemetry.metadata_bytes_read = reader.position() - 24;
    artifact.telemetry.estimated_metadata_bytes = reader.allocation_bytes();
    const auto metadata_finished = std::chrono::steady_clock::now();
    artifact.telemetry.metadata_parse_milliseconds =
        std::chrono::duration<double, std::milli>(metadata_finished - started).count();

    const auto alignment_it = artifact.metadata.find("general.alignment");
    if (alignment_it != artifact.metadata.end()) {
        if (alignment_it->second.type != GgufValueType::UInt32) {
            throw GgufError("general.alignment must be uint32");
        }
        artifact.alignment = static_cast<uint32_t>(std::get<uint64_t>(alignment_it->second.data));
    }
    if (artifact.alignment == 0 || artifact.alignment % 8 != 0 || artifact.alignment > 1'048'576) {
        throw GgufError("general.alignment must be a nonzero multiple of 8 no larger than 1048576");
    }

    artifact.tensors.reserve(static_cast<size_t>(tensor_count));
    std::unordered_set<std::string> names;
    for (uint64_t i = 0; i < tensor_count; ++i) {
        GgufTensorInfo tensor;
        tensor.name = reader.string(64, "tensor name");
        if (tensor.name.empty()) throw GgufError("tensor name must not be empty");
        if (!names.insert(tensor.name).second) throw GgufError("duplicate tensor name '" + tensor.name + "'");
        const uint32_t rank = reader.u32("tensor rank for '" + tensor.name + "'");
        if (rank == 0 || rank > limits.max_tensor_rank) {
            throw GgufError("tensor '" + tensor.name + "' has unsupported rank " + std::to_string(rank));
        }
        uint64_t elements = 1;
        for (uint32_t d = 0; d < rank; ++d) {
            const uint64_t dimension = reader.u64("tensor dimension for '" + tensor.name + "'");
            if (dimension == 0 || dimension > limits.max_dimension) {
                throw GgufError("tensor '" + tensor.name + "' has impossible dimension");
            }
            elements = checked_mul(elements, dimension, "tensor element count for '" + tensor.name + "'");
            tensor.gguf_dimensions.push_back(dimension);
        }
        tensor.encoding = parse_encoding(reader.u32("tensor type for '" + tensor.name + "'"), tensor.name);
        tensor.relative_offset = reader.u64("tensor offset for '" + tensor.name + "'");
        if (tensor.relative_offset % artifact.alignment != 0) {
            throw GgufError("tensor '" + tensor.name + "' offset is not aligned");
        }
        const EncodingTraits traits = encoding_traits(tensor.encoding);
        if (tensor.gguf_dimensions.front() % traits.block_elements != 0 ||
            elements % traits.block_elements != 0) {
            throw GgufError("tensor '" + tensor.name + "' dimensions are not divisible by encoding block size");
        }
        tensor.encoded_length = checked_mul(elements / traits.block_elements, traits.block_bytes,
                                            "encoded byte length for '" + tensor.name + "'");
        std::vector<int64_t> logical_dims;
        logical_dims.reserve(rank);
        for (auto it = tensor.gguf_dimensions.rbegin(); it != tensor.gguf_dimensions.rend(); ++it) {
            logical_dims.push_back(checked_i64(*it, "tensor dimension"));
        }
        tensor.logical_shape = TensorShape(std::move(logical_dims));
        artifact.tensors.push_back(std::move(tensor));
    }

    const bool has_quantized_tensors = std::any_of(
        artifact.tensors.begin(), artifact.tensors.end(), [](const GgufTensorInfo& tensor) {
            return tensor.encoding != GgufTensorEncoding::F32 && tensor.encoding != GgufTensorEncoding::F16;
        });
    if (has_quantized_tensors) {
        const auto quantization = artifact.metadata.find("general.quantization_version");
        if (quantization == artifact.metadata.end() || quantization->second.type != GgufValueType::UInt32) {
            throw GgufError("quantized GGUF requires uint32 general.quantization_version");
        }
    }

    uint64_t tensor_index_bytes = checked_mul(
        artifact.tensors.capacity(), sizeof(GgufTensorInfo), "tensor index allocation");
    tensor_index_bytes = checked_add(
        tensor_index_bytes,
        reader.allocation_bytes() - artifact.telemetry.estimated_metadata_bytes,
        "tensor index allocation");
    for (const auto& tensor : artifact.tensors) {
        tensor_index_bytes = checked_add(
            tensor_index_bytes,
            checked_mul(tensor.gguf_dimensions.capacity(), sizeof(uint64_t), "GGUF dimension allocation"),
            "tensor index allocation");
        tensor_index_bytes = checked_add(
            tensor_index_bytes,
            checked_mul(tensor.logical_shape.dims().capacity(), sizeof(int64_t), "logical dimension allocation"),
            "tensor index allocation");
    }
    artifact.telemetry.estimated_tensor_index_bytes = tensor_index_bytes;
    artifact.telemetry.tensor_index_milliseconds =
        std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - metadata_finished).count();

    artifact.tensor_data_offset = align_up(reader.position(), artifact.alignment);
    if (artifact.tensor_data_offset > file_size) throw GgufError("tensor data origin lies past EOF");

    std::vector<std::pair<uint64_t, uint64_t>> extents;
    extents.reserve(artifact.tensors.size());
    for (GgufTensorInfo& tensor : artifact.tensors) {
        tensor.absolute_offset = checked_add(artifact.tensor_data_offset, tensor.relative_offset,
                                             "absolute offset for tensor '" + tensor.name + "'");
        const uint64_t end = checked_add(tensor.absolute_offset, tensor.encoded_length,
                                         "end offset for tensor '" + tensor.name + "'");
        if (end > file_size) {
            throw GgufError("tensor '" + tensor.name + "' ends at byte " + std::to_string(end) +
                            " but file size is " + std::to_string(file_size));
        }
        extents.emplace_back(tensor.absolute_offset, end);
    }
    std::sort(extents.begin(), extents.end());
    for (size_t i = 1; i < extents.size(); ++i) {
        if (extents[i].first < extents[i - 1].second) throw GgufError("tensor data extents overlap");
    }

    (void)metadata_string(artifact, "general.architecture");
    artifact.telemetry.estimated_manifest_bytes = checked_add(
        artifact.telemetry.estimated_metadata_bytes,
        artifact.telemetry.estimated_tensor_index_bytes,
        "manifest allocation estimate");
    artifact.telemetry.parse_milliseconds =
        std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - started).count();
    return artifact;
}

const GgufValue& require_metadata(const GgufArtifact& artifact, const std::string& key) {
    const auto it = artifact.metadata.find(key);
    if (it == artifact.metadata.end()) throw GgufError("required metadata key '" + key + "' is missing");
    return it->second;
}

uint64_t metadata_u64(const GgufArtifact& artifact, const std::string& key) {
    const GgufValue& value = require_metadata(artifact, key);
    switch (value.type) {
        case GgufValueType::UInt8:
        case GgufValueType::UInt16:
        case GgufValueType::UInt32:
        case GgufValueType::UInt64:
            return std::get<uint64_t>(value.data);
        default:
            throw GgufError("metadata key '" + key + "' must be unsigned integer");
    }
}

double metadata_f64(const GgufArtifact& artifact, const std::string& key) {
    const GgufValue& value = require_metadata(artifact, key);
    if (value.type != GgufValueType::Float32 && value.type != GgufValueType::Float64) {
        throw GgufError("metadata key '" + key + "' must be floating point");
    }
    return std::get<double>(value.data);
}

std::string metadata_string(const GgufArtifact& artifact, const std::string& key) {
    const GgufValue& value = require_metadata(artifact, key);
    if (value.type != GgufValueType::String) {
        throw GgufError("metadata key '" + key + "' must be string");
    }
    return std::get<std::string>(value.data);
}

std::string gguf_encoding_name(GgufTensorEncoding encoding) {
    switch (encoding) {
        case GgufTensorEncoding::F32: return "F32";
        case GgufTensorEncoding::F16: return "F16";
        case GgufTensorEncoding::Q4_0: return "Q4_0";
        case GgufTensorEncoding::Q4_1: return "Q4_1";
        case GgufTensorEncoding::Q5_0: return "Q5_0";
        case GgufTensorEncoding::Q5_1: return "Q5_1";
        case GgufTensorEncoding::Q8_0: return "Q8_0";
        case GgufTensorEncoding::Q2_K: return "Q2_K";
        case GgufTensorEncoding::Q3_K: return "Q3_K";
        case GgufTensorEncoding::Q4_K: return "Q4_K";
        case GgufTensorEncoding::Q5_K: return "Q5_K";
        case GgufTensorEncoding::Q6_K: return "Q6_K";
        case GgufTensorEncoding::Q8_K: return "Q8_K";
    }
    return "UNKNOWN";
}

bool gguf_encoding_materializable(GgufTensorEncoding encoding) {
    return encoding == GgufTensorEncoding::F32 || encoding == GgufTensorEncoding::F16;
}

std::string semantic_tensor_name(const SemanticTensorId& semantic) {
    if (semantic.layer < 0) {
        switch (semantic.role) {
            case SemanticTensorRole::TokenEmbedding: return "token_embedding";
            case SemanticTensorRole::FinalNorm: return "final_norm_weight";
            case SemanticTensorRole::OutputHead: return "lm_head";
            default: throw GgufError("layer tensor role is missing a layer index");
        }
    }
    const std::string prefix = "layer" + std::to_string(semantic.layer) + ".";
    switch (semantic.role) {
        case SemanticTensorRole::AttentionNorm: return prefix + "attn_norm_weight";
        case SemanticTensorRole::AttentionQuery: return prefix + "w_q";
        case SemanticTensorRole::AttentionKey: return prefix + "w_k";
        case SemanticTensorRole::AttentionValue: return prefix + "w_v";
        case SemanticTensorRole::AttentionOutput: return prefix + "w_o";
        case SemanticTensorRole::FfnNorm: return prefix + "ffn_norm_weight";
        case SemanticTensorRole::FfnGate: return prefix + "w_gate";
        case SemanticTensorRole::FfnUp: return prefix + "w_up";
        case SemanticTensorRole::FfnDown: return prefix + "w_down";
        default: throw GgufError("global tensor role has a layer index");
    }
}

ModelArtifactManifest map_llama_model(const GgufArtifact& artifact) {
    ModelArtifactManifest manifest;
    manifest.architecture = metadata_string(artifact, "general.architecture");
    if (manifest.architecture != "llama") {
        manifest.unsupported_reasons.push_back("only general.architecture=llama is supported");
        return manifest;
    }

    const std::string prefix = "llama.";
    manifest.config.hidden = checked_i64(metadata_u64(artifact, prefix + "embedding_length"), "hidden size");
    manifest.config.intermediate = checked_i64(metadata_u64(artifact, prefix + "feed_forward_length"), "feed-forward size");
    manifest.config.n_layers = checked_i64(metadata_u64(artifact, prefix + "block_count"), "layer count");
    manifest.config.n_q_heads = checked_i64(metadata_u64(artifact, prefix + "attention.head_count"), "attention head count");
    const auto kv_it = artifact.metadata.find(prefix + "attention.head_count_kv");
    manifest.config.n_kv_heads = kv_it == artifact.metadata.end()
        ? manifest.config.n_q_heads
        : checked_i64(metadata_u64(artifact, prefix + "attention.head_count_kv"), "KV head count");
    manifest.config.max_positions = checked_i64(metadata_u64(artifact, prefix + "context_length"), "context length");
    manifest.config.rmsnorm_epsilon = static_cast<float>(
        metadata_f64(artifact, prefix + "attention.layer_norm_rms_epsilon"));
    const auto rope_base = artifact.metadata.find(prefix + "rope.freq_base");
    manifest.config.rope_theta = rope_base == artifact.metadata.end()
        ? 10000.0f : static_cast<float>(metadata_f64(artifact, prefix + "rope.freq_base"));
    if (manifest.config.n_q_heads <= 0) throw GgufError("Llama attention head count must be > 0");
    manifest.config.head_dim = manifest.config.hidden / manifest.config.n_q_heads;

    const auto layout = artifact.metadata.find(prefix + "tensor_data_layout");
    if (layout != artifact.metadata.end() && metadata_string(artifact, prefix + "tensor_data_layout") != "reference") {
        throw GgufError("unsupported Llama tensor_data_layout");
    }
    const auto experts = artifact.metadata.find(prefix + "expert_count");
    if (experts != artifact.metadata.end() && metadata_u64(artifact, prefix + "expert_count") != 0) {
        throw GgufError("Llama MoE tensors are outside the Phase-2 profile");
    }
    const auto rope_dimension = artifact.metadata.find(prefix + "rope.dimension_count");
    if (rope_dimension != artifact.metadata.end() &&
        checked_i64(metadata_u64(artifact, prefix + "rope.dimension_count"), "RoPE dimension") !=
            manifest.config.head_dim) {
        throw GgufError("partial rotary dimensions are outside the Phase-2 Llama profile");
    }

    std::unordered_map<std::string, const GgufTensorInfo*> tensors;
    for (const auto& tensor : artifact.tensors) tensors.emplace(tensor.name, &tensor);
    const auto& embedding = require_tensor(tensors, "token_embd.weight");
    if (embedding.logical_shape.ndim() != 2 || embedding.logical_shape.dim(1) != manifest.config.hidden) {
        throw GgufError("token_embd.weight dimensions do not match Llama embedding metadata");
    }
    manifest.config.vocab = embedding.logical_shape.dim(0);
    try {
        validate_model_config(manifest.config);
    } catch (const ValidationError& ex) {
        throw GgufError(std::string("invalid Llama model configuration: ") + ex.what());
    }

    std::unordered_set<std::string> used;
    auto add = [&](const std::string& source_name, SemanticTensorId semantic,
                   const std::vector<int64_t>& expected) {
        const auto& source = require_tensor(tensors, source_name);
        if (source.logical_shape.dims() != expected) {
            throw GgufError("tensor '" + source_name + "' has logical shape " +
                            source.logical_shape.to_string() + ", expected " +
                            TensorShape(expected).to_string());
        }
        if (!used.insert(source_name).second) {
            throw GgufError("tensor '" + source_name + "' has duplicate semantic assignment");
        }
        manifest.mapped_tensors.push_back({
            semantic,
            source_name,
            LogicalTensor(semantic_tensor_name(semantic), source.logical_shape),
            BackingExtent(artifact.path.string(), checked_i64(source.absolute_offset, "backing offset"),
                          checked_i64(source.encoded_length, "backing length"),
                          backing_encoding(source.encoding)),
            source.encoding,
        });
    };

    const int64_t q_dim = manifest.config.n_q_heads * manifest.config.head_dim;
    const int64_t kv_dim = manifest.config.n_kv_heads * manifest.config.head_dim;
    add("token_embd.weight", {SemanticTensorRole::TokenEmbedding, -1},
        {manifest.config.vocab, manifest.config.hidden});
    add("output_norm.weight", {SemanticTensorRole::FinalNorm, -1}, {manifest.config.hidden});
    const bool output_present = tensors.contains("output.weight");
    manifest.tied_embeddings = !output_present;
    if (output_present) {
        add("output.weight", {SemanticTensorRole::OutputHead, -1},
            {manifest.config.vocab, manifest.config.hidden});
    }
    for (int64_t layer = 0; layer < manifest.config.n_layers; ++layer) {
        const std::string p = "blk." + std::to_string(layer) + ".";
        add(p + "attn_norm.weight", {SemanticTensorRole::AttentionNorm, layer}, {manifest.config.hidden});
        add(p + "attn_q.weight", {SemanticTensorRole::AttentionQuery, layer}, {q_dim, manifest.config.hidden});
        add(p + "attn_k.weight", {SemanticTensorRole::AttentionKey, layer}, {kv_dim, manifest.config.hidden});
        add(p + "attn_v.weight", {SemanticTensorRole::AttentionValue, layer}, {kv_dim, manifest.config.hidden});
        add(p + "attn_output.weight", {SemanticTensorRole::AttentionOutput, layer}, {manifest.config.hidden, q_dim});
        add(p + "ffn_norm.weight", {SemanticTensorRole::FfnNorm, layer}, {manifest.config.hidden});
        add(p + "ffn_gate.weight", {SemanticTensorRole::FfnGate, layer}, {manifest.config.intermediate, manifest.config.hidden});
        add(p + "ffn_up.weight", {SemanticTensorRole::FfnUp, layer}, {manifest.config.intermediate, manifest.config.hidden});
        add(p + "ffn_down.weight", {SemanticTensorRole::FfnDown, layer}, {manifest.config.hidden, manifest.config.intermediate});
    }

    std::set<std::string> unsupported_encodings;
    for (const auto& tensor : manifest.mapped_tensors) {
        if (!gguf_encoding_materializable(tensor.encoding)) {
            unsupported_encodings.insert(gguf_encoding_name(tensor.encoding));
        }
    }
    for (const auto& encoding : unsupported_encodings) {
        manifest.unsupported_reasons.push_back(
            "indexed successfully; CPU materialization unsupported for " + encoding);
    }
    for (const auto& tensor : artifact.tensors) {
        if (!used.contains(tensor.name)) manifest.unused_tensor_names.push_back(tensor.name);
    }
    manifest.kind = ModelArtifactKind::FullModel;
    manifest.materializable = unsupported_encodings.empty();
    return manifest;
}

ResidentView materialize_gguf_tensor(const MappedGgufTensor& tensor) {
    if (!gguf_encoding_materializable(tensor.encoding)) {
        throw GgufError("unsupported GGUF tensor encoding " + gguf_encoding_name(tensor.encoding) +
                        " for tensor '" + tensor.source_name +
                        "'; indexing succeeded, Phase-2 CPU materialization supports F32/F16 only");
    }
    if (tensor.backing.byte_offset() < 0 || tensor.backing.byte_length() <= 0) {
        throw GgufError("invalid backing extent for tensor '" + tensor.source_name + "'");
    }
    const int64_t elements = tensor.logical.shape().element_count();
    const int64_t bytes_per_element = tensor.encoding == GgufTensorEncoding::F32 ? 4 : 2;
    if (elements > std::numeric_limits<int64_t>::max() / bytes_per_element ||
        tensor.backing.byte_length() != elements * bytes_per_element) {
        throw GgufError("backing length does not match logical tensor '" + tensor.logical.name() + "'");
    }

    std::ifstream stream(tensor.backing.source_path(), std::ios::binary);
    if (!stream) throw GgufError("cannot reopen backing file '" + tensor.backing.source_path() + "'");
    stream.seekg(tensor.backing.byte_offset());
    if (!stream) throw GgufError("cannot seek to tensor '" + tensor.source_name + "'");
    std::vector<uint8_t> bytes(static_cast<size_t>(tensor.backing.byte_length()));
    stream.read(reinterpret_cast<char*>(bytes.data()), static_cast<std::streamsize>(bytes.size()));
    if (!stream) throw GgufError("short read materializing tensor '" + tensor.source_name + "'");

    std::vector<float> values(static_cast<size_t>(elements));
    if (tensor.encoding == GgufTensorEncoding::F32) {
        for (size_t i = 0; i < values.size(); ++i) {
            const size_t p = i * 4;
            const uint32_t bits = static_cast<uint32_t>(bytes[p]) |
                                  (static_cast<uint32_t>(bytes[p + 1]) << 8) |
                                  (static_cast<uint32_t>(bytes[p + 2]) << 16) |
                                  (static_cast<uint32_t>(bytes[p + 3]) << 24);
            values[i] = std::bit_cast<float>(bits);
        }
    } else {
        for (size_t i = 0; i < values.size(); ++i) {
            const size_t p = i * 2;
            values[i] = half_to_float(static_cast<uint16_t>(bytes[p]) |
                                      static_cast<uint16_t>(static_cast<uint16_t>(bytes[p + 1]) << 8));
        }
    }
    return ResidentView(tensor.logical.shape(), std::move(values));
}

Model materialize_gguf_model(const ModelArtifactManifest& manifest) {
    if (manifest.kind != ModelArtifactKind::FullModel) throw GgufError("artifact is not a complete Llama model");
    if (!manifest.materializable) throw GgufError("model contains indexed but non-materializable encodings");

    Model model;
    model.manifest.config = manifest.config;
    model.manifest.tied_embeddings = manifest.tied_embeddings;
    model.layers.resize(static_cast<size_t>(manifest.config.n_layers));
    for (const auto& tensor : manifest.mapped_tensors) {
        ResidentView view = materialize_gguf_tensor(tensor);
        model.manifest.tensors.push_back(tensor.logical);
        const int64_t layer = tensor.semantic.layer;
        switch (tensor.semantic.role) {
            case SemanticTensorRole::TokenEmbedding: model.token_embedding = std::move(view); break;
            case SemanticTensorRole::FinalNorm: model.final_norm_weight = std::move(view); break;
            case SemanticTensorRole::OutputHead: model.lm_head = std::move(view); break;
            case SemanticTensorRole::AttentionNorm: model.layers.at(static_cast<size_t>(layer)).attn_norm_weight = std::move(view); break;
            case SemanticTensorRole::AttentionQuery: model.layers.at(static_cast<size_t>(layer)).w_q = std::move(view); break;
            case SemanticTensorRole::AttentionKey: model.layers.at(static_cast<size_t>(layer)).w_k = std::move(view); break;
            case SemanticTensorRole::AttentionValue: model.layers.at(static_cast<size_t>(layer)).w_v = std::move(view); break;
            case SemanticTensorRole::AttentionOutput: model.layers.at(static_cast<size_t>(layer)).w_o = std::move(view); break;
            case SemanticTensorRole::FfnNorm: model.layers.at(static_cast<size_t>(layer)).ffn_norm_weight = std::move(view); break;
            case SemanticTensorRole::FfnGate: model.layers.at(static_cast<size_t>(layer)).w_gate = std::move(view); break;
            case SemanticTensorRole::FfnUp: model.layers.at(static_cast<size_t>(layer)).w_up = std::move(view); break;
            case SemanticTensorRole::FfnDown: model.layers.at(static_cast<size_t>(layer)).w_down = std::move(view); break;
        }
    }
    validate_model(model, {0});
    return model;
}

}  // namespace orcengine
