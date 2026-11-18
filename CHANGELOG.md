# Changelog

All notable changes to the **FreeAutocorrect** project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [Unreleased]

### Added
- Planned: Customizable audio profiles and sound scheme selection.
- Planned: Quick-toggle global hotkey for application filtering modes.

---

## [V0.0.31] - 2026-11-18

### Added
- **Acoustic Feedback Options (`SoundFeedbackService`)**:
  - Created `Services/SoundFeedbackService.cs` delivering lightweight, 100% offline acoustic cues for real-time auto-corrections and accepted suggestions.
  - Added `PlaySoundOnSuggestion` configuration property in `AppSettings` alongside `PlaySoundOnCorrection`.
  - Added dedicated checkbox toggle in Live Autocorrect Dashboard: `Play subtle sound when a suggestion / grammar fix is accepted`.
  - Integrated acoustic cues into active typing auto-correction (`SoundFeedbackService.PlayCorrectionSound`), floating widget suggestion cards (`SoundFeedbackService.PlaySuggestionSound`), live suggestion banner, and Assistant Studio editor replacements.
  - Safe exception containment ensures zero hardware crashes on headless, muted, or audio-disabled systems.
- **Quick Application Filter Presets**:
  - Added 1-click configuration buttons under Settings > Target Applications & Window Filter:
    - `🏢 Office Suite`: Word (`winword.exe`), Excel (`excel.exe`), Outlook (`outlook.exe`), PowerPoint (`powerpnt.exe`), OneNote (`onenote.exe`).
    - `🌐 Web Browsers`: Chrome (`chrome.exe`), Edge (`msedge.exe`), Firefox (`firefox.exe`), Brave (`brave.exe`), Opera (`opera.exe`), Vivaldi (`vivaldi.exe`).
    - `💬 Messaging & Social`: Slack (`slack.exe`), Discord (`discord.exe`), Teams (`teams.exe`), Telegram (`telegram.exe`), WhatsApp (`whatsapp.exe`).
    - `📝 Notes & Editors`: Notepad (`notepad.exe`), Obsidian (`obsidian.exe`), OneNote (`onenote.exe`), WordPad (`wordpad.exe`).
    - `🔄 Reset to Defaults`: Restores default blacklist filtering (`explorer.exe`, `shellexperiencehost.exe`, `searchhost.exe` excluded).
  - Added `ApplyAppPreset` and `ResetAppFiltersToDefault` methods in `AppSettings` with automatic `.exe` normalization, duplicate suppression, and instant persistence.
- **Self-Test Suite 23**: Added automated testing for sound feedback safe execution (enabled and disabled), 1-click filter presets, duplicate suppression, reset to defaults, and UI controls.

---

## [V0.0.30] - 2026-11-18

### Added
- **In-Place Undo Toast Notification (`⤺ Undo Fix`)**:
  - Applying any correction (via single card fix, quick replacement chips, live suggestion banner, or keyboard accelerator) displays a temporary undo toast (`⤺ Undo (Ctrl+Z): typo ➔ fix`).
  - Clicking `⤺ Undo` or pressing `Ctrl+Z` reverts the active replacement back to the original typo in both selected text mode and active application typing, restoring the original issue item into the suggestions list.
  - Undo toast auto-dismisses after 6 seconds of inactivity.
- **Custom Dictionary Export & Import**:
  - Added `📥 Export (.txt)` in Settings to export all custom words to a text file (one word per line) or JSON format.
  - Added `📤 Import (.txt / .json)` in Settings supporting bulk dictionary imports, automatic deduplication against existing words, runtime registration, and settings synchronization.
- **Self-Test Suite 22**: Added automated testing for undo toast tracking, replacement reversion, text/JSON dictionary import parsing, and duplicate suppression.

---

## [V0.0.29] - 2026-11-18

### Added
- **Quick-Add Custom Dictionary Action from In-Card Context Snippet**: Added an inline `+ Add "{typo}"` action chip inside the in-card context snippet preview in both the Floating Widget dropdown and Assistant Studio for spelling errors, instantly registering unrecognized domain terms and project names into the offline custom dictionary.
- **Runtime Word Registration in AutoCorrectDictionary**: Added `AddCustomWord(string word)` and `IsCustomWordOrValid` in `AutoCorrectDictionary`, ensuring custom words are immediately recognized and excluded from typos and Levenshtein matches without restarting the app.
- **Numbered Keyboard Accelerators (`[1]`, `[2]`, `[3]`, `[D]`)**:
  - In-card quick-replacement chips now display keyboard accelerator hints (`[1] ✓ Replace: candidate`, `[2] ✓ candidate`, `[3] ✓ candidate`, `[D] + Add "{typo}"`).
  - Added `OnPreviewKeyDown` in `FloatingWidget` allowing users to press `1`, `2`, or `3` to instantly apply replacements and `D` (or `Ctrl+D`) to add to custom dictionary when a card is expanded.
- **Bi-Directional Dictionary Sync Event**: Added `OnWordAddedToDictionary` on `FloatingWidget` wired to `MainWindow` and `AutoCorrectDictionary`, keeping Assistant Studio, the floating widget, and the background keyboard hook synchronized.
- **Self-Test Suite 21**: Added automated testing for runtime custom dictionary registration, typo suppression, in-card dictionary addition, and event dispatch.

---

## [V0.0.28] - 2026-11-18

### Added
- **Interactive Inline Quick-Replacement Chips**: The in-card context snippet preview in both the Floating Widget dropdown and Assistant Studio now renders interactive quick-replacement chips for the primary suggestion and up to 2 candidate alternatives, allowing 1-click replacement with candidate selection directly from within the preview panel.
- **Protected Technical Content Pill Indicator**: Added a dedicated `🛡️ Protected` status pill in both the Floating Widget expanded view and Assistant Studio displaying dynamic summaries of protected fenced code blocks, inline backticks, file paths, and file names (e.g. `💻 2 code block(s) & 3 technical span(s) protected`).
- **Clean Suggestion Extraction & Deduplication**: Added `LintItem.CleanSuggestionText` and `GetCleanSuggestions()` to strip engine boilerplate (such as `Replace with: “...”` or `Remove: “...”`) and deduplicate candidate suggestions across cards.
- **Self-Test Suite 20**: Added automated testing for candidate cleaning, alternative suggestions extraction, technical spans summary computation, and protected pill visibility contracts.

### Changed
- **Assistant Studio In-Card Context Viewer**: Clicking `👁️ Show` in Assistant Studio now highlights the text selection and toggles an in-card context snippet panel with `#38BDF8` focus border and interactive replacement chips.
- **Candidate Replacement Re-Alignment**: `ApplySingleFix` now accepts custom replacement candidates, applying the chosen alternative and updating character offset spans.

---

## [V0.0.27] - 2026-11-18

### Added
- **In-Card Context Snippet Viewer**: Clicking `👁️ Show` on any floating widget suggestion card now toggles an in-card context preview (`... [typo] ...`), providing immediate visual context with a sky-blue card focus highlight even when targeting non-UIA or legacy applications.
- **Studio & Floating Widget Bi-Directional Highlighting**: Clicking `👁️ Show` in the floating widget directly selects the error span in Assistant Studio when active.
- **Markdown Code & Inline Span Protection**: Fenced code blocks (` ```...``` `, `~~~...~~~`) and inline code spans (`` `...` ``) are automatically identified and excluded from spell-checking and suggestions.
- **SemVer & Environment Variable Protection**: Added detection for SemVer version strings (`v1.2.3`, `0.0.27`, `2.0.0-rc.1`) and environment variables (`%APPDATA%`, `$PATH`, `${HOME}`).
- **Self-Test Suite 19**: Added automated verification for in-card context snippet generation, markdown code fence exclusion, inline backtick protection, SemVer/env var parsing, and studio integration.

### Changed
- **Extended Settings Model**: Added `IgnoreFileNamesAndPaths`, `IgnoreCodeAndTechnicalIdentifiers`, and `IgnoreMarkdownCodeBlocks` configuration properties in `AppSettings`.

---

## [V0.0.26] - 2026-11-18

### Added
- **Knowledge of 120+ File Types & Extensions**: Integrated `PathAndFileFilter` engine featuring comprehensive definitions for programming, web, documents, archives, configs, audio, video, graphics, and fonts (`.exe`, `.docx`, `.pdf`, `.png`, `.json`, `.cs`, `.py`, `.ts`, `.html`, `.css`, `.zip`, `.tar.gz`, etc.).
- **File Name Recognition & Protection**: Autocorrect and suggestion engine now automatically identifies file names (e.g. `report.docx`, `index.html`, `FreeAutocorrect.csproj`, `archive.tar.gz`, `image_01.png`, `README.md`, `.gitignore`) and ignores them completely, suppressing false positives on file stems or extensions.
- **Directory Path Protection**: Windows drive paths (`C:\...`, `c:/...`), Unix paths (`/usr/bin/`), UNC network shares (`\\server\share`), and relative folder paths (`./src/components/`, `..\models\`, `bin\Release\net9.0-windows\`) are strictly excluded from autocorrect, auto-capitalization, and suggestions.
- **Show Icon Before Fix Errors**:
  - Floating suggestion cards now feature an `👁️ Show` action button positioned right before the `✓ Accept` fix button to locate and highlight errors in the active document prior to applying changes.
  - Added an `👁️` preview indicator badge before the error/fix visual diff row on every suggestion card.
  - Added an `👁️ Show` button before the `✓ Replace` button in the live suggestion notification banner.
  - Added an `👁️ Show All` button before `⚡ Accept All` in the floating widget dropdown.
- **Self-Test Suite 18**: Automated tests validating file types knowledge, file name ignoring, directory path protection, phantom error suppression, and the show icon before fix errors UI.

### Changed
- **Phantom Error Suppression**: Stripped false-positive suggestions generated by technical code symbols (CamelCase, PascalCase, snake_case, alphanumeric mixes like `net9`, `win32`, `x64`, hex `0x1F`, and CLI flags `--test`).

### Fixed
- **Period Delimiter False Sentence Capitalization**: Resolved an issue where words following dots inside filenames (e.g. `txt` in `file.txt`) or technical abbreviations were incorrectly flagged as uncapitalized sentence starts.

---

## [V0.0.25] - 2026-11-17

### Added
- **Selected Text Snippet Preview**: Floating widget dropdown now features a dedicated active selection preview banner displaying the exact highlighted snippet being audited.
- **Import Selected Text in Assistant Studio**: Added one-click "🎯 Import Selected Text" toolbar action to Assistant Studio, instantly transferring highlighted text from any external application (Chrome, Word, Notepad) into the studio scratchpad.
- **Modernized Floating Widget Context Menu**: Added one-click entries for "🎯 Check Selected Text (Ctrl+Shift+G)", "⚡ Accept All Suggestions", and "📝 Open Assistant Studio".
- **Comprehensive Project Changelog & Documentation**: Initialized full `CHANGELOG.md` from the project inception and updated `README.md` with Grammarly 4-pillar architecture.

### Changed
- **Multi-Fix Span Re-Alignment**: In selected text mode, applying single fixes sequentially now automatically re-aligns the start and end character offsets of remaining suggestions by the fix length delta, preventing replacement drift or corruption.

### Fixed
- **Dispatcher Thread Safety**: Streamlined clipboard and UI Automation calls across background threads with dedicated STA execution helpers to prevent WPF Dispatcher deadlocks.

---

## [V0.0.24] - 2026-11-17

### Added
- **Multi-Tier Selected Text Extraction**:
  - **Tier 1 (UI Automation)**: Extracts selected text natively via `TextPattern.GetSelection()` without touching the system clipboard.
  - **Tier 2 (Win32 Edit)**: Queries selection bounds in native edit controls using `EM_GETSEL` and retrieves text via `WM_GETTEXT`.
  - **Tier 3 (Focused Clipboard Fallback)**: Automatically releases physical modifier keys (`Ctrl`, `Shift`, `Alt`) before simulating `Ctrl+C` to prevent keystroke corruption.
- **Active Selected Text Indicator**: Floating widget displays a `[🎯 Active Selected Text]` pill when operating on user-highlighted text.
- **Self-Test Suite 16**: Added automated verification for selected text extraction contracts, fail-safe replacement, physical modifier release, and widget selection mode.

### Fixed
- **Physical Shift Key Conflict on Hotkey**: Resolved a critical issue where pressing `Ctrl+Shift+G` sent `Ctrl+Shift+C` into browsers and editors (triggering DevTools instead of copy).
- **Target App Window Focus Restoration**: Added `ActivateWindow()` with Win32 `AttachThreadInput` + `SetForegroundWindow` prior to any replacement action.
- **In-Place Selection Replacement**: Fixed `ApplySingleFix` and `ApplyAll_Click` in selected text mode to replace the highlighted selection via `EM_REPLACESEL` or focused clipboard paste instead of sending backspaces.

---

## [V0.0.23] - 2026-11-07

### Added
- **Official Grammarly 4-Pillars System**:
  - 🔴 **Correctness** (`#EF4444`): Spelling, grammar, punctuation, and typographical mistakes.
  - 🔵 **Clarity** (`#0284C7`): Conciseness, wordy phrases, passive voice, readability, and sentence flow.
  - 🟢 **Engagement** (`#10B981`): Vocabulary variety, repetitive words, and lexical enhancements.
  - 🟣 **Delivery** (`#8B5CF6`): Tone detection (confident, polite, friendly, formal), impression, and formatting.
- **Grammarly Performance Scorecard**:
  - Circular performance score dial (0–100) with rating badges (*Exceptional*, *Great job!*, *Looking good*, *Needs polish*).
  - Tone detector badge with real-time emoji indicators (e.g. `💼 Formal & Academic`, `😊 Warm & Friendly`, `🎯 Confident & Convincing`, `⚡ Direct & Concise`).
  - Document metrics: Word count, character count, estimated reading time, and estimated speaking time.
  - Category filter pills for Correctness, Clarity, Engagement, and Delivery.
- **Self-Test Suite 14**: Added automated tests for pillar classification, score calculation, tone detection, and scorecard UI rendering.

### Changed
- Transformed the floating widget dropdown and Assistant Studio right-hand sidebar into the official Grammarly for Windows UI layout.

---

## [V0.0.22] - 2026-11-07

### Added
- **Per-Application Process Filtering**:
  - Disabled autocorrect in `explorer.exe` (Windows Explorer) by default to prevent unwanted replacements during file renaming and navigation.
  - Added "Enable in Windows Explorer" toggle in Settings and Live Autocorrect tabs.
  - Added support for Blacklist (`AllExceptExcluded`) and Whitelist (`OnlyIncluded`) application modes.
- **Launch on Windows Startup**: Added auto-start setting backed by `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.
- **Suggestions Dropdown on Floating Button Click**: Clicking the floating pill now opens the suggestions dropdown directly on the desktop.
- **Self-Test Suite 13**: Added automated tests for process filtering, registry auto-start, and dropdown toggle.

---

## [V0.0.21] - 2026-11-06

### Added
- **Link & URL Protection**:
  - Prevented autocorrect from modifying URLs, web addresses (`http://`, `https://`, `ftp://`), domains (`.com`, `.org`, `.net`, `.io`), email addresses (`user@domain.com`), and markdown link targets.
  - Preserved spell-checking and grammar corrections in normal prose surrounding links.
- **Self-Test Suite 12**: Verified zero false-positive suggestions across web addresses and emails.

---

## [V0.0.20] - 2026-11-06

### Added
- **Strict Editable-Only Text Filtering**:
  - Filtered document scanning strictly for active editable elements (`ControlType.Edit`, `ControlType.Document` with active caret, or non-readonly `ValuePattern`).
  - Suppressed inspections on static web pages and read-only controls.
- **Word Prefix Recovery Across Pauses**: Restored full word continuity when the user pauses typing, switches windows, or resumes writing mid-word.
- **Instant Fault Recognition**: Accelerated dictionary lookup dispatch to under 2ms.

---

## [V0.0.19] - 2026-11-05

### Added
- **Draft Mail & Pre-Existing Document Scanning**:
  - Automatically scans existing draft emails and documents upon focus or click via UI Automation.
  - Seeded word context from pre-existing letters so editing mid-draft retains full sentence and spelling awareness.
  - In-place UIA typo replacement for active document controls.

---

## [V0.0.18] - 2026-11-05

### Added
- **Diagnostic Analysis Logging**:
  - Integrated real-time diagnostic engine logging to `%APPDATA%\FreeAutocorrect\autocorrect.log`.
  - Added log viewer, clear log, and refresh actions in the Settings tab.
- **Chromium Keystroke Cadence Optimization**:
  - Removed artificial sleep latencies and eliminated backspace coalescing in Chromium and Electron browsers.

---

## [V0.0.17] - 2026-11-04

### Added
- **Top-Left Corner Docking & Auto-Dock**:
  - Moved floating widget default position to the top-left corner of the primary screen.
  - Added smooth auto-dock timer that returns the widget to the top-left after inactivity.

---

## [V0.0.16] - 2026-11-04

### Fixed
- **Chromium/Opera/Antigravity Word Removal**:
  - Resolved word replacement issues in Chromium browsers by enforcing cadence separation between backspaces and replacement keystrokes.
  - Reliable target window activation prior to simulated inputs.

---

## [V0.0.15] - 2026-11-03

### Added
- **Massive Whole-Dictionary Scale**:
  - Expanded dictionary to over 30,000 common English typos and misspellings.
  - Prioritized suggestions for common, easy words (C2 level).
  - Synchronized tray and window settings state in real time.

---

## [V0.0.14] - 2026-11-03

### Fixed
- **Capitalization Safety**:
  - Prevented sentence auto-capitalization from triggering mid-word or after decimals and abbreviations.
  - Preserved previously written word letters when rewriting words.

---

## [V0.0.13] - 2026-11-02

### Fixed
- **Floating Widget Jumps**: Resolved floating widget repositioning glitch after applying suggestions.
- **Atomic Keystroke Input**: Prevented leftover characters during fast typing replacements.

---

## [V0.0.12] - 2026-11-02

### Added
- **Wavy Squiggly Underlines**: Rendered smooth wavy lines beneath flagged errors in Assistant Studio.
- **Extra Space & Annotation Detection**: Flagged double spaces, misplaced punctuation spacing, and editorial notes (`TODO:`, `[citation needed]`).

---

## [V0.0.11] - 2026-11-01

### Fixed
- **Lingering Suggestions**: Automatically dismisses suggestions and banners when the user corrects the word manually.
- **Modernized Fix UI**: Polished replacement badges and suggestion card layout.

---

## [V0.0.10] - 2026-11-01

### Added
- **Sentence Auto-Capitalization**: Capitalizes the first letter of sentences following `.`, `!`, or `?`.
- **Accidental Double Capitals Fix**: Automatically corrects words starting with two capital letters (e.g. `THis` → `This`).

---

## [V0.0.9] - 2026-10-31

### Added
- **Multi-Resolution Taskbar Icon**: Added high-DPI Windows application icon (`Assets/icon.ico`).
- **Ultra-Compact Floating Icon**: Minimized default floating widget to a 28x28 icon with right-click menu.

---

## [V0.0.8] - 2026-10-31

### Fixed
- **Modifier Key Release on Auto-Finish**: Released held modifier keys before completing words.
- **Overlay Focus Prevention**: Added `WS_EX_NOACTIVATE` and `WS_EX_TOOLWINDOW` to floating widget to prevent stealing focus from active apps.

---

## [V0.0.7] - 2026-10-30

### Added
- **Continuous Multi-Line Linting**: Audited rolling buffers across multiple lines and previous sentences.
- **High-Contrast UI Theme**: Refined dark slate styling for optimal readability.

---

## [V0.0.6] - 2026-10-30

### Added
- **Configurable Auto-Finish Shortcuts**: Supported Tab+Right, Tab, Ctrl+Tab, Right Arrow, Shift+Tab, and Alt+Right for word completion.

---

## [V0.0.5] - 2026-10-29

### Added
- **Tab+RightArrow Auto-Finish**: Suffix word completion while typing.
- **Hard Word Candidate Underlines**: Detected unknown words and displayed top dictionary alternatives.

---

## [V0.0.4] - 2026-10-29

### Changed
- Configured default auto-finish key to `Ctrl+Tab` for universal application compatibility.

---

## [V0.0.3] - 2026-10-28

### Added
- **Tab Word Completion**: Auto-finish prefix matches while typing.
- **Daily Dictionary Auto-Updater**: Background dictionary enrichment from open-source wordlists.
- **Multi-Word Phrase Corrections**: Corrected two-word grammatical phrases (e.g. `a apple` → `an apple`, `could of` → `could have`).

---

## [V0.0.2] - 2026-10-28

### Fixed
- **64-Bit SendInput Structure Alignment**: Fixed native interop struct padding for 64-bit Windows.
- **Dual Fallback Typing**: Enhanced character insertion fallback using `keybd_event` and `WM_CHAR`.
- **UTF-8 Encoding for Wordlists**: Fixed dictionary file reading across international Windows locales.

---

## [V0.0.1] - 2026-10-27

### Fixed
- **System Tray Minimize**: Corrected window close and minimize events to reliably dock to the notification area.
- **Chromium Backspace Timing**: Adjusted backspace insertion timing for Electron, Chrome, and VS Code.

---

## [V0.0.0] - 2026-10-27

### Initial Release
- **Offline Rust Core**: High-performance grammar checking powered by embedded Harper engine (`harper-cli.exe`).
- **Real-Time Keystroke Interception**: Low-level Win32 keyboard hook (`WH_KEYBOARD_LL`) for live typo corrections.
- **Single Instance Mutex**: Windows named Mutex preventing duplicate running instances.
- **System Tray Docking**: Background operation with notification icon and quick settings.
