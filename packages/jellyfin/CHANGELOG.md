# Changelog: Atlas for Jellyfin

Own version, SemVer. Tags: `jellyfin-v<version>`. The manifest version is `<semver>.<n>` with n = 0 for the 10.11 build and 1 for the 12 build
(`abis.json`). `min_client_api` and the Jellyfin ABI range are in the README table.

## 0.1.0 - unreleased

First version.

- Metadata providers "Atlas" for books, movies and series (`IRemoteMetadataProvider`), with search for the Identify dialog.
- Image provider for Primary, Backdrop and Logo images (`IRemoteImageProvider`); the token is only ever in a header.
- External id "Atlas" (`IExternalId`) on books, movies and series. Its link opens the record page on the configured Atlas node (`IExternalUrlProvider`).
- Dates: `PremiereDate` only at day precision, `ProductionYear` otherwise. A day is never invented.
- Identity first: Isbn, Tmdb, Imdb, Tvdb and AniList ids go to Atlas before any title. A title alone never applies a record, including an
  `exact` title answer to an id lookup (offered in Identify only).
- Settings page: Atlas address, pairing code (or paste a read token), connection test, cache days, per-type switches. The token is stored outside
  the plugin configuration XML and is never shown again.
- Offline: a cached record is used for up to 7 days when Atlas cannot be reached.
- Builds for Jellyfin 10.11 (`net9.0`, targetAbi 10.11.0.0) and Jellyfin 12 (`net10.0`, targetAbi 12.0.0.0).
- Reads only. Never writes to Atlas.
- client-api 1.0 (Atlas A24): the ISBN comes from `provider_ids.isbn` or the first edition's `isbn_13`; only creators with the role `author`
  are authors (translators, illustrators and editors get their own person kinds, other roles are kept as Unknown); the series comes from
  the record's relations; a book's `developer` (its author) is no longer shown as a studio; images use `primary_by_role`; the pairing link
  is `verify_url_complete`.
