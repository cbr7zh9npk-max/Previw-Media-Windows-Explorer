ShiftPreview - photo and video previews on Shift + hover (Windows 11)
====================================================================

SETUP
  1. Put ShiftPreview.cs and build.bat in the same folder (e.g. C:\Tools\ShiftPreview).
  2. Double-click build.bat. It compiles ShiftPreview.exe with the C# compiler built into
     Windows and starts it. A blue "play" icon appears in the system tray.
  3. Right-click the tray icon -> "Start with Windows" if you want it always running.

USING IT
  * In File Explorer (or on the desktop), point at a photo or video and hold Shift.
    A popup shows it next to the mouse. Let go of Shift and it closes.
  * Move the mouse onto the popup and it stays open, even after you release Shift.
    Then you can use:
      - Mouse wheel ........... volume up/down (video)
      - Volume slider / mute .. bottom bar (video)
      - Progress bar .......... click to jump to a spot (video)
      - Click the video ....... pause/play
      - Drag any edge/corner .. resize (the size is remembered)
      - Drag the top bar ...... move it. It will then always open in that spot
                                (turn off in the tray menu: "Keep popup in a fixed spot").
      - Pin button ............ keep the preview open without holding Shift
    Move the mouse away from the popup and it closes.

  KEYS (while a preview is open)
  * Shift+V = next file, Shift+C = previous file - in the order File Explorer
    shows them, including your sort (e.g. "Media created"), filters and grouping.
    Photos and videos are included. The list wraps around at the end.
  * For videos, Shift+X jumps forward 5 seconds and Shift+Z jumps back 5 seconds.
  * Shift+1 ... Shift+5 = star rating, Shift+0 = clear rating.
  * Space = pause/play video, Esc = close.
  * Arrow keys are not used, so they keep working normally in Explorer.
  * If the next/previous order is ever wrong, send %APPDATA%\ShiftPreview\log.txt -
    it records how the order was read.

  STAR RATING
  * Click the stars in the popup's title bar, or press Shift+1 ... Shift+5.
    Shift+0 (or clicking the current rating again) clears it.
  * It's the normal Windows rating, so it shows in Explorer's "Rating" column and in
    Properties > Details. Rating support depends on the file format and its Windows
    property handler. If Windows can't save a rating, the preview shows an error.
  * Saving a rating changes the file's "Date modified", like it does in Explorer.

  LOCKED PREVIEW
  * Once a preview is open it stays on that file while you hold Shift - hovering other
    files does nothing, so you can move freely to the player.
  * To preview a different file: use Shift+C / Shift+V, or close the preview (let go of
    Shift, press Esc, or click the X) and then hover the new file with Shift.
  * The shortcut keys go to the preview while it's open, not to Explorer.

  PINNED
  * Click the pin in the title bar: the preview stays open after
    you let go of Shift and move the mouse away.
  * Close it by tapping Shift (press and release it on its own), pressing Esc, or clicking X.
  * Shift shortcuts (Shift+C/V/Z/X/1-5) and Shift+click in Explorer don't close it.
  * Holding Shift on its own over another photo or video switches to that file.

TRAY MENU (right-click the icon)
  Enabled, fixed-spot mode, size presets, Shift hold delay (so typing capital letters
  doesn't open previews), Start with Windows, Exit.

  Settings are saved in %APPDATA%\ShiftPreview\settings.ini

FORMATS
  Photo previews support JPEG (.jpg, .jpeg, .jpe), PNG, BMP, GIF and TIFF.
  GIFs and multi-page TIFFs show the first frame or page.

  Two video engines are built in:
  * Classic player (default) - fast and light.
  * Modern player - the engine the new Windows "Media Player" app uses. If a video won't
    let the classic player jump (Shift+Z/X or clicking the progress bar), that video
    switches to the modern player automatically, at the spot you wanted.
    Tray menu > "Always use the modern video player" uses it for everything.
  Uses Windows' built-in video playback: mp4, mov, m4v, wmv, avi, mkv, mpg, ts/m2ts, 3gp.
  HEVC/H.265 needs the "HEVC Video Extensions" from the Microsoft Store. Some webm files
  may not play. If nothing plays at all, turn on "Windows Media Player Legacy" under
  Settings > System > Optional features.

LIMITS
  Works in normal folders and on the desktop. It does not work in the "Home"/"Gallery"
  views, search results, or inside zip files.
