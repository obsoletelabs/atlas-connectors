# catalogues/

Generated output, never edited by hand: the Jellyfin repository `manifest.json` (GitHub Pages) and the Playnite
add-on database entries, written by `tools/gen_catalogues.py` from release metadata with checksums computed from the
built artefacts. `tools/gen_catalogues.py jellyfin` writes `jellyfin/manifest.json` from the built zips (PR X2); `tools/gen_catalogues.py verify` and `tests/test_jellyfin_package.py` fail when a zip and its catalogue entry disagree. The Playnite entries are not generated: add-on submissions are on hold (`docs/VENDOR-FACTS.md`). Nothing generated is committed here; the release workflow publishes the Jellyfin manifest to GitHub Pages.
