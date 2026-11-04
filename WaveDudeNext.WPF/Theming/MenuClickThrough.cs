// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alexander Graef <alex@graef.cc>
//
// This file is part of WaveDude Next, a 2D wave simulator.
//
// Based on the work of
//   Jeroen Vleggaar (Huygens Optics), author of Wave Dude 2D, Copyright (C) 2026
//     <https://www.youtube.com/@HuygensOptics>, <https://www.huygensoptics.com>
//   0x23 (Diffraction Limited), author of WaveSimulator2D
//     <https://www.youtube.com/@DiffractionLimited>, <https://github.com/0x23/WaveSimulator2D>
//
// WaveDude Next is free software: you can redistribute it and/or modify it under
// the terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. It is distributed WITHOUT ANY WARRANTY. See the LICENSE.md file or
// <https://www.gnu.org/licenses/>.

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;

namespace WaveDudeNext.WPF.Theming;

/// <summary>
/// A click on a window's title bar, its buttons or its system menu while a menu or a context menu is open closes the
/// menu and lands (2026-11-04, the user's request: the main window's close button had needed a second click). An open
/// menu holds the mouse, for WPF and for Windows alike, so the click that dismisses it reaches the window as a client
/// click, never as the non-client click the title bar acts on. The dismissing click is hit-tested against the window
/// under the pointer with <c>WM_NCHITTEST</c> and, when it falls on the caption, the system menu or one of the three
/// buttons, posted again as <c>WM_NCLBUTTONDOWN</c>, which is handled after the menu has closed and let the mouse go:
/// <c>DefWindowProc</c> then treats it as the real click, and the button's release comes from the mouse itself.
/// One class handler on <see cref="MenuBase"/>, so the menu bar's menus and every context menu are covered.
/// </summary>
public static class MenuClickThrough
{
	/// <summary>Registers the handler; once, at start-up.</summary>
	public static void Install() =>
		EventManager.RegisterClassHandler(typeof(MenuBase), Mouse.PreviewMouseDownOutsideCapturedElementEvent, new MouseButtonEventHandler(OnClickOutside));

	private static void OnClickOutside(object sender, MouseButtonEventArgs e)
	{
		if (e.ChangedButton != MouseButton.Left || !GetCursorPos(out NativePoint point))
		{
			return;
		}
		IntPtr hwnd = WindowFromPoint(point);
		// one of this application's windows, and the click on its non-client area
		if (hwnd == IntPtr.Zero || HwndSource.FromHwnd(hwnd) == null)
		{
			return;
		}
		IntPtr position = (IntPtr)((point.Y << 16) | (point.X & 0xFFFF));
		long hit = (long)SendMessage(hwnd, WM_NCHITTEST, IntPtr.Zero, position);
		if (hit is HTCAPTION or HTSYSMENU or HTMINBUTTON or HTMAXBUTTON or HTCLOSE)
		{
			PostMessage(hwnd, WM_NCLBUTTONDOWN, (IntPtr)hit, position);
		}
	}

	private const uint WM_NCHITTEST = 0x0084;
	private const uint WM_NCLBUTTONDOWN = 0x00A1;
	private const long HTCAPTION = 2;
	private const long HTSYSMENU = 3;
	private const long HTMINBUTTON = 8;
	private const long HTMAXBUTTON = 9;
	private const long HTCLOSE = 20;

	[StructLayout(LayoutKind.Sequential)]
	private struct NativePoint
	{
		public int X;
		public int Y;
	}

	[DllImport("user32.dll")]
	private static extern bool GetCursorPos(out NativePoint point);

	[DllImport("user32.dll")]
	private static extern IntPtr WindowFromPoint(NativePoint point);

	[DllImport("user32.dll")]
	private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

	[DllImport("user32.dll")]
	private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
