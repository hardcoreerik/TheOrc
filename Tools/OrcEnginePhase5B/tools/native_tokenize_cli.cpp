// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// A4: minimal CLI so the native OrcEngine Phase 5B encoder can be
// exercised as the third leg of the three-way tokenizer comparison
// (Hugging Face tokenizers==0.22.2 / pinned llama.cpp b10436 / native
// Phase 5B), matching the plain "print the ID list" contract the other
// two oracles already use (tokenizer_dual_source_check.py's HF leg,
// llama-tokenize.exe's `--ids` output). Encodes with LiteralText mode
// (the native default), matching the pinned encode_special_tokens=True
// convention the dual-source script's HF leg already uses for these
// ordinary-text fixtures. Not a general-purpose tool -- reads one UTF-8
// text per LINE from stdin (not argv: Windows' narrow-argv construction
// re-encodes the wide command line through the ANSI/OEM codepage, which
// can corrupt non-ASCII bytes before this program ever sees them --
// confirmed directly: passing "café résumé" via argv produced a mangled,
// invalid-UTF-8 byte sequence; reading the identical text from stdin,
// opened in binary mode, does not have this problem) and prints one line
// of comma-separated IDs per input line, nothing else.
#include <cstdio>
#include <string>
#include <vector>

#ifdef _WIN32
#include <fcntl.h>
#include <io.h>
#endif

#include "orcengine/tokenizer.hpp"

using namespace orcengine;

namespace {
void emit_ids(const TokenizerProfile& profile, const std::string& line) {
    const std::vector<int64_t> ids = profile.encode(line);
    for (size_t k = 0; k < ids.size(); ++k) {
        if (k) std::fputc(',', stdout);
        std::printf("%lld", static_cast<long long>(ids[k]));
    }
    std::fputc('\n', stdout);
}
}  // namespace

int main(int argc, char** argv) {
    if (argc != 2) {
        std::fprintf(stderr, "usage: %s <gguf-path>  (reads UTF-8 texts, one per line, from stdin)\n", argv[0]);
        return 2;
    }
#ifdef _WIN32
    _setmode(_fileno(stdin), _O_BINARY);
#endif
    try {
        const TokenizerProfile profile = load_tokenizer_profile(argv[1]);
        std::string line;
        int ch;
        while ((ch = std::getchar()) != EOF) {
            if (ch == '\n') {
                if (!line.empty() && line.back() == '\r') line.pop_back();
                emit_ids(profile, line);
                line.clear();
            } else {
                line.push_back(static_cast<char>(ch));
            }
        }
        if (!line.empty()) emit_ids(profile, line);
        return 0;
    } catch (const std::exception& ex) {
        std::fprintf(stderr, "ERROR: %s\n", ex.what());
        return 1;
    }
}
