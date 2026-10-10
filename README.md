<p align="center"><img src="assets/logo.png" width="96" alt="Duo Click logo"></p>
<h1 align="center">Duo Click</h1>
<p align="center"><strong>Two buttons. One screenshot.</strong></p>
<p align="center">English · <a href="README.zh-CN.md">简体中文</a></p>

A lightweight Windows screenshot tool. Hold both mouse buttons and drag to capture an area. Capture more areas to build one vertical image, then paste the whole sequence into a chat, document, or image editor.

## Download

**[Download Duo Click for Windows](https://github.com/DylanZhangzzz/DuoClick/releases/latest)**

- **Installer:** download `DuoClick-…-Setup.exe`. It installs for your Windows account without administrator privileges, adds a Start menu shortcut, and offers optional launch at sign-in (off by default). The installer supports English and Simplified Chinese.
- **Portable:** download `DuoClick-…-Windows.zip`, extract it, and open `DuoClick.exe`. Keep `DuoClick.exe.config` beside the program. No installation is needed.

Requires Windows 10 / 11 and .NET Framework 4.8.

To update an installed copy, exit Duo Click from its tray menu and run the newer installer. Your language and capture-limit settings are kept. To uninstall, use Windows **Settings → Apps → Installed apps → Duo Click**. Uninstall removes the program and its startup entry but keeps your preferences in `%LocalAppData%\DuoClick` for a future reinstall. The portable copy uses the same preferences.

## Take a screenshot

1. Find Duo Click's blue-and-teal mouse icon in the system tray (it may be under the **^** arrow).
2. At the starting point, hold down **both the left and right mouse buttons**. Either button can go first.
3. Drag to select an area.
4. Release **both** buttons. Your screenshot joins the current batch on the clipboard.
5. Repeat to capture more areas. They stack **top to bottom in capture order**, at their original sizes, with numbered headers and clear separators. The tray shows the count.
6. Press **Ctrl + V** or **Shift + Insert** in an app that accepts images. The batch counter resets, and your next capture starts a new image.

Press **Esc** to cancel. This gesture means holding the two buttons together, not double-clicking one button.

## Language and controls

Right-click the tray icon to see the current count, start a new batch, pause or resume capture, read the instructions, or exit. Under **Language / 语言**, choose **English** or **简体中文**. The change takes effect immediately and is remembered for your next launch. The first launch follows your Windows display language (Chinese or English).

The tray tooltip briefly confirms a successful copy, then returns to the current ready or paused state.

Choose **Settings** to set the maximum number of captures from **1 to 100** (default: **3**). The setting is remembered. When full, each new successful capture replaces the oldest one. Lowering the limit keeps the newest captures.

## Capture a sequence

For example, capture three areas and paste once: you get **one long image containing all three**, in order. Each capture has a separate numbered header (1, 2, 3…) and a visible dividing line. Narrower captures are aligned left on a white background; images are not resized. With a limit of 3, capturing a fourth area keeps captures 2, 3, and 4, renumbered 1–3.

The counter resets when Duo Click observes **Ctrl + V** or **Shift + Insert** while its image is still on the clipboard. It cannot confirm that the destination accepted the paste. The finished image stays on the clipboard, so you can paste it again, even if the first attempt did not work.

Right-click/menu paste cannot be detected reliably across apps. After using it, choose **Start new batch** from the tray menu. This clears the pending group without erasing the image already on the clipboard. Copying other content does not reset the capture count.

## Good to know

- Screenshots stay on your computer. Duo Click does not upload them or save image files automatically.
- Ordinary mouse clicks still work. The first button press can reach the app underneath, so choose your starting point with care.
- Canceling or selecting a tiny area keeps your previous clipboard content.
- The finished image, including headers and separators, is subject to a 40-million-pixel canvas limit and a 32,767-pixel limit on either dimension. If the next capture would exceed these limits, it is rejected with a notice and the existing batch is kept.
- The desktop is captured just before the selection overlay appears; moving content may differ slightly from the instant you press the buttons.
- Protected video, the Windows secure desktop, and apps that block capture may not be captured. Mixed-scale multi-monitor setups still need further device testing.
- Updating? Exit the older Duo Click from its tray menu before opening the new copy.

## Feedback

Found a problem or have an idea? [Open an issue](https://github.com/DylanZhangzzz/DuoClick/issues).
