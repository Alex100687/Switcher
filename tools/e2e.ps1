# End-to-end test: run Switcher with synthetic input accepted, type into a real TextBox, check the result.
param([string]$Exe = "$PSScriptRoot\..\bin\Release\net8.0-windows\Switcher.exe", [string]$Only = "", [switch]$Debug, [switch]$NoUia)

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
# SendKeys defaults to a journal playback hook, and while one is installed Windows attaches every thread's input
# queue — keyboard layouts then leak between windows and the test becomes meaningless. Force SendInput instead.
$f = [System.Windows.Forms.SendKeys].GetField("sendMethod", [Reflection.BindingFlags]"NonPublic,Static")
$t = [System.Windows.Forms.SendKeys].GetNestedType("SendMethodTypes", [Reflection.BindingFlags]"NonPublic")
if ($f -and $t) { $f.SetValue($null, [Enum]::ToObject($t, 3)) } else { "WARN: cannot switch SendKeys to SendInput" }
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# A running Switcher would double every correction (two hooks), so it is stopped for the test — and started again at the end.
$running = @(Get-Process Switcher, LayoutFix -ErrorAction SilentlyContinue | ForEach-Object { $_.Path } | Where-Object { $_ })
Get-Process Switcher, LayoutFix -ErrorAction SilentlyContinue | Stop-Process -Force
$script:failures = 0
$env:SWITCHER_ACCEPT_INJECTED = "1"; $env:SWITCHER_NO_EXCLUDE = "1"; if ($Debug) { $env:SWITCHER_DEBUG = "1" }; if ($NoUia) { $env:SWITCHER_NO_UIA = "1" }
$data = Join-Path $env:TEMP "Switcher_e2e"; Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory $data | Out-Null
$env:SWITCHER_DATA_DIR = $data
'{ "Hotkey": "F9", "LogActions": true }' | Set-Content -Path (Join-Path $data "settings.json") -Encoding UTF8
$proc = Start-Process -FilePath (Resolve-Path $Exe) -PassThru
# wait until dictionaries and frequency lists are loaded (the log says so)
$logPath = Join-Path $data "log.txt"
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Milliseconds 250
    if ((Test-Path $logPath) -and (Select-String -Path $logPath -Pattern "Frequencies loaded" -Quiet)) { break }
}
Start-Sleep -Milliseconds 500

$form = New-Object System.Windows.Forms.Form
$form.Text = "Switcher e2e"; $form.Width = 500; $form.Height = 200; $form.TopMost = $true
$form.StartPosition = 'CenterScreen'
$tb = New-Object System.Windows.Forms.TextBox
$tb.Multiline = $true; $tb.Dock = 'Fill'; $tb.Font = New-Object System.Drawing.Font("Consolas", 14)
$form.Controls.Add($tb)
$pw = New-Object System.Windows.Forms.TextBox
$pw.UseSystemPasswordChar = $true; $pw.Dock = 'Bottom'
$form.Controls.Add($pw)
$form.Show(); $form.Activate(); $tb.Focus()
[System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 500

# typer.py types physical keys by scan code (pythonw: a console window would steal the foreground)
$pyw = Join-Path (Split-Path (Get-Command python).Source) "pythonw.exe"   # not the WindowsApps store stub
function Typer($keys, $delayMs = 40) {
    $p = Start-Process -FilePath $pyw -ArgumentList @("`"$PSScriptRoot\typer.py`"", "`"$keys`"", $delayMs) -PassThru
    while (-not $p.HasExited) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 5 }
    for ($i = 0; $i -lt 10; $i++) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 50 }
}
# every case expects Caps Lock off (letter case is checked exactly)
if ([System.Windows.Forms.Control]::IsKeyLocked('CapsLock')) { Typer "{CAPS}" }

$en = [System.Windows.Forms.InputLanguage]::InstalledInputLanguages | ? { $_.Culture.Name -eq 'en-US' }
$ru = [System.Windows.Forms.InputLanguage]::InstalledInputLanguages | ? { $_.Culture.Name -eq 'ru-RU' }

Add-Type -Namespace W -Name U -MemberDefinition '[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();'
function Ensure-Foreground($name) {
    if ([W.U]::GetForegroundWindow() -eq $form.Handle) { return $true }
    $form.Activate(); [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 300
    if ([W.U]::GetForegroundWindow() -eq $form.Handle) { return $true }
    "ABORT ${name}: test window is not in the foreground, refusing to type into another app"
    return $false
}
function Step($name, $lang, $keys, $expected) {
    if ($Only -and $name -notlike "*$Only*") { return }
    if (-not (Ensure-Foreground $name)) { return }
    $tb.Clear(); [System.Windows.Forms.SendKeys]::SendWait("{ESC}")   # clearing sends no key: tell Switcher the context is gone
    [System.Windows.Forms.InputLanguage]::CurrentInputLanguage = $lang
    [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 300
    [System.Windows.Forms.SendKeys]::SendWait($keys)
    for ($i = 0; $i -lt 15; $i++) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 100 }
    $got = $tb.Text
    $layoutNow = [System.Windows.Forms.InputLanguage]::CurrentInputLanguage.Culture.Name
    $ok = if ($got -ceq $expected) { "OK  " } else { $script:failures++; "FAIL" }
    "{0} {1,-22} typed='{2}' got='{3}' expected='{4}' layout={5}" -f $ok, $name, $keys, $got, $expected, $layoutNow
}

Step "en→ru switch"     $en "ghbdtn "        "привет "
Step "en→ru + punct"    $en "ghbdtn+? "      "привет, "
Step "en keep"          $en "hello "         "hello "
Step "en typo fix"      $en "hlelo "         "hello "
Step "en tech keep"     $en "async "         "async "
Step "ru→en switch"     $ru "руддщ "         "hello "
Step "ru keep"          $ru "привет "        "привет "
Step "ru typo fix"      $ru "првиет "        "привет "
Step "ru ortho fix"     $ru "жызнь "         "жизнь "
Step "ru ortho fix 2"   $ru "сдесь "         "здесь "
Step "ru autocorrect"   $ru "вобщем "        "в общем "
Step "en ortho fix"     $en "teh "           "the "
Step "jargon keep"      $ru "пивот "         "пивот "
Step "command keep"     $en "sudo "          "sudo "
Step "fix + switch"      $en ";spym "         "жизнь "
function Burst($name, $lang, $first, $rest, $accept) {
    if ($Only -and $name -notlike "*$Only*") { return }
    if (-not (Ensure-Foreground $name)) { return }
    $tb.Clear(); [System.Windows.Forms.SendKeys]::SendWait("{ESC}"); [System.Windows.Forms.InputLanguage]::CurrentInputLanguage = $lang; [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 300
    [System.Windows.Forms.SendKeys]::SendWait($first); Start-Sleep -Milliseconds 350   # fix is being computed while the next word has started
    [System.Windows.Forms.SendKeys]::SendWait($rest)
    for ($i = 0; $i -lt 15; $i++) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 100 }
    $got = $tb.Text
    if ($accept -ccontains $got) { "OK   {0,-22} got='{1}'" -f $name, $got } else { $script:failures++; "FAIL {0,-22} got='{1}' expected one of: {2}" -f $name, $got, ($accept -join " | ") }
}
# after our layout switch the same physical keys F,R,L,T,K,F now produce Cyrillic — SendKeys must be given the Cyrillic
Burst "fix+switch, next word" $en "cltfknm r" "ак дела " @("сделать как дела ")
Burst "fix, next word"        $ru "првиет к" "ак дела " @("привет как дела ")
# Enter is held while "првиет" is being fixed; the next word and its space follow at once. The Enter must stay
# between the words (v0.3.0 delivered it at the very end: "првиеткак \r\n").
Burst "held enter, next word" $ru "првиет{ENTER}к" "ак " @("привет`r`nкак ", "првиет`r`nкак ")
function Human($name, $lang, $keys, $expected, $delayMs = 40) {
    # physical keys by scan code (tools	yper.py), <keys> in US-layout letters, a real pause between keys
    if ($Only -and $name -notlike "*$Only*") { return }
    if (-not (Ensure-Foreground $name)) { return }
    $tb.Focus(); $tb.Clear(); [System.Windows.Forms.SendKeys]::SendWait("{ESC}"); [System.Windows.Forms.InputLanguage]::CurrentInputLanguage = $lang; [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 300
    # pythonw: a console window would steal the foreground and swallow the keys
    $pyw = Join-Path (Split-Path (Get-Command python).Source) "pythonw.exe"   # not the WindowsApps store stub
    $p = Start-Process -FilePath $pyw -ArgumentList @("`"$PSScriptRoot\typer.py`"", "`"$keys`"", $delayMs) -PassThru
    while (-not $p.HasExited) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 5 }
    for ($i = 0; $i -lt 15; $i++) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 100 }
    $got = $tb.Text
    if ($got -ceq $expected) { "OK   {0,-22} got='{1}'" -f $name, $got } else { $script:failures++; "FAIL {0,-22} got='{1}' expected='{2}'" -f $name, $got, $expected }
}
# a fast typist: 25 keys/s, no pauses between words (keys given as US-layout letters)
Human "fast typist switch"    $en "cltkfnm rfr ltkf " "сделать как дела "
Human "fast typist fix+sw"    $en "cltfknm rfr ltkf " "сделать как дела "
Human "fast typist fix"       $ru "ghdbtn rfr ltkf ghdbtn " "привет как дела привет "
Human "fast typist mixed"     $en "ghbdtn {SLEEP:300}hello wjrld " "привет hello world "
# Russian context first (a real Russian word), then "ult" in EN, Alt+Shift by hand, and the sentence goes on
Human "real Alt+Shift"        $en "{ALTSHIFT}ghbdtn {ALTSHIFT}ult{ALTSHIFT} kt;fn " "привет где лежат "
Human "burst 0ms"             $en "cltkfnm rfr " "сделать как " 0
Step "nofix fast"        $en "asdf qwer "      "asdf qwer "
Step "collision ctx"     $en "ghbdtn tot "     "привет еще "
Step "collision norm"    $en "tot "            "еще "
Step "caps lock layout"  $en "GHBDTN "         "ПРИВЕТ "
Step "camelCase keep"    $en "myVar "          "myVar "
# manual layout switch in the middle of a word: "ult", Alt+Shift, " лежат"
if ((-not $Only -or "manual switch" -like "*$Only*") -and (Ensure-Foreground "manual switch")) {
    $tb.Clear(); [System.Windows.Forms.SendKeys]::SendWait("{ESC}"); [System.Windows.Forms.InputLanguage]::CurrentInputLanguage = $en; [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 200
    [System.Windows.Forms.SendKeys]::SendWait("ult"); Start-Sleep -Milliseconds 150
    [System.Windows.Forms.InputLanguage]::CurrentInputLanguage = $ru; [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 200
    [System.Windows.Forms.SendKeys]::SendWait(" лежат ")
    for ($i = 0; $i -lt 10; $i++) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 100 }
    if ($tb.Text -ceq "где лежат ") { "OK   manual switch         got='$($tb.Text)'" } else { $script:failures++; "FAIL manual switch         got='$($tb.Text)' expected='где лежат '" }
}
# password box: never rewritten
if ((-not $Only -or "password" -like "*$Only*") -and (Ensure-Foreground "password keep")) {
    $pw.Clear(); $pw.Focus(); [System.Windows.Forms.SendKeys]::SendWait("{ESC}"); [System.Windows.Forms.InputLanguage]::CurrentInputLanguage = $en; [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 300
    [System.Windows.Forms.SendKeys]::SendWait("ghbdtn ")
    for ($i = 0; $i -lt 10; $i++) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 100 }
    if ($pw.Text -ceq "ghbdtn ") { "OK   password keep         got='$($pw.Text)'" } else { $script:failures++; "FAIL password keep         got='$($pw.Text)' expected='ghbdtn '" }
    $tb.Focus(); [System.Windows.Forms.Application]::DoEvents()
}
Step "missed space"       $ru "инужно "        "и нужно "
Step "vtoryi"             $ru "вторы "         "вторым "
Step "ru sentence"      $en "ghbdtn rfr ltkf "  "привет как дела "
Step "backspace"        $en "ghbdtnn{BS} "   "привет "
Step "enter boundary"   $en "ntrcn{ENTER}"   "текст`r`n"

# 0.5.0: letter case, spaces, words before, editing
Step "two caps"           $ru "ПОжалуйста "      "Пожалуйста "
Step "proper noun"        $ru "москва "          "Москва "
Step "abbreviation"       $ru "сша "             "США "
Step "sentence start"     $ru "привет. как "     "привет. Как "
Step "shifted space"      $ru "ка кдела "        "как дела "
Step "space inside word"  $ru "при вет "         "привет "
Step "comma after space"  $ru "привет ,как "     "привет, как "
Step "no space after ,"   $ru "привет,как "      "привет, как "
Step "short word before"  $en "z ljvf "          "я дома "
Step "shifted digit key"  $en "ghbdtn! "         "привет! "
# Backspace into the word just finished: it is judged whole again (v0.4.0 fixed the tail alone: "пр" + "ивет" → "прживет")
Step "reopen word"        $ru "пр {BS}ивет "     "привет "
Step "reopen and fix"     $ru "пр {BS}евет "     "привет "
# caret moved into a word with arrows: the letters typed there are a tail, not a word (v0.4.0: "интересгость")
Step "tail after arrows"  $ru "интерес{LEFT}{RIGHT}ность " "интересность "
# Caps Lock on by mistake: "пРИВЕТ" → "Привет", and Caps Lock goes off. Typed with typer.py: SendKeys juggles
# Caps Lock itself to make its characters come out as written.
if ((-not $Only -or "caps lock slip" -like "*$Only*") -and (Ensure-Foreground "caps lock slip")) {
    $tb.Focus(); $tb.Clear(); [System.Windows.Forms.SendKeys]::SendWait("{ESC}"); [System.Windows.Forms.InputLanguage]::CurrentInputLanguage = $ru; [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 300
    if ([System.Windows.Forms.Control]::IsKeyLocked('CapsLock')) { Typer "{CAPS}" }
    Typer "{CAPS}Ghbdtn "       # Shift+G with Caps Lock on gives "п", the rest come out capital: "пРИВЕТ "
    for ($i = 0; $i -lt 10; $i++) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 100 }
    $capsOn = [System.Windows.Forms.Control]::IsKeyLocked('CapsLock')
    if ($capsOn) { Typer "{CAPS}" }
    if ($tb.Text -ceq "Привет " -and -not $capsOn) { "OK   caps lock slip        got='$($tb.Text)'" } else { $script:failures++; "FAIL caps lock slip        got='$($tb.Text)' capsLockStillOn=$capsOn expected='Привет ' and Caps Lock off" }
}

Step "hotkey mid-word"   $en "ghbdtn{F9}"     "привет"
Step "hotkey last word"  $en "hello {F9}"     "руддщ "
# a second space: the word is no longer right before the caret, undo must not count back from here (v0.3.0: "пghbdtn ")
Step "undo after 2 spaces" $en "ghbdtn  {F9}"  "привет  "
Step "auto + undo"       $en "ghbdtn {F9}"    "ghbdtn "
Step "learned exception" $en "ghbdtn "        "ghbdtn "
# the hotkey in the middle of a word, then the word goes on in the new layout: one word, not "при" + a tail
Burst "hotkey, word goes on"  $en "ghb{F9}" "вет " @("привет ")
# a word we fixed, reopened with Backspace and typed back as it was: not fixed again (and remembered as rejected)
Burst "edited back"           $ru "превет " "{BS}{BS}{BS}{BS}{BS}{BS}{BS}превет " @("превет ")
"blocked.txt: " + ((Get-Content (Join-Path $data "blocked.txt") -Encoding UTF8 -ErrorAction SilentlyContinue | Where-Object { $_ -notlike "#*" }) -join ", ")

if ([System.Windows.Forms.Control]::IsKeyLocked('CapsLock')) { Typer "{CAPS}" }   # leave the keyboard as we found it
$form.Close()
Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 300
foreach ($exe in $running | Select-Object -Unique) { if (Test-Path $exe) { Start-Process $exe | Out-Null; "restarted $exe" } }
"--- log tail ---"
Get-Content (Join-Path $data "log.txt") -Encoding UTF8 -Tail 80
"RESULT: $script:failures failed"
exit ([int]($script:failures -gt 0))
