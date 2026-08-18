from __future__ import annotations

import asyncio
import json
import subprocess
import sys
from pathlib import Path

import edge_tts


VOICE = "en-US-AndrewMultilingualNeural"

NARRATION = {
    "introduction": (
        "Welcome to Hee-saab Kee-taab Works. See how a retail team manages daily operations using safe, fictional demonstration data."
    ),
    "dashboard": (
        "After sign-in, the owner begins on a live business dashboard. The cards summarize sales, cash on hand, "
        "purchases, checks, payroll, expenses, and net profit. Trend panels make direction easy to see, while recent "
        "activity helps management identify what changed. Every number shown here comes from the selected store, "
        "giving the owner a reliable starting point for daily review."
    ),
    "multiple-stores": (
        "One authorized login can manage multiple businesses without mixing their records. The store selector changes "
        "the complete working context, including transactions, employees, schedules, reports, and store-specific automation. "
        "Owners can choose a default store and arrange their preferred store order, while each business remains independently controlled."
    ),
    "cash-sales-summary": (
        "Cash and Sales Summary is the daily business-sales record. Select an imported report to see gross sales, net sales, "
        "sales tax, cash sales, card sales, and department profit. The additional tabs preserve tender detail, hourly sales, "
        "department totals, and system statistics. Cash drop is automatically combined from the matching Z-report batches in "
        "Shift Cash Drop. If the register paid a small operating expense, enter the payout and its reason, then save the reconciliation. "
        "The over or short amount updates immediately. Reports may also be opened, deleted by an administrator, or traced directly to the shift log."
    ),
    "shift-cash-drop": (
        "Shift Cash Drop works at register-batch level. The manager selects an automatically synchronized Z report, which loads "
        "the shift number, employee, cash, cards, net sales, tax, and source report. The manager enters the physical cash drop and "
        "any register payout. Hee-saab Kee-taab calculates gross sales, total cash due, total received, and variance. Saving updates "
        "the selected batch instead of creating a duplicate entry. Separate batches remain visible for over-or-short review, and the "
        "combined drop for each date flows into Cash and Sales Summary and Cash On Hand. Manual entry, correction, import, filtering, and Excel export are also available."
    ),
    "cash-on-hand": (
        "Cash On Hand maintains the store's auditable running cash record. In this example, the user records an eight-hundred-fifty-dollar "
        "safe deposit, selects a vendor and purpose, adds a clear description, and saves it. The opening balance, money added today, "
        "monthly payouts, closing balance, and carry-forward value recalculate from the ledger. Existing rows can be selected for an "
        "administrator correction or update, and a monthly carry-forward can be established without rewriting historical entries. "
        "Automatically synchronized cash drops appear with their shift-log reference, preserving the connection back to the original register batch."
    ),
    "check-payout": (
        "Check Payout provides a controlled workflow for vendor checks. The user starts a new check, enters the payee, amount, purpose, "
        "and check number, and sees the printable check preview update while typing. Saving places the check into the payout register. "
        "The cleared checkbox can be updated directly when the item posts at the bank. Summary cards separate uncleared and cleared totals, "
        "and the next check number helps reduce numbering mistakes. Authorized users can print, void, clear, or delete selected checks, "
        "and can export the register for review."
    ),
    "operations-hub": (
        "Operations Hub brings the most important operating areas together. The owner can compare today's shift activity, available cash, "
        "bank movement, check payouts, purchases, product-cost changes, price alerts, and net profit. Each card includes a current status, "
        "a concise total, a recent trend, and a direct action. This provides an efficient morning review without opening every section individually."
    ),
    "vendors-purposes": (
        "Vendors and Purposes standardizes names used throughout the system. Here, a new supplier is entered with contact details, category, "
        "status, email, and notes. Saving makes the vendor available to cash, purchase, and reporting workflows. Purpose records work the same way "
        "for consistent payout descriptions. Administrators may select an existing row to update it, create a traceable correction, deactivate it, "
        "or remove it when appropriate. Consistent master data prevents duplicate spellings and makes reports much easier to understand."
    ),
    "purchases": (
        "Purchases records supplier invoices and their product lines. The user enters the vendor, invoice number, category, payment method, tax, "
        "and total. Individual products include description or SKU, quantity, and unit cost; the line total is calculated before the invoice is saved. "
        "Invoices may also be imported from PDF, and configured email or cloud-inbox workflows can locate incoming invoices automatically. "
        "The purchase register supports date periods, updates, deletion, source-document viewing, and Excel export. Imported line items update Product Costs "
        "and can generate Price Alerts when a supplier cost changes beyond the configured threshold."
    ),
    "bank-statement": (
        "Bank Statement supports importing, categorizing, matching, and reviewing account activity. Search narrows the transaction list, while account, "
        "month, year, category, and matched-status filters define the working period. A selected bank row can be categorized, matched to an existing business "
        "record, or marked reviewed. Transactions checked for profit and loss are included only when they are not already matched, preventing duplicate expense "
        "recognition. The summary compares debits, credits, unmatched values, and the ending balance. Live-bank synchronization can be configured separately for each store."
    ),
    "product-costs": (
        "Product Costs turns invoice details into a supplier-cost history. Search and filters help locate a product, vendor, category, or recent change. "
        "Each row shows the latest unit cost, previous cost, change percentage, supplier, invoice date, and update source. New costs normally arrive through "
        "invoice import, keeping the figures tied to supporting documents. Authorized corrections are traceable, selected records can be reviewed or removed, "
        "and the full list can be exported for pricing analysis."
    ),
    "price-alerts": (
        "Price Alerts focuses attention on supplier changes that may affect margin. The user selects an alert, marks it read, reviews the old and new unit costs, "
        "and resolves it after a pricing decision is made. Category, supplier, status, and priority filters make large alert lists manageable. Administrators can define "
        "the minimum percentage threshold, choose automatic alert creation, update selected records, mark all alerts read, or delete records that are no longer needed."
    ),
    "scheduling": (
        "Scheduling begins with a weekly planner designed for the whole team. Employees appear as rows and the seven days appear as columns, allowing a manager to create "
        "the complete week in one place. Common shift descriptions can be entered quickly, previous weeks can be copied, and off days are clearly identified. Saving preserves the "
        "weekly plan, while PDF export produces a professional employee schedule with off days highlighted in red. The employee-schedule editor provides detailed start time, end time, "
        "unpaid break, status, and notes for individual shifts. Managers can also review, publish, cancel, or correct schedules. Approved hours flow into payroll, eliminating the need to re-enter "
        "the same time information. Optional text-message configuration and delivery history remain protected behind developer access until a messaging provider is configured."
    ),
    "payroll": (
        "Payroll connects employees, approved hours, calculations, checks, pay stubs, and history. Employee records hold identity, pay rate, withholding setup, employment status, and work-state information. "
        "The hours screen summarizes regular, overtime, holiday, and other approved time. When a payroll run is created, the user selects the pay period and pay date, reviews every employee, and calculates gross pay, "
        "federal withholding, Social Security, Medicare, state withholding, deductions, and net pay using the signed tax-rule package. The run stays in draft until the authorized reviewer confirms it. Finalizing locks the payroll history "
        "and enables checks and pay stubs. Previous runs remain available for audit and year-to-date review. This demonstration does not transmit tax payments; any future filing or payment connection would require approved provider enrollment and accountant-controlled authorization."
    ),
    "profit-loss": (
        "Profit and Loss converts operating activity into a clear management statement. Revenue comes from consolidated Cash and Sales Summary reports rather than individual register batches, preventing duplicate sales. "
        "Purchases provide cost of goods sold, while cash payouts, checks, payroll, and selected unmatched bank activity contribute to expenses. The screen shows total income, gross profit, expenses, net profit, margin, a trend view, "
        "and expense categories. Date, grouping, and store controls let the owner analyze the required period, and the completed statement can be exported for an accountant or business review."
    ),
    "reports": (
        "The Reports center provides Sales Summary, Shift Log, Cash On Hand, Check Payouts, Profit and Loss, and Payroll reporting. Choose the report type, date period, output format, detail level, and whether supporting documents should be included. "
        "Quick-view buttons move directly to common reports. Generate Report creates a preview and a history entry, while Print, PDF export, combined report packages, and Excel export support different review needs. Stored source documents remain available through the appropriate transaction section. "
        "The reporting period always follows the selected store, so data from separate businesses is not combined unless a specifically authorized consolidated report is created."
    ),
    "administration": (
        "Store administration controls how multiple licensed businesses appear under the account. An owner can review connected databases, choose the default store, arrange store order, add an authorized business, or disconnect one from the current login without deleting its database. "
        "User Accounts gives each employee a separate sign-in and role. Owners and administrators can control access to sensitive sections, while managers receive only the permissions needed for daily work. Store and user changes are restricted to authorized accounts, and developer-only integrations remain protected by the separate developer password."
    ),
    "closing": (
        "Hee-saab Kee-taab Works brings retail operations, cash control, employees, and reporting into one organized Windows application. Contact us to schedule your demonstration."
    ),
}


async def synthesize(text: str, output: Path) -> None:
    communicate = edge_tts.Communicate(text, VOICE, rate="-3%", volume="+0%")
    await communicate.save(str(output))


def probe_duration(ffprobe: str, path: Path) -> float:
    result = subprocess.run(
        [ffprobe, "-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", str(path)],
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


async def main() -> None:
    if len(sys.argv) != 6:
        raise SystemExit("usage: generate_detailed_demo_narration.py CUES WORK OUT_AUDIO OUT_SRT FFPROBE")

    cues_path, work_dir, audio_path, srt_path, ffprobe = (
        Path(value).resolve() for value in sys.argv[1:]
    )
    work_dir.mkdir(parents=True, exist_ok=True)
    cues: list[tuple[float, str]] = []
    for line in cues_path.read_text(encoding="utf-8").splitlines():
        if not line.strip():
            continue
        seconds, key = line.split("|", 1)
        cues.append((float(seconds), key.strip()))
    if not cues or cues[-1][1] != "END":
        raise RuntimeError("Presentation cues are incomplete.")

    ffmpeg = str(Path(ffprobe).with_name("ffmpeg.exe"))
    segments: list[Path] = []
    subtitle_blocks: list[str] = []

    if cues[0][0] > 0:
        initial = work_dir / "audio-00.m4a"
        subprocess.run([
            ffmpeg, "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "anullsrc=r=48000:cl=mono",
            "-t", f"{cues[0][0]:.3f}", "-c:a", "aac", "-b:a", "160k", str(initial)
        ], check=True)
        segments.append(initial)

    for index, ((start, key), (end, _)) in enumerate(zip(cues, cues[1:]), start=1):
        text = NARRATION.get(key)
        if text is None:
            raise KeyError(f"Missing narration for cue: {key}")
        allotted = max(1.0, end - start)
        speech = work_dir / f"speech-{index:02}.mp3"
        await synthesize(text, speech)
        speech_duration = probe_duration(str(ffprobe), speech)
        if speech_duration > allotted - 0.8:
            raise RuntimeError(f"Narration for {key} is {speech_duration:.1f}s but the scene is only {allotted:.1f}s.")
        segment = work_dir / f"audio-{index:02}.m4a"
        subprocess.run([
            ffmpeg, "-hide_banner", "-loglevel", "error", "-y", "-i", str(speech),
            "-af", f"adelay=450|450,apad=pad_dur={allotted:.3f}", "-t", f"{allotted:.3f}",
            "-c:a", "aac", "-b:a", "160k", "-ar", "48000", str(segment)
        ], check=True)
        segments.append(segment)
        subtitle_blocks.append(
            f"{index}\n{srt_time(start + 0.45)} --> {srt_time(min(end - 0.25, start + speech_duration + 0.65))}\n"
            f"{text.replace('Hee-saab Kee-taab', 'HISAB KITAB')}\n"
        )

    concat_file = work_dir / "audio-concat.txt"
    concat_file.write_text("\n".join(f"file '{path.as_posix()}'" for path in segments), encoding="utf-8")
    subprocess.run([
        ffmpeg, "-hide_banner", "-loglevel", "error", "-y", "-f", "concat", "-safe", "0", "-i", str(concat_file),
        "-c", "copy", str(audio_path)
    ], check=True)
    srt_path.write_text("\n".join(subtitle_blocks), encoding="utf-8")
    (work_dir / "narration-manifest.json").write_text(
        json.dumps({"voice": VOICE, "chapters": [key for _, key in cues[:-1]]}, indent=2), encoding="utf-8"
    )


if __name__ == "__main__":
    asyncio.run(main())
