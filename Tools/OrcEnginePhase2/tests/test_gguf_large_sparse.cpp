// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
#include <cstdint>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <limits>
#include <stdexcept>
#include <string>
#include <vector>

#ifdef _WIN32
#define NOMINMAX
#include <windows.h>
#include <winioctl.h>
#else
#include <unistd.h>
#endif

#include "orcengine/gguf.hpp"

using namespace orcengine;

namespace {

constexpr uint64_t kFourGiB = 4ULL * 1024 * 1024 * 1024;

void require(bool condition, const std::string& message) {
    if (!condition) throw std::runtime_error(message);
}

void append_u32(std::vector<uint8_t>& out, uint32_t value) {
    for (unsigned shift = 0; shift < 32; shift += 8) out.push_back(static_cast<uint8_t>(value >> shift));
}

void append_u64(std::vector<uint8_t>& out, uint64_t value) {
    for (unsigned shift = 0; shift < 64; shift += 8) out.push_back(static_cast<uint8_t>(value >> shift));
}

void append_string(std::vector<uint8_t>& out, const std::string& value) {
    append_u64(out, value.size());
    out.insert(out.end(), value.begin(), value.end());
}

std::vector<uint8_t> gguf_prefix(uint64_t relative_offset) {
    std::vector<uint8_t> out{'G', 'G', 'U', 'F'};
    append_u32(out, 3);
    append_u64(out, 1);  // tensor count
    append_u64(out, 1);  // metadata count
    append_string(out, "general.architecture");
    append_u32(out, static_cast<uint32_t>(GgufValueType::String));
    append_string(out, "llama");
    append_string(out, "boundary.weight");
    append_u32(out, 1);
    append_u64(out, 1);
    append_u32(out, static_cast<uint32_t>(GgufTensorEncoding::F32));
    append_u64(out, relative_offset);
    out.resize((out.size() + 31) / 32 * 32, 0);
    return out;
}

class TemporaryFile {
public:
    explicit TemporaryFile(std::string suffix) {
#ifdef _WIN32
        const auto process = static_cast<unsigned long>(GetCurrentProcessId());
#else
        const auto process = static_cast<unsigned long>(getpid());
#endif
        path = std::filesystem::temp_directory_path() /
            ("orcengine_phase2_" + std::to_string(process) + "_" + std::move(suffix));
    }
    ~TemporaryFile() {
        std::error_code ignored;
        std::filesystem::remove(path, ignored);
    }
    std::filesystem::path path;
};

void write_regular(const std::filesystem::path& path, const std::vector<uint8_t>& bytes) {
    std::ofstream out(path, std::ios::binary | std::ios::trunc);
    if (!out) throw std::runtime_error("cannot create boundary fixture");
    out.write(reinterpret_cast<const char*>(bytes.data()), static_cast<std::streamsize>(bytes.size()));
    if (!out) throw std::runtime_error("cannot write boundary fixture");
}

void write_sparse(const std::filesystem::path& path, const std::vector<uint8_t>& prefix,
                  uint64_t payload_offset) {
    const uint32_t payload = 0x3f800000U;
#ifdef _WIN32
    HANDLE file = CreateFileW(path.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr,
                              CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) throw std::runtime_error("cannot create sparse GGUF fixture");
    DWORD returned = 0;
    if (!DeviceIoControl(file, FSCTL_SET_SPARSE, nullptr, 0, nullptr, 0, &returned, nullptr)) {
        CloseHandle(file);
        throw std::runtime_error("filesystem does not support sparse test files");
    }
    DWORD written = 0;
    if (!WriteFile(file, prefix.data(), static_cast<DWORD>(prefix.size()), &written, nullptr) ||
        written != prefix.size()) {
        CloseHandle(file);
        throw std::runtime_error("cannot write sparse GGUF header");
    }
    LARGE_INTEGER position{};
    position.QuadPart = static_cast<LONGLONG>(payload_offset);
    if (!SetFilePointerEx(file, position, nullptr, FILE_BEGIN) ||
        !WriteFile(file, &payload, sizeof(payload), &written, nullptr) || written != sizeof(payload)) {
        CloseHandle(file);
        throw std::runtime_error("cannot write sparse GGUF payload");
    }
    CloseHandle(file);
#else
    std::ofstream out(path, std::ios::binary | std::ios::trunc);
    if (!out) throw std::runtime_error("cannot create sparse GGUF fixture");
    out.write(reinterpret_cast<const char*>(prefix.data()), static_cast<std::streamsize>(prefix.size()));
    out.seekp(static_cast<std::streamoff>(payload_offset));
    out.write(reinterpret_cast<const char*>(&payload), sizeof(payload));
    if (!out) throw std::runtime_error("cannot write sparse GGUF fixture");
#endif
}

uint64_t allocated_size(const std::filesystem::path& path) {
#ifdef _WIN32
    DWORD high = 0;
    SetLastError(NO_ERROR);
    const DWORD low = GetCompressedFileSizeW(path.c_str(), &high);
    if (low == INVALID_FILE_SIZE && GetLastError() != NO_ERROR) {
        throw std::runtime_error("cannot query sparse allocation size");
    }
    return (static_cast<uint64_t>(high) << 32) | low;
#else
    return std::filesystem::file_size(path);  // Logical size only; sparse behavior is asserted on Windows CI.
#endif
}

template <typename Function>
void expect_gguf_error(Function&& function, const std::string& expected) {
    try {
        function();
    } catch (const GgufError& error) {
        require(std::string(error.what()).find(expected) != std::string::npos,
                "unexpected GGUF error: " + std::string(error.what()));
        return;
    }
    throw std::runtime_error("malformed boundary fixture was accepted");
}

}  // namespace

int main() {
    try {
        TemporaryFile sparse("large_sparse.gguf");
        const uint64_t relative_offset = kFourGiB;
        const std::vector<uint8_t> prefix = gguf_prefix(relative_offset);
        const uint64_t absolute_offset = static_cast<uint64_t>(prefix.size()) + relative_offset;
        require(absolute_offset > std::numeric_limits<uint32_t>::max(),
                "test did not cross the uint32 boundary");
        write_sparse(sparse.path, prefix, absolute_offset);

        expect_gguf_error([&] { (void)index_gguf(sparse.path); }, "file size exceeds configured cap");

        GgufLimits limits;
        limits.max_file_bytes = 8ULL * 1024 * 1024 * 1024;
        const GgufArtifact artifact = index_gguf(sparse.path, limits);
        require(artifact.tensors.size() == 1, "sparse tensor index count mismatch");
        require(artifact.tensors[0].absolute_offset == absolute_offset,
                "64-bit tensor offset was truncated");
        require(static_cast<uint64_t>(static_cast<int64_t>(artifact.tensors[0].absolute_offset)) ==
                    absolute_offset,
                "BackingExtent-compatible int64 conversion changed the offset");
        require(artifact.tensors[0].encoded_length == 4, "sparse tensor length mismatch");
        require(artifact.telemetry.file_size == absolute_offset + 4, "sparse logical size mismatch");
        require(artifact.telemetry.estimated_manifest_bytes < 1024 * 1024,
                "indexing sparse GGUF allocated model-sized memory");
#ifdef _WIN32
        require(allocated_size(sparse.path) < 16 * 1024 * 1024,
                "sparse GGUF consumed unexpected physical storage");
#endif

        std::filesystem::resize_file(sparse.path, absolute_offset + 3);
        expect_gguf_error([&] { (void)index_gguf(sparse.path, limits); }, "ends at byte");

        TemporaryFile overflow("offset_overflow.gguf");
        write_regular(overflow.path, gguf_prefix(std::numeric_limits<uint64_t>::max() - 31));
        expect_gguf_error([&] { (void)index_gguf(overflow.path, limits); }, "overflows uint64");

        std::printf("LARGE SPARSE GGUF PASS: logical_size=%llu offset=%llu encoded_length=4 "
                    "estimated_manifest=%llu allocated=%llu\n",
                    static_cast<unsigned long long>(absolute_offset + 4),
                    static_cast<unsigned long long>(absolute_offset),
                    static_cast<unsigned long long>(artifact.telemetry.estimated_manifest_bytes),
                    static_cast<unsigned long long>(allocated_size(sparse.path)));
        return 0;
    } catch (const std::exception& error) {
        std::fprintf(stderr, "[FAIL] %s\n", error.what());
        return 1;
    }
}
