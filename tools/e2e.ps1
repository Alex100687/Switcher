# End-to-end test: run LayoutFix with synthetic input accepted, type into a real TextBox, check the result.
param([string]$Exe = "$PSScriptRoot\..\bin\Release\net8.0-windows\LayoutFix.exe")

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Get-Process LayoutFix -ErrorAction SilentlyContinue | Stop-Process -Force
$env:LAYOUTFIX_ACCEPT_INJECTED = "1"; $env:LAYOUTFIX_NO_EXCLUDE = "1"
$data = Join-Path $env:TEMP "LayoutFix_e2e"; Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory $data | Out-Null
$env:LAYOUTFIX_DATA_DIR = $data
'{ "Hotkey": "F9" }' | Set-Content -Path (Join-Path $data "settings.json") -Encoding UTF8
$proc = Start-Process -FilePath (Resolve-Path $Exe) -PassThru
Start-Sleep -Seconds 3

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
function Step($name, $lang, $keys, $expected) {
    if ([W.U]::GetForegroundWindow() -ne $form.Handle) {
        $form.Activate(); [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 300
        if ([W.U]::GetForegroundWindow() -ne $form.Handle) { "ABORT ${name}: test window is not in the foreground, refusing to type into another app"; return }
    }
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
Step "fix+switch fast"   $en "cltfknm rfr ltkf " "сделать как дела "
Step "fix fast"          $ru "првиет как дела " "привет как дела "
Step "nofix fast"        $en "asdf qwer "      "asdf qwer "
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
Get-Content (Join-Path $data "log.txt") -Encoding UTF8 -Tail 12
