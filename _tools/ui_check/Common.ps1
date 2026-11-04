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

# Shared part of the UI harnesses (dot-source it): the native helpers, UI Automation lookups and the
# launch/park/stop logic. Keystrokes are posted to the app window (WM_KEYDOWN/WM_CHAR), results are
# read back through UI Automation *properties*. Nothing here activates the app window or uses the
# mouse, so a harness can run while the machine is in use. UI Automation *patterns* (Invoke,
# SetValue, Select, SetFocus) are deliberately not called: on this app each one focuses its target
# and brings the window to the foreground. Modifier keys cannot be posted either (WPF reads Shift/
# Ctrl/Alt from the real keyboard state), so every shortcut a harness needs must have an unmodified key.
#
# Expects the caller to have declared $Trace (switch) before dot-sourcing; sets $script:p, $win, $hwnd, $park.

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public static class Native
{
	[DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
	[DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
	[DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
	[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
	[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
	[DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

	// resizes the window in place without activating it (SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE)
	public static void Resize(IntPtr hwnd, int width, int height) { SetWindowPos(hwnd, IntPtr.Zero, 0, 0, width, height, 0x0002 | 0x0004 | 0x0010); }
	public static int WindowWidth(IntPtr hwnd) { RECT r; GetWindowRect(hwnd, out r); return r.Right - r.Left; }
	public static int WindowHeight(IntPtr hwnd) { RECT r; GetWindowRect(hwnd, out r); return r.Bottom - r.Top; }
	[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

	// PW_RENDERFULLCONTENT: ask DWM for the composed content, which a hardware-rendered WPF window needs
	static Bitmap Grab(IntPtr hwnd, out RECT r)
	{
		GetWindowRect(hwnd, out r);
		var bmp = new Bitmap(r.Right - r.Left, r.Bottom - r.Top);
		using (var g = Graphics.FromImage(bmp))
		{
			IntPtr hdc = g.GetHdc();
			PrintWindow(hwnd, hdc, 2);
			g.ReleaseHdc(hdc);
		}
		return bmp;
	}

	public static void Capture(IntPtr hwnd, string path)
	{
		RECT r;
		using (var bmp = Grab(hwnd, out r)) bmp.Save(path, ImageFormat.Png);
	}

	// MD5 of a screen-rectangle crop of the window's content, for "the picture changed" checks
	public static string Hash(IntPtr hwnd, int x, int y, int w, int h)
	{
		RECT r;
		using (var bmp = Grab(hwnd, out r))
		using (var crop = bmp.Clone(new Rectangle(x - r.Left, y - r.Top, w, h), bmp.PixelFormat))
		using (var ms = new System.IO.MemoryStream())
		{
			crop.Save(ms, ImageFormat.Bmp);
			using (var md5 = System.Security.Cryptography.MD5.Create())
				return BitConverter.ToString(md5.ComputeHash(ms.ToArray()));
		}
	}

	// WM_KEYDOWN / WM_KEYUP posted straight to the window: no SendInput, no focus change
	public static void Key(IntPtr hwnd, int vk)
	{
		PostMessage(hwnd, 0x0100, (IntPtr)vk, (IntPtr)1);
		PostMessage(hwnd, 0x0101, (IntPtr)vk, (IntPtr)(1 | (1 << 30) | (1 << 31)));
	}

	// WM_CHAR: text input independent of the keyboard layout
	public static void Char(IntPtr hwnd, char c) { PostMessage(hwnd, 0x0102, (IntPtr)c, (IntPtr)1); }
	// WM_SYSKEYDOWN / WM_SYSKEYUP (bit 29 = the Alt context), what the keyboard driver sends for Alt+key and F10;
	// WPF's menus and access keys listen to these, not to plain WM_KEYDOWN
	public static void SysKey(IntPtr hwnd, int vk, bool alt)
	{
		long context = alt ? (1L << 29) : 0;
		PostMessage(hwnd, 0x0104, (IntPtr)vk, (IntPtr)(1 | context));
		PostMessage(hwnd, 0x0105, (IntPtr)vk, (IntPtr)(1 | context | (1L << 30) | (1L << 31)));
	}
	// WM_SETFOCUS, posted: WPF's keyboard provider then assigns its focused element (no OS activation involved)
	public static void SetFocusMessage(IntPtr hwnd) { PostMessage(hwnd, 0x0007, IntPtr.Zero, IntPtr.Zero); }
	// WM_SYSCHAR: the character of an Alt+letter, which WPF turns into an access key (menu mnemonic)
	public static void SysChar(IntPtr hwnd, char c) { PostMessage(hwnd, 0x0106, (IntPtr)c, (IntPtr)(1 | (1L << 29))); }
}
"@

$A = [System.Windows.Automation.AutomationElement]
$VK_BACK = 0x08; $VK_TAB = 0x09; $VK_SPACE = 0x20; $VK_END = 0x23; $VK_HOME = 0x24
$VK_LEFT = 0x25; $VK_UP = 0x26; $VK_RIGHT = 0x27; $VK_DOWN = 0x28; $VK_PRIOR = 0x21; $VK_NEXT = 0x22; $VK_RETURN = 0x0D; $VK_ESCAPE = 0x1B
$VK_F4 = 0x73; $VK_F5 = 0x74; $VK_F6 = 0x75
$VK_F = 0x46; $VK_I = 0x49; $VK_O = 0x4F; $VK_X = 0x58; $VK_L = 0x4C; $VK_COMMA = 0xBC; $VK_PERIOD = 0xBE
$script:failures = 0

function Check([string]$name, [bool]$ok, [string]$detail = "") {
	$tag = if ($ok) { "PASS" } else { $script:failures++; "FAIL" }
	if ($Trace) { $detail += ("   [fg={0} focus={1}]" -f (Foreground), (FocusedId)) }
	Write-Output ("{0}  {1}  {2}" -f $tag, $name, $detail)
}
function Foreground() {
	$fg = [Native]::GetForegroundWindow()
	if ($fg -eq $script:hwnd) { "APP" } elseif ($fg -eq $script:park.Handle) { "harness" } else { "other" }
}
# UI Automation lookups can miss while the UI thread is busy, so retry
function Find([string]$id) {
	$c = New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty, $id)
	for ($i = 0; $i -lt 10; $i++) {
		$e = $script:win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
		if ($e -ne $null) { return $e }
		Start-Sleep -Milliseconds 200
	}
	throw "element '$id' not found"
}
# the same lookup in the raw view: a collapsed element (WPF leaves what is not visible out of the control view,
# rightly, so a screen reader never meets it) is still there, with IsOffscreen set
function FindRaw([string]$id) {
	$c = New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty, $id)
	$cr = New-Object System.Windows.Automation.CacheRequest
	$cr.TreeFilter = [System.Windows.Automation.Automation]::RawViewCondition
	$cr.Push()
	try {
		for ($i = 0; $i -lt 10; $i++) {
			$e = $script:win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
			if ($e -ne $null) { return $e }
			Start-Sleep -Milliseconds 200
		}
	} finally { $cr.Pop() }
	throw "element '$id' not found in the raw view"
}
# a pattern object, used only to *read* its Current properties (never call its methods)
function Pattern($e, [string]$name) {
	$type = [System.Windows.Automation.InvokePattern].Assembly.GetType("System.Windows.Automation." + $name + "Pattern", $true)
	return $e.GetCurrentPattern($type.GetField("Pattern").GetValue($null))
}
function Frame() { return (Find "PreviewFrame").Current.Name }
function Position() { return [int](((Frame) -split " / ")[0]) }
function Playing() { return (Find "PreviewPause").Current.IsEnabled }
function Selected([string]$id) { return (Pattern (Find $id) "SelectionItem").Current.IsSelected }
function SpeedText() { return (Pattern (Find "PreviewSpeed") "Value").Current.Value }
function Focused([string]$id) { return (FocusedId) -eq $id }
function StepText() { return (Find "PreviewStep").Current.Name }
function ToggleOn([string]$id) { return (Pattern (Find $id) "Toggle").Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On }
function LoopOn() { return ToggleOn "PreviewLoop" }
function LiveOn() { return ToggleOn "PreviewLive" }
function RunStatus() { return (Find "StatusRun").Current.Name }
# MD5 of the field picture as drawn (overlay included), for "the picture changed" checks
function FieldHash() {
	$r = (Find "PreviewFieldImage").Current.BoundingRectangle
	return [Native]::Hash($script:hwnd, [int]$r.Left, [int]$r.Top, [int]$r.Width, [int]$r.Height)
}
# the centre of an element's screen rectangle
function CentreOf([string]$id) { $r = (Find $id).Current.BoundingRectangle; return @(($r.Left + $r.Width / 2), ($r.Top + $r.Height / 2)) }
# where canvas pixel (x, y) of a w x h canvas lands inside a picture element (uniform scale, centred), as the overlay maps it
function CanvasPoint([string]$imageId, [int]$w, [int]$h, [double]$x, [double]$y) {
	$r = (Find $imageId).Current.BoundingRectangle
	$scale = [math]::Min($r.Width / $w, $r.Height / $h)
	$ox = $r.Left + ($r.Width - $w * $scale) / 2; $oy = $r.Top + ($r.Height - $h * $scale) / 2
	return @(($ox + ($x + 0.5) * $scale), ($oy + ($y + 0.5) * $scale), $scale)
}
# whether the tree item with this name is selected (reads the SelectionItem pattern's property only)
function TreeItemSelected([string]$name) {
	$c = New-Object System.Windows.Automation.AndCondition(
		(New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::TreeItem)),
		(New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $name)))
	$e = $script:win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
	if ($e -eq $null) { return $false }
	return (Pattern $e "SelectionItem").Current.IsSelected
}
# the element with keyboard focus, in one UI Automation query (asking 17 ids one by one cost half a second per
# Tab and made FocusOn crawl, 2026-10-14): its automation id, "PreviewSpeed" for the editable combo's inner text
# box, else "(other: <type> '<name>')"; "(none)" when nothing in the window has focus (the window was never activated)
function FocusedElement() {
	$c = New-Object System.Windows.Automation.PropertyCondition($A::HasKeyboardFocusProperty, $true)
	return $script:win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}
function FocusedId() {
	$e = FocusedElement
	if ($e -eq $null) { return "(none)" }
	$id = $e.Current.AutomationId
	if ($id -ne "" -and -not $id.StartsWith("PART_")) { return $id }
	# the editable combo keeps focus in its inner text box (a template part, "PART_EditableTextBox"): the combo names it
	$walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
	$parent = $walker.GetParent($e)
	for ($i = 0; $i -lt 4 -and $parent -ne $null; $i++) {
		if ($parent.Current.AutomationId -eq "PreviewSpeed") { return "PreviewSpeed" }
		$parent = $walker.GetParent($parent)
	}
	return "(other: " + $e.Current.ControlType.ProgrammaticName + " '" + $e.Current.Name + "')"
}
# the settle covers the app's handling of the key before the next UI Automation read; 60 ms is plenty for a binding
# update, a layout change wants ~150, a scratch frame or a run is waited for with WaitFor instead (2026-10-14)
function Key([int]$vk, [int]$settle = 60) { [Native]::Key($script:hwnd, $vk); Start-Sleep -Milliseconds $settle }
# resizes the app window by the given amounts (no activation) and lets the layout settle
function ResizeApp([int]$dw, [int]$dh) {
	[Native]::Resize($script:hwnd, ([Native]::WindowWidth($script:hwnd) + $dw), ([Native]::WindowHeight($script:hwnd) + $dh))
	Start-Sleep -Milliseconds 500
}
# Tab (posted) until the wanted element holds focus; the cycle runs through the whole window
function FocusOn([string]$id) {
	for ($i = 0; $i -lt 80; $i++) {
		if ((FocusedId) -eq $id) { return }
		Key $VK_TAB 30
	}
	throw "could not reach '$id' with Tab; focus is at $(FocusedId)"
}
# polls a condition; returns whether it became true within the timeout
function WaitFor([scriptblock]$condition, [int]$timeoutMs = 10000, [int]$pollMs = 50) {
	$deadline = [DateTime]::Now.AddMilliseconds($timeoutMs)
	while ([DateTime]::Now -lt $deadline) {
		if (& $condition) { return $true }
		Start-Sleep -Milliseconds $pollMs
	}
	return (& $condition)
}

# Launches the app (optionally with a project file), finds its window and parks the foreground elsewhere.
# Windows may hand a new window the foreground. Park it on an off-screen window of our own (when the user
# is busy elsewhere this silently fails and the foreground stays theirs, which is just as good): every
# check must then leave the app inactive.
function Start-App([string]$Exe, [string]$ProjectFile = "", [string]$Switches = "") {
	# an edited project asks "save?" on closing (2026-10-14) in the app's own message window (MessageWindow, 2026-10-18); the harnesses
	# have no business there, so the app is told to discard. $Switches are start-up switches put before the file
	# ("--greyscale-text" for the figures, 2026-11-04)
	$env:WAVEDUDE_DISCARD_EDITS = "1"
	$arguments = $Switches
	if ($ProjectFile -ne "") { $arguments = ($Switches + ' "' + $ProjectFile + '"').Trim() }
	if ($arguments -ne "") { $script:p = Start-Process -FilePath $Exe -ArgumentList $arguments -PassThru }
	else { $script:p = Start-Process -FilePath $Exe -PassThru }
	$script:win = $null
	for ($i = 0; $i -lt 100 -and $script:win -eq $null; $i++) {
		Start-Sleep -Milliseconds 200
		$c = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $script:p.Id)
		$script:win = $A::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $c)
	}
	if ($script:win -eq $null) { throw "main window not found" }
	$script:p.Refresh()
	$script:hwnd = $script:p.MainWindowHandle
	# ready when the status bar's run item exists (the window is fully built), instead of a fixed wait
	$null = WaitFor { try { (Find "StatusRun") -ne $null } catch { $false } } 5000 50

	$script:park = New-Object System.Windows.Forms.Form
	$script:park.StartPosition = "Manual"
	$script:park.Location = New-Object System.Drawing.Point(-3000, -3000)
	$script:park.Size = New-Object System.Drawing.Size(10, 10)
	$script:park.ShowInTaskbar = $false
	$script:park.Show(); $script:park.Activate(); [System.Windows.Forms.Application]::DoEvents()
	Start-Sleep -Milliseconds 200
	for ($i = 0; $i -lt 3 -and (Foreground) -eq "APP"; $i++) {
		[Native]::ShowWindow($script:hwnd, 6) | Out-Null         # SW_MINIMIZE: the OS activates something else
		Start-Sleep -Milliseconds 150
		$script:park.Activate(); [System.Windows.Forms.Application]::DoEvents()
		Start-Sleep -Milliseconds 150
		[Native]::ShowWindow($script:hwnd, 4) | Out-Null         # SW_SHOWNOACTIVATE
		Start-Sleep -Milliseconds 250
	}
	# a window that Windows never activated (the user was busy elsewhere) has no focused element, and posted keys
	# then go nowhere; a posted WM_SETFOCUS makes WPF assign its keyboard focus without activating the window
	if ((FocusedId) -eq "(none)") {
		[Native]::SetFocusMessage($script:hwnd)
		Start-Sleep -Milliseconds 150
		if ((FocusedId) -eq "(none)") { Write-Output "note: no element has keyboard focus; keys may be lost" }
	}
	Write-Output ("window: '{0}'  foreground: {1}" -f $script:win.Current.Name, (Foreground))
}

function Stop-App() {
	if ($script:p -ne $null) {
		$script:p.CloseMainWindow() | Out-Null
		if (-not $script:p.WaitForExit(8000)) { $script:p.Kill() }
		$script:p = $null
	}
	if ($script:park -ne $null) { $script:park.Close(); $script:park = $null }
}
