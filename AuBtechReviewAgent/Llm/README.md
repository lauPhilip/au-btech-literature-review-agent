# Llm

Everything about talking to the language model. `LlmProvider.cs` creates the chat service (Mistral, or any OpenAI-compatible server such as Ollama). `LlmJson.cs` asks for JSON, reads the answer robustly and sends a problem back to the model once. `LlmCallRecorder.cs` records every call (prompt, answer, model, temperature) for the run's ledger. `PromptSafety.cs` marks text from papers and users as data, and flags instruction-like phrases (possible prompt injection).
