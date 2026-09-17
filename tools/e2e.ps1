# End-to-end test: run LayoutFix with synthetic input accepted, type into a real TextBox, check the result.
param([string]$Exe = "$PSScriptRoot\..\bin\Release\net8.0-windows\LayoutFix.exe", [string]$Only = "", [switch]$Debug)

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Get-Process LayoutFix -ErrorAction SilentlyContinue | Stop-Process -Force
$env:LAYOUTFIX_ACCEPT_INJECTED = "1"; $env:LAYOUTFIX_NO_EXCLUDE = "1"; if ($Debug) { $env:LAYOUTFIX_DEBUG = "1" }
$data = Join-Path $env:TEMP "LayoutFix_e2e"; Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory $data | Out-Null
$env:LAYOUTFIX_DATA_DIR = $data
'{ "Hotkey": "F9" }' | Set-Content -Path (Join-Path $data "settings.json") -Encoding UTF8
$proc = Start-Process -FilePath (Resolve-Path $Exe) -PassThru
# wait until dictionaries and frequency lists are loaded (the log says so)
$logPath = Join-Path $data "log.txt"
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Milliseconds 250
    if ((Test-Path $logPath) -and (Select-String -Path $logPath -Pattern "Frequencies loaded" -Quiet)) { break }
}
Start-Sleep -Milliseconds 500

$form = New-Object System.Windows.Forms.Form
$form.Text = "LayoutFix e2e"; $form.Width = 500; $form.Height = 200; $form.TopMost = $true
$form.StartPosition = 'CenterScreen'
$tb = New-Object System.Windows.Forms.TextBox
$tb.Multiline = $true; $tb.Dock = 'Fill'; $tb.Font = New-Object System.Drawing.Font("Consolas", 14)
$form.Controls.Add($tb)
$form.Show(); $form.Activate(); $tb.Focus()
[System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 500

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
    $tb.Clear()
    [System.Windows.Forms.InputLanguage]::CurrentInputLanguage = $lang
    [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 300
    [System.Windows.Forms.SendKeys]::SendWait($keys)
    for ($i = 0; $i -lt 15; $i++) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 100 }
    $got = $tb.Text
    $layoutNow = [System.Windows.Forms.InputLanguage]::CurrentInputLanguage.Culture.Name
    $ok = if ($got -eq $expected) { "OK  " } else { "FAIL" }
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
    $tb.Clear(); [System.Windows.Forms.InputLanguage]::CurrentInputLanguage = $lang; [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 300
    [System.Windows.Forms.SendKeys]::SendWait($first); Start-Sleep -Milliseconds 350   # fix is being computed while the next word has started
    [System.Windows.Forms.SendKeys]::SendWait($rest)
    for ($i = 0; $i -lt 15; $i++) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 100 }
    $got = $tb.Text
    if ($accept -contains $got) { "OK   {0,-22} got='{1}'" -f $name, $got } else { "FAIL {0,-22} got='{1}' expected one of: {2}" -f $name, $got, ($accept -join " | ") }
}
# after our layout switch the same physical keys F,R,L,T,K,F now produce Cyrillic — SendKeys must be given the Cyrillic
Burst "fix+switch, next word" $en "cltfknm r" "ак дела " @("сделать как дела ")
Burst "fix, next word"        $ru "првиет к" "ак дела " @("привет как дела ")
Burst "firehose no garbage"   $en "cltfknm rfr ltkf " "" @("сделать как дела ", "сдеалть как дела ")
Step "nofix fast"        $en "asdf qwer "      "asdf qwer "
Step "collision ctx"     $en "ghbdtn tot "     "привет еще "
Step "collision norm"    $en "tot "            "еще "
Step "caps lock layout"  $en "GHBDTN "         "ПРИВЕТ "
Step "camelCase keep"    $en "myVar "          "myVar "
# manual layout switch in the middle of a word: "ult", Alt+Shift, " лежат"
if ((-not $Only -or "manual switch" -like "*$Only*") -and (Ensure-Foreground "manual switch")) {
    $tb.Clear(); [System.Windows.Forms.InputLanguage]::CurrentInputLanguage = $en; [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 200
    [System.Windows.Forms.SendKeys]::SendWait("ult"); Start-Sleep -Milliseconds 150
    [System.Windows.Forms.InputLanguage]::CurrentInputLanguage = $ru; [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 200
    [System.Windows.Forms.SendKeys]::SendWait(" лежат ")
    for ($i = 0; $i -lt 10; $i++) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 100 }
    if ($tb.Text -eq "где лежат ") { "OK   manual switch         got='$($tb.Text)'" } else { "FAIL manual switch         got='$($tb.Text)' expected='где лежат '" }
}
Step "missed space"       $ru "инужно "        "и нужно "
Step "vtoryi"             $ru "вторы "         "вторым "
Step "ru sentence"      $en "ghbdtn rfr ltkf "  "привет как дела "
Step "backspace"        $en "ghbdtnn{BS} "   "привет "
Step "enter boundary"   $en "ntrcn{ENTER}"   "текст`r`n"

Step "hotkey mid-word"   $en "ghbdtn{F9}"     "привет"
Step "hotkey last word"  $en "hello {F9}"     "руддщ "
Step "auto + undo"       $en "ghbdtn {F9}"    "ghbdtn "
Step "learned exception" $en "ghbdtn "        "ghbdtn "
"exceptions.txt: " + ((Get-Content (Join-Path $data "exceptions.txt") -Encoding UTF8) -join ", ")

$form.Close()
Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
"--- log tail ---"
Get-Content (Join-Path $data "log.txt") -Encoding UTF8 -Tail 80
