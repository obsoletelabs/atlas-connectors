# Mapping vectors

A vector says: given this Atlas record (a file under `../fixtures/`), a connector must produce these host fields.
Each connector's tests load its vector file and compare. One file per connector: `<package>.json`.

```json
{
  "vector_version": 1,
  "connector": "playnite",
  "min_client_api": "1.0",
  "cases": [
    {"name": "...", "record": "games/record-portal.json", "expect": {"HostField": "value"}}
  ]
}
```

Rules (CONTRACT-C15 section 8.4): a day is never invented (partial dates stay partial), unknown vocabulary
values map to unknown/other, unknown fields are ignored.

`expect` lists host fields that must equal the given value; `expect_absent` lists fields that must be null or empty. Field names
are the Playnite SDK `GameMetadata` property names (checked against PlayniteSDK 6.18.0): `Name`, `Description`, `ReleaseDate`
(`Year` plus `Month` and `Day` only when present), `Developers`, `Publishers`, `Series`, `Genres`, `Tags` (lists of names), `Links`
(`Name` and `Url`). `base_url` is the Atlas address the connector is configured with when the case runs.
A `record` starting with `vectors/` is a synthetic record kept next to the vectors (`records/`, CC0); any other `record` is a
file under `../fixtures/`.

Status: `playnite.json` is complete for Playnite connector 0.1.0 (`packages/playnite`, which runs it). `jellyfin.json` is complete for
Jellyfin plugin 0.1.0 (`packages/jellyfin`, which runs it against the real `Book`, `Movie` and `Series` classes).

`jellyfin.json` cases add `kind` (`book`, `movie` or `series`: which Jellyfin provider maps the record). Field names are the Jellyfin `BaseItem`
properties: `Name`, `Overview`, `ProductionYear`, `PremiereDate` (`yyyy-MM-dd`, only at day precision), `Genres`, `Tags`, `Studios`,
`SeriesName` and `IndexNumber` (books), `Authors` (names of the people with kind Author), `ProviderIds` (a subset match: listed keys must be equal).
Synthetic records for movie, series, anime and book edge cases are in `records/`.
