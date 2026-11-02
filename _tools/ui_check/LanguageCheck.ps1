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

# Drives View > Language by keyboard (F10 focuses the menu bar, Down opens a menu, Left/Right move between menus, Edit lying between File and View, Enter picks): switches to
# German, checks that menus, tooltips and property labels change at once, closes the app, starts it again
# and checks the choice was kept (the Language user setting), then switches back to System and checks the
# strings are English again. Posted keys and UI Automation properties only, as in the other harnesses.
#
#   powershell -ExecutionPolicy Bypass -File _tools\ui_check\LanguageCheck.ps1 [-Trace]
param([switch]$Trace)
. (Join-Path $PSScriptRoot "Common.ps1")
$exe = Join-Path $PSScriptRoot "..\..\WaveDudeNext.WPF\bin\Debug\net10.0-windows\WaveDudeNext.WPF.exe"
$project = Join-Path $PSScriptRoot "tiny.wdproj"
$failures = 0
function Check([string]$name, [bool]$ok, [string]$detail = "") {
	if ($ok) { if ($Trace) { "PASS  $name" } } else { "FAIL  $name  $detail"; $script:failures++ }
}
function MenuNames() {
	$c = New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)
	return ($script:win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $c) | ForEach-Object { $_.Current.Name }) -join "|"
}
# any element of this name in the window (a menu entry, a tree folder, a pane title)
function MenuItemVisible([string]$name) {
	$c = New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $name)
	return ($script:win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c) -ne $null)
}
# View (2nd header) > Language (3rd entry) > the language at $index (0 = System, 1 = English, 2 = Deutsch, ...)
function Step([string]$what, [int]$vk, [int]$settle) {
	Key $vk $settle
	if ($Trace) { "    {0,-12} focus {1}  foreground {2}" -f $what, (FocusedId), (Foreground) }
}
function ChooseLanguage([int]$index) {
	Step "F10" 0x79 150                            # the menu bar's first header (File)
	Step "Down" 0x28 150                           # opens File
	Step "Right" $VK_RIGHT 150                     # moves to Edit and opens it
	Step "Right" $VK_RIGHT 150                     # moves to View and opens it on its first entry
	Step "Down" 0x28 100; Step "Down" 0x28 100; Step "Down" 0x28 100     # Theme, UI zoom, Language
	Step "Right" $VK_RIGHT 150                     # opens Language on System
	for ($i = 0; $i -lt $index; $i++) { Step "Down" 0x28 100 }
	Step "Enter" $VK_RETURN 300
}

Start-App $exe $project
try {
	Check "menus are English to begin with" ((MenuNames) -eq "File|Edit|View|Simulation|Help") (MenuNames)
	Key 0x79 200; Key 0x28 200; Key $VK_RIGHT 300; Key $VK_RIGHT 500
	Check "F10, Down, Right, Right opens the View menu by keyboard (Edit between)" (MenuItemVisible "Panes") (MenuNames)
	Key $VK_ESCAPE 150; Key $VK_ESCAPE 150
	ChooseLanguage 2
	Check "View > Language > Deutsch relabels the menus at once" ((MenuNames) -eq "Datei|Bearbeiten|Ansicht|Simulation|Hilfe") (MenuNames)
	Check "tooltips follow (Play)" ((Find "PreviewPlay").Current.Name -eq "Wiedergabe (Leertaste)") ((Find "PreviewPlay").Current.Name)
	FocusOn "Source:Source 1"
	$c = New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, "Frequenz")
	Check "the Properties pane's labels follow (Frequenz)" ($script:win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c) -ne $null)
	# the dock pane titles are not exposed to UI Automation by name, so only the tree folders are checked here
	Check "the tree's folders follow at once (Sonden, Farbschemata)" ((MenuItemVisible "Sonden") -and (MenuItemVisible "Farbschemata") -and -not (MenuItemVisible "Probes"))
	# the dock pane titles are exposed to UI Automation since 2026-10-15 (id DockTitle:<pane id>)
	Check "the dock pane titles follow (Projekt, Eigenschaften)" (((Find "DockTitle:project").Current.Name -eq "Projekt") -and ((Find "DockTitle:properties").Current.Name -eq "Eigenschaften")) ((Find "DockTitle:project").Current.Name)
	Check "the app never took the foreground" ((Foreground) -ne "APP") (Foreground)
}
finally { Stop-App }

# the choice is a user setting: a fresh start comes up German
Start-App $exe $project
try {
	Check "after a restart the menus are still German (setting kept)" ((MenuNames) -eq "Datei|Bearbeiten|Ansicht|Simulation|Hilfe") (MenuNames)
	ChooseLanguage 0
	Check "View > Language > System brings English back" ((MenuNames) -eq "File|Edit|View|Simulation|Help") (MenuNames)
	Check "the folders are English again (Probes, Colour schemes)" ((MenuItemVisible "Probes") -and (MenuItemVisible "Colour schemes") -and -not (MenuItemVisible "Sonden"))
	Check "the app never took the foreground" ((Foreground) -ne "APP") (Foreground)
}
finally { Stop-App }

"$failures failure(s)"
exit $failures
