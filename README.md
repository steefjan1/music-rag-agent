# Music RAG Agent — Azure Functions companion code

Companion code for the blog post [Building RAG Pipelines with Azure Functions](https://sjwiggers.com/rag-pipelines-azure-functions).

A two-pipeline RAG system built on Azure Functions:

1. **Ingestion pipeline** — an EventGrid-triggered function scrapes band and album data from [SputnikMusic](https://www.sputnikmusic.com), generates embeddings using the Azure OpenAI SDK, and indexes the result into Azure AI Search.

2. **Chat agent** — an HTTP-triggered function accepts natural language queries about bands and albums, retrieves relevant passages from Azure AI Search, augments the prompt with retrieved context, and returns a grounded response via the Azure OpenAI SDK.

## What you can ask

- "What are the best-reviewed albums by Tool?"
- "Give me a discography overview for Opeth."
- "Which Radiohead album has the highest Sputnik rating?"
- "Compare Tool and Opeth's most acclaimed albums."

## Architecture

```
scripts/ingest.py              Azure Blob Storage
(scrapes SputnikMusic)  →  →   (band-data/{artist_id}.json)
                                       ↓ EventGrid
                          BlobTriggerIngest (C# Function)
                          - Azure OpenAI embeddings (SDK)
                          - Azure AI Search index write
                                       ↓
                          Azure AI Search (music-index)
                                       ↑
                          MusicChatAgent (C# Function)
                          - Azure AI Search retrieval
                          - Azure OpenAI chat completion (SDK)
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

# 2. Post-provision manual steps (see Known Issues below)
# Enable AAD auth on Azure AI Search
az search service update --name <search-name> --resource-group <rg> \
  --auth-options aadOrApiKey --aad-auth-failure-mode http401WithBearerChallenge

# Add storage roles for function app managed identity
principalId=$(az functionapp identity show --name <func-name> --resource-group <rg> --query principalId -o tsv)
storageId=$(az storage account show --name <storage-name> --resource-group <rg> --query id -o tsv)
az role assignment create --assignee $principalId --role "Storage Queue Data Contributor" --scope $storageId
az role assignment create --assignee $principalId --role "Storage Table Data Contributor" --scope $storageId

# Create EventGrid system topic and subscription for blob trigger
az eventgrid system-topic create --name <baseName>-eg-topic \
  --resource-group <rg> \
  --source $storageId \
  --topic-type Microsoft.Storage.StorageAccounts \
  --location <location>

funcId=$(az functionapp show --name <func-name> --resource-group <rg> --query id -o tsv)
az eventgrid system-topic event-subscription create \
  --name <baseName>-blob-sub \
  --resource-group <rg> \
  --system-topic-name <baseName>-eg-topic \
  --endpoint "$funcId/functions/BlobTriggerIngest" \
  --endpoint-type azurefunction \
  --included-event-types Microsoft.Storage.BlobCreated \
  --subject-begins-with /blobServices/default/containers/band-data/

# 3. Install Python scraper dependencies
pip install requests beautifulsoup4 lxml azure-storage-blob azure-identity --only-binary=cryptography

# 4. Ingest bands
set AZURE_STORAGE_ACCOUNT_NAME=<storage-name>
python scripts/ingest.py --artist-id 83 --artist-name "Tool"
python scripts/ingest.py --artist-id 932 --artist-name "Opeth"
python scripts/ingest.py --artist-id 328 --artist-name "Porcupine Tree"
python scripts/ingest.py --artist-id 86 --artist-name "Radiohead"

# 5. Query the agent
curl -X POST "https://<func-name>.azurewebsites.net/api/chat?code=<key>" \
  -H "Content-Type: application/json" \
  -d '{"message": "What are the best Tool albums according to Sputnik ratings?"}'
```

## Known Sputnik artist IDs

| Artist | ID |
|---|---|
| Tool | 83 |
| Opeth | 932 |
| Porcupine Tree | 328 |
| Radiohead | 86 |
| Mastodon | 9186 |
| Sigur Rós | 5526 |

Find others by browsing `https://www.sputnikmusic.com/bands/a/{id}`.

## Known constraints and deployment notes

### Blob triggers on Flex Consumption require EventGrid

Standard polling `BlobTrigger` is not supported on Flex Consumption plans — Azure requires `BlobTriggerSource.EventGrid`. This means two extra post-provision steps:

1. Create an EventGrid system topic for the storage account
2. Create an event subscription pointing to the `BlobTriggerIngest` function

The CLI commands are in the Quick start above. These are not included in the Bicep template because the EventGrid subscription requires the function to already be deployed, creating a circular dependency.

### Azure AI Search defaults to API key authentication

After provisioning, the Search service uses `apiKeyOnly` auth by default. The function app's managed identity cannot authenticate until you enable `aadOrApiKey`:

```bash
az search service update --name <search-name> --resource-group <rg> \
  --auth-options aadOrApiKey --aad-auth-failure-mode http401WithBearerChallenge
```

### Storage Queue and Table Data Contributor roles required

The Bicep assigns `Storage Blob Data Owner` but Durable Functions internals also need queue and table access. Assign both manually after provision:

```bash
az role assignment create --assignee <principalId> --role "Storage Queue Data Contributor" --scope <storageId>
az role assignment create --assignee <principalId> --role "Storage Table Data Contributor" --scope <storageId>
```

### Azure OpenAI binding extension — version instability

The `Microsoft.Azure.Functions.Worker.Extensions.OpenAI` package is in alpha and its API surface changes between preview versions. `EmbeddingsStoreOutput` and related binding types are not stable across versions. This sample uses the Azure OpenAI SDK directly for both ingestion and chat to avoid version fragmentation. The binding extension approach is documented in post 5 as the intended production pattern once it reaches GA.

### Python on Windows ARM64 — cryptography build failure

Installing `azure-identity` on Python 3.13 ARM64 Windows fails because the `cryptography` package requires Rust to build from source. Fix:

```bash
pip install azure-identity azure-storage-blob --only-binary=cryptography
```

### SputnikMusic scraping

SputnikMusic does not provide an official public API. The scraper uses HTML parsing and may break if the site structure changes. Rate limiting is set to 1.5 seconds between requests — do not remove this.

## Series

Companion to the ongoing series on AI and Azure Functions at [sjwiggers.com](https://sjwiggers.com).
