# Two windows of one program, each with a text box, for the scripted checks of the
# Mac shortcuts:
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tests\external\two-windows.ps1
#
# The windows close themselves after three minutes, or when either is closed.
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

function New-CheckWindow([string] $title, [int] $left) {
    $form = New-Object System.Windows.Forms.Form
    $form.Text = $title
    $form.StartPosition = 'Manual'
    $form.Location = New-Object System.Drawing.Point($left, 120)
    $form.ClientSize = New-Object System.Drawing.Size(420, 160)
    $box = New-Object System.Windows.Forms.TextBox
    $box.Multiline = $true
    $box.AcceptsReturn = $true
    $box.Dock = 'Fill'
    $form.Controls.Add($box)
    return $form
}

$first = New-CheckWindow 'KeyFlip check A' 120
$second = New-CheckWindow 'KeyFlip check B' 580
$second.Add_FormClosed({ $first.Close() })
$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 180000
$timer.Add_Tick({ $first.Close() })
$timer.Start()
$second.Show()
[System.Windows.Forms.Application]::Run($first)
