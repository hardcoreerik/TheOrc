# TheOrc Release Asset Audit

Generated 2026-07-13 00:15 UTC by scripts/audit_release.ps1.

| Tag | Published | Missing assets | Native backend proxy | Launch result | Status |
|---|---|---|---|---|---|
| v1.11.0 | 06/26/2026 03:48:35 | none | **NO CUDA DLLs FOUND (pre-fix era -- real bug)** | not_executed (historical binary -- static checks only, see script header) | FLAGGED |
| v1.11.2 | 07/03/2026 05:29:59 | none | **NO CUDA DLLs FOUND (pre-fix era -- real bug)** | not_executed (historical binary -- static checks only, see script header) | FLAGGED |
| v1.12.0 | 07/13/2026 05:22:39 | none | absent from zip (expected -- installer fetches at install time, see script header) | started_and_stayed_up | no known issues found by this audit |

## Flagged tags (detail)

### v1.11.0
- PRE-CUDA-FIX, CONFIRMED (published before 2026-07-04 AND this audit's own download found zero CUDA runtime DLLs in the portable zip -- the historical omission bug the review documents, not just a date-based assumption)
- CONCURRENCY-BUG-RELEASE (documented in release.yml's own comments: could silently omit Windows/Linux Warband assets due to a matrix-concurrency race)

### v1.11.2
- PRE-CUDA-FIX, CONFIRMED (published before 2026-07-04 AND this audit's own download found zero CUDA runtime DLLs in the portable zip -- the historical omission bug the review documents, not just a date-based assumption)

