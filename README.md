# FreeAutocorrect 🚀

[![Version](https://img.shields.io/badge/version-V0.0.33-blue.svg)](FreeAutocorrect.csproj)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%20%2F%2011-lightgrey.svg)](https://microsoft.com)
[![Engine](https://img.shields.io/badge/engine-Harper%20Rust%20Core-orange.svg)](https://github.com/Automattic/harper)
[![Privacy](https://img.shields.io/badge/privacy-100%25%20Offline%20%26%20Private-success.svg)](#-privacy--security)
[![License](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

> A free, high-performance, **100% offline-first Grammarly alternative for Windows**. Features continuous typing auto-correction, Grammarly-style 4-pillar suggestions (Correctness, Clarity, Engagement, Delivery), circular performance scorecards, tone detection, and multi-tier selected text checking across all Windows applications. Powered by the open-source **Harper** Rust grammar engine.

---

## 🌟 Key Highlights

- **🔒 100% Private & Fully Offline:** Zero cloud tracking, zero telemetry, and zero subscription costs. Your keystrokes and documents never leave your local machine.
- **⚡ System-Wide Continuous Auto-Correction:** Runs silently in the background and intercepts keystrokes across **all Windows applications** (Word, Notepad, Google Chrome, Slack, Discord, Opera, VS Code, etc.).
- **🎯 Grammarly 4-Pillars System:** Categorizes suggestions into **Correctness**, **Clarity**, **Engagement**, and **Delivery** with custom color-coded badges and inline cards.
- **📊 Performance Score & Tone Detector:** Real-time circular score dial (0–100) and multi-tone analyzer (*Formal & Academic*, *Confident*, *Warm & Friendly*, *Direct & Concise*).
- **🖱️ Selected Text Auditing (`Ctrl+Shift+G`):** Highlight text in any application and press `Ctrl+Shift+G` (or right-click the widget) to audit only that snippet with in-place safe replacement.
- **📥 Import Selected Text into Studio:** Instant one-click import from any active document or browser into Assistant Studio for distraction-free editing.
- **🛡️ Process Filtering:** Built-in safeguards preventing interference in Windows Explorer (`explorer.exe`) during file renaming, with full whitelist/blacklist control.
- **🚀 Windows Auto-Start:** Integrated toggle to automatically launch on system boot.
- **📌 System Tray Docking & Single Instance:** Docks cleanly to the Windows notification tray; enforced single instance via named Mutex and Named Pipe activation.

---

## ✨ Feature Deep Dive

### 1. Grammarly 4-Pillars Suggestion Architecture

Every detected issue is audited against the Harper Rust grammar engine and mapped into one of four distinct pillars:

| Pillar | Color | Description | Example Checks |
| :--- | :---: | :--- | :--- |
| **🔴 Correctness** | `#EF4444` | Grammar, spelling, punctuation, typos, and syntax errors | `teh` → `the`, subject-verb agreement, repeated words |
| **🔵 Clarity** | `#0284C7` | Redundancy, wordiness, passive voice, and readability | `in order to` → `to`, simplify sentences, remove filler |
| **🟢 Engagement** | `#10B981` | Vocabulary variety, strong verbs, and repetition reduction | Monotonous phrasing, dynamic word alternatives |
| **🟣 Delivery** | `#8B5CF6` | Tone alignment, politeness, confidence, and formality | Slang in formal emails, hedging phrases |

### 2. Selected Text Checking & In-Place Replacement

FreeAutocorrect introduces a multi-tier selection engine to inspect highlighted text without touching or corrupting clipboard history:

- **Tier 1 (UI Automation TextPattern):** Queries active UI elements natively via Windows UI Automation to extract highlighted text without clipboard interactions.
- **Tier 2 (Win32 Native Edit Controls):** Directly accesses native controls (`EM_GETSEL`, `WM_GETTEXT`) in classic applications.
- **Tier 3 (Protected Focused Clipboard Fallback):** Automatically releases any physically held modifier keys (`Shift`, `Ctrl`, `Alt`) before triggering copy, preventing hotkey conflicts such as accidental DevTools triggers in browsers.
- **Active Selection Preview Banner:** Floating widget displays a dedicated `[🎯 Active Selected Text]` banner showing the quoted snippet currently being audited.
- **Sequential Span Re-Alignment:** Applying single fixes one-by-one automatically re-aligns remaining character offsets (`CharStart` / `CharEnd`) by the length difference of previous replacements, guaranteeing 100% position accuracy without drift.

### 3. Continuous Typing Auto-Correction

- **Instant Typo Correction:** Auto-corrects thousands of frequent English typos instantly upon pressing Space or punctuation (e.g. `teh ` → `the `, `seperate ` → `separate `, `recieve ` → `receive `, `dont ` → `don't `).
- **Smart Double-Capitals:** Automatically repairs accidental double capitalization (e.g. `THis ` → `This `, `WOrk ` → `Work `).
- **Case Preservation:** Maintains proper casing across replacements (`teh` → `the`, `Teh` → `The`, `TEH` → `THE`).
- **Sentence Auto-Capitalization:** Automatically capitalizes the first word after punctuation marks (`.`, `!`, `?`).
- **URL & Link Guard:** Ignores URLs, domains, file paths, and email addresses (`https://...`, `user@example.com`) to prevent accidental replacements while typing links.

### 4. Floating Suggestion Pill & Context Menu

- **Desktop Floating Overlay:** A subtle, draggable floating button appears on screen showing live suggestion counts.
- **Interactive Quick-Fix Dropdown:** Click the pill to expand a clean suggestion panel showing diffs (`typo ➔ correction`), explanations, and one-click accept buttons.
- **Context Menu:** Right-click the floating widget to quickly:
  - 🎯 **Check Selected Text (Ctrl+Shift+G)**
  - ⚡ **Accept All Suggestions**
  - 📝 **Open Assistant Studio**
  - ⚙️ **Open Settings**
  - ✕ **Hide to Tray**

### 5. File Types, File Names & Directory Paths Protection

- **120+ Known File Types:** Full offline catalog of programming, web, documents, graphics, media, binaries, and archive formats (`.exe`, `.docx`, `.pdf`, `.png`, `.json`, `.cs`, `.py`, `.ts`, `.html`, `.css`, `.zip`, `.tar.gz`, etc.).
- **Automatic File Name Exclusion:** Recognizes file names with extensions (e.g. `report.docx`, `index.html`, `FreeAutocorrect.csproj`, `archive.tar.gz`, `image_01.png`, `README.md`, `.gitignore`, `docker-compose.yml`) and ignores them completely.
- **Directory Paths Protection:** Windows drive paths (`C:\...`, `c:/...`), Unix paths (`/usr/bin/`), UNC network shares (`\\server\share`), and relative folder paths (`./src/components/`, `..\models\`, `bin\Release\net9.0-windows\`) are strictly excluded from autocorrect, auto-capitalization, and suggestions.
- **Markdown Code & Inline Span Protection:** Fenced code blocks (` ```...``` `, `~~~...~~~`) and inline code spans (`` `...` ``) are automatically identified and excluded from spell-checking and suggestions.
- **Phantom Error Suppression:** Automatically eliminates false positives from code symbols (CamelCase, PascalCase, snake_case, alphanumeric mixes like `net9`, `win32`, `x64`, hex `0x1F`, SemVer `v1.2.3`, environment variables `%APPDATA%`, and CLI flags `--test`).

### 6. Show Icon Before Fix Errors & In-Card Context Viewer

- **`👁️ Show` Before Fix:** Floating suggestion cards feature an `👁️ Show` action button positioned right before the `✓ Accept` fix button, allowing users to locate and highlight the error in the document before applying changes.
- **In-Card Context Snippet Viewer:** Clicking `👁️ Show` toggles an in-card surrounding context viewer (`... [typo] ...`) with sky-blue card focus, guaranteeing instant visual context even on legacy or non-UIA applications.
- **Interactive Inline Quick-Replacement Chips:** The in-card context snippet preview in both the Floating Widget dropdown and Assistant Studio renders clickable quick-replacement chips for the primary suggestion and alternative candidates, allowing instant 1-click replacement directly from within the preview panel.
- **Numbered Keyboard Accelerators (`[1]`, `[2]`, `[3]`, `[D]`):** Press `1`, `2`, or `3` to instantly apply candidate replacements, or `D` (or `Ctrl+D`) to add unrecognized domain terms and names directly to the offline custom dictionary.
- **In-Place Undo Toast (`⤺ Undo Fix`):** Immediate undo notification banner allows users to revert an accepted fix with a single click or `Ctrl+Z`, restoring original text in both active typing and selected text modes.
- **Custom Dictionary Export & Import:** Settings panel supports one-click dictionary export and bulk import in plain text (`.txt`) and `.json` formats with automated duplicate suppression.
- **Quick-Add Custom Dictionary Action:** Spelling cards feature an inline `+ Add "{typo}"` chip that immediately registers custom terms in `AutoCorrectDictionary` and `AppSettings`, preventing future phantom warnings.
- **Protected Technical Content Pill Indicator:** Displays a dedicated `🛡️ Protected` status pill in both the Floating Widget expanded view and Assistant Studio, showing real-time counts of protected code fences, inline spans, file paths, and file names (e.g. `💻 2 code block(s) & 3 technical span(s) protected`).
- **Visual Diff Show Badge:** An `👁️` preview indicator badge sits directly before every error/fix diff row (`👁️ "typo" ➔ "fix"`).
- **`👁️ Show All`:** Action bar provides a one-click button to view and highlight all detected issues across the document before accepting all fixes.
- **Live Banner Show Action:** In the floating live suggestion banner, an `👁️ Show` button is available alongside `✓ Replace`.

### 7. Application Process Filtering & Quick Presets

- **Windows Explorer Protection:** Autocorrect is disabled by default in `explorer.exe` to prevent accidental text changes while renaming files or navigating folders.
- **Blacklist & Whitelist Modes:** Choose between checking all apps except excluded ones, or only checking designated applications.
- **Global Hotkey Filter Toggle (`Ctrl+Shift+F`):** Press `Ctrl+Shift+F` anywhere to toggle Whitelist and Blacklist modes on the fly, with instant notification of whether the active foreground app is enabled.
- **1-Click Application Presets:** Instant configuration buttons in Settings for standard productivity stacks:
  - 🏢 **Office Suite:** Word (`winword.exe`), Excel (`excel.exe`), Outlook (`outlook.exe`), PowerPoint (`powerpnt.exe`), OneNote (`onenote.exe`).
  - 🌐 **Web Browsers:** Chrome (`chrome.exe`), Edge (`msedge.exe`), Firefox (`firefox.exe`), Brave (`brave.exe`), Opera (`opera.exe`), Vivaldi (`vivaldi.exe`).
  - 💬 **Messaging & Social:** Slack (`slack.exe`), Discord (`discord.exe`), Teams (`teams.exe`), Telegram (`telegram.exe`), WhatsApp (`whatsapp.exe`).
  - 📝 **Notes & Editors:** Notepad (`notepad.exe`), Obsidian (`obsidian.exe`), OneNote (`onenote.exe`), WordPad (`wordpad.exe`).
  - 🔄 **Reset to Defaults:** Restores default safe blacklist filtering with one click.
- **Launch on Windows Startup:** Configurable auto-start setting stored in `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.

### 8. Customizable Sound Schemes & Fast Wavy Underlines

- **4 Offline Sound Schemes:** Choose between procedural acoustic themes generated dynamically in memory:
  - 🫧 **Subtle Pop:** Soft, organic downward frequency sweep (520Hz ➔ 260Hz) with smooth decay.
  - ⌨️ **Typewriter Click:** Mechanical keyboard switch transient with tactile texture.
  - 🔔 **Soft Chime:** Gentle two-tone harmonic bell (880Hz / 1760Hz shimmer).
  - 🔊 **Windows Default:** Classic system acoustic cues (`Beep` / `Asterisk`).
- **Sub-Millisecond 2-Phase Checking:** Instantaneous (< 0.2ms) dictionary typo detection on every keypress, followed by background Harper grammar analysis with hash-based LRU caching for zero-latency editing.
- **Line-Aware Wavy Underlines:** Redesigned squiggly underlines calculate character spans line-by-line, eliminating diagonal skewing across line wraps and aligning flawlessly under the text baseline.

---

## 🏗️ Project Architecture

```
FreeAutocorrect/
├── FreeAutocorrect.csproj       # .NET 9 WPF & WinForms project specification
├── App.xaml / App.xaml.cs       # Single-instance mutex check, service bootstrapper
├── MainWindow.xaml / .cs        # Assistant Studio with squiggly layer, scorecard, settings
├── CHANGELOG.md                 # Complete historical changelog from V0.0.0 to V0.0.33
├── README.md                    # Project documentation and user guide
├── harper-bin/                  # Embedded offline Harper Rust grammar binary
│   └── harper-cli.exe
├── Models/
│   ├── LintItem.cs              # Issue models with Grammarly 4-pillars & span offsets
│   ├── CheckResult.cs           # Document statistics, readability score, tone
│   └── AppSettings.cs           # Persistent JSON settings stored in %APPDATA%
├── Services/
│   ├── SoundFeedbackService.cs  # Procedural sound scheme synthesizer (Pop, Click, Chime, Default)
│   ├── PathAndFileFilter.cs     # 120+ file types, file names, paths & phantom error engine
│   ├── ActiveDocumentService.cs # UI Automation & Win32 active selection reader
│   ├── TextAutomationService.cs # Safe Unicode input synthesizer & clipboard helper
│   ├── HarperEngineService.cs   # Offline Harper CLI JSON bridge with hash LRU caching
│   ├── AutoCorrectDictionary.cs # Sub-millisecond typo lookup table & compiled regex parser
│   ├── LowLevelKeyboardHook.cs  # Win32 WH_KEYBOARD_LL low-level keyboard hook
│   ├── GlobalHotkeyService.cs   # Win32 RegisterHotKey engine (Ctrl+Shift+G, Ctrl+Shift+F)
│   ├── ReadabilityService.cs    # Flesch-Kincaid & tone analysis algorithms
│   ├── SingleInstanceManager.cs # Global Named Mutex & Named Pipe IPC
│   ├── TrayIconService.cs       # Windows NotifyIcon with context menu
│   └── SelfTestRunner.cs        # Comprehensive automated self-test verification suite (24 suites)
└── UI/
    ├── FloatingWidget.xaml      # Grammarly-style floating pill overlay with Show before Fix
    └── FloatingWidget.xaml.cs   # Floating widget drag, quick-fix & dropdown logic
```

---

## 🚀 Getting Started

### Prerequisites

- **Windows 10 or Windows 11** (x64)
- **[.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)** (or later)

### Installation & Build

1. **Clone the repository:**
   ```powershell
   git clone https://github.com/Bisrat24/FreeAutocorrect.git
   cd FreeAutocorrect
   ```

2. **Build Release Binary:**
   ```powershell
   dotnet build -c Release
   ```

3. **Run the Application:**
   ```powershell
   .\bin\Release\net9.0-windows\FreeAutocorrect.exe
   ```

### Running Automated Self-Tests

FreeAutocorrect includes a built-in automated test suite covering all 24 feature domains:

```powershell
# Execute all automated self-tests in headless mode
Start-Process -FilePath ".\bin\Release\net9.0-windows\FreeAutocorrect.exe" -ArgumentList "--test" -Wait -NoNewWindow

# View the test results
Get-Content test-results.txt
```

---

## ⌨️ Default Keyboard Shortcuts

| Shortcut | Action | Description |
| :--- | :--- | :--- |
| **`Ctrl + Shift + G`** | **Audit Selected Text** | Checks highlighted text in the active application and displays suggestions in the floating widget. |
| **`Ctrl + Shift + F`** | **Toggle App Filter Mode** | Quickly toggles between Whitelist and Blacklist application filter modes on the fly. |
| **`Ctrl + Tab`** | **Auto-Finish Word** | Completes the current prefix match with the top dictionary candidate. |
| **`Space` / `Punctuation`** | **Auto-Correct Typo** | Instantly replaces known typos upon pressing space or punctuation. |

---

## 🛡️ Privacy & Security

FreeAutocorrect is designed from the ground up for strict privacy:
- **No Cloud APIs:** All grammar checking is performed locally using the embedded Harper Rust engine.
- **No Keystroke Logging:** Keystrokes are evaluated in a rolling in-memory buffer solely for typo matching and are immediately discarded.
- **No Network Tracking:** The application makes zero telemetry or analytics calls.

---

## 📄 License

This project is licensed under the [MIT License](LICENSE).
Harper grammar engine is licensed under the Apache 2.0 License.
