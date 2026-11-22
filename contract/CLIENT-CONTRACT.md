# Atlas client contract: client-api v1

Status: **draft for client-api 1.0**. It becomes binding when Atlas ships `client_api` in `/api/v2/site` (Atlas PR
A24, CONTRACT-C15 section 14.1). Until then every statement marked *(A24)* describes an Atlas feature that does not
exist yet, and connectors must not depend on it.

This document is the only thing a connector may rely on. A connector talks HTTP to the **`client`-tagged subset of
Atlas `/api/v2`**, copies no Atlas code, and is tested against `contract/fixtures` and a real Atlas build.

Source of truth: CONTRACT-C15 sections 8.4, 9 and 10 (Atlas programme notes). Where this file and the generated
`openapi-client-v1.json` disagree, the OpenAPI file wins for shapes and this file wins for behaviour.

## 1. Versioning

- `client-api` is versioned separately from the federation feed protocol. It starts at `1`.
- Atlas advertises it in the public `GET /api/v2/site` *(A24)*:

  ```json
  "client_api": {"version": "1.0", "min_supported": "1.0",
                 "capabilities": ["books", "pairing", "etag", "batch"], "deprecations": []}
  ```

- A connector reads `/site` once per session. It refuses, with a clear message, an Atlas whose `client_api.version`
  **major** differs from the one it was built for, or whose `capabilities` lack one it requires. A connector declares
  `min_client_api` and its host range (Playnite SDK version, Jellyfin `targetAbi`) in its own README table.
- `/site` already returns `"contract": 1` today. That is the general `/api/v2` marker and stays `1`. Connectors read
  only `client_api.version`; the two never change together by implication.
- Contract releases are tagged `client-contract-v<major>.<minor>` in this repository.

## 2. Guarantees (client-api v1)

1. **Additive only within v1.** New fields, endpoints and query parameters may appear at any time. Clients **must
   ignore unknown fields** (fixture: `fixtures/forward-compat/record-unknown-members.json`).
2. **Closed vocabularies may grow.** A client maps an unknown value to "unknown/other" and never fails (same fixture:
   `platform_family`).
3. **Ids are permanent.** An Atlas id never changes meaning. A merged id keeps resolving, and the response says so in
   `redirected_from`. A connector silently updates the Atlas id it stored.
4. **Breaking changes need `/api/v3`.** `/api/v2` stays served for at least 12 months after v3 ships. Deprecated
   endpoints send `Deprecation` and `Sunset` headers and a `Link` to the replacement.
5. **Errors and retry** follow Atlas `docs/api-contract.md`. `429` carries `Retry-After`, which a client honours.
6. **Enforced in Atlas CI.** Atlas generates `openapi-client-v1.json` from the routes tagged `client`, and a diff test
   fails when a change removes or narrows anything in it. This repository pins that file by sha256
   (`contract/SHA256SUMS`, `tools/contract_pin.py`).

## 3. Authentication

- Bearer token with scope `read`: `Authorization: Bearer <token>`. Obtained by an admin (`POST /tokens`), by a signed-in
  user (`/me/tokens`, capped to `read`, at most 10 active) or by **pairing** *(A24)*:
  `POST /api/v2/client/pair` then `POST /api/v2/client/pair/poll`. The poll answers `slow_down` if called faster than
  every 5 s.
- Optional anonymous read (`public_read`, default off) *(A24)*.
- **Never put a token in a URL.** Never write to Atlas: connectors use read scope only.
- Identify yourself: `User-Agent: atlas-connector-<host>/<version>` with `<host>` one of
  `playnite|jellyfin|emby|plex`.
- Store the token protected at rest: Playnite uses Windows DPAPI (current user); Jellyfin uses the plugin configuration
  (admin-readable only) and its config page never echoes the token back.

## 4. Endpoints a connector may use

"Today" means verified against a real Atlas build on 2026-11-09 (commit `1142dc7`, see `fixtures/games`).

| Need | Endpoint | Today | Notes |
|---|---|---|---|
| Discover the node | `GET /api/v2/site` (public) | yes | `media_types`, `contract: 1`, `node_id`. `client_api` block *(A24)* |
| Search | `GET /api/v2/games?q&type&released_from&released_to&sort&limit<=100&offset` | yes | `author=` and ISBN detection for books *(books PRs)* |
| One record | `GET /api/v2/games/{id}` | yes | Follows merge redirects; sends an `ETag`. `If-None-Match` answering `304` *(A24)* |
| Map host ids to Atlas | `POST /api/v2/resolve` | yes | See section 5 |
| Many records | `POST /api/v2/games/batch {ids<=200}` | no *(A24)* | Capability `batch` |
| Relations, series | `GET /api/v2/games/{id}/relations` | programme branch | `position` for series order *(books PRs)* |
| Artwork list | `GET /api/v2/games/{id}/artwork` | yes | `primary` per role *(A24)* |
| Artwork bytes | `GET /api/v2/artwork/{id}/image` (read scope), `GET /media/{digest}` (public, immutable) | yes | See section 6 |
| Achievements | `GET /api/v2/games/{id}/achievements` | yes | `applicable` flag *(A24)* |
| Incremental refresh | `GET /api/v2/changes?feed=local` (epoch and cursor) | yes | Optional for connectors |
| Alias | `/api/v2/records` = `/games` | no *(A24)* | `/games` is kept for good |

Every media type is served under `/games` today. That is intentional, not a deprecation.

## 5. Identity first (`POST /resolve`)

- Send whatever ids the host already knows as `provider_ids` (Playnite: the library plugin's game id such as a Steam app
  id or GOG id; Jellyfin: `Isbn`, `Tmdb`, `Imdb`, `Tvdb`, `AniList`). Batch at most **200** items per request. Send
  `include_view: true` to receive the record with the answer.
- A title is evidence only when no id matches, and the answer is then a **candidate list for the user, never an
  automatic match**.
- **A client may auto-assign only the identity reasons `id`, `redirect` and `crosswalk`.** Every other reason
  (`exact`, which Atlas uses for an exact *title* match, `conflict`, fuzzy matches, `none`) is shown to the user or
  ignored. The answer's `assigned` flag says whether Atlas itself assigned the result (it does for `exact`); a
  client needs both an identity reason and `assigned` not `false`.
- Write the Atlas id back to the host: `ProviderIds["Atlas"]` in Jellyfin; a link plus hidden field in Playnite.
- The cases are in `fixtures/resolve/resolve-contract-v1.json` with their seeds. `tests/live/` replays all of them
  against a real Atlas.

## 6. Dates, images, failure

- **Dates** are partial (`first_release.precision` is `year`, `month` or `day`). Playnite maps to
  `ReleaseDate(year[, month[, day]])`. Jellyfin sets `PremiereDate` only at day precision, otherwise `ProductionYear`
  only. **A day is never invented.**
- **Images.** `/artwork/{id}/image` needs the read token and, for a provider row that is not cached yet, answers **307
  to the provider's host**. A connector therefore:
  1. prefers `media_url` (`/media/{digest}`) when the artwork list gives one;
  2. otherwise disables automatic redirects, follows a 307/308 **at most once, without `Authorization`**, only to
     `https`, only for an `image/*` answer within a size cap;
  3. downloads the bytes itself (Playnite `MetadataFile(name, bytes)`; Jellyfin `GetImageResponse`).

  A contract test with a fixture 307 to a foreign host asserts that no `Authorization` header reaches it.
- **Offline.** Serve a cached response when Atlas is unreachable (default TTL 7 days). `429` honours `Retry-After`. A
  `redirected_from` silently updates the stored Atlas id.

## 7. Fixtures and vectors

- `fixtures/` holds synthetic CC0 data. The final set is generated by Atlas (`export_client_fixtures.py`, A24) and
  pinned in `fixtures/VERSION`; the scaffold set is described in `fixtures/README.md`, including which files are
  hand-written stand-ins.
- `vectors/<package>.json` states, per connector, which host fields an Atlas record must produce.
- Each connector's test run must (a) load every fixture and ignore unknown members, (b) pass the vectors, (c) pass the
  no-`Authorization`-on-redirect test.
