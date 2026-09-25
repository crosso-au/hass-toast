<p align="center">
  <img src="https://cdn.crossboxlabs.com/cbl-logo.png" />
</p>


# HASS Windows Toast

[![github](https://img.shields.io/badge/CrossboxLabs%20|%20crosso_au-8A2BE2)](https://github.com/crosso-au/) 
[![hacs_badge](https://img.shields.io/badge/HACS-Custom-41BDF5.svg)](https://github.com/hacs/integration)
[![hacs_badge](https://badgen.net/github/release/crosso-au/hass-toast)](https://github.com/crosso-au/hass-toast/releases)
[![hacs_badge](https://badgen.net/github/last-commit/crosso-au/hass-toast/main)](https://github.com/crosso-au/hass-toast)

[![Buy Me A Coffee](https://img.buymeacoffee.com/button-api/?text=Buy%20Ryan%20a%20tasty%20coffee&emoji=☕&slug=crosso_au&button_colour=FFDD00&font_colour=000000&font_family=Lato&outline_colour=000000&coffee_colour=ffffff)](https://buymeacoffee.com/crosso_au)

Rich Windows notifications from Home Assistant.

Send native Windows 10 and 11 toast notifications from any Home Assistant automation, with
images, buttons, text replies, drop-down lists, progress bars, multi-column layouts and sounds.
When someone clicks a button or types a reply, Home Assistant gets it back as an event, so your
automations can act on it.

- **Works from anywhere Home Assistant does.** Your PC connects out to Home Assistant, so there
  are no ports to open and no firewall rules to add.
- **One installer.** It sets everything up, including the Home Assistant side, and finishes by
  sending you a test notification.
- **Two-way.** Buttons, text boxes and drop-down lists report back to Home Assistant.
- **Safe by default.** Every notification is signed, so only your Home Assistant can send them,
  and buttons can only open the kinds of links you allow.
- **Several computers.** Install it on as many PCs as you like and choose which ones each
  notification goes to.

## Contents

- [Requirements](#requirements)
- [Installing](#installing)
- [Sending your first notification](#sending-your-first-notification)
- [What you can put in a notification](#what-you-can-put-in-a-notification)
- [Updating and removing notifications](#updating-and-removing-notifications)
- [Reacting to clicks and replies](#reacting-to-clicks-and-replies)
- [Using images](#using-images)
- [The tray icon](#the-tray-icon)
- [Troubleshooting](#troubleshooting)
- [Security settings](#security-settings)
- [Uninstalling](#uninstalling)
- [More information](#more-information)

## Requirements

- Windows 10 (version 1809 or later) or Windows 11, 64-bit.
- Home Assistant 2025.1 or later.
- An administrator account in Home Assistant, to create an access token during setup.
- [HACS](https://hacs.xyz/) is recommended. Without it, you copy one folder into Home Assistant
  by hand.

## Installing

Download `HassToast.Setup-<version>-win-x64.exe` from the
[latest release](https://github.com/crosso-au/hass-toast/releases/latest) and run it. It installs
for your Windows account only, so no administrator prompt appears. Nothing else needs installing
first.

The installer walks you through five steps:

1. **Home Assistant.** Enter your Home Assistant address, the same one you use in a browser,
   such as `https://homeassistant.local:8123`. You also need a long-lived access token: select
   **Open Profile Security**, scroll to **Long-lived access tokens**, create one, and paste it in.
   Then select **Test connection**.
2. **This machine.**
   - **Device id:** the name Home Assistant uses for this computer. You use it to send
     notifications to this PC only.
   - **Display name:** shown at the top of each notification.
   - **Start with Windows:** starts HASS Windows Toast when you sign in.
   - **Add to PATH:** lets you run `hass-toast` commands from any command prompt.
3. **Integration.** Home Assistant needs the HASS Windows Toast integration.
   - **Install through HACS** does it for you. Leave **Restart Home Assistant automatically**
     ticked, or untick it to restart Home Assistant yourself and then select **Check again**.
   - **Install manually** opens the `hass_toast` folder. Copy it into the `custom_components`
     folder of your Home Assistant configuration (create that folder if it doesn't exist),
     restart Home Assistant, then select **Check again**.
4. **Install now.** Installs the app and connects this computer to Home Assistant.
5. **Test notification.** Sends a real notification through Home Assistant. If it doesn't
   appear, select **No, nothing appeared** and the installer checks what went wrong.

To change settings later, run the installer again. It fills in your current settings; only the
access token has to be entered again.

## Sending your first notification

In Home Assistant, go to **Developer tools → Actions**, select **Go to YAML mode**, paste this and
select **Perform action**:

```yaml
action: notify.hass_toast
data:
  title: Hello from Home Assistant
  message: HASS Windows Toast is working.
```

Use `notify.hass_toast` anywhere you would use any other notify action: in automations, scripts
and dashboards.

| Field | Required | What it does |
| --- | --- | --- |
| `message` | Yes | The main text. |
| `title` | No | A bold line above the message. |
| `target` | No | The device id of the computer to send to, or a list of them. Leave it out to send to every computer. |
| `tag` | No | Names the notification so it can be replaced, updated or removed later. |
| `group` | No | Groups notifications so they can be removed together. |
| `scenario` | No | `default`, `reminder`, `alarm`, `incomingCall` or `urgent`. |
| `image` | No | Web address of a large picture shown across the top. |
| `attribution` | No | A small grey line at the bottom. |
| `data` | No | Everything else: buttons, inputs, more images, progress bars, sounds and more. |

## What you can put in a notification

Everything beyond the fields above goes in the nested `data:` block. The
[samples folder](samples/) has ready-to-paste examples of every option, with each one explained.

| You want | Sample |
| --- | --- |
| Images: a round logo, a banner, a full-width picture, a camera snapshot | [03-images.yaml](samples/03-images.yaml) |
| A notification that stays on screen, rings, or breaks through Do Not Disturb | [04-scenarios.yaml](samples/04-scenarios.yaml) |
| Buttons: coloured, opening a web page, snooze and dismiss, right-click menus, icons | [05-buttons.yaml](samples/05-buttons.yaml) |
| Text boxes, drop-down lists and quick replies | [06-inputs.yaml](samples/06-inputs.yaml) |
| A progress bar that moves along | [07-progress-bar.yaml](samples/07-progress-bar.yaml) |
| Columns and tables | [08-columns.yaml](samples/08-columns.yaml) |
| Larger, smaller and lighter text | [09-text-styles.yaml](samples/09-text-styles.yaml) |
| A different sound, a looping alarm, or silence | [10-sounds.yaml](samples/10-sounds.yaml) |
| Expiring, silent delivery, headings, clicking the notification | [11-lifecycle.yaml](samples/11-lifecycle.yaml) |
| All of it at once | [13-everything.yaml](samples/13-everything.yaml) |

For example, a notification with a picture and two buttons:

```yaml
action: notify.hass_toast
data:
  title: Someone is at the front door
  message: Motion detected 10 seconds ago
  tag: door-front
  image: https://picsum.photos/364/180
  data:
    buttons:
      - content: Unlock
        args:
          action: unlock_front_door
        style: Success
      - content: Ignore
        args:
          action: ignore
        style: Critical
```

## Updating and removing notifications

Give a notification a `tag` and you can come back to it later:

- **Send again with the same `tag`** to replace it rather than add a second one.
- **`hass_toast.update`** moves a progress bar along without a new pop-up. See
  [07-progress-bar.yaml](samples/07-progress-bar.yaml).
- **`hass_toast.remove`** removes notifications by `tag`, by `group`, or both.
- **`hass_toast.clear`** removes every notification HASS Windows Toast has shown on a computer.

```yaml
action: hass_toast.remove
data:
  tag: door-front
```

All three actions take an optional `target`. Without it they apply to every computer, and for
`hass_toast.clear` that means clearing every computer.

## Reacting to clicks and replies

When someone clicks a button, or the notification itself, Home Assistant receives a
`hass_toast_response` event:

```yaml
device_id: my-pc          # which computer it came from
tag: door-front           # the notification's tag
group: security           # the notification's group
arguments:                # the args of whatever was clicked
  action: unlock_front_door
inputs:                   # anything typed or chosen in text boxes and lists
  reply: On my way
```

Use it as an automation trigger:

```yaml
alias: Unlock the front door from a notification
triggers:
  - trigger: event
    event_type: hass_toast_response
    event_data:
      tag: door-front
conditions:
  - condition: template
    value_template: "{{ trigger.event.data.arguments.action == 'unlock_front_door' }}"
actions:
  - action: lock.unlock
    target:
      entity_id: lock.front_door
```

Snooze and dismiss buttons, and buttons that open a web page, don't send an event.
[12-responding-to-clicks.yaml](samples/12-responding-to-clicks.yaml) has more examples,
including acting on typed replies and waiting for an answer inside a script.

To watch events as they arrive, go to **Developer tools → Events**, enter `hass_toast_response`
under **Listen to events**, and select **Start listening**.

## Using images

Images are downloaded by your PC, not by Home Assistant, so the address must be reachable from
the PC without logging in.

| Where | Size limit |
| --- | --- |
| `app_logo` (small, optionally round) and `hero` / `image` (banner) | 200 KB |
| `inline` (full width, below the text) | 3 MB |

PNG, JPG, GIF, WebP and BMP all work. An image that is too large, unreachable or not a picture is
left out, and the rest of the notification still appears. Camera snapshots are usually over
200 KB, so use `inline` for them.

For a Home Assistant camera, the `entity_picture` attribute includes a temporary access token.
Put your Home Assistant address in front of it:

```yaml
inline:
  src: "https://homeassistant.local:8123{{ state_attr('camera.doorbell', 'entity_picture') }}"
```

Files in your Home Assistant `config/www` folder are available at `/local/` without logging in,
for example `https://homeassistant.local:8123/local/snapshot.jpg`.

## The tray icon

HASS Windows Toast runs in the notification area. Its icon shows the connection:

| Colour | Meaning |
| --- | --- |
| Green | Connected to Home Assistant. |
| Amber | Connecting. |
| Grey | Not connected. It keeps retrying. |
| Red | Home Assistant rejected the access token. Run the installer again with a new token. |

Right-click the icon to see the status, send a test notification, open the log folder, turn
**Start with Windows** on or off, or exit.

## Troubleshooting

**No notification appears.**
- Check the tray icon is green.
- Check notifications are on for the app under **Windows Settings → System → Notifications**. It
  is listed under its display name, which is **Home Assistant** unless you changed it.
- Check Do Not Disturb (Windows 11) or Focus Assist (Windows 10) is off.
- If `target` is set, check it matches the device id exactly.
- Right-click the tray icon and select **Send test toast**. If that works, the problem is on the
  Home Assistant side. Check **Settings → System → Logs** for `hass_toast`.

**An image or button is missing.** It was blocked or couldn't be used. The log explains why.
Right-click the tray icon and select **Open log folder**. Logs are in
`%LOCALAPPDATA%\HassToast\logs`.

**A reminder or alarm disappears after a few seconds.** Windows only keeps these on screen when
they have at least one button. HASS Windows Toast adds a Dismiss button if you give none, but
check your buttons aren't all being dropped, such as icon buttons with a missing icon.

**Moving to a new Home Assistant address, or a new token.** Run the installer again.

**Command-line checks.** If you ticked **Add to PATH**, these work from any command prompt:

| Command | What it does |
| --- | --- |
| `hass-toast --selftest` | Sends a notification through Home Assistant and back. |
| `hass-toast --test-toast` | Shows a notification locally and reports what Windows did with it. |
| `hass-toast --check-registration` | Checks the Windows registration notifications depend on. |
| `hass-toast --repair` | Rebuilds that registration. Exit the tray app first. |
| `hass-toast --list-toasts` | Lists the notifications currently in the notification centre. |
| `hass-toast --clear-toasts` | Removes them. |

**Adding a computer to Home Assistant by hand.** The installer normally does this. If it
couldn't, go to **Settings → Devices & services → Add integration → HASS Windows Toast**, and
enter the device id and the signing key shown by `hass-toast --show-key`. Keep that key private.

## Security settings

Every notification is signed with a key only your Home Assistant and your PC know, so other
software cannot send you fake notifications. Your access token and that key are stored
encrypted, readable only by your Windows account.

Optional settings are in `%LOCALAPPDATA%\HassToast\config.json`, under `security`. Restart the app
after changing them.

| Setting | Default | What it controls |
| --- | --- | --- |
| `allowedUriSchemes` | `https`, `http`, `mailto` | Which kinds of link buttons may open. |
| `allowedImageSchemes` | `https`, `http` | Where images may come from. Add `file` to allow pictures from this PC. |
| `maxToastsPerMinute` | `30` | Limits notification floods. |
| `timestampToleranceSeconds` | `120` | How far apart Home Assistant's clock and this PC's can be. |

If your Home Assistant uses a self-signed certificate, set `homeAssistant.pinnedSpkiSha256` to
trust that one certificate, rather than turning off certificate checks with `verifyTls: false`.

## Uninstalling

Go to **Windows Settings → Apps → Installed apps**, find **HASS Windows Toast**, and select
**Uninstall**. This removes everything it added to the computer.

- **Also remove this computer from Home Assistant** deletes this computer's entry. The
  integration stays installed for your other computers.
- The access token you created still works until you delete it in Home Assistant under
  **Profile → Security → Long-lived access tokens**.

To remove the integration completely, delete it from **Settings → Devices & services**, then
from HACS if you installed it that way.

## More information

- [Samples](samples/): every option, ready to paste.
- [Issues and feature requests](https://github.com/crosso-au/hass-toast/issues). Feedback is welcome.
