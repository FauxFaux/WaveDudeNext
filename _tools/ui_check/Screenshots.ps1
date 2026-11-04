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

# Makes the manual's figures: screenshots of the app, keyboard-driven on Common.ps1, each written as an SVG under
# Assets\ with the PNG inlined as a data URI and numbered badges as vector circles, plus Assets\Legend.md naming
# what each number marks, and Assets\main-window.png, the main window without badges for the README. The badges sit
# at UI Automation rectangles, so a run after a UI change moves them along.
# Two sessions: the example project for the window, the menus, the tree and the preview (a scratch frame at -Frame
# puts waves into the pictures), then figures.wdproj (one object of every kind) with a short tree, so each
# properties form has the pane's full height, and two crops of the rectangle's form for the settings several forms
# share. The window is captured through PrintWindow and may lie behind other
# windows; a menu popup is a window of its own, captured the same way, or from the screen when that comes back
# empty. The user settings are set for the figures (the saved window size and pane arrangement, the dark theme,
# English, the default preview quality) and put back afterwards, and so is the mouse pointer when it rested on the
# window (parked outside it for the captures, so no hover state gets into a figure).
#
#   powershell -ExecutionPolicy Bypass -File _tools\ui_check\Screenshots.ps1 [-Exe <path>] [-Example <wdproj>]
#       [-Out <folder>] [-Frame <n>] [-PngFolder <folder>] [-Zoom <factor>]
#
# -PngFolder also writes each figure's PNG (without badges) there, for a look at the captures.
# -Zoom puts the UI zoom setting (View > UI zoom, 2026-11-01, the user's choice) into the figures' settings: the
# windows' content and size at that scale, so the figures come out at that many times the pixels, as from a monitor
# at that scale (the title bar excepted, which is Windows' and stays at the monitor's). The pixel constants below
# (margins, row heights, the badges) scale with it; the SVG keeps its size in CSS pixels and puts the pixels into a
# scaled view box, which the manual's converter and browsers draw at the 100 % size, crisp on a monitor at the zoom's
# scale; the PNGs declare their pixel density. The window must fit the screen (nothing off it is composed): on a 4K
# monitor 1.5 does, 2 does not. 1.25 is the default and the figures' zoom (2026-11-02, the user's choice); 1 makes
# figures at the monitor's own pixels.

param(
	[string]$Exe = (Join-Path $PSScriptRoot "..\..\WaveDudeNext.WPF\bin\Debug\net10.0-windows\WaveDudeNext.WPF.exe"),
	[string]$Example = (Join-Path $PSScriptRoot "..\..\Examples\Example16_Wave_in_2_boxes.wdproj"),
	[string]$Out = (Join-Path $PSScriptRoot "..\..\Assets"),
	[int]$Frame = 1200,
	[string]$PngFolder = "",
	[double]$Zoom = 1.25,
	[string]$TextSwitch = "--greyscale-text"
)
. (Join-Path $PSScriptRoot "Common.ps1")

Add-Type -TypeDefinition @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public static class Shot
{
	[DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
	[DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
	[DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT rect, int size);
	[DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
	[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
	[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
	[StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
	public static RECT WindowRect(IntPtr hwnd) { RECT r; GetWindowRect(hwnd, out r); return r; }
	// screen position of the last Window() capture's top-left pixel
	public static int LastLeft, LastTop;

	// the window as DWM composes it (PW_RENDERFULLCONTENT), cropped to its visible frame: GetWindowRect includes the
	// invisible resize border, DWMWA_EXTENDED_FRAME_BOUNDS (9) does not
	public static Bitmap Window(IntPtr hwnd)
	{
		RECT full, visible;
		GetWindowRect(hwnd, out full);
		if (DwmGetWindowAttribute(hwnd, 9, out visible, Marshal.SizeOf(typeof(RECT))) != 0) visible = full;
		var bmp = new Bitmap(full.Right - full.Left, full.Bottom - full.Top, PixelFormat.Format32bppArgb);
		using (var g = Graphics.FromImage(bmp))
		{
			IntPtr hdc = g.GetHdc();
			PrintWindow(hwnd, hdc, 2);
			g.ReleaseHdc(hdc);
		}
		var crop = new Rectangle(visible.Left - full.Left, visible.Top - full.Top, visible.Right - visible.Left, visible.Bottom - visible.Top);
		crop.Intersect(new Rectangle(0, 0, bmp.Width, bmp.Height));
		Bitmap result = bmp.Clone(crop, PixelFormat.Format24bppRgb);
		bmp.Dispose();
		LastLeft = full.Left + crop.X;
		LastTop = full.Top + crop.Y;
		return result;
	}
	public static Bitmap Screen(int x, int y, int w, int h)
	{
		var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
		using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(x, y, 0, 0, bmp.Size);
		return bmp;
	}
	// whether a capture is one flat colour (PrintWindow on a window it cannot render leaves that)
	public static bool IsBlank(Bitmap bmp)
	{
		Color first = bmp.GetPixel(0, 0);
		for (int i = 1; i < 16; i++)
		{
			if (bmp.GetPixel(bmp.Width * i / 16, bmp.Height * i / 16) != first) return false;
		}
		return true;
	}
	public static Bitmap Crop(Bitmap src, int x, int y, int w, int h)
	{
		var r = new Rectangle(x, y, w, h);
		r.Intersect(new Rectangle(0, 0, src.Width, src.Height));
		return src.Clone(r, PixelFormat.Format24bppRgb);
	}
	public static void Paste(Bitmap dst, Bitmap src, int x, int y)
	{
		using (var g = Graphics.FromImage(dst)) g.DrawImageUnscaled(src, x, y);
	}
}
"@ -ReferencedAssemblies System.Drawing

$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]
$VK_F10 = 0x79; $VK_APPS = 0x5D
$BadgeRadius = 9
# the legend's heading per figure: the view or feature shown
$Headings = @{
	"main-window" = "The main window"; "project-tree" = "The project tree"; "preview" = "The preview"; "timeline" = "The timeline"
	"menu-file" = "The File menu"; "menu-file-export" = "File > Export"; "menu-file-examples" = "File > Examples"; "menu-edit" = "The Edit menu"; "menu-view" = "The View menu"
	"menu-simulation" = "The Simulation menu"; "menu-help" = "The Help menu"
	"menu-add-part" = "The Add part menu"; "menu-add-part-logic" = "Add part > Optical logic"
	"properties-project" = "Project"; "properties-scene" = "Scene"; "properties-simulation" = "Simulation"; "properties-display" = "Display"
	"properties-source" = "Source"; "properties-composite-source" = "Composite source"
	"properties-lens" = "Lens"; "properties-polygon" = "Polygon"; "properties-rectangle" = "Rectangle"; "properties-ellipse" = "Ellipse"
	"properties-triangle" = "Triangle"; "properties-outline" = "Outline"; "properties-fibre" = "Fibre"; "properties-bragg-reflector" = "Bragg reflector"
	"properties-grin-lens" = "GRIN lens"; "properties-xor-gate" = "XOR gate"; "properties-and-gate" = "AND gate"; "properties-or-gate" = "OR gate"
	"properties-zone-plate" = "Zone plate"; "properties-probe" = "Probe"; "properties-line-probe" = "Line probe"; "properties-colour-scheme" = "Colour scheme"
	"properties-part-common" = "Settings every part shares"; "properties-part-material" = "Glass and mirror settings"
}
$script:legend = New-Object System.Text.StringBuilder
$script:figureCount = 0
$script:problems = 0

function Problem([string]$text) { $script:problems++; Write-Output ("PROBLEM  " + $text) }
# a number for the SVG or the command line, in the invariant culture (a zoomed figure has halves)
function Inv($x) { return ([double]$x).ToString([Globalization.CultureInfo]::InvariantCulture) }
function Rect($e) { return $e.Current.BoundingRectangle }
function Has([string]$id) { try { (Find $id) -ne $null } catch { $false } }
function Cond([string]$property, $value) { return New-Object System.Windows.Automation.PropertyCondition($A::$property, $value) }
function FindByClass([string]$className) {
	for ($i = 0; $i -lt 10; $i++) {
		$e = $script:win.FindFirst($TS::Descendants, (Cond "ClassNameProperty" $className))
		if ($e -ne $null) { return $e }
		Start-Sleep -Milliseconds 200
	}
	throw "no element of class '$className'"
}
function ByType($root, [string]$type) { return @($root.FindAll($TS::Descendants, (Cond "ControlTypeProperty" $CT::$type))) }
# the process's top-level windows other than the main one: open menu popups
function Popups() {
	$result = @()
	foreach ($top in $A::RootElement.FindAll($TS::Children, (Cond "ProcessIdProperty" $script:p.Id))) {
		if ([int64]$top.Current.NativeWindowHandle -ne $script:hwnd.ToInt64()) { $result += $top }
	}
	return $result
}
# the open menu popups: a tooltip is a popup window too (the mouse may rest on the app), a menu holds menu entries
function Menu-Popups() { return @(Popups | Where-Object { @(ByType $_ "MenuItem").Count -gt 0 } | Sort-Object { $_.Current.BoundingRectangle.Left }) }
function PopupFocusedId() {
	foreach ($top in Popups) {
		$e = $top.FindFirst($TS::Descendants, (Cond "HasKeyboardFocusProperty" $true))
		if ($e -ne $null) { return $e.Current.AutomationId }
	}
	return "(none)"
}
# Tab until a tree row holds focus (the cycle passes every control of the window)
function FocusOnTree() {
	for ($i = 0; $i -lt 250; $i++) {
		if ((FocusedId) -like "Tree:*") { return }
		Key $VK_TAB 30
	}
	throw "could not reach the tree with Tab; focus is at $(FocusedId)"
}
# what a tree row shows, from its automation id (a row's UI Automation name is its object's ToString)
function Row-Label([string]$id) {
	switch -Wildcard ($id) {
		"Tree:Project" { return "Project" }
		"Tree:TreeColorSchemes" { return "Colour schemes" }
		"Tree:Tree*" { return $id.Substring(9) }
		default { return $id.Substring(5) }
	}
}
# selects the tree row with this automation id by keyboard: Home, then Down, expanding each group on the way
function Select-TreeRow([string]$id) {
	FocusOnTree
	Key $VK_HOME 120
	for ($i = 0; $i -lt 80 -and (FocusedId) -ne $id; $i++) {
		if ((FocusedId) -like "Tree:Tree*") { Key $VK_RIGHT 60 }
		if ((FocusedId) -eq $id) { break }
		Key $VK_DOWN 60
	}
	if ((FocusedId) -ne $id) { throw "tree row '$id' not reached; focus is at $(FocusedId)" }
	Start-Sleep -Milliseconds 500
}

# --- the user settings for the figures -------------------------------------------------------------------------
$settingsPath = Join-Path $env:LOCALAPPDATA "WaveDudeNext\Settings.xml"
$script:settingsBackup = if (Test-Path $settingsPath) { [IO.File]::ReadAllText($settingsPath) } else { $null }
# the window is the user's 1314 × 1126; a taller one (the properties session, so the longest form with its description
# fits the pane) starts at the top of the screen
function Set-FigureSettings([int]$treeHeight, [int]$propertiesHeight, [int]$windowHeight = 1126) {
	$xml = New-Object System.Xml.XmlDocument
	$xml.LoadXml($(if ($script:settingsBackup) { $script:settingsBackup } else { "<Settings />" }))
	$root = $xml.DocumentElement
	$root.SetAttribute("WindowWidth", "1314")
	$root.SetAttribute("WindowHeight", "$windowHeight")
	if ($windowHeight -gt 1126) { $root.SetAttribute("WindowTop", "0") }
	$root.SetAttribute("Theme", "Dark")
	$root.SetAttribute("Language", "en")
	# the part forms' description open (2026-10-29, the user's choice), so the figure shows the kind's text
	$root.SetAttribute("PartDescriptionExpanded", "true")
	foreach ($name in "WindowState", "PreviewResolution", "PreviewFrames", "PreviewSampleBytes", "PreviewDitherBits", "PreviewOrientation", "UiZoom") { $root.RemoveAttribute($name) }
	# the UI zoom setting (View > UI zoom) for a zoomed figure, which takes 16-bit samples too: those pictures are drawn
	# at the picture's size in device pixels, while the 8-bit default is a bitmap of the store's size that WPF scales
	# up, soft at a zoom
	if ($Zoom -ne 1) { $root.SetAttribute("UiZoom", (Inv $Zoom)); $root.SetAttribute("PreviewSampleBytes", "2") }
	$old = $root.SelectSingleNode("Layout")
	if ($old -ne $null) { $root.RemoveChild($old) | Out-Null }
	$layout = $xml.CreateElement("Layout")
	$layout.InnerXml = '<Split Length="1" Orientation="Horizontal"><Split Length="406" Orientation="Vertical">' +
		"<Group Length=`"$treeHeight`" Active=`"project`"><Item>project</Item></Group>" +
		"<Group Length=`"$propertiesHeight`" Active=`"properties`"><Item>properties</Item></Group>" +
		'</Split><Document Length="888" /></Split>'
	$root.PrependChild($layout) | Out-Null
	New-Item -ItemType Directory -Force (Split-Path $settingsPath) | Out-Null
	$xml.Save($settingsPath)
}
function Restore-Settings() {
	if ($script:settingsBackup -ne $null) { [IO.File]::WriteAllText($settingsPath, $script:settingsBackup) }
	elseif (Test-Path $settingsPath) { Remove-Item $settingsPath }
}

# --- figures ----------------------------------------------------------------------------------------------------
# a figure: a bitmap, where its top-left pixel lies on the screen, and the badges (screen rectangle, anchor, label)
# $start: the number of the figure's first badge (a crop of a form keeps the numbers of the whole form's figure)
function New-Figure([string]$name, [string]$title, [System.Drawing.Bitmap]$bitmap, [int]$left, [int]$top, [int]$start = 1) {
	return @{ Name = $name; Title = $title; Bitmap = $bitmap; Left = $left; Top = $top; Start = $start; Items = (New-Object System.Collections.ArrayList) }
}
# crops a window capture to a screen rectangle
function Crop-Figure([string]$name, [string]$title, $capture, [double]$l, [double]$t, [double]$r, [double]$b, [int]$start = 1) {
	# kept inside the capture, and the figure's origin is where the crop really starts (2026-10-29: a crop reaching
	# above the window was cut, but the badges were placed from the uncut top)
	$x = [math]::Max(0, [int][math]::Floor($l) - $capture.Left); $y = [math]::Max(0, [int][math]::Floor($t) - $capture.Top)
	$w = [math]::Min($capture.Bitmap.Width - $x, [int][math]::Ceiling($r) - $capture.Left - $x)
	$h = [math]::Min($capture.Bitmap.Height - $y, [int][math]::Ceiling($b) - $capture.Top - $y)
	$bmp = [Shot]::Crop($capture.Bitmap, $x, $y, $w, $h)
	return New-Figure $name $title $bmp ($capture.Left + $x) ($capture.Top + $y) $start
}
# The figures must not depend on where the mouse pointer rests (2026-10-30: it lay on a probe's graph box during a run,
# and the box's hover border made the main window's picture differ from the run before). A pointer over the app window
# is parked just outside the window, where it highlights nothing, before a capture, and put back at the end unless
# the user has moved it meanwhile. The only mouse action in the harnesses: it clicks nothing and activates nothing.
$script:parkedFrom = $null; $script:parkedAt = $null
function Park-Pointer() {
	$r = [Shot]::WindowRect($script:hwnd)
	$p = New-Object Shot+POINT
	$null = [Shot]::GetCursorPos([ref]$p)
	if ($p.X -lt $r.Left -or $p.X -ge $r.Right -or $p.Y -lt $r.Top -or $p.Y -ge $r.Bottom) { return }
	$screen = [System.Windows.Forms.SystemInformation]::VirtualScreen
	foreach ($spot in @(@(($r.Right + 8), $p.Y), @(($r.Left - 8), $p.Y), @($p.X, ($r.Bottom + 8)), @($p.X, ($r.Top - 8)))) {
		if ($spot[0] -lt $screen.Left -or $spot[0] -ge $screen.Right -or $spot[1] -lt $screen.Top -or $spot[1] -ge $screen.Bottom) { continue }
		if ($script:parkedFrom -eq $null) { $script:parkedFrom = @($p.X, $p.Y) }
		$null = [Shot]::SetCursorPos($spot[0], $spot[1])
		$script:parkedAt = @($spot[0], $spot[1])
		Write-Output ("note: the pointer rested on the window at {0},{1}; parked at {2},{3} for the captures" -f $p.X, $p.Y, $spot[0], $spot[1])
		Start-Sleep -Milliseconds 200                  # the hover visuals go
		return
	}
	Problem ("the pointer rests on the window at {0},{1} and no place outside it is on the screen; a hover state may show" -f $p.X, $p.Y)
}
function Restore-Pointer() {
	if ($script:parkedAt -eq $null) { return }
	$p = New-Object Shot+POINT
	$null = [Shot]::GetCursorPos([ref]$p)
	if ($p.X -eq $script:parkedAt[0] -and $p.Y -eq $script:parkedAt[1]) { $null = [Shot]::SetCursorPos($script:parkedFrom[0], $script:parkedFrom[1]) }
	$script:parkedAt = $null
}
function Capture-Window() {
	Park-Pointer
	$bmp = [Shot]::Window($script:hwnd)
	return @{ Bitmap = $bmp; Left = [Shot]::LastLeft; Top = [Shot]::LastTop }
}
# anchors: on (centre), tl (inside the top-left corner), corner (on the top-right corner), left, right, above, below
# (just outside that edge), gutter (left of the picture, on the element's first line), rgutter (right of the picture,
# at the element's centre line), bottom (below the picture)
# the hint is the control's tooltip (its UI Automation help text), the legend's description of the feature
function Add-Badge($fig, $rect, [string]$anchor, [string]$label, [string]$hint = "") {
	if ($rect -eq $null -or $rect.IsEmpty) { Problem ("{0}: no rectangle for '{1}'" -f $fig.Name, $label); return }
	$null = $fig.Items.Add(@{ Rect = $rect; Anchor = $anchor; Label = $label; Hint = $hint })
}
function Add-BadgeFor($fig, [string]$id, [string]$anchor, [string]$label) {
	if (-not (Has $id)) { Problem ("{0}: element '{1}' not found" -f $fig.Name, $id); return }
	$e = Find $id
	Add-Badge $fig (Rect $e) $anchor $label $e.Current.HelpText
}
# a form row's tooltip: the label has none, the editor beside it (its next sibling in the control view) has
$script:walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
function Row-Hint($row) {
	$hint = $row.Current.HelpText
	if ($hint -eq "" -and $row.Current.ClassName -eq "TextBlock") {
		$next = $script:walker.GetNextSibling($row)
		if ($next -ne $null) { $hint = $next.Current.HelpText }
	}
	return $hint
}
# the badge's centre in the figure's pixels; the radius and the gaps are the app's pixels, so they scale with the zoom
function Badge-Centre($fig, $item) {
	$R = $BadgeRadius * $Zoom; $gap = 3 * $Zoom
	$l = $item.Rect.Left - $fig.Left; $t = $item.Rect.Top - $fig.Top; $w = $item.Rect.Width; $h = $item.Rect.Height
	$cx = $l + $w / 2; $cy = $t + $h / 2
	switch ($item.Anchor) {
		"tl" { $cx = $l + $R + 4 * $Zoom; $cy = $t + $R + 4 * $Zoom }
		"corner" { $cx = $l + $w - 2 * $Zoom; $cy = $t + 2 * $Zoom }
		"left" { $cx = $l - $R - $gap }
		"right" { $cx = $l + $w + $R + $gap }
		"above" { $cy = $t - $R - $gap }
		"below" { $cy = $t + $h + $R + $gap }
		"gutter" { $cx = -$R - $gap; $cy = $t + [math]::Min($h, 23 * $Zoom) / 2 }
		"rgutter" { $cx = $fig.Bitmap.Width + $R + $gap }
		"bottom" { $cy = $fig.Bitmap.Height + $R + $gap }
	}
	return @([int][math]::Round($cx), [int][math]::Round($cy))
}
function Save-Figure($fig) {
	$R = $BadgeRadius * $Zoom
	$W = $fig.Bitmap.Width; $H = $fig.Bitmap.Height
	# a zoomed figure's PNG declares its pixel density; at 1 the bitmap is left alone, so the bytes stay the same
	if ($Zoom -ne 1) { $fig.Bitmap.SetResolution(96 * $Zoom, 96 * $Zoom) }
	$ms = New-Object System.IO.MemoryStream
	$fig.Bitmap.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
	$png = $ms.ToArray(); $ms.Dispose()
	if ($PngFolder -ne "") { [IO.File]::WriteAllBytes((Join-Path $PngFolder ($fig.Name + ".png")), $png) }
	$minX = 0; $minY = 0; $maxX = $W; $maxY = $H
	$badges = New-Object System.Text.StringBuilder
	$n = $fig.Start - 1
	$heading = if ($Headings.ContainsKey($fig.Name)) { $Headings[$fig.Name] } else { $fig.Name }
	$null = $script:legend.AppendLine("## " + $heading).AppendLine().AppendLine(("![{0}]({0}.svg)" -f $fig.Name)).AppendLine().AppendLine($fig.Title).AppendLine()
	foreach ($item in $fig.Items) {
		$n++
		$c = Badge-Centre $fig $item
		$minX = [math]::Min($minX, $c[0] - $R - 2 * $Zoom); $minY = [math]::Min($minY, $c[1] - $R - 2 * $Zoom)
		$maxX = [math]::Max($maxX, $c[0] + $R + 2 * $Zoom); $maxY = [math]::Max($maxY, $c[1] + $R + 2 * $Zoom)
		$title = [System.Security.SecurityElement]::Escape($item.Label)
		$null = $badges.AppendLine(('  <g><title>{0}</title><circle cx="{1}" cy="{2}" r="{3}" fill="#FFC107" stroke="#202020" stroke-width="{5}"/><text x="{1}" y="{2}" dy=".36em" fill="#000">{4}</text></g>' -f $title, $c[0], $c[1], (Inv $R), $n, (Inv $Zoom)))
		# the feature in bold, its description (the tooltip) on the next line of the item
		$null = $script:legend.AppendLine(("{0}. **{1}**" -f $n, $item.Label) + $(if ($item.Hint -ne "") { "  " } else { "" }))
		if ($item.Hint -ne "") { $null = $script:legend.AppendLine("   " + $item.Hint) }
	}
	$null = $script:legend.AppendLine()
	$svg = New-Object System.Text.StringBuilder
	# the size in CSS pixels at 100 %, the view box in the figure's pixels: a zoomed figure is drawn at its 100 % size
	$null = $svg.AppendLine(('<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="{0}" height="{1}" viewBox="{2} {3} {4} {5}">' -f (Inv (($maxX - $minX) / $Zoom)), (Inv (($maxY - $minY) / $Zoom)), (Inv $minX), (Inv $minY), (Inv ($maxX - $minX)), (Inv ($maxY - $minY))))
	$null = $svg.AppendLine(('  <image x="0" y="0" width="{0}" height="{1}" xlink:href="data:image/png;base64,{2}"/>' -f $W, $H, [Convert]::ToBase64String($png)))
	if ($n -gt 0) {
		$null = $svg.AppendLine(('  <g font-family="Segoe UI, Helvetica, Arial, sans-serif" font-size="{0}" font-weight="bold" text-anchor="middle">' -f (Inv (11 * $Zoom))))
		$null = $svg.Append($badges.ToString()).AppendLine('  </g>')
	}
	$null = $svg.AppendLine('</svg>')
	[IO.File]::WriteAllText((Join-Path $Out ($fig.Name + ".svg")), $svg.ToString())
	$fig.Bitmap.Dispose()
	$script:figureCount++
	Write-Output ("figure {0}: {1}x{2}, {3} badges" -f $fig.Name, $W, $H, $n)
}

# the dock group holding a pane: the title bar row (21 px at zoom 1) above the pane's content, inside a 1 px border
function Pane-Rect([string]$paneId, [string]$contentClass) {
	$title = Rect (Find ("DockTitle:" + $paneId))
	$content = Rect (FindByClass $contentClass)
	$top = $title.Top + $title.Height / 2 - 11.5 * $Zoom
	return @(($content.Left - $Zoom), $top, ($content.Right + $Zoom), ($content.Bottom + $Zoom))
}

# an English string of the app, by its key in the string table (for elements a harness finds by name)
$script:enStrings = $null
function En-String([string]$key) {
	if ($script:enStrings -eq $null) {
		$script:enStrings = New-Object System.Xml.XmlDocument
		$script:enStrings.Load((Join-Path $PSScriptRoot "..\..\WaveDudeNext.WPF\Strings\Strings.EN.xaml"))
	}
	$node = $script:enStrings.SelectSingleNode("//*[@*[local-name()='Key']='$key']")
	if ($node -eq $null) { throw "no string '$key' in the English table" }
	return $node.InnerText
}
# the description of a shipped example from its menu entry "n. Title": the n-th file of the repository's Examples
# folder, which the app embeds in file order, has a Description element
function Example-Description([string]$entry) {
	if ($entry -notmatch "^(\d+)\.") { return "" }
	$file = Get-ChildItem (Join-Path $PSScriptRoot "..\..\Examples") -Filter ("Example{0:00}_*.wdproj" -f [int]$Matches[1]) | Select-Object -First 1
	if ($file -eq $null) { return "" }
	$xml = New-Object System.Xml.XmlDocument
	$xml.Load($file.FullName)
	$node = $xml.SelectSingleNode("/Project/Description")
	return $(if ($node -ne $null) { $node.InnerText.Trim() } else { "" })
}
# an element of the properties pane: by automation id, or by name for "key:<string key>" (the first match)
function Find-InPane($pane, [string]$key) {
	$c = if ($key.StartsWith("key:")) { Cond "NameProperty" (En-String $key.Substring(4)) } else { Cond "AutomationIdProperty" $key }
	return $pane.FindFirst($TS::Descendants, $c)
}

# the properties form's rows: the labels and full-width check boxes at the label column (the smallest Left among the
# form's TextBlocks), top to bottom; section headings are Labels and the editors sit in the right column. The
# extras, pairs of an element key (see Find-InPane) and a label, get badges in the right gutter: the controls of a
# row without a label of its own (a stop, a point) and the buttons. A crop of the form (2026-10-30, the user's
# request, for the settings several forms share): the rows from $fromRow to $toRow (row names, both inclusive, "" for
# the form's first or last), the picture from the pane's top, or from the section heading named by the string key
# $heading, or from the first row, down to the last row's editor; the badges count from $start, so they keep the
# numbers of the whole form's figure.
function Properties-Figure([string]$name, [string]$title, $extras = @(), [string]$fromRow = "", [string]$toRow = "", [string]$heading = "", [int]$start = 1) {
	$pane = FindByClass "PropertiesPane"
	$all = @($pane.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition))
	$texts = @($all | Where-Object { $_.Current.ClassName -eq "TextBlock" -and -not $_.Current.IsOffscreen })
	if ($texts.Count -eq 0) { Problem "$name`: no text in the properties pane"; return }
	$column = ($texts | ForEach-Object { $_.Current.BoundingRectangle.Left } | Measure-Object -Minimum).Minimum
	# a section heading is a Label, whose text is a TextBlock inside it (its parent in the control view is the Label);
	# a hint paragraph wraps, so a row is a single line
	$walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
	$rows = @($all | Where-Object { ($_.Current.ClassName -eq "TextBlock" -or $_.Current.ClassName -eq "CheckBox") -and [math]::Abs($_.Current.BoundingRectangle.Left - $column) -le 2 * $Zoom -and $_.Current.BoundingRectangle.Height -le 24 * $Zoom -and $walker.GetParent($_).Current.ControlType -ne $CT::Text } | Sort-Object { $_.Current.BoundingRectangle.Top })
	$offscreen = @($rows | Where-Object { $_.Current.IsOffscreen })
	if ($offscreen.Count -gt 0) { Problem ("{0}: {1} rows lie outside the pane (form taller than the pane)" -f $name, $offscreen.Count) }
	$rect = Pane-Rect "properties" "PropertiesPane"
	$top = $rect[1]
	# cropped below the form's last control (the containers span the pane; the form's margin is 8 px)
	$bottom = ($all | Where-Object { -not $_.Current.IsOffscreen -and $_.Current.ControlType -ne $CT::Pane -and $_.Current.ControlType -ne $CT::Custom -and $_.Current.ControlType -ne $CT::Group } | ForEach-Object { $_.Current.BoundingRectangle.Bottom } | Measure-Object -Maximum).Maximum + 9 * $Zoom
	if ($fromRow -ne "" -or $toRow -ne "") {
		$names = @($rows | ForEach-Object { $_.Current.Name })
		$first = if ($fromRow -ne "") { [array]::IndexOf($names, $fromRow) } else { 0 }
		$last = if ($toRow -ne "") { [array]::IndexOf($names, $toRow) } else { $names.Count - 1 }
		if ($first -lt 0 -or $last -lt $first) { Problem ("{0}: the rows '{1}' to '{2}' were not found" -f $name, $fromRow, $toRow); return }
		$rows = @($rows[$first..$last])
		# down to the last row's editor (the next sibling in the control view, when it lies on the row's line)
		$lastRect = Rect $rows[$rows.Count - 1]
		$bottom = $lastRect.Bottom + 6 * $Zoom
		$editor = $walker.GetNextSibling($rows[$rows.Count - 1])
		if ($editor -ne $null -and (Rect $editor).Top -lt $lastRect.Bottom) { $bottom = [math]::Max($bottom, (Rect $editor).Bottom + 6 * $Zoom) }
		if ($heading -ne "") {
			$label = Find-InPane $pane ("key:" + $heading)
			if ($label -eq $null) { Problem ("{0}: the section heading '{1}' was not found" -f $name, $heading); return }
			$top = (Rect $label).Top - 6 * $Zoom
		}
		elseif ($fromRow -ne "") { $top = (Rect $rows[0]).Top - 6 * $Zoom }
	}
	$capture = Capture-Window
	$fig = Crop-Figure $name $title $capture $rect[0] $top $rect[2] ([math]::Min($rect[3], $bottom)) $start
	$capture.Bitmap.Dispose()
	# a part form opens with the kind's description under a collapsible header (open in the figures, the user's choice)
	$toggle = $(if ($fromRow -eq "") { Find-InPane $pane "PartDescriptionToggle" } else { $null })
	if ($toggle -ne $null) { Add-Badge $fig (Rect $toggle) "rgutter" "Description of the kind, opened and closed with Space; one setting for every kind" $toggle.Current.HelpText }
	foreach ($row in $rows) { if (-not $row.Current.IsOffscreen) { Add-Badge $fig (Rect $row) "gutter" $row.Current.Name (Row-Hint $row) } }
	foreach ($extra in $extras) {
		$e = Find-InPane $pane $extra[0]
		if ($e -eq $null) { Problem ("{0}: element '{1}' not found" -f $name, $extra[0]); continue }
		Add-Badge $fig (Rect $e) "rgutter" $extra[1] $e.Current.HelpText
	}
	Save-Figure $fig
}

# opens the k-th menu of the bar (0 = File) by keyboard: F10 enters the menu mode, Right moves along the headers,
# Down opens the popup on its first entry
function Open-Menu([int]$index) {
	for ($attempt = 0; $attempt -lt 3; $attempt++) {
		[Native]::SysKey($script:hwnd, $VK_F10, $false); Start-Sleep -Milliseconds 300
		for ($i = 0; $i -lt $index; $i++) { Key $VK_RIGHT 150 }
		Key $VK_DOWN 400
		if (WaitFor { (Menu-Popups).Count -gt 0 } 1500 100) { return }
		Write-Output ("note: menu {0} did not open (focus {1}, foreground {2}); trying again" -f $index, (FocusedId), (Foreground))
		Close-Menus
	}
}
function Close-Menus() { Key $VK_ESCAPE 150; Key $VK_ESCAPE 150; Key $VK_ESCAPE 150 }
# a popup's pixels: PrintWindow on its own window, else the screen (a WPF popup is topmost)
function Capture-Popup($popup) {
	Park-Pointer
	$r = Rect $popup
	$bmp = [Shot]::Window([IntPtr][int64]$popup.Current.NativeWindowHandle)
	if ([Shot]::IsBlank($bmp)) {
		$bmp.Dispose()
		$bmp = [Shot]::Screen([int]$r.Left, [int]$r.Top, [int]$r.Width, [int]$r.Height)
		return @{ Bitmap = $bmp; Left = [int]$r.Left; Top = [int]$r.Top; FromScreen = $true }
	}
	return @{ Bitmap = $bmp; Left = [Shot]::LastLeft; Top = [Shot]::LastTop; FromScreen = $false }
}
# the figure of the open menus: the menu bar with the popups composed over the window; the entries of the last
# (innermost) popup get the badges, left of the popup
function Menu-Figure([string]$name, [string]$title, [scriptblock]$hintOf = $null) {
	$popups = Menu-Popups
	if ($popups.Count -eq 0) { Problem "$name`: no menu popup is open"; return }
	$capture = Capture-Window
	# the crop: from the menu bar down to the popups, or from a popup's top when it opened upward (a context menu
	# near the top of the window, 2026-10-29)
	$bar = Rect @(ByType $script:win "Menu")[0]
	$top = $bar.Top - $Zoom; $right = $bar.Left + 260 * $Zoom; $bottom = 0
	foreach ($popup in $popups) {
		$shot = Capture-Popup $popup
		if ($shot.FromScreen) { Write-Output ("note: popup of {0} read from the screen" -f $name) }
		[Shot]::Paste($capture.Bitmap, $shot.Bitmap, ($shot.Left - $capture.Left), ($shot.Top - $capture.Top))
		$shot.Bitmap.Dispose()
		$pr = Rect $popup
		$top = [math]::Min($top, $pr.Top - 12 * $Zoom); $right = [math]::Max($right, $pr.Right + 12 * $Zoom); $bottom = [math]::Max($bottom, $pr.Bottom + 12 * $Zoom)
	}
	$fig = Crop-Figure $name $title $capture $capture.Left $top $right $bottom
	$capture.Bitmap.Dispose()
	$last = $popups[$popups.Count - 1]
	foreach ($item in (ByType $last "MenuItem")) {
		if ($item.Current.IsOffscreen) { continue }
		# the description: the entry's tooltip, or what the caller knows about the entry (an example's project file)
		$hint = if ($hintOf -ne $null) { & $hintOf $item } else { $item.Current.HelpText }
		Add-Badge $fig (Rect $item) "left" $item.Current.Name $hint
	}
	Save-Figure $fig
}

New-Item -ItemType Directory -Force $Out | Out-Null
if ($PngFolder -ne "") { New-Item -ItemType Directory -Force $PngFolder | Out-Null }
$null = $script:legend.AppendLine("# Figures").AppendLine().AppendLine("Generated by ``_tools/ui_check/Screenshots.ps1``; under each figure, what its numbered badges mark, each with the control's tooltip where it has one.").AppendLine()

try {
	# ----- session 1: the example project ---------------------------------------------------------------------
	Set-FigureSettings 475 563
	Start-App $Exe $Example $TextSwitch
	try {
		# waves in the pictures: a scratch frame at the wanted position (PageUp moves ten frames)
		FocusOn "PreviewTimeline"
		Key $VK_HOME 100
		for ($i = 0; $i -lt [int]($Frame / 10); $i++) { Key $VK_PRIOR 10 }
		Start-Sleep -Milliseconds 300
		if (-not (WaitFor { (Position) -eq $Frame } 5000 100)) { Problem ("position is {0}, wanted {1}" -f (Position), $Frame) }
		# the frame has arrived when the picture stops changing (the busy dimming and spinner move until then)
		$h0 = FieldHash
		$settled = WaitFor { Start-Sleep -Milliseconds 700; $h1 = FieldHash; ($h1 -ne $h0) -and ($h1 -eq (FieldHash)) } 60000 100
		if (-not $settled) { Problem "the scratch frame did not arrive" }
		# the Project row selected, as after opening, with the focus on it
		Select-TreeRow "Tree:Project"

		$capture = Capture-Window
		# the README's picture (2026-10-30, the user's request): the same capture as a plain PNG, without badges
		if ($Zoom -ne 1) { $capture.Bitmap.SetResolution(96 * $Zoom, 96 * $Zoom) }
		$capture.Bitmap.Save((Join-Path $Out "main-window.png"), [System.Drawing.Imaging.ImageFormat]::Png)
		$fig = New-Figure "main-window" "The main window with the example project at frame $Frame." $capture.Bitmap $capture.Left $capture.Top
		$headers = @(ByType @(ByType $script:win "Menu")[0] "MenuItem")
		Add-Badge $fig (Rect $headers[$headers.Count - 1]) "right" "Menu bar"
		Add-BadgeFor $fig "DockTitle:project" "right" "Project pane: the tree of the project's objects"
		Add-BadgeFor $fig "DockTitle:properties" "right" "Properties pane: the selected object's form"
		Add-BadgeFor $fig "PreviewFieldImage" "tl" "Field picture"
		Add-BadgeFor $fig "PreviewIntensityImage" "tl" "Intensity picture"
		Add-BadgeFor $fig "PreviewFieldImageBrightness" "on" "Header strip of a picture: title, brightness, Auto"
		Add-BadgeFor $fig "PreviewTimeline" "on" "Timeline"
		Add-BadgeFor $fig "PreviewFrame" "on" "Transport controls, position readout, speed, layout"
		Add-BadgeFor $fig "StatusMessage" "right" "Status bar"
		Save-Figure $fig

		# the project tree: pane title, toolbar, every row, and the controls of one group row and one item row
		$capture = Capture-Window
		$rect = Pane-Rect "project" "ProjectTree"
		# cropped below the last row (the root row's rectangle spans every row)
		$lastRow = (ByType (ByType $script:win "Tree")[0] "TreeItem" | ForEach-Object { $_.Current.BoundingRectangle.Bottom } | Measure-Object -Maximum).Maximum
		$fig = Crop-Figure "project-tree" "The Project pane with the example project." $capture $rect[0] $rect[1] $rect[2] ([math]::Min($rect[3], $lastRow + 9 * $Zoom))
		$capture.Bitmap.Dispose()
		Add-BadgeFor $fig "DockTitle:project" "right" "Pane title"
		Add-BadgeFor $fig "ToolAddSource" "corner" "Add a source"
		Add-BadgeFor $fig "ToolAddProbe" "corner" "Add a probe"
		Add-BadgeFor $fig "ToolAddPart" "corner" "Add a part (a menu of the kinds)"
		Add-BadgeFor $fig "ToolMoveUp" "corner" "Move the selected object up"
		Add-BadgeFor $fig "ToolMoveDown" "corner" "Move the selected object down"
		Add-BadgeFor $fig "ToolRemove" "corner" "Remove the selected object"
		# the rows as concepts, not the example's objects (2026-10-30, the user's request): the root, the three settings
		# rows, the Sources group with its expander and its plus, one object row with its controls, the other groups
		Add-BadgeFor $fig "Tree:Project" "gutter" "Project: the root row"
		Add-BadgeFor $fig "Tree:Scene" "gutter" "Scene"
		Add-BadgeFor $fig "Tree:Simulation" "gutter" "Simulation"
		Add-BadgeFor $fig "Tree:Display" "gutter" "Display"
		Add-BadgeFor $fig "Tree:TreeSources" "gutter" "Sources group"
		$group = Find "Tree:TreeSources"
		$groupTop = (Rect $group).Top
		# the group row's own buttons lie on its first line: the expander at the left, the plus at the right
		$groupButtons = @(ByType $group "Button" | Where-Object { $_.Current.BoundingRectangle.Top -lt $groupTop + 23 * $Zoom } | Sort-Object { $_.Current.BoundingRectangle.Left })
		if ($groupButtons.Count -ge 2) {
			Add-Badge $fig (Rect $groupButtons[0]) "on" "Expand or collapse a group" $groupButtons[0].Current.HelpText
			Add-Badge $fig (Rect $groupButtons[$groupButtons.Count - 1]) "on" "Add to this group" $groupButtons[$groupButtons.Count - 1].Current.HelpText
		}
		else { Problem ("project-tree: {0} buttons on the Sources row, expected the expander and the plus" -f $groupButtons.Count) }
		# the controls of one object row (the row itself needs no badge, 2026-10-30, the user's call)
		$item = @(ByType $group "TreeItem")
		if ($item.Count -gt 0) {
			$row = $item[0]
			Add-BadgeFor $fig ("Enabled:" + $row.Current.Name) "on" "Enabled"
			$buttons = @(ByType $row "Button" | Sort-Object { $_.Current.BoundingRectangle.Left })
			$labels = "Move up", "Move down", "Remove"
			for ($i = 0; $i -lt [math]::Min(3, $buttons.Count); $i++) { Add-Badge $fig (Rect $buttons[$i]) "on" $labels[$i] $buttons[$i].Current.HelpText }
		}
		else { Problem "project-tree: the Sources group has no object row" }
		Add-BadgeFor $fig "Tree:TreeParts" "gutter" "Parts group"
		Add-BadgeFor $fig "Tree:TreeProbes" "gutter" "Probes group"
		Add-BadgeFor $fig "Tree:TreeColorSchemes" "gutter" "Colour schemes group"
		Save-Figure $fig

		# the preview: both pictures with their header strips, handles, a graph box and the splitter
		$capture = Capture-Window
		$fieldImage = Rect (Find "PreviewFieldImage"); $intensityImage = Rect (Find "PreviewIntensityImage")
		$fieldStrip = Rect (Find "PreviewFieldImageBrightness"); $intensityStrip = Rect (Find "PreviewIntensityImageBrightness")
		$left = [math]::Min($fieldStrip.Left, $intensityStrip.Left) - 6 * $Zoom
		$top = [math]::Min($fieldImage.Top, $fieldStrip.Top) - 10 * $Zoom
		$fig = Crop-Figure "preview" "The preview: field and intensity pictures of the example project at frame $Frame." $capture $left $top ($fieldImage.Right + 8 * $Zoom) ($intensityImage.Bottom + 8 * $Zoom)
		$capture.Bitmap.Dispose()
		Add-BadgeFor $fig "PreviewFieldImage" "tl" "Field picture"
		Add-BadgeFor $fig "PreviewFieldImageBrightness" "on" "Brightness of the field picture"
		Add-BadgeFor $fig "PreviewFieldImageAutoBrightness" "on" "Auto brightness (fit to the frame shown)"
		Add-BadgeFor $fig "PreviewIntensityImage" "tl" "Intensity picture"
		Add-BadgeFor $fig "PreviewIntensityImageBrightness" "on" "Brightness of the intensity picture"
		Add-BadgeFor $fig "PreviewIntensityImageAutoBrightness" "on" "Auto brightness of the intensity picture"
		Add-BadgeFor $fig "PreviewSplitter" "on" "Splitter between the pictures"
		$handles = @($script:win.FindAll($TS::Descendants, (Cond "ControlTypeProperty" $CT::Thumb)) | Where-Object { $_.Current.AutomationId -like "Source:*" -or $_.Current.AutomationId -like "Part:*" -or $_.Current.AutomationId -like "Probe:*" -or $_.Current.AutomationId -like "ProbeBox:*" })
		$kindsDone = @{}
		foreach ($h in $handles) {
			$kind = $h.Current.AutomationId.Split(":")[0]
			if ($kindsDone.ContainsKey($kind)) { continue }
			$kindsDone[$kind] = $true
			$name = $h.Current.AutomationId.Substring($kind.Length + 1) -replace ":([AB])$", ', end $1'
			$label = switch ($kind) { "Source" { "Source handle ($name)" } "Part" { "Part handle ($name)" } "Probe" { "Probe handle ($name)" } "ProbeBox" { "Probe graph box ($name)" } }
			Add-Badge $fig (Rect $h) $(if ($kind -eq "ProbeBox") { "above" } else { "right" }) $label $h.Current.HelpText
		}
		Save-Figure $fig

		# the timeline and the transport row
		$capture = Capture-Window
		$timeline = Rect (Find "PreviewTimeline"); $run = Rect (Find "PreviewRun"); $stacked = Rect (Find "PreviewStacked")
		$fig = Crop-Figure "timeline" "The timeline with the playhead and the in and out points, and the transport controls." $capture ($run.Left - 6 * $Zoom) ($timeline.Top - 22 * $Zoom) ($stacked.Right + 6 * $Zoom) ($run.Bottom + 5 * $Zoom)
		$capture.Bitmap.Dispose()
		# the playhead and the brackets, placed by the bar's own mapping: a one-pixel inset at both ends, then
		# frame × track width / maximum; the bar reports the playhead and the maximum through its range value and the
		# in and out points through its item status
		$bar = Find "PreviewTimeline"
		$range = Pattern $bar "RangeValue"
		$maximum = [math]::Max(1, $range.Current.Maximum)
		$marks = New-Object System.Collections.ArrayList
		$null = $marks.Add(@("Playhead: the position shown", $range.Current.Value))
		if ($bar.Current.ItemStatus -match "^range (\d+)\.\.(\d+)$") { $null = $marks.Add(@("In point", [int]$Matches[1])); $null = $marks.Add(@("Out point", [int]$Matches[2])) }
		else { Problem "the timeline reports no in/out range" }
		foreach ($mark in $marks) {
			$x = $timeline.Left + $Zoom + $mark[1] * ($timeline.Width - 2 * $Zoom) / $maximum
			Add-Badge $fig (New-Object System.Windows.Rect(($x - $Zoom), $timeline.Top, (2 * $Zoom), $timeline.Height)) "above" $mark[0]
		}
		foreach ($pair in @(
			@("PreviewRun", "Run the simulation"), @("PreviewPauseRun", "Pause the simulation"), @("PreviewPlay", "Play"), @("PreviewPause", "Pause"),
			@("PreviewStepBack", "One frame back"), @("PreviewStepForward", "One frame forward"),
			@("PreviewSetIn", "Set the in point"), @("PreviewSetOut", "Set the out point"), @("PreviewClearRange", "Clear the in and out points"),
			@("PreviewLoop", "Loop"), @("PreviewLive", "Live: follow a running run"),
			@("PreviewFrame", "Position: frame of frames"), @("PreviewStep", "Step of the position"),
			@("PreviewSpeed", "Playback speed"), @("PreviewShowField", "Show the field"), @("PreviewShowIntensity", "Show the intensity"),
			@("PreviewSideBySide", "Pictures side by side"), @("PreviewStacked", "Pictures stacked"))) {
			Add-BadgeFor $fig $pair[0] "bottom" $pair[1]
		}
		Save-Figure $fig

		# the menus
		$menuNames = "File", "Edit", "View", "Simulation", "Help"
		for ($m = 0; $m -lt $menuNames.Count; $m++) {
			Open-Menu $m
			Menu-Figure ("menu-" + $menuNames[$m].ToLower()) ("The " + $menuNames[$m] + " menu.")
			Close-Menus
		}
		Open-Menu 0
		for ($i = 0; $i -lt 12 -and (PopupFocusedId) -ne "MenuExport"; $i++) { Key $VK_DOWN 120 }
		if ((PopupFocusedId) -eq "MenuExport") {
			Key $VK_RIGHT 400
			Menu-Figure "menu-file-export" "The File > Export submenu."
		}
		else { Problem "the Export entry of the File menu was not reached" }
		Close-Menus
		Open-Menu 0
		for ($i = 0; $i -lt 12 -and (PopupFocusedId) -ne "MenuExamples"; $i++) { Key $VK_DOWN 120 }
		if ((PopupFocusedId) -eq "MenuExamples") {
			Key $VK_RIGHT 400
			# each entry's description is its project's Description element (the entries have no tooltip)
			Menu-Figure "menu-file-examples" "The File > Examples submenu: the shipped example projects." { param($item) Example-Description $item.Current.Name }
		}
		else { Problem "the Examples entry of the File menu was not reached" }
		Close-Menus

		# the Add part menu of the tree's toolbar: the kinds, each entry's tooltip its description; Space on the button
		# runs its command, which opens the menu under the button as a click does (the Apps key would centre it on the
		# button, above the window's top), Down then walks its entries
		FocusOn "ToolAddPart"
		Key $VK_SPACE 400
		if (WaitFor { (Menu-Popups).Count -gt 0 } 1500 100) {
			Menu-Figure "menu-add-part" "The Add part menu of the project tree: the kinds of optical parts."
			for ($i = 0; $i -lt 16 -and (PopupFocusedId) -ne "AddPart:OpticalLogic"; $i++) { Key $VK_DOWN 120 }
			if ((PopupFocusedId) -eq "AddPart:OpticalLogic") {
				Key $VK_RIGHT 400
				Menu-Figure "menu-add-part-logic" "The Optical logic submenu of the Add part menu: the gates."
			}
			else { Problem "the Optical logic entry of the Add part menu was not reached" }
		}
		else { Problem "the Add part menu did not open" }
		Close-Menus
	}
	finally { Stop-App }

	# ----- session 2: one object of every kind, a short tree, the properties pane at full height ------------------
	Restore-Settings
	Set-FigureSettings 140 1172 1400
	Start-App $Exe (Join-Path $PSScriptRoot "figures.wdproj") $TextSwitch
	try {
		$tree = (ByType $script:win "Tree")[0]
		$ids = @(ByType $tree "TreeItem" | ForEach-Object { $_.Current.AutomationId })
		Write-Output ("tree rows: " + ($ids -join ", "))
		# tree row, figure name, title, extras (element key and label, badges in the right gutter)
		foreach ($spec in @(
			@("Tree:Project", "properties-project", "The project's own properties.", @(, @("ProjectNewSeed", "New seed: a fresh random seed"))),
			@("Tree:Scene", "properties-scene", "The Scene form (an image scene).", @(, @("SceneBrowse", "The image buttons: the dots pick an image file, the save glyph writes the embedded image out, the X drops it"))),
			@("Tree:Simulation", "properties-simulation", "The Simulation form.", @()),
			@("Tree:Display", "properties-display", "The Display form.", @()),
			@("Tree:Source 1", "properties-source", "A point source.", @()),
			@("Tree:Source 2", "properties-composite-source", "A composite (array) source, pulsed.", @()),
			@("Tree:Lens*", "properties-lens", (En-String "Kind.LensPart"), @()),
			@("Tree:Polygon*", "properties-polygon", (En-String "Kind.PolygonPart"), @()),
			@("Tree:Rectangle*", "properties-rectangle", (En-String "Kind.RectanglePart"), @(, @("PartLinked", "Linked: the chain keeps the width equal to the length"))),
			@("Tree:Ellipse*", "properties-ellipse", (En-String "Kind.EllipsePart"), @(, @("PartLinked", "Linked: the chain keeps the width equal to the length"))),
			@("Tree:Triangle*", "properties-triangle", (En-String "Kind.TrianglePart"), @()),
			@("Tree:Outline*", "properties-outline", (En-String "Kind.OutlinePart"), @(@("key:TipRemovePoint", "A point: X, Y, remove"), @("PartAddPoint", "Add a point"))),
			@("Tree:Fib*", "properties-fibre", (En-String "Kind.FiberPart"), @(@("key:TipRemoveNode", "Per node: smooth, remove"), @("PartAddNode", "Add a node"))),
			@("Tree:Bragg*", "properties-bragg-reflector", (En-String "Kind.BraggReflectorPart"), @()),
			@("Tree:GRIN*", "properties-grin-lens", (En-String "Kind.GrinLensPart"), @()),
			@("Tree:XOR*", "properties-xor-gate", (En-String "Kind.XorGatePart"), @()),
			@("Tree:AND*", "properties-and-gate", (En-String "Kind.AndGatePart"), @()),
			@("Tree:OR *", "properties-or-gate", (En-String "Kind.OrGatePart"), @()),
			@("Tree:Zone*", "properties-zone-plate", (En-String "Kind.ZonePlatePart"), @()),
			@("Tree:Probe 1", "properties-probe", "A point probe of the field.", @()),
			@("Tree:Probe 2", "properties-line-probe", "A line probe of the intensity, logarithmic.", @()),
			@("Tree:Scheme*", "properties-colour-scheme", "A colour scheme of the project.", @(@("StopSwatch:0", "A stop: position, colour swatch (opens the picker), hex value, remove"), @("SchemeAddStop", "Add a stop"))))) {
			$id = @($ids | Where-Object { $_ -like $spec[0] })
			if ($id.Count -eq 0) { Problem ("no tree row matches '{0}'" -f $spec[0]); continue }
			Select-TreeRow $id[0]
			Properties-Figure $spec[1] $spec[2] $spec[3]
		}
		# two crops of the rectangle's form (2026-10-30, the user's request), for the manual's sections on the settings
		# several forms share: the top of the form down to the Angle row, the badges 1 to 6 of every part's figure; the
		# Material section, the badges 7 to 15 of the shape figures
		$rectangle = @($ids | Where-Object { $_ -like "Tree:Rectangle*" })
		if ($rectangle.Count -gt 0) {
			Select-TreeRow $rectangle[0]
			Properties-Figure "properties-part-common" "The settings every part shares: the top of every part's form, here the rectangle's." @() "" (En-String "Label.OpticalPart.Angle")
			Properties-Figure "properties-part-material" "The glass and mirror settings: the Material section of a shape's form, here the rectangle's." @() (En-String "Label.ShapePart.Mirror") (En-String "Label.ShapePart.AbsorptionRamp") "SectionMaterial" 7
		}
		else { Problem "no tree row matches 'Tree:Rectangle*' for the crops" }
	}
	finally { Stop-App }
}
finally {
	Restore-Pointer
	Restore-Settings
	[IO.File]::WriteAllText((Join-Path $Out "Legend.md"), $script:legend.ToString())
}
Write-Output ("{0} figures written to {1}; {2} problems" -f $script:figureCount, (Resolve-Path $Out), $script:problems)
