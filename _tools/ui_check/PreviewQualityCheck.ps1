# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Alexander Graef <alex@graef.cc>
#
# This file is part of WaveDude Next, a 2D wave simulator.
#
# Based on the work of
#   Jeroen Vleggaar (Huygens Optics), author of Wave Dude 2D, Copyright (C) 2026
#     <https://www.youtube.com/@HuygensOptics>, <https://www.huygensoptics.com>
#   0x23 (Diffraction Limited), author of WaveSimulator2D
#     <https://www.youtube.com/@DiffractionLimited>, <https://github.com/0x23/WaveSimulator2D>
#
# WaveDude Next is free software: you can redistribute it and/or modify it under
# the terms of the GNU General Public License as published by the Free Software
# Foundation, either version 3 of the License, or (at your option) any later
# version. It is distributed WITHOUT ANY WARRANTY. See the LICENSE.md file or
# <https://www.gnu.org/licenses/>.

# Drives View > Preview resolution / frames / samples by keyboard (F10 focuses the menu bar, Down opens a menu, Right moves
# over Edit to View, Down walks its entries, Right opens a submenu, Enter picks) on tiny.wdproj (64 x 48, 200 steps, a frame every
# 2 steps): a run is made, then each choice marks it stale and the automatic re-run follows in the reduced shape (the
# status bar, the frame readout and the log file next to the exe are the witnesses: "50 of 100 frames stored as 32x24
# (1/2 resolution, every 2. frame, 8-bit)"). The timeline never changes: with every second frame stored, frames 98 and
# 99 show the same picture while 97 differs; the source marker stays at the source's canvas position on a half-size
# picture; the settings survive a restart; everything is put back to Full at the end. Posted keys and UI Automation
# properties only, as in the other harnesses.
#
#   powershell -ExecutionPolicy Bypass -File _tools\ui_check\PreviewQualityCheck.ps1 [-Trace]
param([switch]$Trace)
. (Join-Path $PSScriptRoot "Common.ps1")
$exe = Join-Path $PSScriptRoot "..\..\WaveDudeNext.WPF\bin\Debug\net10.0-windows\WaveDudeNext.WPF.exe"
$log = Join-Path (Split-Path $exe) "WaveDudeNext.WPF.log"
$project = Join-Path $PSScriptRoot "tiny.wdproj"
$by = [string][char]0xD7                           # the multiplication sign of the log's "64x48", built from its code so the script stays ASCII
$failures = 0
function Check([string]$name, [bool]$ok, [string]$detail = "") {
	if ($ok) { if ($Trace) { "PASS  $name" } } else { "FAIL  $name  $detail"; $script:failures++ }
}
function Step([string]$what, [int]$vk, [int]$settle) {
	Key $vk $settle
	if ($Trace) { "    {0,-12} focus {1}  foreground {2}" -f $what, (FocusedId), (Foreground) }
}
# the log of this session (the app archives the previous one on start-up; the file stays open, so share the read)
function LogText() {
	$fs = New-Object IO.FileStream($log, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
	try { $sr = New-Object IO.StreamReader($fs); return $sr.ReadToEnd() } finally { $fs.Dispose() }
}
function LogHas([string]$text) { return (LogText).Contains($text) }
# View (3rd header, after File and Edit) > entry $entry (0 Panes, 1 Theme, 2 UI zoom, 3 Language, 4 Preview resolution, 5 Preview frames, 6 Preview samples) > item $item
function ChooseView([int]$entry, [int]$item) {
	Step "F10" 0x79 150                            # the menu bar's first header (File)
	Step "Down" 0x28 150                           # opens File
	Step "Right" $VK_RIGHT 150                     # moves to Edit and opens it
	Step "Right" $VK_RIGHT 150                     # moves to View and opens it on its first entry
	for ($i = 0; $i -lt $entry; $i++) { Step "Down" 0x28 100 }
	Step "Right" $VK_RIGHT 150                     # opens the submenu on its first item
	for ($i = 0; $i -lt $item; $i++) { Step "Down" 0x28 100 }
	Step "Enter" $VK_RETURN 300
}
# a choice stales the run and the automatic re-run completes
function ChooseAndRerun([string]$what, [int]$entry, [int]$item) {
	ChooseView $entry $item
	Check "$what marks the run stale" (WaitFor { (RunStatus) -like "*edited since*" } 3000) (RunStatus)
	Check "$what is followed by the automatic re-run" (WaitFor { (RunStatus) -like "Completed:*" -and (RunStatus) -notlike "*edited since*" } 20000 200) (RunStatus)
	Start-Sleep -Milliseconds 300
}
function MarkerAtSource([string]$when) {
	$expected = CanvasPoint "PreviewFieldImage" 64 48 20 24
	$centre = CentreOf "Source:Source 1"
	Check "the source marker sits at the source's canvas position $when" ([math]::Abs($centre[0] - $expected[0]) -lt 3 -and [math]::Abs($centre[1] - $expected[1]) -lt 3) ("marker {0:0},{1:0} expected {2:0},{3:0}" -f $centre[0], $centre[1], $expected[0], $expected[1])
}
# the pictures of the last three frames: with every frame stored all three differ, with every second one 99 repeats 98
function LastFrames([string]$when, [bool]$repeated) {
	FocusOn "PreviewPlay"                          # the frame-step keys are the pane's own bindings; the menu left the focus on the tree
	Key $VK_END 500
	Check "the last frame is 99 at step 198 $when" ((Frame) -eq "99 / 99" -and (StepText) -match "198$") ("{0}, {1}" -f (Frame), (StepText))
	$at99 = FieldHash
	Key $VK_COMMA 500
	$at98 = FieldHash
	Key $VK_COMMA 500
	$at97 = FieldHash
	Check "frame 97 differs from 98 $when" ((Frame) -eq "97 / 99" -and $at97 -ne $at98) (Frame)
	if ($repeated) { Check "frame 99 shows the stored frame 98 again $when" ($at99 -eq $at98) }
	else { Check "frame 99 differs from 98 $when" ($at99 -ne $at98) }
}
# a second start sometimes comes up active in spite of Start-App's parking (seen 2026-10-15): park it again, a little slower
function Park() {
	for ($i = 0; $i -lt 5 -and (Foreground) -eq "APP"; $i++) {
		[Native]::ShowWindow($script:hwnd, 6) | Out-Null         # SW_MINIMIZE
		Start-Sleep -Milliseconds 300
		$script:park.Activate(); [System.Windows.Forms.Application]::DoEvents()
		Start-Sleep -Milliseconds 300
		[Native]::ShowWindow($script:hwnd, 4) | Out-Null         # SW_SHOWNOACTIVATE
		Start-Sleep -Milliseconds 400
	}
}

Start-App $exe $project
try {
	ChooseView 6 0                                 # 16-bit samples first: 8-bit is the default since 2026-10-16, and the first run must be the full quality
	FocusOn "PreviewPlay"                          # a window never activated has no focused element, and F5 (a routed gesture) then goes nowhere
	Key 0x74 300                                   # F5
	Check "F5 run completes" (WaitFor { (RunStatus) -like "Completed:*" } 30000) (RunStatus)
	Start-Sleep -Milliseconds 300
	Check "the full run has 100 frames" ((Frame) -like "* / 99") (Frame)
	Check "the log says all frames are stored" (LogHas "100 of 100 frames stored as 64${by}48 (full)")
	MarkerAtSource "at full resolution"
	LastFrames "at full quality" $false

	ChooseAndRerun "View > Preview frames > Half" 5 1
	Check "the timeline still has 100 frames" ((Frame) -like "* / 99") (Frame)
	Check "the log says 50 of 100 frames are stored" (LogHas "50 of 100 frames stored as 64${by}48 (1/1 resolution, every 2. frame, 16-bit)")
	LastFrames "with every second frame stored" $true

	ChooseAndRerun "View > Preview resolution > Half" 4 1
	Check "the log says the frames are 32x24" (LogHas "50 of 100 frames stored as 32${by}24 (1/2 resolution, every 2. frame, 16-bit)")
	MarkerAtSource "on a half-size picture"

	ChooseAndRerun "View > Preview samples > 8-bit" 6 1
	Check "the log says 8-bit" (LogHas "50 of 100 frames stored as 32${by}24 (1/2 resolution, every 2. frame, 8-bit)")
	Check "the app never took the foreground" ((Foreground) -ne "APP") (Foreground)
}
finally { Stop-App }

# the choices are user settings: a fresh start runs reduced, and the menu puts everything back
Start-Sleep -Milliseconds 800                      # let the first instance's window go before the next one is parked
Start-App $exe $project
Park
try {
	FocusOn "PreviewPlay"
	Key 0x74 300
	Check "after a restart F5 runs in the kept quality" (WaitFor { (RunStatus) -like "Completed:*" } 30000) (RunStatus)
	Start-Sleep -Milliseconds 300
	Check "still 50 of 100 frames of 32x24, 8-bit (settings kept)" ((Frame) -like "* / 99" -and (LogHas "50 of 100 frames stored as 32${by}24 (1/2 resolution, every 2. frame, 8-bit)")) (Frame)
	ChooseAndRerun "View > Preview samples > 16-bit" 6 0
	ChooseAndRerun "View > Preview resolution > Full" 4 0
	ChooseAndRerun "View > Preview frames > Full" 5 0
	Check "back to all frames stored" ((Frame) -like "* / 99" -and (LogHas "100 of 100 frames stored as 64${by}48 (full)")) (Frame)
	LastFrames "after the return to Full" $false
	Check "the app never took the foreground" ((Foreground) -ne "APP") (Foreground)
}
finally { Stop-App }

"$failures failure(s)"
exit $failures
