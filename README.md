# TraceableAI: An Open-Source Agentic Framework for PRISMA-Compliant Literature Synthesis

[![Build Status](https://img.shields.io/badge/.NET-10.0-blue.svg)](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
[![LLM Engine](https://img.shields.io/badge/Orchestration-Mistral%20Large-purple.svg)](https://mistral.ai/)
[![Compliance](https://img.shields.io/badge/Reporting-PRISMA%202020-green.svg)](https://prisma-statement.org/)
[![Conference Presentation](https://img.shields.io/badge/OSSYM-2026-003B5C.svg)](https://opensearchfoundation.org/)

**TraceableAI** is an open-source agentic framework developed at the *Department of Business Development and Technology (BTECH), Aarhus University*, designed to execute systematic literature reviews (SLRs) while maintaining strict transparency and reproducibility.

---

## 🎯 The Vision

Automated literature surveys and deep research agents often suffer from a severe flaw: they produce fluent text with broken, mismatched, or completely fabricated citations. This lack of auditability makes them unusable for strict academic standards like the PRISMA 2020 statement. 

### Key Features:
* **True Grounding:** Every claim points directly back to source facts, entirely skipping raw placeholder strings.
* **Isolated Data Capsule:** Wipes stale records on every execution, delivering a clean directory bundle containing a step-by-step process trace (`transparent-process.json`), compiled checklist text (`prisma-report.json`), and the exact pool of parsed source manuscripts (`/PapersWorkspace/`).
* **Document Preview:** Generates an immediate, dual-column academic paper view embedded with live empirical graphs (publication year bar graphs and multi-venue type distribution pie charts).

---

## 🛠️ System Architecture

![System Architecture](./diagram%20for%20traceability%20AI%20review%20agent.png)

The agent orchestrates literature ingestion through five distinct phases:

1. **Platform Extraction:** Targets open-access repositories and developer indexing gateways (`arXiv`, `ScienceDirect/Scopus`, `IEEE Xplore`, `Google Scholar`, and `ResearchGate`) using literal query phrases.
2. **Dual-Layer Screening:** Routes candidate metadata through a single-screener LLM classification model verified by background runtime checks to cross-reference structural workflow constraints.
3. **Manuscript Ingestion:** Quietly downloads target PDF files into an unmonitored disk directory (`/PapersWorkspace/`).
4. **Semantic Layout Chunking:** Reads raw paper layers page-by-page using pure C# extractors (`PdfPig`) and segments text into 600-character overlapping windows tagged with explicit source ID tokens.
5. **Grounded Synthesis:** Hands the combined context matrices to Mistral Large to output structured, multi-source validated PRISMA compliance items and scrubbed reference indexing arrays.

---

## 📂 Standardized Workspace Footprint

Completing an evaluation loop populates a clean, transparent workspace footprint:

```text
├── PapersWorkspace/            # Self-clearing container for current run PDF source files
│   ├── Framework_Design_Patterns.pdf
│   └── Trustworthy_System_Safety.pdf
├── prisma-report.json          # Synthesized PRISMA expanded checklist items 
└── transparent-process.json    # Complete machine-readable ledger tracking screener logic
