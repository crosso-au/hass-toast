# Samples

Ready-to-use Home Assistant YAML for every option HASS Windows Toast supports. Each file covers
one feature, with every option explained in the comments at the top.

## How to try one

1. In Home Assistant, go to **Developer tools → Actions**.
2. Choose **Go to YAML mode**.
3. Paste one block from a file. Blocks are separated by `---` lines.
4. Select **Perform action**.

The same YAML works as an action in any automation or script. The automations in
[12-responding-to-clicks.yaml](12-responding-to-clicks.yaml) go under **Settings → Automations &
scenes → Create automation → ⋮ → Edit in YAML** instead.

Where a sample says `my-pc`, use your own device id, or leave `target` out to send to every
computer.

## The files

| File | What it shows |
| --- | --- |
| [01-basic.yaml](01-basic.yaml) | Message, title, choosing which computer, templates |
| [02-quick-options.yaml](02-quick-options.yaml) | The shortcut fields: `tag`, `group`, `scenario`, `image`, `attribution` |
| [03-images.yaml](03-images.yaml) | Round logo, banner and full-width images, including Home Assistant camera snapshots |
| [04-scenarios.yaml](04-scenarios.yaml) | Reminder, alarm, incoming call and urgent toasts, and how long a toast stays |
| [05-buttons.yaml](05-buttons.yaml) | Buttons: colours, opening web pages, snooze and dismiss, right-click menus, icons |
| [06-inputs.yaml](06-inputs.yaml) | Text boxes, drop-down lists and quick replies |
| [07-progress-bar.yaml](07-progress-bar.yaml) | Progress bars, and moving them along with `hass_toast.update` |
| [08-columns.yaml](08-columns.yaml) | Multi-column layouts: forecasts, pictures beside text, entity tables |
| [09-text-styles.yaml](09-text-styles.yaml) | Every text style |
| [10-sounds.yaml](10-sounds.yaml) | System sounds, looping alarms and silent toasts |
| [11-lifecycle.yaml](11-lifecycle.yaml) | Replacing, expiring and removing toasts, headings, and clicking the toast itself |
| [12-responding-to-clicks.yaml](12-responding-to-clicks.yaml) | Automations that act on button clicks and typed replies |
| [13-everything.yaml](13-everything.yaml) | One toast using most options together |

## Where the options go

```yaml
action: notify.hass_toast
data:
  message: ...          # required
  title: ...            # the quick fields: title, target, tag, group,
  target: ...           # scenario, image, attribution
  data:                 # everything else goes in this nested data block
    scenario: ...
    duration: ...
    visual:
      text: [...]
      app_logo: {...}
      hero: {...}
      inline: {...}
      progress: {...}
      groups: [...]
    inputs: [...]
    buttons: [...]
    context_menu: [...]
    audio: {...}
    header: {...}
    launch: {...}
    expires_in: ...
    suppress_popup: ...
    timestamp: ...
```

`data:` appears twice on purpose. The outer one holds the action's fields. The inner one holds
the Windows-specific options.

If you set `visual.text`, it replaces `title` and `message` as the toast's lines. `message` is
still required by the action, so keep it.

