#!/usr/bin/env python3
"""Objective checks on a recorded WAV, for the recording-quality tests.

The field test (docs/FIELD_TEST_JA.md) asks whether a recording lost audio,
clipped, or drifted in level over an hour. Listening finds the obvious cases;
this script finds the ones that are one second long at minute 43, and it puts
a timestamp on them so the reviewer can go and listen to that spot.

It reports, per file and per minute:

    * duration, and how far it is from the expected length if one is given
    * peak and RMS level in dBFS
    * runs of samples at (or next to) full scale - candidate clipping
    * runs of exactly zero samples - candidate capture dropouts, because this
      application applies no noise gate: a real room is never digitally silent
    * stretches quieter than a threshold, which are usually just quiet, and
      which the reviewer confirms by listening

It reads 16-bit PCM WAV, which is what this application writes (48 kHz mono for
the meeting file, 16 kHz mono for the recognition files). It deliberately does
not read MP3: measure the WAV, before the lossy step.

Usage:
    python3 tools/wav_quality.py analyze meeting.wav
    python3 tools/wav_quality.py analyze recognition-*.wav --per-minute
    python3 tools/wav_quality.py analyze meeting.wav --expect-seconds 3603 --markdown
    python3 tools/wav_quality.py analyze meeting.wav --json
    python3 tools/wav_quality.py selftest

It is a measurement tool. It is not part of the build and nothing it produces
is shipped to users.
"""
from __future__ import annotations

import argparse
import array
import json
import math
import struct
import sys
import tempfile
import wave
from pathlib import Path

FULL_SCALE = 32768.0

#: Samples at least this loud count towards a clipping run. 32767 is the
#: literal ceiling; a converter that has been driven into it usually leaves a
#: few samples just below, so the default is slightly under full scale.
DEFAULT_CLIP_LEVEL = 32700

#: A clipped sample on its own is a transient. A run of them is a flat top.
DEFAULT_CLIP_RUN = 3

#: Nothing in this application writes digital silence into a live capture, so a
#: run of exact zeros this long is a dropout until proven otherwise.
DEFAULT_ZERO_MS = 120

#: Frames quieter than this, for long enough, are reported as quiet stretches.
DEFAULT_QUIET_DBFS = -60.0
DEFAULT_QUIET_SECONDS = 5.0

FRAME_MS = 20
CHUNK_FRAMES = 1 << 16


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


def dbfs(amplitude: float) -> float:
    """Level in dBFS, floored so that silence prints instead of raising."""
    return 20.0 * math.log10(amplitude / FULL_SCALE) if amplitude > 0 else -float("inf")


def _format_db(value: float) -> str:
    return "-inf" if value == -float("inf") else f"{value:.1f}"


def timestamp(seconds: float) -> str:
    """h:mm:ss.mmm, because "at 2612 seconds" is not a place anyone can find."""
    hours, rest = divmod(seconds, 3600)
    minutes, secs = divmod(rest, 60)
    return f"{int(hours)}:{int(minutes):02d}:{secs:06.3f}"


class Analysis:
    """Accumulates the measurements while the file streams past."""

    def __init__(self, rate: int, channels: int, clip_level: int, clip_run: int, zero_ms: int):
        self.rate = rate
        self.channels = channels
        self.clip_level = clip_level
        self.clip_run = clip_run
        self.zero_samples = max(1, int(rate * channels * zero_ms / 1000))

        self.samples = 0
        self.peak = 0
        self.square_sum = 0.0
        self.clip_events: list[dict] = []
        self.zero_events: list[dict] = []
        self.minutes: list[dict] = []
        self.frames: list[tuple[float, float]] = []  # (start seconds, rms amplitude)

        self._clip_run_length = 0
        self._zero_run_length = 0
        self._minute_index = 0
        self._minute_peak = 0
        self._minute_square_sum = 0.0
        self._minute_samples = 0
        self._minute_clips = 0
        self._minute_zeros = 0
        self._frame_square_sum = 0.0
        self._frame_samples = 0
        self._frame_length = max(1, int(rate * channels * FRAME_MS / 1000))

    def _at(self, sample_index: int) -> float:
        return sample_index / (self.rate * self.channels)

    def feed(self, block: array.array) -> None:
        for value in block:
            magnitude = -value if value < 0 else value
            self.samples += 1
            self.square_sum += value * value
            if magnitude > self.peak:
                self.peak = magnitude
            if magnitude > self._minute_peak:
                self._minute_peak = magnitude
            self._minute_square_sum += value * value
            self._minute_samples += 1
            self._frame_square_sum += value * value
            self._frame_samples += 1

            if magnitude >= self.clip_level:
                self._clip_run_length += 1
            else:
                self._close_clip_run()

            if value == 0:
                self._zero_run_length += 1
            else:
                self._close_zero_run()

            if self._frame_samples >= self._frame_length:
                start = self._at(self.samples - self._frame_samples)
                self.frames.append((start, math.sqrt(self._frame_square_sum / self._frame_samples)))
                self._frame_square_sum = 0.0
                self._frame_samples = 0

            if self._minute_samples >= self.rate * self.channels * 60:
                self._close_minute()

    def _close_clip_run(self) -> None:
        if self._clip_run_length >= self.clip_run:
            start = self.samples - self._clip_run_length
            self.clip_events.append(
                {
                    "at_seconds": self._at(start),
                    "at": timestamp(self._at(start)),
                    "samples": self._clip_run_length,
                    "ms": self._clip_run_length * 1000.0 / (self.rate * self.channels),
                }
            )
            self._minute_clips += 1
        self._clip_run_length = 0

    def _close_zero_run(self) -> None:
        if self._zero_run_length >= self.zero_samples:
            start = self.samples - self._zero_run_length
            self.zero_events.append(
                {
                    "at_seconds": self._at(start),
                    "at": timestamp(self._at(start)),
                    "samples": self._zero_run_length,
                    "ms": self._zero_run_length * 1000.0 / (self.rate * self.channels),
                }
            )
            self._minute_zeros += 1
        self._zero_run_length = 0

    def _close_minute(self) -> None:
        if self._minute_samples == 0:
            return
        self.minutes.append(
            {
                "minute": self._minute_index,
                "peak_dbfs": dbfs(self._minute_peak),
                "rms_dbfs": dbfs(math.sqrt(self._minute_square_sum / self._minute_samples)),
                "clip_runs": self._minute_clips,
                "zero_runs": self._minute_zeros,
            }
        )
        self._minute_index += 1
        self._minute_peak = 0
        self._minute_square_sum = 0.0
        self._minute_samples = 0
        self._minute_clips = 0
        self._minute_zeros = 0

    def finish(self) -> None:
        self._close_clip_run()
        self._close_zero_run()
        if self._frame_samples:
            start = self._at(self.samples - self._frame_samples)
            self.frames.append((start, math.sqrt(self._frame_square_sum / self._frame_samples)))
        self._close_minute()

    def quiet_stretches(self, threshold_dbfs: float, minimum_seconds: float) -> list[dict]:
        """Contiguous frames below the threshold, as candidate silent passages."""
        found: list[dict] = []
        run_start: float | None = None
        run_end = 0.0
        for start, rms in self.frames:
            if dbfs(rms) < threshold_dbfs:
                if run_start is None:
                    run_start = start
                run_end = start + FRAME_MS / 1000.0
            elif run_start is not None:
                if run_end - run_start >= minimum_seconds:
                    found.append(
                        {"at_seconds": run_start, "at": timestamp(run_start), "seconds": run_end - run_start}
                    )
                run_start = None
        if run_start is not None and run_end - run_start >= minimum_seconds:
            found.append({"at_seconds": run_start, "at": timestamp(run_start), "seconds": run_end - run_start})
        return found


def analyze(path: Path, args: argparse.Namespace) -> dict:
    with wave.open(str(path), "rb") as source:
        width = source.getsampwidth()
        if width != 2:
            raise ValueError(
                f"{path}: 16bit PCM ではありません（{width * 8}bit）。"
                "このスクリプトはアプリが書き出す 16bit PCM WAV のみを対象にします。"
            )
        rate = source.getframerate()
        channels = source.getnchannels()
        total_frames = source.getnframes()

        analysis = Analysis(rate, channels, args.clip_level, args.clip_run, args.zero_ms)
        remaining = total_frames
        while remaining > 0:
            wanted = min(CHUNK_FRAMES, remaining)
            raw = source.readframes(wanted)
            if not raw:
                break
            block = array.array("h")
            block.frombytes(raw)
            if sys.byteorder == "big":
                block.byteswap()
            analysis.feed(block)
            remaining -= wanted
        analysis.finish()

    duration = analysis.samples / (rate * channels) if analysis.samples else 0.0
    rms = math.sqrt(analysis.square_sum / analysis.samples) if analysis.samples else 0.0
    result = {
        "file": str(path),
        "sample_rate": rate,
        "channels": channels,
        "duration_seconds": duration,
        "duration": timestamp(duration),
        "peak_dbfs": dbfs(analysis.peak),
        "rms_dbfs": dbfs(rms),
        "clip_runs": len(analysis.clip_events),
        "clip_events": analysis.clip_events[: args.list_events],
        "zero_runs": len(analysis.zero_events),
        "zero_events": analysis.zero_events[: args.list_events],
        "quiet_stretches": analysis.quiet_stretches(args.quiet_dbfs, args.quiet_seconds),
        "per_minute": analysis.minutes,
        "settings": {
            "clip_level": args.clip_level,
            "clip_run_samples": args.clip_run,
            "zero_run_ms": args.zero_ms,
            "quiet_dbfs": args.quiet_dbfs,
            "quiet_seconds": args.quiet_seconds,
        },
    }
    if args.expect_seconds is not None:
        result["expected_seconds"] = args.expect_seconds
        result["difference_seconds"] = duration - args.expect_seconds
    return result


def print_text(result: dict, per_minute: bool) -> None:
    print(result["file"])
    print(f"  形式        : {result['sample_rate']} Hz / {result['channels']} ch / 16bit PCM")
    print(f"  長さ        : {result['duration']}  ({result['duration_seconds']:.3f} 秒)")
    if "difference_seconds" in result:
        difference = result["difference_seconds"]
        print(f"  想定との差  : {difference:+.3f} 秒（想定 {result['expected_seconds']:.3f} 秒）")
    print(f"  ピーク      : {_format_db(result['peak_dbfs'])} dBFS")
    print(f"  RMS         : {_format_db(result['rms_dbfs'])} dBFS")
    print(f"  クリップ候補: {result['clip_runs']} 箇所（連続 {result['settings']['clip_run_samples']} サンプル以上）")
    for event in result["clip_events"]:
        print(f"      {event['at']}  {event['samples']} サンプル / {event['ms']:.1f} ms")
    print(f"  ゼロ連続    : {result['zero_runs']} 箇所（{result['settings']['zero_run_ms']} ms 以上）")
    for event in result["zero_events"]:
        print(f"      {event['at']}  {event['ms']:.1f} ms")
    print(f"  無音区間    : {len(result['quiet_stretches'])} 箇所（{result['settings']['quiet_dbfs']:.0f} dBFS 未満が "
          f"{result['settings']['quiet_seconds']:.0f} 秒以上）")
    for stretch in result["quiet_stretches"][:20]:
        print(f"      {stretch['at']}  {stretch['seconds']:.1f} 秒")
    if per_minute and result["per_minute"]:
        print("  分ごと:")
        print("    | 分 | ピーク dBFS | RMS dBFS | クリップ | ゼロ連続 |")
        print("    | ---: | ---: | ---: | ---: | ---: |")
        for row in result["per_minute"]:
            print(
                f"    | {row['minute']} | {_format_db(row['peak_dbfs'])} | {_format_db(row['rms_dbfs'])} "
                f"| {row['clip_runs']} | {row['zero_runs']} |"
            )


def print_markdown(results: list[dict]) -> None:
    print("| ファイル | 長さ | ピーク dBFS | RMS dBFS | クリップ候補 | ゼロ連続 | 無音区間 |")
    print("| --- | ---: | ---: | ---: | ---: | ---: | ---: |")
    for result in results:
        print(
            f"| `{Path(result['file']).name}` | {result['duration']} | {_format_db(result['peak_dbfs'])} "
            f"| {_format_db(result['rms_dbfs'])} | {result['clip_runs']} | {result['zero_runs']} "
            f"| {len(result['quiet_stretches'])} |"
        )


def cmd_analyze(args: argparse.Namespace) -> int:
    results = []
    for name in args.files:
        path = Path(name)
        if not path.exists():
            print(f"ファイルがありません: {path}", file=sys.stderr)
            return 2
        results.append(analyze(path, args))

    if args.json:
        print(json.dumps(results, ensure_ascii=False, indent=2))
    elif args.markdown:
        print_markdown(results)
    else:
        for result in results:
            print_text(result, args.per_minute)
            print()

    # A non-zero exit is for the caller's own scripting; the reviewer reads the
    # numbers either way.
    failed = any(r["clip_runs"] or r["zero_runs"] for r in results)
    return 1 if failed and args.strict else 0


def _write_wav(path: Path, samples: list[int], rate: int = 16000) -> None:
    with wave.open(str(path), "wb") as target:
        target.setnchannels(1)
        target.setsampwidth(2)
        target.setframerate(rate)
        target.writeframes(struct.pack(f"<{len(samples)}h", *samples))


def cmd_selftest(_: argparse.Namespace) -> int:
    """Builds WAV files whose defects are known, then checks they are found."""
    failures = []

    def check(label: str, got, want) -> None:
        if got != want:
            failures.append(f"{label}: got {got!r}, want {want!r}")

    defaults = argparse.Namespace(
        clip_level=DEFAULT_CLIP_LEVEL,
        clip_run=DEFAULT_CLIP_RUN,
        zero_ms=DEFAULT_ZERO_MS,
        quiet_dbfs=DEFAULT_QUIET_DBFS,
        quiet_seconds=DEFAULT_QUIET_SECONDS,
        list_events=10,
        expect_seconds=None,
    )
    rate = 16000

    with tempfile.TemporaryDirectory() as folder:
        root = Path(folder)

        # A clean half-amplitude tone: no clipping, no dropouts, -6 dBFS peak.
        tone = [int(16000 * math.sin(2 * math.pi * 440 * n / rate)) for n in range(rate * 3)]
        clean = root / "clean.wav"
        _write_wav(clean, tone, rate)
        result = analyze(clean, defaults)
        check("clean duration", round(result["duration_seconds"], 3), 3.0)
        check("clean clipping", result["clip_runs"], 0)
        check("clean peak within a tenth of a dB", abs(result["peak_dbfs"] + 6.3) < 0.2, True)
        # A sine crosses zero, but never for long enough to look like a dropout.
        check("clean dropouts", result["zero_runs"], 0)

        # A tone driven past full scale, flat-topped twice per cycle.
        loud = [max(-32768, min(32767, value * 4)) for value in tone]
        clipped = root / "clipped.wav"
        _write_wav(clipped, loud, rate)
        result = analyze(clipped, defaults)
        check("clipping found", result["clip_runs"] > 100, True)
        check("clipped peak", round(result["peak_dbfs"], 2), 0.0)

        # One second of the tone replaced by exact zeros: a dropout.
        holed = list(tone)
        holed[rate : rate + rate] = [0] * rate
        dropout = root / "dropout.wav"
        _write_wav(dropout, holed, rate)
        result = analyze(dropout, defaults)
        check("dropout found", result["zero_runs"], 1)
        check("dropout timestamp", round(result["zero_events"][0]["at_seconds"], 3), 1.0)
        check("dropout length", round(result["zero_events"][0]["ms"]), 1000)

        # Six seconds of room tone at about -70 dBFS: quiet, not missing.
        quiet = [int(10 * math.sin(2 * math.pi * 200 * n / rate)) for n in range(rate * 6)]
        room = root / "quiet.wav"
        _write_wav(room, tone[: rate] + quiet + tone[: rate], rate)
        result = analyze(room, defaults)
        check("quiet stretch found", len(result["quiet_stretches"]), 1)
        check("quiet stretch start", round(result["quiet_stretches"][0]["at_seconds"], 1), 1.0)
        check("quiet stretch is not a dropout", result["zero_runs"], 0)

        # Length checking against a stopwatch reading.
        with_expectation = argparse.Namespace(**{**vars(defaults), "expect_seconds": 3.5})
        result = analyze(clean, with_expectation)
        check("length difference", round(result["difference_seconds"], 3), -0.5)

        # Per-minute rows appear once a file is long enough to have minutes.
        long_tone = tone * 45  # 135 seconds
        long_file = root / "long.wav"
        _write_wav(long_file, long_tone, rate)
        result = analyze(long_file, defaults)
        check("per-minute rows", len(result["per_minute"]), 3)
        check("last row is the partial minute", result["per_minute"][-1]["minute"], 2)

        # 8-bit input is refused rather than mis-measured.
        eight_bit = root / "eight.wav"
        with wave.open(str(eight_bit), "wb") as target:
            target.setnchannels(1)
            target.setsampwidth(1)
            target.setframerate(rate)
            target.writeframes(bytes(1000))
        try:
            analyze(eight_bit, defaults)
            failures.append("8-bit input was accepted")
        except ValueError:
            pass

    for failure in failures:
        print(f"FAIL {failure}")
    print(f"{'FAILED' if failures else 'OK'}: {len(failures)} failure(s)")
    return 1 if failures else 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)

    p_analyze = sub.add_parser("analyze", help="measure one or more 16-bit PCM WAV files")
    p_analyze.add_argument("files", nargs="+")
    p_analyze.add_argument("--per-minute", action="store_true", help="print the per-minute table")
    p_analyze.add_argument("--expect-seconds", type=float, help="stopwatch length, to compare against the file")
    p_analyze.add_argument("--clip-level", type=int, default=DEFAULT_CLIP_LEVEL)
    p_analyze.add_argument("--clip-run", type=int, default=DEFAULT_CLIP_RUN)
    p_analyze.add_argument("--zero-ms", type=int, default=DEFAULT_ZERO_MS)
    p_analyze.add_argument("--quiet-dbfs", type=float, default=DEFAULT_QUIET_DBFS)
    p_analyze.add_argument("--quiet-seconds", type=float, default=DEFAULT_QUIET_SECONDS)
    p_analyze.add_argument("--list-events", type=int, default=20, metavar="N")
    p_analyze.add_argument("--json", action="store_true")
    p_analyze.add_argument("--markdown", action="store_true", help="one summary row per file")
    p_analyze.add_argument("--strict", action="store_true", help="exit 1 if anything was found")
    p_analyze.set_defaults(func=cmd_analyze)

    sub.add_parser("selftest", help="verify this script against files with known defects").set_defaults(
        func=cmd_selftest
    )

    args = parser.parse_args(argv)
    _make_output_robust()
    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())
