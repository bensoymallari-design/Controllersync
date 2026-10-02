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

Open the firewall on the laptop that receives the connection (the backup, in the normal setup). The **Open firewall ports** button asks Windows for permission for the show link and for the next port, which carries Resolume clips. Outgoing connections are usually allowed already.

## Resolume clips

Use the **Resolume** tab when the same video or picture has to land in the same layer and clip on every laptop. If the show laptop dies, the backups already have the file on their own disks.

1. On every laptop, choose **Resolume** at the top. PowerPoint sync stays off in that mode, so arrow keys, mouse, and slides are not sent. Stop the link before switching jobs.
2. On every laptop, open Resolume and turn on **Preferences → Web Server**. Leave the port at `8080` unless you changed it, and put that number in **Resolume port**.
3. Start the link on every laptop. Each app then listens for clips on the next port (`24710` listens for clips on `24711`). A laptop left on PowerPoint will not take the clip.
4. On the laptop that has the files, choose one or several. **Browse** can select more than one file, and you can also put one path on each line. Set the **layer** and **clip** (these match the numbers in Resolume, starting at 1). The first file uses that clip. Each following file uses the next clip on the same layer. **Width**, **height**, **X**, and **Y** are optional pixels and apply to every file in the send. Leave a box empty to keep the value already in Resolume. Filled boxes are applied after each file opens and before playback starts. Then choose **Send to every laptop**.
5. The outgoing IP is included. For a third or fourth backup, add one IP per line under **More backup laptops**. If that laptop uses a different incoming port, write `192.168.1.22:24712`.
6. **Start playback after load** connects the clip after Resolume opens it. **Also load on this laptop** does the same thing in the Resolume running on the machine you sent from.

Each file is copied to each laptop and saved in a `media` folder next to that laptop's settings. Resolume is then told to open that local file. A path on the sending laptop cannot be opened by Resolume on another machine.

Sending the same filename again does not overwrite the copy Resolume may already have open. The new copy is saved as `intro-2.mp4`, then `intro-3.mp4`, and that new file is what Resolume opens. The clip slot is cleared first so the new file replaces what was there.

**Cancel** on a file takes that video off the list. If the file was already sent, Cancel clears that clip in Resolume on every laptop and deletes the saved copy from that laptop's media folder. The other files in the send stay.

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
