#!/usr/bin/env python3
"""
Ingestion script for the Music RAG Agent.

Scrapes band and album data from SputnikMusic using the sputnik-api scraper,
then uploads the JSON to Azure Blob Storage so the BlobTriggerIngest function
can pick it up and index it into Azure AI Search.

Usage:
    pip install requests beautifulsoup4 lxml azure-storage-blob azure-identity

    # Ingest by Sputnik artist ID
    python ingest.py --artist-id 6723 --artist-name "Tool"
    python ingest.py --artist-id 1561 --artist-name "Opeth"
    python ingest.py --artist-id 2455 --artist-name "Porcupine Tree"

    # Ingest multiple artists
    python ingest.py --artist-id 6723 --artist-name "Tool"
    python ingest.py --artist-id 1561 --artist-name "Opeth"

Finding artist IDs:
    Browse to https://www.sputnikmusic.com/bands/a/{id} and increment the ID
    until you find the band you want. The ID is in the URL.

    Some well-known IDs:
    Tool           = 6723
    Opeth          = 1561
    Porcupine Tree = 2455
    Radiohead      = 1918
    Mastodon       = 9186
    Godspeed       = 4979
    Sigur Rós      = 5526
"""
import argparse
import json
import os
import sys
import time

import bs4
import requests
from azure.identity import DefaultAzureCredential
from azure.storage.blob import BlobServiceClient

SPUTNIK_BASE = "http://sputnikmusic.com"
HEADERS = {"User-Agent": "Mozilla/5.0 (compatible; MusicRAGAgent/1.0)"}
RATE_LIMIT_SECONDS = 1.5  # Be polite to SputnikMusic


def get_artist(artist_id: str) -> dict | None:
    """
    Scrape artist page from SputnikMusic.
    Returns dict with genres, similar, description, and releases.
    """
    url = f"{SPUTNIK_BASE}/bands/a/{artist_id}"
    print(f"Fetching: {url}")

    try:
        req = requests.get(url, headers=HEADERS, timeout=15)
        req.raise_for_status()
    except requests.RequestException as e:
        print(f"Error fetching artist {artist_id}: {e}")
        return None

    soup = bs4.BeautifulSoup(req.text, "lxml")

    if soup.find("table", class_="bandbox") is None:
        print(f"Artist {artist_id} not found.")
        return None

    artist = {
        "artist_id": artist_id,
        "genres": _get_genres(soup),
        "similar": _get_similar(soup),
        "description": _get_description(soup),
        "releases": _get_releases(soup),
    }
    return artist


def _get_genres(soup: bs4.BeautifulSoup) -> list[str]:
    try:
        genres = soup.find("ul", class_="tags").contents
        return [g.get_text() for g in genres if hasattr(g, "get_text")]
    except AttributeError:
        return []


def _get_similar(soup: bs4.BeautifulSoup) -> list[str]:
    try:
        sims = soup.find("table", class_="bandbox").next_sibling.contents
        return [
            s.get_text() for s in sims
            if isinstance(s, bs4.element.Tag) and s.get_text() != "Similar Bands: "
        ]
    except (AttributeError, TypeError):
        return []


def _get_description(soup: bs4.BeautifulSoup) -> str:
    try:
        desc = soup.find(id="slidebox").get_text()
        return desc.replace(" « hide", " ").strip()
    except AttributeError:
        return ""


def _get_releases(soup: bs4.BeautifulSoup) -> list[dict]:
    releases = []
    headers = ["LPs", "EPs", "Compilations", "Live Albums"]

    try:
        release_table = soup.find("table", class_="plaincontentbox")
        if release_table is None:
            return releases

        album_row = release_table.contents[2]
        while album_row is not None:
            album_row = album_row.next_sibling
            album_row, albums = _get_albums(album_row, headers)
            releases.extend(albums)
    except (AttributeError, IndexError, TypeError):
        pass

    return releases


def _get_albums(album_row, headers: list[str]) -> tuple:
    albums = []
    while album_row is not None and album_row.get_text() not in headers:
        for i in range(1, 5, 3):
            try:
                album = album_row.contents[i]
            except IndexError:
                break
            try:
                release = {
                    "title": album.contents[0].find("a").get_text(),
                    "date": album.contents[2].get_text(),
                    "rating": album.contents[5].contents[0].find("td").contents[0].contents[0].get_text(),
                    "votes": album.contents[5].contents[0].find("td").contents[0].contents[2].get_text(),
                }
                albums.append(release)
            except (AttributeError, IndexError, TypeError):
                continue
        album_row = album_row.next_sibling
    return (album_row, albums)


def upload_to_blob(data: dict, artist_name: str) -> None:
    """
    Upload scraped artist data as JSON to Azure Blob Storage.
    Triggers BlobTriggerIngest function automatically.
    """
    storage_account = os.environ.get("AZURE_STORAGE_ACCOUNT_NAME")
    if not storage_account:
        raise ValueError("AZURE_STORAGE_ACCOUNT_NAME environment variable is required.")

    credential = DefaultAzureCredential()
    blob_service = BlobServiceClient(
        f"https://{storage_account}.blob.core.windows.net",
        credential=credential
    )

    container = blob_service.get_container_client("band-data")
    try:
        container.create_container()
    except Exception:
        pass  # Already exists

    artist_id = data["artist_id"]
    blob_name = f"{artist_id}.json"
    blob_content = json.dumps(data, ensure_ascii=False, indent=2)

    container.upload_blob(
        name=blob_name,
        data=blob_content.encode("utf-8"),
        overwrite=True
    )
    print(f"Uploaded {artist_name} ({len(data['releases'])} releases) → band-data/{blob_name}")


def main():
    parser = argparse.ArgumentParser(description="Ingest SputnikMusic band data into Music RAG Agent.")
    parser.add_argument("--artist-id", required=True, help="Sputnik artist ID (e.g. 6723 for Tool)")
    parser.add_argument("--artist-name", required=True, help="Artist name for logging")
    args = parser.parse_args()

    print(f"Ingesting: {args.artist_name} (ID: {args.artist_id})")

    artist_data = get_artist(args.artist_id)
    if artist_data is None:
        print(f"Failed to fetch artist {args.artist_id}.")
        sys.exit(1)

    artist_data["artist_name"] = args.artist_name

    print(f"Found {len(artist_data['releases'])} releases for {args.artist_name}.")
    for release in artist_data["releases"]:
        print(f"  {release['title']} ({release['date']}) — {release['rating']} ({release['votes']} votes)")

    time.sleep(RATE_LIMIT_SECONDS)

    upload_to_blob(artist_data, args.artist_name)
    print("Done. BlobTriggerIngest will index this shortly.")


if __name__ == "__main__":
    main()
