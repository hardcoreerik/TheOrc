// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// A4: minimal CLI so the native OrcEngine Phase 5B encoder can be
// exercised as the third leg of the three-way tokenizer comparison
// (Hugging Face tokenizers==0.22.2 / pinned llama.cpp b10436 / native
// Phase 5B). ONE invocation represents ONE prompt: the complete stdin
// byte stream, read through EOF, is the prompt verbatim -- no `\n`
// delimiter, no `\r` stripping, no line-record framing of any kind, so
// an individual prompt may itself contain embedded LF, CRLF, NUL, or any
// other byte. (Post-freeze correction, per a Codex finding against the
// original line-delimited protocol: that protocol could not represent a
// SINGLE prompt containing embedded LF/CRLF, since newline itself was
// the record separator -- this version fixes that by making one process
// invocation carry exactly one prompt, matching how llama-tokenize.exe
// and the HF leg are each invoked once per fixture in the driver script.)
// Reads from stdin (not argv: Windows' narrow-argv construction
// re-encodes the wide command line through the ANSI/OEM codepage, which
// can corrupt non-ASCII bytes before this program ever sees them --
// confirmed directly: passing "café résumé" via argv produced a mangled,
// invalid-UTF-8 byte sequence; reading the identical text from stdin,
// opened in binary mode, does not have this problem), and prints exactly
// one line of comma-separated token IDs. Empty stdin is the empty
// prompt and still emits one (empty) record followed by a newline.
//
// The CLI is a validation utility, not a service: one invocation per
// fixture is the accepted cost, per the authorizing instruction. No
// framing protocol, server, or persistent worker is added.
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
void usage(const char* prog) {
    std::fprintf(stderr,
                 "usage: %s <gguf-path> [--recognize-control-tokens]\n"
                 "  Reads the complete stdin byte stream (through EOF) as ONE prompt,\n"
                 "  verbatim -- no line-delimiting, no CR stripping. Prints one line of\n"
                 "  comma-separated token IDs.\n"
                 "  --recognize-control-tokens: encode with SpecialTokenMode::"
                 "RecognizeControlTokens instead of the default LiteralText.\n",
                 prog);
}
}  // namespace

int main(int argc, char** argv) {
    if (argc < 2) {
        usage(argv[0]);
        return 2;
    }
    SpecialTokenMode mode = SpecialTokenMode::LiteralText;
    for (int i = 2; i < argc; ++i) {
        const std::string arg = argv[i];
        if (arg == "--recognize-control-tokens") {
            mode = SpecialTokenMode::RecognizeControlTokens;
        } else {
            std::fprintf(stderr, "unknown option: %s\n", arg.c_str());
            usage(argv[0]);
            return 2;
        }
    }
#ifdef _WIN32
    _setmode(_fileno(stdin), _O_BINARY);
#endif
    try {
        const TokenizerProfile profile = load_tokenizer_profile(argv[1]);

        std::string prompt;
        int ch;
        while ((ch = std::getchar()) != EOF) {
            prompt.push_back(static_cast<char>(ch));
        }

        const std::vector<int64_t> ids = profile.encode(prompt, mode);
        for (size_t k = 0; k < ids.size(); ++k) {
            if (k) std::fputc(',', stdout);
            std::printf("%lld", static_cast<long long>(ids[k]));
        }
        std::fputc('\n', stdout);
        return 0;
    } catch (const std::exception& ex) {
        std::fprintf(stderr, "ERROR: %s\n", ex.what());
        return 1;
    }
}
