# Third-Party References and Bundled Data

Except for the generated identity and token-price snapshot data derived from models.dev and LiteLLM as explicitly described below, AgenTally does not bundle, launch, or depend on the referenced projects at runtime. Their public implementations were consulted during independent development of read-only Agent collectors, exact model matching and validation methods.

## TokenTracker

- Repository: https://github.com/mm7894215/TokenTracker
- Parser consulted: https://github.com/mm7894215/TokenTracker/blob/main/src/lib/codex-rollout-parser.js
- License: https://github.com/mm7894215/TokenTracker/blob/main/LICENSE
- Qoder/Qoder CN Desktop parser revision consulted: https://github.com/xiufengsun/TokenTracker/tree/5122d2e9cabaf1895279c66cad74010f9f8d254d
- Qoder parser reference: https://github.com/xiufengsun/TokenTracker/blob/5122d2e9cabaf1895279c66cad74010f9f8d254d/src/lib/rollout.js
- License at that revision: https://github.com/xiufengsun/TokenTracker/blob/5122d2e9cabaf1895279c66cad74010f9f8d254d/LICENSE

## CC Switch

- Repository: https://github.com/farion1231/cc-switch
- Parser consulted: https://github.com/farion1231/cc-switch/blob/main/src-tauri/src/services/session_usage_codex.rs
- License: https://github.com/farion1231/cc-switch/blob/main/LICENSE

## Codex Usage Tracker

- Repository revision consulted: https://github.com/douglasmonsky/codex-usage-tracker/tree/b3765c27f6c3bf6068e1935ea33d0e9decf1e2f6
- Parser reference: https://github.com/douglasmonsky/codex-usage-tracker/blob/b3765c27f6c3bf6068e1935ea33d0e9decf1e2f6/src/codex_usage_tracker/parser/jsonl_v1.py
- Logical identity reference: https://github.com/douglasmonsky/codex-usage-tracker/blob/b3765c27f6c3bf6068e1935ea33d0e9decf1e2f6/src/codex_usage_tracker/core/usage_identity.py
- License: https://github.com/douglasmonsky/codex-usage-tracker/blob/b3765c27f6c3bf6068e1935ea33d0e9decf1e2f6/LICENSE

## tokscale

- Repository release consulted: https://github.com/junhoyeo/tokscale/tree/v4.8.1
- ZCode SQLite parser and Token normalization reference: https://github.com/junhoyeo/tokscale/blob/v4.8.1/crates/tokscale-core/src/sessions/zcode.rs
- WorkBuddy JSONL/source-selection reference: https://github.com/junhoyeo/tokscale/blob/v4.8.1/crates/tokscale-core/src/sessions/workbuddy.rs and `tencent_buddy.rs`
- Qwen Code JSONL/source-selection reference: https://github.com/junhoyeo/tokscale/blob/v4.8.1/crates/tokscale-core/src/sessions/qwen.rs
- Gemini CLI JSON/JSONL parser reference: https://github.com/junhoyeo/tokscale/blob/v4.8.1/crates/tokscale-core/src/sessions/gemini.rs
- OpenCode SQLite v1/v2 and legacy JSON parser reference: https://github.com/junhoyeo/tokscale/blob/v4.8.1/crates/tokscale-core/src/sessions/opencode.rs
- Model alias and staged pricing-lookup references: https://github.com/junhoyeo/tokscale/blob/v4.8.1/crates/tokscale-core/src/pricing/aliases.rs and `lookup.rs`
- License: https://github.com/junhoyeo/tokscale/blob/v4.8.1/LICENSE

## ccusage

- Repository release consulted: https://github.com/ccusage/ccusage/tree/v20.0.19
- Qwen Code parser reference: https://github.com/ccusage/ccusage/blob/v20.0.19/rust/adapters/qwen/src/parser.rs
- Gemini CLI parser/path reference: https://github.com/ccusage/ccusage/tree/v20.0.19/rust/adapters/gemini
- OpenCode parser/path reference: https://github.com/ccusage/ccusage/tree/v20.0.19/rust/adapters/opencode
- Model alias and pricing-resolution references: https://github.com/ccusage/ccusage/blob/v20.0.19/rust/crates/ccusage-core/src/model_aliases.rs and `pricing.rs`
- License: https://github.com/ccusage/ccusage/blob/v20.0.19/LICENSE

## models.dev

- Repository: https://github.com/anomalyco/models.dev
- Generated identity inputs: https://models.dev/models.json and https://models.dev/catalog.json
- License: https://github.com/anomalyco/models.dev/blob/dev/LICENSE
- Bundled use: AgenTally ships generated snapshots containing normalized model IDs, unambiguous exact alias mappings, and selected per-Token USD rates from each model's original-provider entry. Source keys and artifact hashes are retained for audit; descriptions, benchmarks and runtime code are not bundled.

MIT License

Copyright (c) 2025 models.dev

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## LiteLLM

- Repository: https://github.com/BerriAI/litellm
- Generated identity input: https://github.com/BerriAI/litellm/blob/main/model_prices_and_context_window.json
- License: https://github.com/BerriAI/litellm/blob/main/LICENSE
- Bundled use: AgenTally ships normalized model IDs, exact alias mappings, source-evidence labels, and selected per-Token USD rates from allow-listed direct model-provider entries. Router, cloud-hosted and reseller prices, capabilities, descriptions and LiteLLM runtime code are not bundled.

MIT License

Copyright (c) 2023 Berri AI

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

All project names and copyrights remain with their respective owners. Refer to each linked license for its terms. The shipped runtime introduces no third-party service, executable, hook, plugin or catalog request; only the developer-invoked maintenance script downloads the listed public catalog inputs.
