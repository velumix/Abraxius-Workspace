# Platform support

| Host | Presence | Native notification | Background | Activation |
|---|---|---|---|---|
| Windows desktop | Avalonia tray host | adapter contract; unavailable in current package build | continuous desktop runtime | tray and typed routes |
| macOS desktop | Avalonia status item | `osascript` best-effort | continuous desktop runtime | menu and typed routes |
| Linux desktop | detected Avalonia status notifier | `notify-send` best-effort | continuous desktop runtime | tray where shell supports it |
| Android/iOS | no tray | host adapter contract | OS-constrained checkpoint/resume | typed route contract |
| Browser/WASM | no tray | browser-host contract | page-constrained; remote runtime recommended | typed web route contract |
| Embedded | capability-driven | host-defined | host-defined | typed route contract |

Tray initialization failure never fails application startup. Actual Windows native toast activation, mobile background services, and browser push require their platform host packages and remain explicit limitations rather than simulated support.
