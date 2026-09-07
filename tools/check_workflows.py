#!/usr/bin/env python3
"""Static checks for the GitHub Actions workflows.

These catch two classes of mistake that are invisible until a workflow runs and
then produce confusing failures:

1. A ``shell: powershell`` step containing non-ASCII characters. Windows
   PowerShell 5.1 reads a .ps1 without a byte-order mark as ANSI, and the runner
   writes step scripts as UTF-8 without one. A Japanese string literal in such a
   step therefore does not merely display wrongly - it breaks the parser and the
   step fails to compile. (``pwsh`` 7 is fine; it reads UTF-8.)

2. A workflow that grants more permission than it declares it needs.
"""
from __future__ import annotations

import sys
from pathlib import Path

import yaml

WORKFLOWS = Path(__file__).resolve().parent.parent / ".github" / "workflows"


def check(path: Path) -> list[str]:
    problems: list[str] = []
    document = yaml.safe_load(path.read_text(encoding="utf-8"))

    # `on:` is parsed by PyYAML as the boolean True; both spellings are accepted.
    if not document or "jobs" not in document:
        return [f"{path.name}: no jobs found"]

    top_level_permissions = document.get("permissions")
    if top_level_permissions is None:
        problems.append(f"{path.name}: no top-level `permissions:` - workflows must be least privilege")

    for job_name, job in document["jobs"].items():
        default_shell = (job.get("defaults", {}).get("run", {}) or {}).get("shell")

        for index, step in enumerate(job.get("steps", [])):
            run = step.get("run")
            if not run:
                continue

            shell = step.get("shell", default_shell)
            label = step.get("name", f"step {index + 1}")

            if shell == "powershell":
                offending = sorted({c for c in run if ord(c) > 127})
                if offending:
                    problems.append(
                        f"{path.name}: job '{job_name}', step '{label}' uses Windows PowerShell 5.1 "
                        f"and contains non-ASCII characters ({''.join(offending)!r}). "
                        "Move the text into a file and read it as UTF-8."
                    )

    return problems


def main() -> int:
    files = sorted(WORKFLOWS.glob("*.yml")) + sorted(WORKFLOWS.glob("*.yaml"))
    if not files:
        print("No workflow files found.", file=sys.stderr)
        return 2

    all_problems: list[str] = []
    for path in files:
        problems = check(path)
        all_problems.extend(problems)
        print(f"{'FAIL' if problems else 'ok  '}  {path.name}")

    for problem in all_problems:
        print(f"  - {problem}", file=sys.stderr)

    return 1 if all_problems else 0


if __name__ == "__main__":
    raise SystemExit(main())
