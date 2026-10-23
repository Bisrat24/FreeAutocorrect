# FreeAutocorrect 🚀
> A free, lightweight, 100% offline Grammarly alternative for Windows with continuous typing auto-correction and system tray docking. Powered by the open-source **Harper** Rust grammar engine.

---

## ✨ Features

- **🛡️ 100% Offline & Private:**
  - Uses the **Harper** Rust engine (`harper-cli`) embedded directly into the app.
  - Zero telemetry, zero cloud tracking, zero network requests. Your keystrokes and documents never leave your computer.

- **⚡ System-Wide Continuous Auto-Correction:**
  - Runs in the background and intercepts keystrokes across **all Windows applications** (Word, Notepad, Google Chrome, Slack, Discord, VS Code, etc.).
  - Auto-corrects thousands of frequent English typos instantly upon pressing Space or punctuation (e.g. `teh ` → `the `, `seperate ` → `separate `, `recieve ` → `receive `, `dont ` → `don't `).
  - Automatically fixes accidental double capitals at the start of words (e.g. `THis ` → `This `).
  - Preserves letter casing (`teh` → `the`, `Teh` → `The`, `TEH` → `THE`).
  - Auto-capitalizes the start of sentences after periods, exclamation points, and question marks.

- **🎯 Grammarly-Level Grammar & Style Suggestions:**
  - **🔴 Spelling Checks:** Highlights typos and offers one-click corrections or "Add to Dictionary".
  - **🔵 Grammar Checks:** Flags subject-verb disagreement, incorrect articles (e.g. `an test` → `a test`), tense errors, and sentence fragments.
  - **🟡 Style & Clarity:** Flags wordiness, passive voice, repetition, and phrasing issues.
  - **🟢 Punctuation & Typography:** Checks missing commas, misplaced apostrophes, and spacing.
  - **One-Click "Fix All":** Apply all suggested corrections in a single click.

- **📊 Readability & Tone Analysis:**
  - Calculates real-time Flesch Reading Ease score and grade level.
  - Analyzes writing tone (*Formal & Academic*, *Confident*, *Warm & Friendly*, *Direct & Concise*, *Cautious*).
  - Displays word count, character count, sentence count, and reading time estimate.

- **📌 System Tray Docking:**
  - FreeAutocorrect docks to the Windows system tray notification area.
  - Minimizing or clicking the close button (`✕`) keeps the assistant running silently in the tray.
  - Right-click tray menu allows quick toggling of Live Autocorrect, Floating Pill, Settings, or exiting.
  - Double-clicking the tray icon restores the Grammar Studio window.

- **🔒 Single Instance Protection:**
  - Enforced via Windows named Mutex (`Global\FreeAutocorrect_SingleInstance_Mutex_Bisrat`).
  - If a second instance is launched, it automatically shows a message dialog alerting the user and signals the existing instance to bring its window to the front.

- **🪄 Floating Grammarly-Style Suggestion Pill:**
  - A sleek floating pill widget appears on screen showing live issue counts.
  - Expand to view quick-fix cards and apply replacements directly without leaving your current app.

- **⌨️ Global Hotkey (`Ctrl+Shift+G`):**
  - Select any text in any application and press `Ctrl+Shift+G` to immediately check grammar and view suggestions.

- **⚙️ Custom Snippets & Personal Dictionary:**
  - Text expansion shortcuts (e.g., `omw` → `on my way`, `tyvm` → `thank you very much`).
  - Custom dictionary to whitelist jargon, acronyms, or proper nouns.
  - Dialect selection: US English, British English, Canadian English, Australian English.

---

## 🏗️ Project Architecture

```
FreeAutocorrect/
├── FreeAutocorrect.csproj     # .NET 9 WPF & WinForms configuration
├── App.xaml / App.xaml.cs     # Single-instance mutex check, service bootstrapper
├── MainWindow.xaml            # Grammarly-style dark UI with Studio, Dashboard, Settings
├── MainWindow.xaml.cs         # Event handlers, debounced linting, suggestion cards
├── harper-bin/                # Embedded Harper offline Rust binary
│   └── harper-cli.exe
├── Models/
│   ├── LintItem.cs            # Rule, category, spans, suggestions
│   ├── CheckResult.cs         # Word counts, readability score, tone
│   └── AppSettings.cs         # Persistent configuration in %APPDATA%
├── Services/
│   ├── SingleInstanceManager.cs # Global Named Mutex & Named Pipe activator
│   ├── HarperEngineService.cs   # Offline Harper CLI JSON linter
│   ├── AutoCorrectDictionary.cs # High-speed typo lookup table & case preserver
│   ├── LowLevelKeyboardHook.cs  # Win32 WH_KEYBOARD_LL hook for keystroke correction
│   ├── TextAutomationService.cs # Win32 SendInput Unicode text replacer
│   ├── GlobalHotkeyService.cs   # Win32 RegisterHotKey for Ctrl+Shift+G
│   ├── ReadabilityService.cs    # Flesch-Kincaid & tone analysis algorithms
│   ├── TrayIconService.cs       # Windows NotifyIcon with context menu
│   └── SelfTestRunner.cs        # Comprehensive automated verification suite
└── UI/
    ├── FloatingWidget.xaml    # Grammarly-style floating pill overlay
    └── FloatingWidget.xaml.cs # Floating widget drag & quick-fix logic
```

---

## 🚀 How to Run

### Run from Terminal
```powershell
# Navigate to the project directory
cd c:\Users\Bisrat\Documents\APPS\FreeAutocorrect

# Run the app
dotnet run
```

### Run the Compiled Executable Directly
```powershell
.\bin\Release\net9.0-windows\FreeAutocorrect.exe
```

### Run Self-Tests
```powershell
Start-Process -FilePath "bin\Release\net9.0-windows\FreeAutocorrect.exe" -ArgumentList "--test" -Wait
Get-Content test-results.txt
```
