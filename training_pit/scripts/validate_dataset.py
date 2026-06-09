#!/usr/bin/env python3
"""
validate_dataset.py — Validate a chat-JSONL training dataset.

Usage:
    python training_pit/scripts/validate_dataset.py datasets/train_v1.jsonl

Checks:
    - Each line is valid JSON
    - Each object has a 'messages' array and a 'metadata' object
    - 'messages' has at least one 'user' turn and one 'assistant' turn
    - Each message has 'role' and 'content' fields
    - Valid roles: system, user, assistant
    - 'metadata' has all required fields
    - Valid enum values for category, source, quality

Exit codes:
    0 — all valid
    1 — one or more errors found
"""

import json
import sys
from pathlib import Path

VALID_ROLES = {"system", "user", "assistant"}

VALID_CATEGORIES = {
    "boss_planning", "debugging", "delegation", "minimal_patching",
    "powershell", "esp_idf", "ollama", "openclaw", "continue_config",
    "react_dashboard", "python_utility", "validation_commands",
    "uncertainty_handling", "hallucination_resistance", "code_review",
    "imported_adapter_eval",
}

VALID_SOURCES = {
    "manual", "corrected_model_output", "terminal_log", "repo_issue",
    "swarm_capture", "eval_failure", "synthetic", "imported",
}

VALID_QUALITY = {"gold", "silver", "draft", "rejected"}

REQUIRED_METADATA = {
    "category", "source", "quality", "contains_sensitive_data",
    "base_model_target", "created_by",
}


def validate_file(path: str) -> int:
    errors = 0
    file_path = Path(path)

    if not file_path.exists():
        print(f"ERROR: File not found: {path}")
        return 1

    with open(file_path, encoding="utf-8") as f:
        for line_num, raw_line in enumerate(f, start=1):
            line = raw_line.strip()
            if not line:
                continue

            # JSON parse
            try:
                obj = json.loads(line)
            except json.JSONDecodeError as e:
                print(f"Line {line_num}: INVALID JSON — {e}")
                errors += 1
                continue

            # Top-level structure
            if "messages" not in obj:
                print(f"Line {line_num}: MISSING 'messages' field")
                errors += 1
                continue

            if "metadata" not in obj:
                print(f"Line {line_num}: MISSING 'metadata' field")
                errors += 1

            # Messages validation
            messages = obj["messages"]
            if not isinstance(messages, list) or len(messages) < 2:
                print(f"Line {line_num}: 'messages' must be an array with at least 2 items")
                errors += 1
                continue

            roles_seen = set()
            for i, msg in enumerate(messages):
                if not isinstance(msg, dict):
                    print(f"Line {line_num}, message {i}: must be an object")
                    errors += 1
                    continue

                if "role" not in msg:
                    print(f"Line {line_num}, message {i}: MISSING 'role'")
                    errors += 1
                elif msg["role"] not in VALID_ROLES:
                    print(f"Line {line_num}, message {i}: invalid role '{msg['role']}' — must be one of {VALID_ROLES}")
                    errors += 1
                else:
                    roles_seen.add(msg["role"])

                if "content" not in msg:
                    print(f"Line {line_num}, message {i}: MISSING 'content'")
                    errors += 1
                elif not isinstance(msg["content"], str) or not msg["content"].strip():
                    print(f"Line {line_num}, message {i}: 'content' must be a non-empty string")
                    errors += 1

            if "user" not in roles_seen:
                print(f"Line {line_num}: no 'user' message found")
                errors += 1
            if "assistant" not in roles_seen:
                print(f"Line {line_num}: no 'assistant' message found")
                errors += 1

            # Metadata validation
            if "metadata" in obj:
                meta = obj["metadata"]
                if not isinstance(meta, dict):
                    print(f"Line {line_num}: 'metadata' must be an object")
                    errors += 1
                    continue

                for field in REQUIRED_METADATA:
                    if field not in meta:
                        print(f"Line {line_num}: metadata missing required field '{field}'")
                        errors += 1

                if "category" in meta and meta["category"] not in VALID_CATEGORIES:
                    print(f"Line {line_num}: invalid metadata.category '{meta['category']}'")
                    errors += 1

                if "source" in meta and meta["source"] not in VALID_SOURCES:
                    print(f"Line {line_num}: invalid metadata.source '{meta['source']}'")
                    errors += 1

                if "quality" in meta and meta["quality"] not in VALID_QUALITY:
                    print(f"Line {line_num}: invalid metadata.quality '{meta['quality']}'")
                    errors += 1

    if errors == 0:
        print(f"Validation complete: 0 errors. File is valid.")
    else:
        print(f"\nValidation failed: {errors} error(s) found in {path}")

    return 1 if errors > 0 else 0


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("Usage: python validate_dataset.py <path_to_jsonl>")
        sys.exit(1)

    sys.exit(validate_file(sys.argv[1]))
