# SillyTavern Integration Research for TheOrc

## Project Overview

**SillyTavern** is a powerful LLM frontend designed for power users seeking uncensored chat capabilities. 
- **Language**: JavaScript (Node.js/Express backend)
- **GitHub**: https://github.com/SillyTavern/SillyTavern
- **Stars**: ~29,600
- **License**: AGPL-3.0
- **Website**: https://sillytavern.app

## Core Architecture

### Backend System
SillyTavern uses an **agnostic multi-backend architecture** that supports both API-based and local model backends:

#### API-Based Backends
- Claude (Anthropic)
- OpenAI (GPT-4, GPT-3.5)
- OpenRouter
- Google Gemini
- Mistral AI
- Cohere
- Groq
- DeepSeek
- And 20+ more commercial APIs

#### Local/Self-Hosted Backends (Key for Uncensored Chat)
- **Ollama** (GGUF format, easy Docker/local setup)
- **KoboldCpp** (KoboldAI, supports various quants)
- **vLLM** (High-performance serving)
- **Aphrodite Engine** (Based on vLLM)
- **LM Studio** (Desktop app wrapper)
- **Text-completions API** (Generic OpenAI-compatible)

### The "Uncensored" Pattern

**Critical insight**: SillyTavern itself has **zero content filtering**. The uncensored behavior comes from:

1. **No Safety Layer in SillyTavern** - The frontend never filters, modifies, or rejects responses
2. **Local Model Freedom** - Local models (Ollama, KoboldCpp, etc.) have no API-enforced safety guardrails
3. **Direct Passthrough** - Requests are converted to the backend's format but not sanitized
4. **No Output Filtering** - All model outputs are streamed directly to the user

### Key Technical Components

#### 1. **Endpoint Handlers** (`/src/endpoints/`)
- `backends/chat-completions.js` - Handles API calls to Claude, OpenAI, etc.
- `backends/text-completions.js` - Handles local model backends
- `backends/kobold.js` - Legacy KoboldAI support
- Character, chat, preset management endpoints

#### 2. **Middleware** (`/src/middleware/`)
- **No safety/content filtering middleware**
- Auth, CORS, rate limiting, request validation
- User/whitelist management
- Caching and compression

#### 3. **Prompt Conversion** (`/src/prompt-converters.js`)
- Translates user messages to different API formats
- Handles system prompts, tool calling, function definitions
- No modification of unsafe content

#### 4. **Streaming Architecture**
```javascript
// Exemplary flow from text-completions.js
jsonStream.body.on('data', (data) => {
    const json = JSON.parse(data);
    const text = json.response || '';
    response.write(`data: ${JSON.stringify(chunk)}\n\n`);
});
```
- Server-Sent Events (SSE) for real-time streaming
- Works identically whether backend is local or cloud

## Integration Strategy for TheOrc

### Option 1: Replace TheOrc Chat Backend with SillyTavern Pattern (Recommended for v2.0)

**Pros:**
- Proven multi-backend architecture
- Zero effort to support uncensored local models
- Battle-tested streaming implementation
- Active community (29k stars, ongoing updates)

**Cons:**
- Requires JavaScript knowledge
- Dependency on Node.js ecosystem
- AGPL-3.0 license (copyleft - requires source release)

**Implementation Path:**
1. Keep TheOrc's native runtime (LLamaSharp, IModelRuntime)
2. Add Express-based HTTP wrapper (expose LLamaSharp via `/v1/chat/completions`)
3. Or: Embed SillyTavern's backend handlers, replace their model calls with TheOrc's Runtime
4. Use SillyTavern's frontend as the UI (or keep Avalonia, add SillyTavern-style multi-backend routing)

### Option 2: Adopt SillyTavern's Backend Patterns (Minimum Integration)

Copy the architectural patterns without taking the full dependency:

**Patterns to implement in C#:**
1. **Multi-backend routing** - Route requests based on backend type
2. **Streaming response handler** - SSE or WebSocket streaming
3. **Prompt conversion layer** - Different backends need different message formats
4. **Backend-agnostic chat endpoint** - `/api/chat/complete` that works for any backend

**Example C# structure:**
```csharp
// Mimic SillyTavern's pattern
public interface IChatBackend
{
    Task<ChatCompletionResponse> StreamAsync(ChatRequest req);
}

public class OllamaBackend : IChatBackend { }
public class LocalLlamaBackend : IChatBackend { }  // TheOrc native
public class OpenAIBackend : IChatBackend { }

// Route based on user selection
var backend = GetBackendForUser(userId);
await backend.StreamAsync(request);
```

### Option 3: Use SillyTavern as a Local Proxy (Integration via HTTP)

**Simplest approach:**
1. Run SillyTavern as a separate service (Docker container or local process)
2. TheOrc's Avalonia UI calls SillyTavern's HTTP API
3. SillyTavern handles multi-backend routing, streaming, uncensored logic
4. TheOrc focuses on training (Pit Boss) and orchestration (HIVE)

**Pros:**
- Zero code changes to TheOrc core
- Leverage SillyTavern's 29k community
- Easy to update (just pull new SillyTavern releases)
- Clean separation of concerns

**Cons:**
- Extra process/resource overhead
- Dependency management complexity
- Requires TheOrc → SillyTavern API integration in UI

## Critical Implementation Details

### Prompt Conversion
SillyTavern normalizes different message formats:
```javascript
// Input (standardized):
[{ role: 'user', content: 'Hello' }]

// Converts to API-specific format:
// Claude → { role: 'user', content: 'Hello' }
// Ollama → { messages: [...], model: 'llama2', stream: true }
// vLLM → POST /v1/completions with prompt
```

**For TheOrc:** Implement a similar layer that:
- Takes a standardized chat message format
- Converts to the underlying model's expected format
- Handles system prompts, tool definitions, etc.

### No Output Filtering
SillyTavern's text-completions handler:
```javascript
const text = json.response || '';
response.write(`data: ${JSON.stringify(chunk)}\n\n`); // Direct passthrough
```

There is **no filtering, sanitization, or rejection** of model outputs. This is the key to "uncensored" behavior.

### Backend Detection
SillyTavern determines backend type via:
1. Request body `api_type` field
2. Endpoint URL pattern matching
3. Model ID format detection
4. Server capability probing (`/v1/models`, `/api/tags`, etc.)

## Uncensored Chat Best Practices (from SillyTavern)

1. **Use local models exclusively** for uncensored behavior
   - Ollama + Mistral/Dolphin/Neural Chat (permissive models)
   - KoboldCpp + fine-tuned models without safety training
   - Text-completions API pointing to local vLLM

2. **No filtering at the frontend level**
   - Never check prompt/response for banned words
   - Never inject system instructions to prevent outputs
   - Never terminate conversations based on content

3. **Support model-level control**
   - Expose temperature, top_p, top_k (creativity/safety tradeoff)
   - Allow custom system prompts (override safety instructions)
   - Support stopping sequences for user control

4. **Streaming for transparency**
   - Users see outputs in real-time
   - No "content policy violation" post-hoc filtering
   - Direct feedback loop

## Recommended Path Forward for TheOrc

### Phase 1 (Minimal) - Months 1-2
- [ ] Add OpenAI-compatible `/v1/chat/completions` endpoint to TheOrc's native runtime
- [ ] Route TheOrc's local models through this endpoint
- [ ] Create a `ChatBackend` router that supports both cloud and local
- [ ] Document the uncensored local-only requirement in ROADMAP

### Phase 2 (Medium) - Months 2-3
- [ ] Implement SillyTavern-style prompt conversion (handle different model formats)
- [ ] Add streaming chat UI to Avalonia
- [ ] Support switching between backends (Ollama, KoboldCpp, local TheOrc models)
- [ ] Implement system prompt overrides (bypass safety training)

### Phase 3 (Full Integration) - Months 3+
- [ ] Evaluate replacing SillyTavern's chat endpoint with TheOrc's (remove JS dependency)
- [ ] OR: Embed SillyTavern as a Docker service alongside TheOrc
- [ ] Multi-node HIVE chat clustering (queue requests across training PITs)
- [ ] Integrate with ORC ACADEMY for custom uncensored model training

## Model Recommendations for Uncensored Chat

**Best Local Models (from SillyTavern community):**
1. **Mistral 7B** - Permissive, high quality
2. **Neural Chat 7B** - Optimized for multi-turn conversation
3. **Dolphin 2.2** (Mixtral) - Uncensored, high performance
4. **Openhermes 2.5** - Good all-rounder, fewer safety guardrails
5. **Hermes 3** - Very capable, locally uncensored

**Quantization Strategy:**
- Q4_K_M (4-bit quantized): ~4GB VRAM, good quality/speed tradeoff
- Q5_K_M: ~6GB VRAM, better quality
- Q8_0: ~8GB VRAM, near-original quality

**TheOrc Advantage:** Train custom fine-tunes using ORC ACADEMY with zero safety guardrails

## License Considerations

**SillyTavern is AGPL-3.0**, which means:
- ✅ Can use as a local frontend
- ⚠️ If you distribute SillyTavern (as part of TheOrc), you must release source
- ❌ Cannot use its code in closed-source commercial product

**Recommendation:** If choosing **Option 3 (proxy approach)**, SillyTavern remains separate and license obligation is minimal. If doing **Option 1-2 (code integration)**, budget for maintaining AGPL compliance or implement from scratch.

## Next Steps

1. **Review this research** with team
2. **Decide integration depth:**
   - Minimal: Implement patterns only (Option 2)
   - Medium: Use as HTTP proxy (Option 3)
   - Deep: Rewrite with SillyTavern patterns in C# (Option 1)
3. **Assign ownership** for chat backend work
4. **Create spike** to test local model streaming (1-2 day proof-of-concept)
5. **Update v1.8 roadmap** with chat feature gate

## References

- SillyTavern Docs: https://docs.sillytavern.app/
- Discord Community: https://discord.gg/sillytavern
- Reddit: https://reddit.com/r/SillyTavernAI
- OpenAI Chat Completions API (reference standard): https://platform.openai.com/docs/api-reference/chat/create
