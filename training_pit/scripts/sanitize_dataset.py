#!/usr/bin/env python3
"""
sanitize_dataset.py — Scan a chat-JSONL dataset for secrets and PII.

Usage:
    python training_pit/scripts/sanitize_dataset.py datasets/train_v1.jsonl

Hard reject patterns (exits non-zero if any match found):
    - API keys (sk-, ghp_, ghs_, npm_, etc.)
    - SSH private key headers
    - US Social Security Numbers

Soft review flags (reported as warnings, does not fail exit):
    - Private IP addresses (RFC1918)
    - Local Windows/Linux filesystem paths
    - Long hex strings (potential wallet addresses / tokens)
    - Bare IP addresses

Exit codes:
    0 — clean (no rejects; reviews are warnings only)
    1 — one or more REJECT patterns found
"""

import json
import re
import sys
from pathlib import Path

# Patterns that cause REJECT (hard fail)
REJECT_PATTERNS = [
    (r"sk-[A-Za-z0-9]{20,}", "OpenAI/Anthropic API key"),
    (r"ghp_[A-Za-z0-9]{36}", "GitHub personal access token"),
    (r"ghs_[A-Za-z0-9]{36}", "GitHub Actions token"),
    (r"npm_[A-Za-z0-9]{36}", "NPM access token"),
    (r"-----BEGIN (RSA|EC|OPENSSH) PRIVATE KEY-----", "SSH private key"),
    (r"\d{3}-\d{2}-\d{4}", "Potential SSN"),
    (r"password\s*=\s*['\"][^'\"]{4,}", "Hardcoded password"),
    (r"secret\s*=\s*['\"][^'\"]{8,}", "Hardcoded secret"),
    (r"Bearer\s+[A-Za-z0-9._\-]{20,}", "Bearer token"),
]

# Patterns that cause REVIEW (soft warning)
REVIEW_PATTERNS = [
    (r"\b(192\.168|10\.\d+|172\.(1[6-9]|2[0-9]|3[01]))\.\d+\.\d+", "Private IP address"),
    (r"C:\\Users\\[A-Za-z0-9_\-\.]+\\", "Windows user path"),
    (r"/home/[A-Za-z0-9_\-\.]+/", "Linux home path"),
    (r"\b[0-9a-fA-F]{40,}\b", "Long hex string (possible token/hash)"),
    (r"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b", "IP address"),
]


def scan_text(text: str, line_num: int):
    rejects = []
    reviews = []

    for pattern, label in REJECT_PATTERNS:
        matches = re.findall(pattern, text)
        if matches:
            rejects.append((label, matches[0][:60]))

    for pattern, label in REVIEW_PATTERNS:
        matches = re.findall(pattern, text)
        if matches:
            reviews.append((label, matches[0][:60]))

    return rejects, reviews


def sanitize_file(path: str) -> int:
    file_path = Path(path)
    if not file_path.exists():
        print(f"ERROR: File not found: {path}")
        return 1

    total_rejects = 0
    total_reviews = 0

    with open(file_path, encoding="utf-8") as f:
        for line_num, raw_line in enumerate(f, start=1):
            line = raw_line.strip()
            if not line:
                continue

            try:
                obj = json.loads(line)
            except json.JSONDecodeError:
                continue  # validate_dataset.py catches this

            # Build the full text to scan (all message contents + metadata notes)
            scan_parts = []
            for msg in obj.get("messages", []):
                if isinstance(msg.get("content"), str):
                    scan_parts.append(msg["content"])
            meta = obj.get("metadata", {})
            if isinstance(meta.get("notes"), str):
                scan_parts.append(meta["notes"])
            full_text = " ".join(scan_parts)

            rejects, reviews = scan_text(full_text, line_num)

            for label, excerpt in rejects:
                print(f"REJECT  line {line_num}: [{label}] near: ...{excerpt}...")
                total_rejects += 1

            for label, excerpt in reviews:
                print(f"REVIEW  line {line_num}: [{label}] near: ...{excerpt}...")
                total_reviews += 1

    if total_rejects == 0 and total_reviews == 0:
        print(f"Sanitize complete: 0 rejects, 0 reviews. File is clean.")
    else:
        print(f"\nSanitize complete: {total_rejects} REJECT(s), {total_reviews} REVIEW(s).")
        if total_rejects > 0:
            print("Fix all REJECT items before using this file for training.")
        if total_reviews > 0:
            print("Review flagged lines and redact personal paths or IPs if present.")

    return 1 if total_rejects > 0 else 0


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("Usage: python sanitize_dataset.py <path_to_jsonl>")
        sys.exit(1)

    sys.exit(sanitize_file(sys.argv[1]))
