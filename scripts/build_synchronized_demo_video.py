from __future__ import annotations

import subprocess
import sys
from pathlib import Path

from generate_detailed_demo_narration import NARRATION


def duration(ffprobe: Path, media: Path) -> float:
    result = subprocess.run(
        [
            str(ffprobe),
            "-v",
            "error",
            "-show_entries",
            "format=duration",
            "-of",
            "default=noprint_wrappers=1:nokey=1",
            str(media),
        ],
        check=True,
        capture_output=True,
        text=True,
    )
    return float(result.stdout.strip())


def srt_time(seconds: float) -> str:
    millis = max(0, round(seconds * 1000))
    hours, millis = divmod(millis, 3_600_000)
    minutes, millis = divmod(millis, 60_000)
    secs, millis = divmod(millis, 1000)
    return f"{hours:02}:{minutes:02}:{secs:02},{millis:03}"


def main() -> None:
    if len(sys.argv) != 8:
        raise SystemExit(
            "usage: build_synchronized_demo_video.py RAW CUES NARRATION_DIR OUTPUT SRT FFMPEG FFPROBE"
        )

    raw, cues_path, narration_dir, output, captions, ffmpeg, ffprobe = (
        Path(value).resolve() for value in sys.argv[1:]
    )
    cues: list[tuple[float, str]] = []
    for line in cues_path.read_text(encoding="utf-8").splitlines():
        if line.strip():
            seconds, key = line.split("|", 1)
            cues.append((float(seconds), key.strip()))
    if not cues or cues[-1][1] != "END":
        raise RuntimeError("Presentation cues are incomplete.")

    chapters: list[dict[str, object]] = []
    subtitle_blocks: list[str] = []
    synchronized_cursor = 0.0
    # The hidden recorder begins one second before the presentation cue clock.
    # Apply that pre-roll offset to every source-video trim so spoken chapters
    # and visible section changes share the same zero point.
    video_preroll = 1.0
    for index, ((start, key), (end, _)) in enumerate(zip(cues, cues[1:]), start=1):
        speech = narration_dir / f"speech-{index:02}.mp3"
        speech_duration = duration(ffprobe, speech)
        # An 80 ms visual lead makes the new section visible before its first
        # spoken word. The matching 80 ms tail avoids clipping while keeping
        # transitions effectively immediate.
        target_duration = speech_duration + 0.16
        chapters.append(
            {
                "key": key,
                "start": start + video_preroll,
                "end": end + video_preroll,
                "speech": speech,
                "speech_duration": speech_duration,
                "target_duration": target_duration,
            }
        )
        text = NARRATION[key].replace("Hee-saab Kee-taab", "HISAB KITAB")
        subtitle_blocks.append(
            f"{index}\n"
            f"{srt_time(synchronized_cursor + 0.08)} --> "
            f"{srt_time(synchronized_cursor + 0.08 + speech_duration)}\n"
            f"{text}\n"
        )
        synchronized_cursor += target_duration

    captions.parent.mkdir(parents=True, exist_ok=True)
    captions.write_text("\n".join(subtitle_blocks), encoding="utf-8")
    output.parent.mkdir(parents=True, exist_ok=True)

    arguments = [
        str(ffmpeg),
        "-hide_banner",
        "-loglevel",
        "error",
        "-y",
        "-i",
        str(raw),
    ]
    for chapter in chapters:
        arguments.extend(["-i", str(chapter["speech"])])
    arguments.extend(["-i", str(captions)])

    filters: list[str] = []
    video_inputs = "".join(f"[source{index}]" for index in range(len(chapters)))
    filters.append(f"[0:v]split={len(chapters)}{video_inputs}")
    for index, chapter in enumerate(chapters):
        original_duration = float(chapter["end"]) - float(chapter["start"])
        target_duration = float(chapter["target_duration"])
        speed_factor = target_duration / original_duration
        filters.append(
            f"[source{index}]"
            f"trim=start={float(chapter['start']):.3f}:end={float(chapter['end']):.3f},"
            f"setpts={speed_factor:.9f}*(PTS-STARTPTS),"
            "fps=30,scale=1920:1080:flags=lanczos,setsar=1"
            f"[video{index}]"
        )
        filters.append(
            f"[{index + 1}:a]"
            "adelay=80|80,"
            f"apad,atrim=duration={target_duration:.3f},asetpts=PTS-STARTPTS"
            f"[audio{index}]"
        )

    video_concat = "".join(f"[video{index}]" for index in range(len(chapters)))
    audio_concat = "".join(f"[audio{index}]" for index in range(len(chapters)))
    filters.append(f"{video_concat}concat=n={len(chapters)}:v=1:a=0[video]")
    filters.append(
        f"{audio_concat}concat=n={len(chapters)}:v=0:a=1,"
        "loudnorm=I=-16:TP=-1.5:LRA=11[audio]"
    )

    subtitle_input = len(chapters) + 1
    arguments.extend(
        [
            "-filter_complex",
            ";".join(filters),
            "-map",
            "[video]",
            "-map",
            "[audio]",
            "-map",
            f"{subtitle_input}:0",
            "-c:v",
            "libx264",
            "-preset",
            "medium",
            "-crf",
            "18",
            "-pix_fmt",
            "yuv420p",
            "-c:a",
            "aac",
            "-b:a",
            "192k",
            "-ar",
            "48000",
            "-c:s",
            "mov_text",
            "-metadata:s:s:0",
            "language=eng",
            "-metadata:s:s:0",
            "title=English Captions",
            "-movflags",
            "+faststart",
            str(output),
        ]
    )
    subprocess.run(arguments, check=True)


if __name__ == "__main__":
    main()
