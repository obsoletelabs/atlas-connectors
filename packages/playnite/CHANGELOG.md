# Changelog: Atlas for Playnite

Own version, SemVer. Tags: `playnite-v<version>`. `min_client_api` and the Playnite SDK range are in the README table.

## 0.1.0 - unreleased

First version.

- Metadata provider "Atlas": name, release date (year, year-month or full date, never a day Atlas does not have),
  developers, publishers, series, genres and tags, description, links, cover / background / icon.
- Identity first: Steam games are sent to Atlas by Steam app id; everything else by title, and a title match is only ever offered
  to the user (manual download), never applied by a background download.
- An "Atlas" link on the game stores the Atlas id; a merged id is followed and the link is updated.
- Settings view: Atlas address, pairing code (or paste a read token), connection test, offline cache days.
  The token is stored with Windows DPAPI (current user), not in clear text.
- Offline: a cached record is used for up to 7 days when Atlas cannot be reached.
- Reads only. Never writes to Atlas.
- client-api 1.0 (Atlas A24): genres from `genres`, images by `primary_by_role`, batch answers in the `results`/`status`/`game` shape,
  the pairing link is `verify_url_complete`.
- A candidate id that Atlas merged away is offered as its surviving record (once), instead of being dropped.
