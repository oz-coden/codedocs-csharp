"""Validate real CLI snapshots without depending on compiler or renderer internals."""

import hashlib
import json
import re
import sys
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import unquote, urlsplit


class Page(HTMLParser):
    def __init__(self, text):
        super().__init__(convert_charrefs=True)
        self.ids = set()
        self.links = []
        self.text = []
        self.feed(text)

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if "id" in attrs:
            assert attrs["id"] not in self.ids, f"Duplicate HTML id: {attrs['id']}"
            self.ids.add(attrs["id"])
        if tag == "a" and "href" in attrs:
            self.links.append(attrs["href"])

    def handle_data(self, data):
        self.text.append(data)


def validate(root):
    model = json.loads((root / "json/api.json").read_text(encoding="utf-8"))
    assert model["state"] == "Success", model["state"]
    assert len(model["projects"]) == 5, "Expected all solution projects"
    types = model["types"]
    assert types, "No types exported"
    expected = {"SolutionDocument", "WorkspaceLoader", "SiteRenderer", "BuildOptions"}
    assert expected <= {t["name"] for t in types}, "Expected public types missing"
    symbols = [s for t in types for s in [t, *t["members"]]]
    ids = [s["id"] for s in symbols]
    anchors = [s["anchor"] for s in symbols]
    assert len(ids) == len(set(ids)), "Symbol IDs collided"
    assert len(anchors) == len(set(anchors)), "Symbol anchors collided"
    pages = {p.resolve(): Page(p.read_text(encoding="utf-8")) for p in (root / "html").rglob("*.html")}
    links = 0
    for path, page in pages.items():
        for url in page.links:
            parsed = urlsplit(url)
            if parsed.scheme or parsed.netloc:
                continue
            target = (path.parent / unquote(parsed.path)).resolve() if parsed.path else path
            assert target.is_file(), f"Missing target {url} from {path}"
            if parsed.fragment and target in pages:
                assert parsed.fragment in pages[target].ids, f"Missing anchor {url} from {path}"
            links += 1
    assert len(list((root / "html/types").glob("*.html"))) == len(types), "Type page count mismatch"
    for t in types:
        path = (root / "html/types" / (t["anchor"] + ".html")).resolve()
        assert t["anchor"] in pages[path].ids, f"Missing type anchor for {t['name']}"
        for member in t["members"]:
            assert member["anchor"] in pages[path].ids, f"Missing member detail {member['id']}"
    search = json.loads((root / "html/search-index.json").read_text(encoding="utf-8"))
    expected_entries = len(model["namespaces"]) + len(symbols)
    assert len(search) == expected_entries, "Search index missing symbols"
    for entry in search:
        parsed = urlsplit(entry["url"])
        target = (root / "html" / parsed.path).resolve()
        assert target in pages and (not parsed.fragment or parsed.fragment in pages[target].ids), entry
    markdown_files = list((root / "markdown").rglob("*.md"))
    for path in markdown_files:
        content = path.read_text(encoding="utf-8")
        # Check the generator's explicit symbol links, excluding documentation/code text.
        for target_file, anchor in re.findall(r"\]\(([^)\n]*?)#(s-[0-9a-f]{64})\)", content):
            target = (path.parent / target_file).resolve() if target_file else path
            assert target.is_file(), f"Missing Markdown target {target}"
            assert f'id="{anchor}"' in target.read_text(encoding="utf-8"), f"Missing Markdown anchor {anchor}"
    single = root / "markdown/api.md"
    if single.exists():
        text = single.read_text(encoding="utf-8")
        for anchor in anchors:
            assert f'id="{anchor}"' in text, f"Missing single-file symbol {anchor}"
    else:
        assert (root / "markdown/README.md").is_file()
        assert len(list((root / "markdown/types").glob("*.md"))) == len(types)
    result = {
        "projects": len(model["projects"]), "namespaces": len(model["namespaces"]),
        "types": len(types), "members": len(symbols) - len(types), "html_pages": len(pages),
        "html_links_checked": links, "search_entries": len(search),
        "markdown_files": len(markdown_files),
        "markdown_bytes": sum(p.stat().st_size for p in markdown_files),
    }
    print(root.name + ": " + json.dumps(result, sort_keys=True))
    return result


def hashes(root):
    return {p.relative_to(root).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest()
            for p in root.rglob("*") if p.is_file()}


def main():
    first, repeated, public = map(Path, sys.argv[1:])
    all_result = validate(first)
    public_result = validate(public)
    assert all_result["types"] > public_result["types"], "Visibility did not filter private/internal types"
    assert all_result["members"] > public_result["members"], "Visibility did not filter members"
    a, b = hashes(first), hashes(repeated)
    assert a == b, "Repeated generation changed output: " + str(sorted(k for k in a.keys() | b.keys() if a.get(k) != b.get(k)))
    print(f"Determinism: {len(a)} files are byte-identical across two independent generations.")
    print("PASS: real solution snapshots, internal links, search coverage, visibility and determinism.")


if __name__ == "__main__":
    main()
