# Atlas.Client: assumptions and open points

What the shared client assumes that Atlas has **not published yet**, so nobody mistakes a guess for the contract.
Replace each item when Atlas PR A24 ships `openapi-client-v1.json` and the generated fixtures.

## Provisional wire shapes (Atlas PR A24 not shipped)

| Item | What the client does | Source of the guess |
|---|---|---|
| `POST /client/pair` request | sends `{"label": "...", "client": "<hostName>"}`, no token | CONTRACT-C15 section 10.1 names only the endpoint and the answer `{code, verify_url, poll_token, expires_in}` |
| pair answer | reads `code` (or `user_code`), `verify_url`, `poll_token`, `expires_in`, optional `interval` | same |
| `POST /client/pair/poll` request | sends `{"poll_token": "..."}` | guess |
| poll answer | `status` in `pending | slow_down | approved | denied | expired` plus `token` (or `access_token`) when approved; also understands RFC 8628 error codes (`authorization_pending`, `slow_down`, `access_denied`, `expired_token`) in an error envelope, and a 429 as `slow_down`; an unknown status keeps waiting | OAuth device grant, which section 10.1 says Atlas follows "without OAuth" |
| `POST /games/batch` | request `{"ids": [...]}` (200 max); reads `results` (or `items`) | section 10.1 gives `{ids <= 200}` and "records with redirects followed" only |
| `client_api` in `/site` | `{version, min_supported, capabilities[], deprecations[]}` | section 9.1 (exact JSON given) |
| artwork `primary` | accepts today's `{role: {artwork object}}` and A24's `{role: art_id}` | section 10.1 vs the current route |
| list key | search and artwork read `results`, falling back to `items` | games fixtures use `results`; the hand-written book drafts use `items` |
| `If-None-Match` | sent; a 304 means "keep your copy"; an Atlas without A24 simply ignores it and returns the record | section 10.1 |

The pairing and batch tests pin these shapes, so a mismatch shows up as a failing test when the real shapes land.

## Not implemented here (belongs to the packages)

- Response cache with a TTL and the offline fallback (CONTRACT-C15 section 8.4): the client offers the ETag and the
  `redirected_from` signal, the package owns storage.
- `wire_omitted` notices (C16 section 10.1): not modelled yet; the tolerant reader keeps the member in `Raw` of any record.
- Writing the Atlas id back to the host (`ProviderIds["Atlas"]`, a Playnite link): host API.
- Token storage (DPAPI, plugin config): the client only calls the `TokenProvider` callback.

## Known limits

- `AtlasClient` follows no redirect itself on API calls and sets `AllowAutoRedirect = false` on an `HttpClientHandler` it is
  given. A package that passes its own non-`HttpClientHandler` handler must make sure that handler does not auto-redirect.
- JSON bodies are capped at 64 MiB and images at `MaxImageBytes` (default 20 MiB).
- The tests run on `net10.0`. Running them on .NET Framework 4.6.2 (the Playnite runtime) is **not** done; the `net462`
  project only proves the sources compile there.
