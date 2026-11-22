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

Status: `playnite.json` holds two cases as the format example (one derived from the recorded Portal 2 record, one
from the forward-compatibility probe). The `expect` field names follow the Playnite SDK property names but are
**not yet checked against the SDK**; X1 (Playnite package) does that and completes the file. The Jellyfin vectors
arrive with X2.
