#!/usr/bin/env python3
"""Character Error Rate (CER) for the Japanese transcription tests.

Japanese has no word boundaries, so the field test scores transcription with
CER rather than WER:

    CER = (substitutions + deletions + insertions) / reference characters

The procedure is docs/FIELD_TEST_JA.md. This script exists so that the numbers
in a test report come from one documented computation instead of from three
people counting by hand and disagreeing.

Normalisation (section 8 of the procedure) is deliberately small. It removes
only differences that are not transcription errors:

    * Unicode NFKC (this also unifies half-width and full-width forms)
    * every whitespace character, including newlines
    * the punctuation listed in PUNCTUATION

It does NOT fold kana against kanji, does NOT rewrite numbers, and does NOT
touch proper nouns: those differences are errors and must be counted as such.
Use ``substitutions`` output to write the notes about mere orthographic
variation that the procedure asks for.

Usage:
    python3 tools/cer.py score --ref J01.ref.txt --hyp J01.small.txt
    python3 tools/cer.py score --ref a.txt --hyp b.txt --json
    python3 tools/cer.py score --ref a.txt --hyp b.txt --exact
    python3 tools/cer.py batch pairs.tsv          # id<TAB>ref<TAB>hyp per line
    python3 tools/cer.py normalize --in a.txt     # inspect what is compared
    python3 tools/cer.py selftest

It is a measurement tool. It is not part of the build and nothing it produces
is shipped to users.
"""
from __future__ import annotations

import argparse
import json
import random
import sys
import unicodedata
from collections import Counter
from difflib import SequenceMatcher
from pathlib import Path

#: Removed before scoring. Punctuation is a formatting choice of the decoder,
#: not something a listener said, and whisper's punctuation depends on the
#: prompt. The long vowel mark U+30FC and the repetition mark U+3005 are NOT in
#: this set: they are part of the word.
PUNCTUATION = frozenset(
    "、。，．,.・…‥!?！？:：;；"
    "「」『』（）()〈〉《》【】〔〕［］[]｛｝{}"
    "“”‘’\"'`"
    "―‐-–—〜~｜|/\\"
)

#: Once the band is this wide the comparison is slow enough to be worth a
#: word on stderr rather than a silent pause.
SLOW_BAND = 2000


def _make_output_robust() -> None:
    """Never let an encoding fault hide a measurement.

    These scripts print Japanese labels. A Windows console whose code page
    cannot represent them would otherwise raise UnicodeEncodeError midway
    through a report - losing the numbers over a question of glyphs. Replacing
    the unprintable characters keeps the numbers. For readable Japanese on
    Windows, run `chcp 65001` and set PYTHONIOENCODING=utf-8 first.
    """
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(errors="replace")


def normalize(text: str) -> str:
    """Applies section 8's normalisation and returns the string to be scored."""
    text = unicodedata.normalize("NFKC", text)
    return "".join(ch for ch in text if not ch.isspace() and ch not in PUNCTUATION)


class Alignment:
    """Counts of the three edit operations, plus the substitutions seen."""

    def __init__(self) -> None:
        self.substitutions = 0
        self.deletions = 0
        self.insertions = 0
        self.pairs: Counter[tuple[str, str]] = Counter()

    @property
    def distance(self) -> int:
        return self.substitutions + self.deletions + self.insertions

    def add(self, other: "Alignment") -> None:
        self.substitutions += other.substitutions
        self.deletions += other.deletions
        self.insertions += other.insertions
        self.pairs.update(other.pairs)


def _dp_align(ref: str, hyp: str) -> Alignment:
    """Exact Levenshtein alignment of two strings, in linear memory.

    Each cell carries the operation counts of one optimal path, so the totals
    are consistent with the distance rather than recovered by a second pass.
    Ties are broken substitution first, which keeps the reported operations
    close to what a reader sees when comparing the two texts.
    """
    result = Alignment()
    if not ref and not hyp:
        return result
    if not ref:
        result.insertions = len(hyp)
        return result
    if not hyp:
        result.deletions = len(ref)
        return result

    # row[j] = (distance, substitutions, deletions, insertions) for ref[:i] vs hyp[:j]
    row = [(j, 0, 0, j) for j in range(len(hyp) + 1)]
    for i, r in enumerate(ref, start=1):
        previous = row
        row = [(i, 0, i, 0)] + [(0, 0, 0, 0)] * len(hyp)
        for j, h in enumerate(hyp, start=1):
            if r == h:
                row[j] = previous[j - 1]
                continue
            sub = previous[j - 1]
            dele = previous[j]
            ins = row[j - 1]
            best = min(sub[0], dele[0], ins[0]) + 1
            if sub[0] + 1 == best:
                row[j] = (best, sub[1] + 1, sub[2], sub[3])
            elif dele[0] + 1 == best:
                row[j] = (best, dele[1], dele[2] + 1, dele[3])
            else:
                row[j] = (best, ins[1], ins[2], ins[3] + 1)

    _, subs, dels, ins = row[len(hyp)]
    result.substitutions = subs
    result.deletions = dels
    result.insertions = ins
    return result


def _banded_align(ref: str, hyp: str, k: int) -> Alignment | None:
    """Exact alignment restricted to a band of width ``k`` around the diagonal.

    Ukkonen's observation: if the true edit distance is at most ``k``, an
    optimal path never leaves that band, so the banded matrix gives the exact
    answer. Returns None when the distance turns out to exceed ``k``, which is
    the caller's signal to widen the band and try again. This is what makes a
    one-hour transcript scoreable in pure Python: the work is O(n*k) instead of
    O(n*m), and k is the number of errors, not the length of the meeting.
    """
    n, m = len(ref), len(hyp)
    if abs(n - m) > k:
        return None

    infinite = (1 << 30, 0, 0, 0)
    lo_prev, hi_prev = 0, min(m, k)
    previous = [(j, 0, 0, j) for j in range(lo_prev, hi_prev + 1)]

    for i in range(1, n + 1):
        lo, hi = max(0, i - k), min(m, i + k)
        current = [infinite] * (hi - lo + 1)
        r = ref[i - 1]
        for j in range(lo, hi + 1):
            if j == 0:
                current[0] = (i, 0, i, 0)
                continue
            best = infinite
            # Substitution (or a free match) wins ties: of two paths with the
            # same cost, the one that keeps the characters side by side is the
            # one a human reading the diff would draw.
            if lo_prev <= j - 1 <= hi_prev:
                cell = previous[j - 1 - lo_prev]
                candidate = cell if r == hyp[j - 1] else (cell[0] + 1, cell[1] + 1, cell[2], cell[3])
                if candidate[0] < best[0]:
                    best = candidate
            if lo_prev <= j <= hi_prev:
                cell = previous[j - lo_prev]
                candidate = (cell[0] + 1, cell[1], cell[2] + 1, cell[3])
                if candidate[0] < best[0]:
                    best = candidate
            if j - 1 >= lo:
                cell = current[j - 1 - lo]
                candidate = (cell[0] + 1, cell[1], cell[2], cell[3] + 1)
                if candidate[0] < best[0]:
                    best = candidate
            current[j - lo] = best
        previous, lo_prev, hi_prev = current, lo, hi

    if not lo_prev <= m <= hi_prev:
        return None
    distance, subs, dels, ins = previous[m - lo_prev]
    if distance > k:
        return None

    result = Alignment()
    result.substitutions = subs
    result.deletions = dels
    result.insertions = ins
    return result


def _edit_script_cost(ref: str, hyp: str) -> int:
    """Cost of the edit script difflib finds, which bounds the true distance.

    difflib does not minimise edit distance, so this is an upper bound and
    never an answer. It is used to size the band: starting there costs one
    pass instead of the seven or eight that doubling from scratch needs.
    """
    cost = 0
    for tag, i1, i2, j1, j2 in SequenceMatcher(a=ref, b=hyp, autojunk=False).get_opcodes():
        if tag == "equal":
            continue
        cost += max(i2 - i1, j2 - j1)
    return cost


def _align(ref: str, hyp: str, verbose: bool = False) -> Alignment:
    """Exact alignment: common ends trimmed, then a band wide enough to contain
    an optimal path."""
    prefix = 0
    limit = min(len(ref), len(hyp))
    while prefix < limit and ref[prefix] == hyp[prefix]:
        prefix += 1
    suffix = 0
    while suffix < limit - prefix and ref[len(ref) - 1 - suffix] == hyp[len(hyp) - 1 - suffix]:
        suffix += 1
    core_ref = ref[prefix : len(ref) - suffix]
    core_hyp = hyp[prefix : len(hyp) - suffix]

    # The distance cannot exceed the cost of the script difflib already found,
    # so the band never has to grow past it - and it cannot exceed the longer
    # string either.
    ceiling = min(max(len(core_ref), len(core_hyp), 1), max(_edit_script_cost(core_ref, core_hyp), 1))
    k = min(max(1, abs(len(core_ref) - len(core_hyp))), ceiling)
    while True:
        if verbose and k > SLOW_BAND:
            print(f"  ... {len(core_ref):,} 文字を band={k:,} で照合中", file=sys.stderr)
        result = _banded_align(core_ref, core_hyp, k)
        if result is not None:
            result.pairs.update(_substitution_pairs(core_ref, core_hyp))
            return result
        if k >= ceiling:
            # Unreachable unless the bound above is wrong: a band this wide
            # already contains every path. Widening to the whole matrix keeps
            # the answer exact instead of returning something unverified.
            result = _banded_align(core_ref, core_hyp, max(len(core_ref), len(core_hyp), 1))
            if result is None:  # pragma: no cover - defensive
                raise RuntimeError("alignment failed at full band width")
            result.pairs.update(_substitution_pairs(core_ref, core_hyp))
            return result
        k = min(k * 2, ceiling)


def _substitution_pairs(ref: str, hyp: str) -> Counter:
    """One-for-one character swaps, for the orthographic-variation notes.

    These come from difflib rather than from the scoring path: they are a
    reading aid, not part of the CER, and nothing here decides that a swap was
    acceptable. That judgement stays with the person writing the report.
    """
    pairs: Counter[tuple[str, str]] = Counter()
    for tag, i1, i2, j1, j2 in SequenceMatcher(a=ref, b=hyp, autojunk=False).get_opcodes():
        if tag != "replace" or (i2 - i1) != (j2 - j1):
            continue
        for a, b in zip(ref[i1:i2], hyp[j1:j2]):
            pairs[(a, b)] += 1
    return pairs


def score(ref_text: str, hyp_text: str, exact: bool = False, verbose: bool = False) -> dict:
    """Returns the CER and its parts for one reference/hypothesis pair."""
    ref = normalize(ref_text)
    hyp = normalize(hyp_text)
    alignment = _dp_align(ref, hyp) if exact else _align(ref, hyp, verbose)
    return {
        "reference_chars": len(ref),
        "hypothesis_chars": len(hyp),
        "substitutions": alignment.substitutions,
        "deletions": alignment.deletions,
        "insertions": alignment.insertions,
        "errors": alignment.distance,
        # A CER above 1.0 is possible and is not a bug: an engine that invents
        # text can insert more characters than the reference contains.
        "cer": alignment.distance / len(ref) if ref else (0.0 if not hyp else 1.0),
        "method": "full-matrix" if exact else "banded",
        "substitution_pairs": alignment.pairs.most_common(20),
    }


def _read(path: str) -> str:
    return Path(path).read_text(encoding="utf-8")


def _print_score(name: str, result: dict, show_pairs: int) -> None:
    print(f"{name}")
    print(f"  正解文字数     : {result['reference_chars']:,}")
    print(f"  認識文字数     : {result['hypothesis_chars']:,}")
    print(
        f"  置換 / 削除 / 挿入 : {result['substitutions']:,}"
        f" / {result['deletions']:,} / {result['insertions']:,}"
    )
    print(f"  誤り合計       : {result['errors']:,}")
    print(f"  CER            : {result['cer'] * 100:.2f} %")
    if show_pairs and result["substitution_pairs"]:
        print("  よく置き換わった文字（表記揺れの注記に使う。自動では正解扱いしない）:")
        for (a, b), count in result["substitution_pairs"][:show_pairs]:
            print(f"    {a} -> {b}  x{count}")


def cmd_score(args: argparse.Namespace) -> int:
    result = score(_read(args.ref), _read(args.hyp), exact=args.exact, verbose=True)
    if args.json:
        print(json.dumps(result, ensure_ascii=False, indent=2))
    else:
        _print_score(f"{args.ref}  vs  {args.hyp}", result, args.show_pairs)
    return 0


def cmd_batch(args: argparse.Namespace) -> int:
    """Scores every ``id<TAB>reference<TAB>hypothesis`` line of a TSV file."""
    rows = []
    for line in Path(args.pairs).read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        parts = line.split("\t")
        if len(parts) != 3:
            print(f"3列（ID・正解・認識）ではありません: {line}", file=sys.stderr)
            return 2
        name, ref, hyp = parts
        rows.append((name, score(_read(ref), _read(hyp), exact=args.exact)))

    if args.json:
        print(json.dumps({name: r for name, r in rows}, ensure_ascii=False, indent=2))
        return 0

    print("| ID | 正解文字数 | 置換 | 削除 | 挿入 | 誤り合計 | CER |")
    print("| --- | ---: | ---: | ---: | ---: | ---: | ---: |")
    total_ref = total_err = 0
    for name, r in rows:
        total_ref += r["reference_chars"]
        total_err += r["errors"]
        print(
            f"| {name} | {r['reference_chars']:,} | {r['substitutions']:,} | {r['deletions']:,} "
            f"| {r['insertions']:,} | {r['errors']:,} | {r['cer'] * 100:.2f} % |"
        )
    if rows:
        overall = total_err / total_ref if total_ref else 0.0
        print(f"| **合計** | {total_ref:,} | | | | {total_err:,} | **{overall * 100:.2f} %** |")
        print()
        print("合計行は文字数で重み付けした値です（各行のCERの平均ではありません）。")
    return 0


def cmd_normalize(args: argparse.Namespace) -> int:
    print(normalize(_read(args.input)))
    return 0


def cmd_selftest(_: argparse.Namespace) -> int:
    """Checks the arithmetic, the normalisation rules, and the fast path."""
    failures = []

    def check(label: str, got, want) -> None:
        if got != want:
            failures.append(f"{label}: got {got!r}, want {want!r}")

    # The worked example from section 8 of the procedure.
    example = score("本日の会議を開始します", "本日の会議を開示します")
    check("example reference length", example["reference_chars"], 11)
    check("example substitutions", example["substitutions"], 1)
    check("example errors", example["errors"], 1)
    check("example CER", round(example["cer"], 6), round(1 / 11, 6))

    # Normalisation removes only what section 8 says it removes.
    check("NFKC width", normalize("ＡＢＣ１２３"), "ABC123")
    check("half-width kana", normalize("ｶﾀｶﾅ"), "カタカナ")
    check("whitespace", normalize(" あ　い\tう\r\nえ "), "あいうえ")
    check("punctuation", normalize("本日は、晴れです。"), "本日は晴れです")
    check("long vowel kept", normalize("データ"), "データ")
    check("repetition mark kept", normalize("各々"), "各々")
    # ... and it must not quietly forgive real errors.
    # 2 characters against 3: two substitutions and one insertion.
    check("kana vs kanji stays an error", score("会議", "かいぎ")["errors"], 3)
    check("number spelling stays an error", score("10時", "十時")["errors"], 2)

    # Degenerate inputs.
    check("empty vs empty", score("", "")["cer"], 0.0)
    check("empty reference, text out", score("", "あ")["cer"], 1.0)
    check("all deleted", score("あいう", "")["deletions"], 3)
    check("all inserted", score("", "あいう")["insertions"], 3)

    # Hallucinated text can exceed 100 %.
    hallucination = score("はい", "はいご視聴ありがとうございました")
    check("insertions counted", hallucination["insertions"], 14)
    check("CER above one", hallucination["cer"] > 1.0, True)

    # The banded path must agree with the plain DP exactly. Random strings are
    # the hard case: real transcript pairs share long identical runs.
    rng = random.Random(20260914)
    alphabet = "あいうえおかきくけこ会議録音対応"
    for _ in range(300):
        a = "".join(rng.choice(alphabet) for _ in range(rng.randint(0, 40)))
        b = "".join(rng.choice(alphabet) for _ in range(rng.randint(0, 40)))
        fast, slow = _align(a, b), _dp_align(a, b)
        if (fast.distance, fast.substitutions, fast.deletions, fast.insertions) != (
            slow.distance,
            slow.substitutions,
            slow.deletions,
            slow.insertions,
        ):
            failures.append(
                f"banded disagreed with the full matrix: {a!r} {b!r} "
                f"{fast.distance}/{fast.substitutions}/{fast.deletions}/{fast.insertions} vs "
                f"{slow.distance}/{slow.substitutions}/{slow.deletions}/{slow.insertions}"
            )
            break

    # And on text shaped like a transcript pair, where the band stays narrow.
    base = "本日の会議を開始します。まず前回の議事録を確認してください。" * 20
    edited = base.replace("議事録", "議事禄", 5).replace("確認", "確任", 3)
    check(
        "banded equals the full matrix on transcript-like text",
        _align(normalize(base), normalize(edited)).distance,
        _dp_align(normalize(base), normalize(edited)).distance,
    )
    check("expected substitutions on transcript-like text", score(base, edited)["substitutions"], 8)

    for failure in failures:
        print(f"FAIL {failure}")
    print(f"{'FAILED' if failures else 'OK'}: {len(failures)} failure(s)")
    return 1 if failures else 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)

    p_score = sub.add_parser("score", help="CER for one reference/hypothesis pair")
    p_score.add_argument("--ref", required=True, help="human-verified reference transcript (UTF-8)")
    p_score.add_argument("--hyp", required=True, help="transcript produced by the application (UTF-8)")
    p_score.add_argument("--json", action="store_true")
    p_score.add_argument("--exact", action="store_true", help="full Levenshtein matrix; slow on long texts")
    p_score.add_argument("--show-pairs", type=int, default=10, metavar="N")
    p_score.set_defaults(func=cmd_score)

    p_batch = sub.add_parser("batch", help="score a TSV of id/reference/hypothesis paths")
    p_batch.add_argument("pairs")
    p_batch.add_argument("--json", action="store_true")
    p_batch.add_argument("--exact", action="store_true")
    p_batch.set_defaults(func=cmd_batch)

    p_norm = sub.add_parser("normalize", help="print the normalised text that would be compared")
    p_norm.add_argument("--in", dest="input", required=True)
    p_norm.set_defaults(func=cmd_normalize)

    sub.add_parser("selftest", help="verify this script against known values").set_defaults(func=cmd_selftest)

    args = parser.parse_args(argv)
    _make_output_robust()
    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())
