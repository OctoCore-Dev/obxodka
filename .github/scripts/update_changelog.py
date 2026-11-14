#!/usr/bin/env python3
"""Автоматическая генерация и обновление CHANGELOG.md на основе Conventional Commits."""

from __future__ import annotations

import argparse
import datetime as dt
import re
import subprocess
import sys
from pathlib import Path

CHANGELOG_MARKER = "<!-- CHANGELOG:START -->"

COMMIT_RE = re.compile(r"^(?P<type>[a-zA-Z]+)(?:\((?P<scope>[^)]+)\))?(?:!)?: (?P<desc>.+)$")
PR_SUFFIX_RE = re.compile(r"\s*\(#\d+\)$")

NOISE_SCOPES = {
    ("chore", "badges"),
    ("chore", "release"),
    ("chore", "changelog"),
    ("chore", "assets"),
}

SECTIONS = [
    ("feat", "### ✨ Новое"),
    ("fix", "### 🐛 Исправления"),
    ("perf", "### ⚡ Производительность"),
    ("refactor", "### ♻️ Архитектура и рефакторинг"),
    ("docs", "### 📚 Документация"),
    ("test", "### 🧪 Тесты"),
    ("infra", "### ⚙️ Инфраструктура и зависимости"),
    ("other", "### 🔧 Прочие изменения"),
]

TYPE_TO_BUCKET = {
    "feat": "feat",
    "fix": "fix",
    "perf": "perf",
    "refactor": "refactor",
    "docs": "docs",
    "test": "test",
    "chore": "infra",
    "ci": "infra",
    "build": "infra",
    "style": "infra",
}


def run_git(args: list[str]) -> str:
    res = subprocess.run(["git", *args], capture_output=True, text=True, check=False)
    if res.returncode != 0:
        return ""
    return res.stdout.strip()


def find_previous_stable_tag(current_tag: str, to_ref: str = "HEAD") -> str | None:
    raw = run_git(["tag", "--merged", to_ref, "--sort=-creatordate", "--list", "v*"])
    for line in raw.splitlines():
        tag = line.strip()
        if not tag or tag == current_tag or "-build." in tag:
            continue
        return tag
    return None


def collect_commits(rev_range: str) -> list[tuple[str, str]]:
    out = run_git(["log", "--no-merges", "--pretty=format:%h%x09%s", rev_range])
    commits: list[tuple[str, str]] = []
    for line in out.splitlines():
        if "\t" not in line:
            continue
        sha, subject = line.split("\t", 1)
        commits.append((sha.strip(), subject.strip()))
    return commits


def build_section(
    tag: str,
    version: str,
    build_number: str,
    date_str: str,
    from_tag: str | None,
    to_ref: str,
) -> str:
    rev_range = f"{from_tag}..{to_ref}" if from_tag else f"{to_ref}~40..{to_ref}"
    commits = collect_commits(rev_range)
    if not commits and from_tag:
        commits = collect_commits(to_ref)

    buckets: dict[str, list[str]] = {key: [] for key, _ in SECTIONS}
    seen_descriptions: set[str] = set()

    for sha, subject in commits:
        if "[skip ci]" in subject.lower() or subject.startswith("Merge "):
            continue

        match = COMMIT_RE.match(subject)
        if match:
            ctype = match.group("type").lower()
            scope = (match.group("scope") or "").strip().lower()
            desc = match.group("desc").strip()
            if (ctype, scope) in NOISE_SCOPES:
                continue
            bucket = TYPE_TO_BUCKET.get(ctype, "other")
            prefix = f"**{scope}:** " if scope else ""
            norm_key = PR_SUFFIX_RE.sub("", desc).strip().lower()
            if norm_key in seen_descriptions:
                continue
            seen_descriptions.add(norm_key)
            buckets[bucket].append(f"- {prefix}{desc} (`{sha}`)")
        else:
            norm_key = subject.lower()
            if norm_key in seen_descriptions:
                continue
            seen_descriptions.add(norm_key)
            buckets["other"].append(f"- {subject} (`{sha}`)")

    lines: list[str] = []
    title_label = tag if tag.startswith("v") else f"v{version}"
    build_suffix = f" (Build {build_number})" if build_number else ""
    lines.append(f"## [{title_label}] - {date_str}{build_suffix}")
    lines.append("")

    has_entries = False
    for key, heading in SECTIONS:
        items = buckets[key]
        if not items:
            continue
        has_entries = True
        lines.append(heading)
        lines.extend(items)
        lines.append("")

    if not has_entries:
        lines.append("- Технические и релизные изменения без отдельных пользовательских правок.")
        lines.append("")

    return "\n".join(lines).rstrip() + "\n"


def update_changelog_file(changelog_path: Path, tag: str, version: str, section_md: str) -> bool:
    header = (
        "# Changelog\n\n"
        "История всех релизов **Obxodka VPN** формируется автоматически на базе Conventional Commits при выпуске релиза.\n"
        "Формат основан на [Keep a Changelog](https://keepachangelog.com/ru/1.1.0/), версионирование — [SemVer](https://semver.org/).\n\n"
        f"{CHANGELOG_MARKER}\n\n"
    )

    if changelog_path.exists():
        content = changelog_path.read_text(encoding="utf-8")
    else:
        content = header

    tag_token = tag if tag.startswith("v") else f"v{version}"
    if f"## [{tag_token}]" in content or f"## [{version}]" in content:
        return False

    if CHANGELOG_MARKER in content:
        before, after = content.split(CHANGELOG_MARKER, 1)
        new_content = before.rstrip() + "\n\n" + CHANGELOG_MARKER + "\n\n" + section_md.strip() + "\n\n---\n\n" + after.lstrip()
    else:
        new_content = header + section_md.strip() + "\n\n---\n\n" + content.lstrip()

    new_content = re.sub(r"\n{3,}", "\n\n", new_content).rstrip() + "\n"
    changelog_path.write_text(new_content, encoding="utf-8")
    return True


def main() -> int:
    parser = argparse.ArgumentParser(description="Generate or update CHANGELOG.md from git commits.")
    parser.add_argument("--version", required=True, help="AppVersion, e.g. 6.2.0")
    parser.add_argument("--build", default="", help="AppBuildNumber, e.g. 93")
    parser.add_argument("--tag", required=True, help="Release tag, e.g. v6.2.0")
    parser.add_argument("--from-tag", default="", help="Explicit previous stable tag")
    parser.add_argument("--to-ref", default="HEAD", help="Target git ref (default: HEAD)")
    parser.add_argument("--date", default=dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%d"))
    parser.add_argument("--changelog", default="CHANGELOG.md", help="Path to CHANGELOG.md")
    parser.add_argument("--stdout-only", action="store_true", help="Print section without modifying CHANGELOG.md")
    args = parser.parse_args()

    from_tag = args.from_tag or find_previous_stable_tag(args.tag, args.to_ref)
    section_md = build_section(
        tag=args.tag,
        version=args.version,
        build_number=args.build,
        date_str=args.date,
        from_tag=from_tag,
        to_ref=args.to_ref,
    )

    if args.stdout_only:
        sys.stdout.write(section_md)
        return 0

    changed = update_changelog_file(Path(args.changelog), args.tag, args.version, section_md)
    print(f"CHANGELOG.md {'updated' if changed else 'already contains this version'}: {args.tag}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
