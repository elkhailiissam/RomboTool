# ⚡ RomboTool

High-performance combo list filter and cleaner. Built with a pure C engine for speed and a WPF interface for ease of use.

```
  ██████╗  ██████╗ ███╗   ███╗██████╗  ██████╗ ████████╗ ██████╗  ██████╗ ██╗     
  ██╔══██╗██╔═══██╗████╗ ████║██╔══██╗██╔═══██╗╚══██╔══╝██╔═══██╗██╔═══██╗██║     
  ██████╔╝██║   ██║██╔████╔██║██████╔╝██║   ██║   ██║   ██║   ██║██║   ██║██║     
  ██╔══██╗██║   ██║██║╚██╔╝██║██╔══██╗██║   ██║   ██║   ██║   ██║██║   ██║██║     
  ██║  ██║╚██████╔╝██║ ╚═╝ ██║██████╔╝╚██████╔╝   ██║   ╚██████╔╝╚██████╔╝███████╗
  ╚═╝  ╚═╝ ╚═════╝ ╚═╝     ╚═╝╚═════╝  ╚═════╝    ╚═╝    ╚═════╝  ╚═════╝ ╚══════╝
```

## What It Does

Takes messy combo lists with URLs, garbage, duplicates and extracts clean `user:pass` pairs.

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

## Features

- **Fast**: Pure C engine processes 500K+ lines/second
- **Smart Parsing**: Handles chaotic formats automatically
- **Deduplication**: O(1) hash-based duplicate removal
- **Multi-Format**: Supports email:pass, user:pass, phone:pass
- **Garbage Filter**: Strips URLs, spam, file paths, promotional text
- **Cross-Platform CLI**: Works on Windows, Linux, macOS
- **Windows GUI**: Modern WPF interface with drag-and-drop

## Project Structure

```
RomboTool/
├── core/
│   └── rombofilter.c    # C filter engine (~200 lines)
├── gui/
│   ├── App.xaml         # WPF app definition
│   ├── MainWindow.xaml  # UI layout
│   ├── MainWindow.xaml.cs # UI logic
│   └── RomboTool.csproj # .NET 8 project
├── build.sh             # Linux build script
├── build.bat            # Windows build script
└── .gitignore
```

## Building

### Linux/macOS (CLI only)

```bash
chmod +x build.sh
./build.sh
```

Or manually:
```bash
cd core
gcc -O3 -o rombofilter rombofilter.c
```

### Windows (CLI + GUI)

```batch
build.bat
```

Or manually:
```batch
cd core
gcc -O3 -o rombofilter.exe rombofilter.c

cd ..\gui
dotnet build -c Release
```

## Usage

### CLI

```bash
# Basic
./rombofilter input.txt output.txt

# With deduplication (recommended)
./rombofilter input.txt output.txt -d
```

**Example:**
```
$ ./rombofilter combos.txt clean.txt -d

  ██████╗  ██████╗ ███╗   ███╗██████╗  ██████╗ ...
  
[*] Input:  combos.txt
[*] Output: clean.txt
[*] Dedup:  ON

═══════════════════════════════════════════════════════
  Total: 9003 | Valid: 3778 | Emails: 1350 | Users: 2390 | Phones: 42
  Duplicates: 4831 | Invalid: 394 | Rate: 42.0%
═══════════════════════════════════════════════════════
[+] Saved to: clean.txt
```

### GUI (Windows)

1. Run `RomboTool.exe`
2. Drag & drop files (or click Browse)
3. Configure options:
   - ✅ Remove Duplicates
   - ☐ Emails Only
   - ☐ Usernames Only
4. Click **Start**
5. Output saved automatically

## How It Works

The filter uses multiple parsing strategies:

1. **Email Detection**: Find `@` with valid domain, take next field as password
2. **Phone Detection**: Find 8-15 digit number, take next field as password
3. **URL Stripping**: Skip parts containing `http`, `.com/`, `/auth/`, etc.
4. **Fallback**: Take last two valid-looking fields

**Garbage Detection:**
- URLs and domains
- File paths (`Browser/`, `Chrome_`, `.txt:`)
- Spam (`@kingulp`, `t.me/+`, `MonkeyBase`, `You can buy`)
- Malformed lines (semicolons, `//` prefix)

**Password Validation:**
- Length: 4-128 characters
- Must contain alphanumeric
- Not just `http`, `https`, or domain endings

## Performance

| Lines | Time | Rate |
|-------|------|------|
| 100K | 0.2s | 500K/s |
| 1M | 1.8s | 555K/s |
| 10M | 18s | 555K/s |

Memory usage: ~8MB for deduplication hash table (1M entries)

## Requirements

**CLI:**
- GCC or Clang
- Any OS

**GUI:**
- Windows 10/11
- .NET 8.0 Runtime

## License

MIT
