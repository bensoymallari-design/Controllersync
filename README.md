# ControllerSync

ControllerSync keeps a backup PowerPoint laptop on the same slide as the show laptop. Both machines run the same app on the local network. When the show laptop changes slides, the backup follows. If the show laptop dies, the backup is already in the right place and the operator keeps presenting there.

The address setup is the same idea as OSC in Resolume: each laptop has an **incoming** address (where it listens) and an **outgoing** address (the other laptop).

Nothing is sent to the internet. Traffic stays between the two laptops.

## Show day

1. Put the same PowerPoint file on both laptops.
2. Copy `ControllerSync.exe` onto each laptop and start it. Windows may say the app is unrecognized because it is not code-signed. Choose More info, then Run anyway.
3. On the **backup** laptop, choose **Backup**. Leave outgoing IP empty. Remember the IP shown under "This laptop's IP". Start the link.
4. On the **show** laptop, choose **Primary**. Set **outgoing IP** to the backup laptop's IP and **outgoing port** to `24710`. Use the same **channel** on both (the default is `show`). Start the link.
5. The tally turns **LINKED** when the two apps can see each other.
6. Open the slideshow on both laptops, then click the slideshow on the show laptop. Arrow keys, space, Page Up, Page Down, Enter, Home, End, F5, Shift+F5, Escape, B, and W are sent across.
7. If the show laptop dies, click the slideshow on the backup and keep going. It is already on the same slide.

Open the firewall on the laptop that receives the connection (the backup, in the normal setup). The **Open firewall port** button asks Windows for permission. Outgoing connections are usually allowed already.

To try it on one computer, run two copies. Give the second copy a different incoming port, such as `24711`, so they do not fight over `24710`. Point the primary's outgoing IP at `127.0.0.1` and the backup's incoming port. Add `--settings=/path/to/folder` if both copies are the same file and you want each one to keep its own settings.

## What is synced

| Control | Default |
| --- | --- |
| Presentation keys | On for the primary |
| Mouse clicks | On for the primary |
| Mouse movement | Off. Slide changes do not need it. |
| Every key | Off. Leave this off so passwords are not sent. |
| Slide position | The primary publishes it. The backup follows it. |

While a slideshow is running, the primary sends the slide number (and the animation step) instead of replaying every arrow. That way a missed keystroke does not leave the backup behind. Mouse clicks inside the slideshow are handled the same way. Black and white screens travel with the slide position.

Keys are not captured while ControllerSync itself is the window in front, so typing an IP address is not sent to the other laptop.

## If you build it yourself

The Windows app is one self-contained file. The laptops do not need a separate .NET install.

```bash
./scripts/publish-windows.sh
```

On Windows:

```powershell
./scripts/publish-windows.ps1
```

The file is written to `dist/win-x64/ControllerSync.exe`. For an ARM Windows laptop, change `win-x64` to `win-arm64`.

```bash
dotnet test
```
