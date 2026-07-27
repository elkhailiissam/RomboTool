# ⚡ RomboTool

A cross-platform desktop toolkit with two features in one app:

1. **Combo Filter** — clean messy combo lists into tidy `user:pass` pairs (streams to disk, so multi-GB inputs stay memory-flat).
2. **Search** — a fast, [BareGrep](https://www.baremetalsoft.com/baregrep/)-style search over a single file or a whole folder, built for multi-GB files with live progress.

Built with a pure **C** engine for combo filtering and a cross-platform **Avalonia (.NET 8)** GUI that runs natively on **macOS, Linux, and Windows**.

```
  ██████╗  ██████╗ ███╗   ███╗██████╗  ██████╗ ████████╗ ██████╗  ██████╗ ██╗
  ██╔══██╗██╔═══██╗████╗ ████║██╔══██╗██╔═══██╗╚══██╔══╝██╔═══██╗██╔═══██╗██║
  ██████╔╝██║   ██║██╔████╔██║██████╔╝██║   ██║   ██║   ██║   ██║██║   ██║██║
  ██╔══██╗██║   ██║██║╚██╔╝██║██╔══██╗██║   ██║   ██║   ██║   ██║██║   ██║██║
  ██║  ██║╚██████╔╝██║ ╚═╝ ██║██████╔╝╚██████╔╝   ██║   ╚██████╔╝╚██████╔╝███████╗
  ╚═╝  ╚═╝ ╚═════╝ ╚═╝     ╚═╝╚═════╝  ╚═════╝    ╚═╝    ╚═════╝  ╚═════╝ ╚══════╝
```

---

## 1. Combo Filter

Takes messy combo lists with URLs, garbage, and duplicates and extracts clean `user:pass` pairs.

**Input (chaos):**
```
https://site.com/auth/login:user@email.com:MyPass123
site.com/:john_doe:secret456
http://example.org/signup:+1234567890:password789
Browser/Chrome_Default.txt:garbage:data
spam line @kingulp buy now!!!
user@test.com|pass123|extra|junk
```

**Output (clean):**
```
user@email.com:MyPass123
john_doe:secret456
+1234567890:password789
user@test.com:pass123
```

Features: O(1) hash dedup, email/user/phone detection, URL/spam/path stripping,
"emails only" / "usernames only" filters. The GUI streams accepted combos straight to the
output file (only a small preview is held in memory), with a live progress bar and a **Stop**
button, so it processes multi-GB lists without ballooning memory. A standalone pure-C CLI
(`core/rombofilter.c`, 500K+ lines/sec) is also included.

## 2. Search

A fast file searcher modeled on BareGrep, tuned for **very large files** (multi-GB combo
dumps included):

- Search a **single file** *or* a **folder** (auto-detected; a chip shows the type and file
  size). Folder mode adds **Subfolders** recursion and a space-separated glob **Files** filter
  (e.g. `*.txt *.log *.cs`).
- **Find** as a literal or **Regex**, with **Ignore Case** and **Invert** options.
- **Live progress** while it runs: a byte-accurate progress bar plus tiles for
  data scanned / total, throughput (MB/s), match count, and elapsed time — you can watch a
  19 GB file stream through. **Stop after** *N* matches keeps huge searches snappy, and a
  **Stop** button cancels instantly.
- Handles the ugly stuff: files up to 8 GB in folder mode (**no cap on a hand-picked file**),
  embedded NUL bytes, CRLF, and pathologically long lines — all read through a bounded byte
  buffer so memory stays flat.
- Literal searches scan raw bytes and only decode lines that actually match, so common terms
  fly (~300+ MB/s). Multi-file searches fan out across CPU cores; a single huge file streams
  sequentially for correct line numbers.
- Results grid (File · Line · Text). **Double-click a result to open the file** in your
  default editor. Press **Enter** in the Find box to search.

---

## Project Structure

```
RomboTool/
├── core/
│   └── rombofilter.c           # C combo-filter engine + CLI
├── gui/                        # Avalonia (.NET 8) cross-platform GUI
│   ├── Program.cs              # entry point
│   ├── App.axaml(.cs)          # app + theme/styles
│   ├── MainWindow.axaml(.cs)   # two-tab UI + wiring
│   ├── Engine/
│   │   ├── ComboFilterEngine.cs  # C# combo parser (used by the GUI)
│   │   └── GrepEngine.cs         # cross-platform regex searcher
│   ├── Assets/                 # app icon (make_icon.py → icon.png + RomboTool.icns)
│   └── RomboTool.csproj
├── build.sh                    # build CLI + GUI (macOS/Linux)
├── run.sh                      # launch the GUI
├── package-mac.sh             # build dist/RomboTool.app (with icon)
├── make-icon.sh               # regenerate the app icon
└── .gitignore
```

## Requirements

- **GUI:** [.NET 8 SDK](https://dotnet.microsoft.com/download) (runs on macOS, Linux, Windows).
- **CLI:** a C compiler (`clang` on macOS, `gcc` on Linux).

## Building

```bash
./build.sh          # builds the C CLI and the GUI
./build.sh cli      # just the C engine
./build.sh gui      # just the GUI
```

## Running

### GUI (macOS / Linux / Windows)

```bash
./run.sh
# or
dotnet run --project gui -c Release
```

### Install as a macOS app

Build a double-clickable `RomboTool.app` (with icon) and drop it in `/Applications`:

```bash
./package-mac.sh                              # creates dist/RomboTool.app
cp -R dist/RomboTool.app /Applications/       # install
open /Applications/RomboTool.app              # launch
```

Regenerate the icon anytime with `./make-icon.sh` (needs Python + Pillow).

### Combo Filter CLI

```bash
cd core && clang -O3 -o rombofilter rombofilter.c   # (build.sh does this)

./rombofilter input.txt output.txt        # basic
./rombofilter input.txt output.txt -d     # with deduplication (recommended)
```

```
$ ./rombofilter combos.txt clean.txt -d
  Total: 9003 | Valid: 3778 | Emails: 1350 | Users: 2390 | Phones: 42
  Duplicates: 4831 | Invalid: 394 | Rate: 42.0%
[+] Saved to: clean.txt
```

## How the Combo Filter Works

1. **Email detection** — find `@` with a valid domain, take the next field as password.
2. **Phone detection** — find an 8–15 digit number, take the next field as password.
3. **URL/garbage stripping** — skip parts containing `http`, `.com/`, `/auth/`, file
   paths (`Browser/`, `Chrome_`, `.txt:`), and spam markers.
4. **Fallback** — take the last valid `user`/`pass` pair on the line.

Password validation: length 4–128, must contain alphanumerics, not a bare URL fragment.

## License

MIT
