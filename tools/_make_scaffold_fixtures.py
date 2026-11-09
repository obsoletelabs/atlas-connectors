"""One-off generator for the derived/hand-written scaffold fixtures (kept so their origin is reproducible).

Writes contract/fixtures/forward-compat/*.json (derived from the recorded Portal 2 record) and
contract/fixtures/books/*.json (hand-written drafts, labelled with a ``_status`` member). Replace both with
Atlas-generated recordings once Atlas PRs A21-A24 exist; then delete this script.
"""
import json
from pathlib import Path

base = Path(__file__).resolve().parents[1] / "contract" / "fixtures"


def dump(path: Path, data) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, indent=2, sort_keys=True) + "\n", encoding="utf-8", newline="\n")


rec = json.loads((base / "games" / "record-portal.json").read_text(encoding="utf-8"))
rec.update({
    "id": "atlas:00000000000000000000000000",
    "title": "Forward Compatibility Probe",
    "provider_ids": {"synthetic": "fc-1"},
    "a_field_added_in_a_later_minor": {"nested": [1, 2, 3]},
    "platform_family": "holographic",  # unknown closed-vocabulary value: clients map it to unknown/other
})
rec.pop("sources", None)
dump(base / "forward-compat" / "record-unknown-members.json", rec)

STATUS = ("HAND-WRITTEN DRAFT of the book view in CONTRACT-C15 sections 2-6. Replace with an Atlas-generated "
          "recording (Atlas PRs A21-A24). Synthetic, CC0.")
book = {
    "_status": STATUS,
    "id": "atlas:00000000000000000000000001", "media_type": "book", "title": "The Synthetic Lighthouse",
    "authors": [{"name": "Ada Example"}, {"name": "B. Placeholder"}],
    "provider_ids": {"isbn": "9780000000002", "openlibrary": "OL0000000W"},
    "release_date": None, "release_year": 1999, "first_release": {"date": "1999", "precision": "year"},
    "series": {"name": "The Fictional Coast", "position": "2"},
    "editions": [{"name": "Hardcover", "provider_ids": {"isbn": "9780000000002"}},
                 {"name": "Paperback", "provider_ids": {"isbn": "9780000000019"}}],
    "subjects": ["Fiction", "Lighthouses"],
    "achievements": {"applicable": False},
    "redirected_from": None, "version": 1, "unknown_future_member": True,
}
dump(base / "books" / "record-synthetic-lighthouse.json", book)
dump(base / "books" / "search-lighthouse.json", {
    "_status": STATUS, "total": 1, "limit": 20, "offset": 0,
    "items": [{"id": book["id"], "media_type": "book", "title": book["title"], "release_year": 1999}]})
