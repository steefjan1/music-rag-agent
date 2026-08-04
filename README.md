# Music RAG Agent — Azure Functions companion code

Companion code for the blog post [Building RAG Pipelines with Azure Functions](https://sjwiggers.com/rag-pipelines-azure-functions).

A two-pipeline RAG system built on Azure Functions:

1. **Ingestion pipeline** — a blob-triggered function scrapes band and album data from [SputnikMusic](https://www.sputnikmusic.com) via the [sputnik-api](https://github.com/dlin94/sputnik-api), generates embeddings using the Azure OpenAI embeddings binding, and indexes the result into Azure AI Search.

2. **Chat agent** — an HTTP-triggered function accepts natural language queries about bands and albums, retrieves relevant passages from Azure AI Search, augments the prompt with retrieved context, and returns a grounded response via the Azure OpenAI chat completion binding.

## What you can ask

- "What are the best-reviewed albums by Tool?"
- "Give me a discography overview for Opeth."
- "What does Sputnik say about Lateralus by Tool?"
- "Which Porcupine Tree albums have the highest ratings?"

## Architecture

```
scripts/ingest.py              Azure Blob Storage
(scrapes SputnikMusic)  →  →   (band-data/{band_id}.json)
                                       ↓
                          BlobTriggerIngest (C# Function)
                          - Azure OpenAI embeddings binding
                          - Azure AI Search index write
                                       ↓
                          Azure AI Search (music-index)
                                       ↑
                          MusicChatAgent (C# Function)
                          - Azure AI Search retrieval
                          - Azure OpenAI chat completion binding
                                       ↑
                              HTTP POST /api/chat
```

## Prerequisites

- [Azure Developer CLI (azd)](https://learn.microsoft.com/azure/developer/azure-developer-cli/install-azd)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Python 3.11+](https://www.python.org/downloads/) for the ingestion script
- An Azure subscription with Azure OpenAI access

## Quick start

```bash
# 1. Deploy infrastructure and function app
azd up

# 2. Install Python scraper dependencies
pip install requests beautifulsoup4 lxml azure-storage-blob

# 3. Ingest a band by Sputnik artist ID
#    Tool = 6723, Opeth = 1561, Porcupine Tree = 2455
python scripts/ingest.py --artist-id 6723 --artist-name "Tool"

# 4. Query the agent
curl -X POST "https://<func-name>.azurewebsites.net/api/chat?code=<key>" \
  -H "Content-Type: application/json" \
  -d '{"message": "What are the best Tool albums according to Sputnik reviews?"}'
```

## Known constraints

- SputnikMusic does not provide an official public API. The scraper is built on HTML parsing and may break if the site structure changes.
- The Azure OpenAI binding extension requires the preview extension bundle (`Microsoft.Azure.Functions.ExtensionBundle.Preview`) — see `host.json`.
- Artist IDs are Sputnik's internal numeric IDs. Use the URL on sputnikmusic.com/bands/ to find them: `sputnikmusic.com/bands/a/6723` = Tool.

## Series

Companion to the ongoing series on AI and Azure Functions at [sjwiggers.com](https://sjwiggers.com).
